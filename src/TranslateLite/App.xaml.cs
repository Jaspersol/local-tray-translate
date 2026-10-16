using System.IO;
using System.Windows;
using TranslateLite.Interop;
using TranslateLite.Models;
using TranslateLite.Services;
using TranslateLite.Windows;

namespace TranslateLite;

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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TranslateLite");
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

        var selectionText = SettingsWindow.FormatHotkey(_appSettings.SelectionHotkey.Modifiers, _appSettings.SelectionHotkey.Key);
        var screenOcrText = SettingsWindow.FormatHotkey(_appSettings.ScreenOcrHotkey.Modifiers, _appSettings.ScreenOcrHotkey.Key);

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

            var selectionText = SettingsWindow.FormatHotkey(_appSettings.SelectionHotkey.Modifiers, _appSettings.SelectionHotkey.Key);
            var screenOcrText = SettingsWindow.FormatHotkey(_appSettings.ScreenOcrHotkey.Modifiers, _appSettings.ScreenOcrHotkey.Key);
            _tray?.UpdateHotkeyTexts(selectionText, screenOcrText);
        }

        _hotkeys?.Register(
            _appSettings.SelectionHotkey.Modifiers, _appSettings.SelectionHotkey.Key,
            _appSettings.ScreenOcrHotkey.Modifiers, _appSettings.ScreenOcrHotkey.Key);
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
