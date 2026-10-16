using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TranslateLite.Interop;

public sealed class HotkeyService : IDisposable
{
    private const int SelectionHotkeyId = 1001;
    private const int ScreenOcrHotkeyId = 1002;
    private const int WmHotkey = 0x0312;

    private HwndSource? _source;

    public event EventHandler? SelectionRequested;
    public event EventHandler? ScreenOcrRequested;

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

    public void UpdateRegistration(uint selectionModifiers, uint selectionKey, uint screenOcrModifiers, uint screenOcrKey)
    {
        if (_source is null)
        {
            return;
        }

        UnregisterAll();
        RegisterHotKey(_source.Handle, SelectionHotkeyId, selectionModifiers, selectionKey);
        RegisterHotKey(_source.Handle, ScreenOcrHotkeyId, screenOcrModifiers, screenOcrKey);
    }

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
