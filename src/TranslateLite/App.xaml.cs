using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
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
    private const string SingleInstanceMutexName = @"Local\LocalTrayTranslate.SingleInstance";

    // 第二个实例用它通知第一个实例“把主窗口叫出来”，比跨进程 ShowWindow 更可靠（WPF 自己维护
    // Window.Visibility，外部 ShowWindow 显示出来的窗口在 WPF 看来仍是隐藏的）。
    private const string ActivationEventName = @"Local\LocalTrayTranslate.Activate";

    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private DictionaryService? _dictionary;
    private ClipboardSelectionService? _selection;
    private OcrService? _ocr;
    private MainWindow? _mainWindow;
    private SettingsService? _settings;
    private AppSettings? _appSettings;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationSignal;
    private Thread? _activationListener;
    private bool _unhandledExceptionShown;

    // 截图流程的单一飞行守卫：ShowDialog 的嵌套消息泵在遮罩期间仍会派发 WM_HOTKEY，
    // 托盘菜单也可能被再次点击；同一时刻只允许一个截图流程，重复触发直接丢弃。
    private int _captureInProgress;

    // 结果弹窗复用同一个实例，避免连续多次翻译堆积出多个窗口。
    private ResultPopupWindow? _resultPopup;

    // WPF 的 OnStartup 只能是 async void；未捕获的异常会直接终止进程。
    // 三层兜底：每个 async void 入口自行 try/catch、Dispatcher 未处理异常、进程级未处理异常。
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        try
        {
            if (!TryAcquireSingleInstance())
            {
                // 已有实例在运行：通知它把主窗口带到前台（通知不到时至少给一句说明），然后退出该进程。
                SignalExistingInstance();
                Shutdown();
                return;
            }

            // 先创建命名事件再初始化：第二个实例在初始化期间启动时也能把请求挂起，初始化完成后再处理。
            _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);

            await InitializeAsync();
            StartActivationListener();
        }
        catch (Exception exception)
        {
            ShowError("启动失败", exception);
            Shutdown();
        }
    }

    private async Task InitializeAsync()
    {
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

        _tray = new TrayService(
            showAction: () => ShowMainWindow(),
            // 托盘菜单点下去时菜单还没关闭、前台窗口也还没拿回焦点，立刻复制会抓错窗口；
            // 两个托盘入口各自等 300ms，热键入口不需要这段延时。
            selectionAction: async () =>
            {
                await Task.Delay(300);
                await TranslateSelectionAsync();
            },
            screenOcrAction: async () =>
            {
                await Task.Delay(300);
                await TranslateScreenRegionAsync();
            },
            settingsAction: OpenSettings,
            exitAction: Shutdown);

        // 先按配置显示组合键；注册失败时 ApplyHotkeyRegistration 会改成「未生效」并弹提示。
        var (selectionText, screenOcrText) = FormatHotkeyTexts(_appSettings);
        _tray.Initialize(selectionText, screenOcrText);
        // 把注册表启动项对齐到设置值，顺便修正程序被挪目录后残留的旧路径。
        SyncAutoStart();

        _hotkeys = new HotkeyService();
        _hotkeys.SelectionRequested += async (_, _) => await TranslateSelectionAsync();
        _hotkeys.ScreenOcrRequested += async (_, _) => await TranslateScreenRegionAsync();

        ApplyHotkeyRegistration();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _dictionary?.Dispose();

        _activationSignal?.Dispose();
        _activationSignal = null;

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 互斥量所有权不在本线程（异常路径）时释放会抛；进程退出本身就会释放句柄。
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }

    // ---- 全局异常处理 ----

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// UI 线程上的未处理异常（剪贴板 COMException、SendKeys、SQLite、OCR 都可能抛出）：
    /// 提示用户后标记为已处理，避免一个偶发失败把常驻托盘的应用整个带走。
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_unhandledExceptionShown)
        {
            // 连续异常时不要弹一长串对话框，首个提示已经说明问题。
            return;
        }

        _unhandledExceptionShown = true;
        ShowError("操作失败", e.Exception);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ShowError("发生未处理的错误", exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // 后台任务的异常没有观察者：标记为已观察，避免终结器线程再次抛出。
        e.SetObserved();
    }

    private static void ShowError(string title, Exception exception)
    {
        try
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                $"LocalTrayTranslate - {title}",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // 连错误对话框都弹不出来（如进程正在退出）时只能放弃提示。
        }
    }

    // ---- 单实例 ----

    private bool TryAcquireSingleInstance()
    {
        // 全局 Mutex 只在当前用户会话内有效；第二个实例拿到 createdNew=false 就说明已有实例。
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (createdNew)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // 命名对象创建失败（权限或内核句柄耗尽）时放弃单实例保护，而不是让应用起不来。
            return true;
        }

        // 同名对象可能还存在，但持有它的进程已经退出（句柄泄漏）。此时 WaitOne(0) 能立即拿到所有权，
        // 说明没有活的实例，本进程应当继续启动；只有真正被另一个实例持有时才退出。
        var acquired = false;
        try
        {
            acquired = _instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者异常终止，所有权已转给本线程。
            acquired = true;
        }

        if (acquired)
        {
            return true;
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
        return false;
    }

    /// <summary>监听第二个实例的激活请求；收到后把主窗口显示到前台。</summary>
    private void StartActivationListener()
    {
        _activationListener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _activationSignal!.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    Dispatcher.Invoke(ShowMainWindow);
                }
                catch (Exception exception) when (exception is TaskCanceledException or InvalidOperationException)
                {
                    // 应用正在退出，Dispatcher 已停止。
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "TranslateLiteActivation"
        };
        _activationListener.Start();
    }

    /// <summary>
    /// 通知已在运行的实例显示主窗口；通知不到时退回窗口句柄查找，最后至少弹一句说明。
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }

                return;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // 没有权限打开事件时退回窗口查找。
        }

        var window = FindWindow(null, "LTT");
        if (window != IntPtr.Zero)
        {
            ShowWindow(window, SwRestore);
            SetForegroundWindow(window);
            return;
        }

        System.Windows.MessageBox.Show(
            "LocalTrayTranslate 已在运行，请从系统托盘打开。",
            "LocalTrayTranslate",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ---- 托盘与热键 ----

    private void OpenSettings()
    {
        if (_appSettings is null || _settings is null)
        {
            return;
        }

        _hotkeys?.UnregisterAll();

        var settingsWindow = new SettingsWindow(_appSettings.SelectionHotkey, _appSettings.ScreenOcrHotkey, _appSettings.AutoStart);
        var result = settingsWindow.ShowDialog();

        if (result == true)
        {
            _appSettings.SelectionHotkey = settingsWindow.SelectionConfig;
            _appSettings.ScreenOcrHotkey = settingsWindow.ScreenOcrConfig;
            _appSettings.AutoStart = settingsWindow.AutoStart;
            _settings.Save(_appSettings);
            SyncAutoStart();
        }

        // 设置窗口无论确认还是取消都要重注册：取消时沿用旧配置，正好把先前 UnregisterAll 的热键恢复。
        // 保存后的组合键同样可能被别的程序占用，因此结果统一走 ApplyHotkeyRegistration 报告。
        ApplyHotkeyRegistration();
    }

    /// <summary>把开机自启设置对齐到注册表；失败时仅提示，不阻断启动或设置保存。</summary>
    private void SyncAutoStart()
    {
        if (_appSettings is null)
        {
            return;
        }

        if (!AutoStartService.Apply(_appSettings.AutoStart) && _appSettings.AutoStart)
        {
            _tray?.ShowWarning(
                "开机自启未生效",
                "无法写入 Windows 启动项，请检查安全软件或系统策略是否禁止修改注册表。");
        }
    }

    /// <summary>注册热键并把结果如实反映到托盘菜单：注册失败的组合不能显示成生效状态。</summary>
    private void ApplyHotkeyRegistration()
    {
        if (_hotkeys is null || _appSettings is null)
        {
            return;
        }

        var registration = _hotkeys.Register(
            _appSettings.SelectionHotkey.Modifiers, _appSettings.SelectionHotkey.Key,
            _appSettings.ScreenOcrHotkey.Modifiers, _appSettings.ScreenOcrHotkey.Key);

        var (selectionText, screenOcrText) = FormatHotkeyTexts(_appSettings);
        if (!registration.SelectionRegistered)
        {
            selectionText += "（未生效）";
        }

        if (!registration.ScreenOcrRegistered)
        {
            screenOcrText += "（未生效）";
        }

        _tray?.UpdateHotkeyTexts(selectionText, screenOcrText);

        if (!registration.AllRegistered)
        {
            var failed = new List<string>();
            if (!registration.SelectionRegistered)
            {
                failed.Add($"划词翻译 {selectionText}");
            }

            if (!registration.ScreenOcrRegistered)
            {
                failed.Add($"截图翻译 {screenOcrText}");
            }

            _tray?.ShowWarning(
                "快捷键注册失败",
                $"以下快捷键被其它程序占用或无效，暂时无法使用：{string.Join("；", failed)}。" +
                "可在「设置」里换一组组合键。");
        }
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
        _mainWindow.WindowState = WindowState.Normal;
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

                // 主库刚被替换，旧的 -wal/-shm 里还是上一版词典的页。SQLite 打开库时会尝试回放 WAL，
                // 新主库配旧 WAL 会把旧页混进来，因此在打开连接之前就把侧车文件删掉。
                DeleteSqliteSidecar(dictionaryPath + "-wal");
                DeleteSqliteSidecar(dictionaryPath + "-shm");
            }
        }
        catch
        {
            // 内置词典只是首启的便利副本：磁盘占用、权限或文件被占用时失败可接受，
            // 沿用已有的用户词典，后续启动会再次尝试。
        }
    }

    private static void DeleteSqliteSidecar(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 侧车文件删不掉时下一次启动会再试；删失败不应影响本次词典复制。
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
        // 防抖：ShowDialog 的嵌套消息泵在遮罩期间仍会派发 WM_HOTKEY，托盘菜单也可能被再次点击；
        // 守卫覆盖整个流程（选区 → OCR → 弹结果），期间任何重复触发都直接丢弃。
        if (Interlocked.CompareExchange(ref _captureInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (_ocr is null)
            {
                return;
            }

            var overlay = new CaptureOverlayWindow();
            var selected = overlay.CaptureRegion();
            if (selected is null)
            {
                return;
            }

            var text = await _ocr.ReadTextAsync(selected);
            await ShowLookupAsync(text);
        }
        finally
        {
            Interlocked.Exchange(ref _captureInProgress, 0);
        }
    }

    private async Task ShowLookupAsync(string? text)
    {
        if (_dictionary is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var results = await _dictionary.LookupAsync(text);
        _resultPopup ??= new ResultPopupWindow(_dictionary);
        _resultPopup.ShowResult(text.Trim(), results);
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
