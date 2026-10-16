using System.Windows;
using System.Windows.Input;
using TranslateLite.Models;

namespace TranslateLite.Windows;

public partial class SettingsWindow : Window
{
    private uint _selectionModifiers;
    private uint _selectionKey;
    private uint _screenOcrModifiers;
    private uint _screenOcrKey;

    public HotkeyConfig SelectionConfig { get; private set; }
    public HotkeyConfig ScreenOcrConfig { get; private set; }

    public SettingsWindow(HotkeyConfig selectionConfig, HotkeyConfig screenOcrConfig)
    {
        InitializeComponent();

        _selectionModifiers = selectionConfig.Modifiers;
        _selectionKey = selectionConfig.Key;
        _screenOcrModifiers = screenOcrConfig.Modifiers;
        _screenOcrKey = screenOcrConfig.Key;

        SelectionConfig = selectionConfig;
        ScreenOcrConfig = screenOcrConfig;

        SelectionHotkeyBox.Text = FormatHotkey(_selectionModifiers, _selectionKey);
        ScreenOcrHotkeyBox.Text = FormatHotkey(_screenOcrModifiers, _screenOcrKey);
    }

    private void OnHotkeyBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.SelectAll();
        }
    }

    private void OnSelectionHotkeyPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var (modifiers, key) = CaptureHotkey(e);
        if (key == 0)
        {
            return;
        }

        _selectionModifiers = modifiers;
        _selectionKey = key;
        SelectionHotkeyBox.Text = FormatHotkey(modifiers, key);
        e.Handled = true;
    }

    private void OnScreenOcrHotkeyPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var (modifiers, key) = CaptureHotkey(e);
        if (key == 0)
        {
            return;
        }

        _screenOcrModifiers = modifiers;
        _screenOcrKey = key;
        ScreenOcrHotkeyBox.Text = FormatHotkey(modifiers, key);
        e.Handled = true;
    }

    private static (uint modifiers, uint key) CaptureHotkey(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.Tab or Key.Escape)
        {
            return (0, 0);
        }

        var modifiers = GetModifierFlags(e.KeyboardDevice.Modifiers);
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        return (modifiers, vk);
    }

    private static uint GetModifierFlags(ModifierKeys modifiers)
    {
        uint flags = 0;
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            flags |= 0x0001;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            flags |= 0x0002;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            flags |= 0x0004;
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            flags |= 0x0008;
        }

        return flags;
    }

    public static string FormatHotkey(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & 0x0002) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & 0x0001) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & 0x0004) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & 0x0008) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyInterop.KeyFromVirtualKey((int)key).ToString());
        return string.Join("+", parts);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SelectionConfig = new HotkeyConfig { Modifiers = _selectionModifiers, Key = _selectionKey };
        ScreenOcrConfig = new HotkeyConfig { Modifiers = _screenOcrModifiers, Key = _screenOcrKey };
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}