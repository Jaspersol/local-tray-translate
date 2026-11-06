using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TranslateLite.Interop;

/// <summary>一次热键注册的结果；失败的组合不能被托盘菜单当成已生效的快捷键展示。</summary>
public readonly record struct HotkeyRegistration(bool SelectionRegistered, bool ScreenOcrRegistered)
{
    public bool AllRegistered => SelectionRegistered && ScreenOcrRegistered;
}

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
    /// 注册划词与截图两个热键，重复调用会先撤销上一次的注册；首次调用时创建消息窗口。
    /// </summary>
    /// <remarks>
    /// <c>RegisterHotKey</c> 失败最常见的原因是组合键被其它程序（截图工具、输入法）占用，
    /// 此时没有 <c>WM_HOTKEY</c> 可收，调用方不能把该组合键当成生效状态展示。
    /// 返回逐项结果而不是抛异常：启动阶段失败仍应让应用可用（托盘菜单可以手动触发）。
    /// 修饰键为空（裸键）的组合一律拒绝注册——全局裸键会吞掉该键在所有程序里的输入。
    /// </remarks>
    public HotkeyRegistration Register(uint selectionModifiers, uint selectionKey, uint screenOcrModifiers,
        uint screenOcrKey)
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

        // RegisterHotKey 对同一 id 重复注册会失败（ERROR_HOTKEY_ALREADY_REGISTERED），先撤销本服务自己的注册。
        UnregisterAll();
        var selection = TryRegister(SelectionHotkeyId, selectionModifiers, selectionKey);
        var screenOcr = TryRegister(ScreenOcrHotkeyId, screenOcrModifiers, screenOcrKey);
        return new HotkeyRegistration(selection, screenOcr);
    }

    private bool TryRegister(int id, uint modifiers, uint key)
    {
        if (_source is null || modifiers == 0 || key == 0)
        {
            return false;
        }

        return RegisterHotKey(_source.Handle, id, modifiers, key);
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
