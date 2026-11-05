using System.IO;
using System.Windows;
using TranslateLite.Interop;
using TranslateLite.Models;
using TranslateLite.Services;
using TranslateLite.Windows;

namespace TranslateLite;

/// <summary>
/// 应用入口：负责组装设置、词典、托盘、全局热键与主窗口，并把三条翻译入口
/// （划词、截图、主窗口查询）统一收敛到 <see cref="ShowLookupAsync"/>。
/// </summary>
public partial class App : System.Windows.Application
{
    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private DictionaryService? _dictionary;
    private ClipboardSelectionService? _selection;
    private OcrService? _ocr;
    private MainWindow? _mainWindow;
    private SettingsService? _settings;
    private AppSettings? _appSettings;

    // WPF 的 OnStartup 只能是 async void；这里的异常会直接终止进程，故每个可失败步骤都自行兜底。
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TranslateLite");
        Directory.CreateDirectory(appData);

        var settingsPath = Path.Combine(appData, "settings.json");
        _settings = new SettingsService(settingsPath);
        _appSettings = _settings.Load();

        var dictionaryPath = Path.Combine(appData, "dictionary.db");
        CopyBundledDictionaryIfNeeded(dictionaryPath);
        var userDictionaryPath = Path.Combine(appData, "user_dictionary.db");
        _dictionary = new DictionaryService(dictionaryPath, userDictionaryPath);
        await _dictionary.InitializeAsync();

        _selection = new ClipboardSelectionService();
        _ocr = new OcrService();
        _mainWindow = new MainWindow(_dictionary);
        _mainWindow.Hide();

        var (selectionText, screenOcrText) = FormatHotkeyTexts(_appSettings);

        _tray = new TrayService(
            showAction: () => ShowMainWindow(),
            selectionAction: async () => await TranslateSelectionAsync(),
            screenOcrAction: async () => await TranslateScreenRegionAsync(),
            settingsAction: OpenSettings,
            exitAction: Shutdown);
        _tray.Initialize(selectionText, screenOcrText);

        _hotkeys = new HotkeyService();
        _hotkeys.SelectionRequested += async (_, _) => await TranslateSelectionAsync();
        _hotkeys.ScreenOcrRequested += async (_, _) => await TranslateScreenRegionAsync();
        _hotkeys.Register(
            _appSettings.SelectionHotkey.Modifiers, _appSettings.SelectionHotkey.Key,
            _appSettings.ScreenOcrHotkey.Modifiers, _appSettings.ScreenOcrHotkey.Key);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _dictionary?.Dispose();
        base.OnExit(e);
    }

    private void OpenSettings()
    {
        if (_appSettings is null || _settings is null)
        {
            return;
        }

        _hotkeys?.UnregisterAll();

        var settingsWindow = new SettingsWindow(_appSettings.SelectionHotkey, _appSettings.ScreenOcrHotkey);
        var result = settingsWindow.ShowDialog();

        if (result == true)
        {
            _appSettings.SelectionHotkey = settingsWindow.SelectionConfig;
            _appSettings.ScreenOcrHotkey = settingsWindow.ScreenOcrConfig;
            _settings.Save(_appSettings);

            var (selectionText, screenOcrText) = FormatHotkeyTexts(_appSettings);
            _tray?.UpdateHotkeyTexts(selectionText, screenOcrText);
        }

        // 设置窗口无论确认还是取消都会重注册：取消时沿用旧配置，正好把先前 UnregisterAll 的热键恢复。
        _hotkeys?.Register(
            _appSettings.SelectionHotkey.Modifiers, _appSettings.SelectionHotkey.Key,
            _appSettings.ScreenOcrHotkey.Modifiers, _appSettings.ScreenOcrHotkey.Key);
    }

    /// <summary>把两组热键格式化成托盘菜单显示的文本。</summary>
    private static (string selectionText, string screenOcrText) FormatHotkeyTexts(AppSettings settings)
    {
        return (
            SettingsWindow.FormatHotkey(settings.SelectionHotkey.Modifiers, settings.SelectionHotkey.Key),
            SettingsWindow.FormatHotkey(settings.ScreenOcrHotkey.Modifiers, settings.ScreenOcrHotkey.Key));
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private static void CopyBundledDictionaryIfNeeded(string dictionaryPath)
    {
        var bundledDictionary = FindBundledDictionary();
        if (bundledDictionary is null)
        {
            return;
        }

        try
        {
            if (ShouldReplaceDictionary(bundledDictionary, dictionaryPath))
            {
                File.Copy(bundledDictionary, dictionaryPath, overwrite: true);
            }
        }
        catch
        {
            // 内置词典只是首启的便利副本：磁盘占用、权限或文件被占用时失败可接受，
            // 沿用已有的用户词典，后续启动会再次尝试。
        }
    }

    private static string? FindBundledDictionary()
    {
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "dictionary.db"),
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "dictionary.db")
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>内置词典比现有文件更大才覆盖，避免旧安装包把用户已更新的词典替换回去。</summary>
    private static bool ShouldReplaceDictionary(string sourcePath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            return true;
        }

        var source = new FileInfo(sourcePath);
        var destination = new FileInfo(destinationPath);
        return source.Length > destination.Length;
    }

    private async Task TranslateSelectionAsync()
    {
        if (_selection is null || _dictionary is null)
        {
            return;
        }

        var text = await _selection.GetSelectedTextAsync();
        await ShowLookupAsync(text);
    }

    private async Task TranslateScreenRegionAsync()
    {
        if (_ocr is null)
        {
            return;
        }

        // 托盘菜单关闭后焦点尚未回到前台窗口，立刻截图会把菜单残影一起抓进去。
        await Task.Delay(300);

        var overlay = new CaptureOverlayWindow();
        var selected = overlay.CaptureRegion();
        if (selected is null)
        {
            return;
        }

        var text = await _ocr.ReadTextAsync(selected);
        await ShowLookupAsync(text);
    }

    private async Task ShowLookupAsync(string? text)
    {
        if (_dictionary is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var results = await _dictionary.LookupAsync(text);
        var popup = new ResultPopupWindow(text.Trim(), results, _dictionary);
        popup.Show();
        popup.Activate();
    }
}
