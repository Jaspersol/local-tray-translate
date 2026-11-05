using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TranslateLite.Interop;

/// <summary>
/// 全局热键注册。实现方式是一个不可见的消息窗口（<see cref="HwndSource"/>）加 <c>WM_HOTKEY</c> 钩子：
/// 热键消息只投递给线程里某个窗口，没有窗口就收不到，因此这里必须自建一个。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int SelectionHotkeyId = 1001;
    private const int ScreenOcrHotkeyId = 1002;
    private const int WmHotkey = 0x0312;

    private HwndSource? _source;

    public event EventHandler? SelectionRequested;
    public event EventHandler? ScreenOcrRequested;

    /// <summary>
    /// 注册划词与截图两个热键，重复调用会覆盖上一次的注册；首次调用时创建消息窗口。
    /// </summary>
    /// <remarks>
    /// <c>RegisterHotKey</c> 的返回值被有意忽略：热键被其它程序占用（如截图工具、输入法）是最常见的失败原因，
    /// 但此时应用主界面和托盘菜单仍然可用，注册失败不弹提示、也不中断启动流程。
    /// 需要诊断时可在此处补日志；改成弹窗会让每次冲突都打断用户。
    /// </remarks>
    public void Register(uint selectionModifiers, uint selectionKey, uint screenOcrModifiers, uint screenOcrKey)
    {
        if (_source is null)
        {
            var parameters = new HwndSourceParameters("TranslateLiteHotkeys")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }

        RegisterHotKey(_source.Handle, SelectionHotkeyId, selectionModifiers, selectionKey);
        RegisterHotKey(_source.Handle, ScreenOcrHotkeyId, screenOcrModifiers, screenOcrKey);
    }

    /// <summary>
    /// 先撤销再按新配置注册，用于设置窗口保存后的生效；消息窗口尚未创建时直接返回。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Register"/> 相同，<c>RegisterHotKey</c> 的返回值被忽略——新组合键同样可能被占用，
    /// 此时用户看到的是“保存成功但热键没反应”，这是已知取舍（见 <see cref="Register"/>）。
    /// </remarks>
    public void UpdateRegistration(uint selectionModifiers, uint selectionKey, uint screenOcrModifiers,
        uint screenOcrKey)
    {
        if (_source is null)
        {
            return;
        }

        UnregisterAll();
        RegisterHotKey(_source.Handle, SelectionHotkeyId, selectionModifiers, selectionKey);
        RegisterHotKey(_source.Handle, ScreenOcrHotkeyId, screenOcrModifiers, screenOcrKey);
    }

    /// <summary>撤销本服务注册的两个热键 id；未注册时调用无害。</summary>
    public void UnregisterAll()
    {
        if (_source is null)
        {
            return;
        }

        UnregisterHotKey(_source.Handle, SelectionHotkeyId);
        UnregisterHotKey(_source.Handle, ScreenOcrHotkeyId);
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            UnregisterHotKey(_source.Handle, SelectionHotkeyId);
            UnregisterHotKey(_source.Handle, ScreenOcrHotkeyId);
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        handled = true;
        var id = wParam.ToInt32();
        if (id == SelectionHotkeyId)
        {
            SelectionRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (id == ScreenOcrHotkeyId)
        {
            ScreenOcrRequested?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
