using System.Windows;
using System.Windows.Input;
using TranslateLite.Models;

namespace TranslateLite.Windows;

/// <summary>快捷键设置窗口：捕获 Ctrl/Alt/Shift/Win 组合键并回传给调用方保存。</summary>
public partial class SettingsWindow : Window
{
    // MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN：user32!RegisterHotKey 的 fsModifiers 位掩码，
    // 取值必须与 Win32 一致——HotkeyService 会把 HotkeyConfig.Modifiers 原样透传给该 API。
    private const uint ModifierAlt = 0x0001;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierShift = 0x0004;
    private const uint ModifierWin = 0x0008;

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

        e.Handled = true;
        if (!ValidateCombination(modifiers, key, _screenOcrModifiers, _screenOcrKey))
        {
            return;
        }

        _selectionModifiers = modifiers;
        _selectionKey = key;
        SelectionHotkeyBox.Text = FormatHotkey(modifiers, key);
        ValidationText.Text = "";
    }

    private void OnScreenOcrHotkeyPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var (modifiers, key) = CaptureHotkey(e);
        if (key == 0)
        {
            return;
        }

        e.Handled = true;
        if (!ValidateCombination(modifiers, key, _selectionModifiers, _selectionKey))
        {
            return;
        }

        _screenOcrModifiers = modifiers;
        _screenOcrKey = key;
        ScreenOcrHotkeyBox.Text = FormatHotkey(modifiers, key);
        ValidationText.Text = "";
    }

    /// <summary>
    /// 校验一次捕获到的组合：必须有修饰键（裸键注册成全局热键会吞掉该键在所有程序里的输入），
    /// 且不能与另一个动作的组合相同（RegisterHotKey 对重复组合只会成功一次）。
    /// </summary>
    private bool ValidateCombination(uint modifiers, uint key, uint otherModifiers, uint otherKey)
    {
        if (key == 0)
        {
            ValidationText.Text = "请先点击输入框并按下一个组合键。";
            return false;
        }

        if (modifiers == 0)
        {
            ValidationText.Text = "全局热键必须包含 Ctrl、Alt、Shift 或 Win 修饰键。";
            return false;
        }

        if (modifiers == otherModifiers && key == otherKey)
        {
            ValidationText.Text = "该组合已用于另一个动作，请换一组。";
            return false;
        }

        return true;
    }

    private static (uint modifiers, uint key) CaptureHotkey(System.Windows.Input.KeyEventArgs e)
    {
        // Alt 组合键在 WPF 里会先被系统吞成 Key.System，真正的按键在 SystemKey 上。
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
            flags |= ModifierAlt;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            flags |= ModifierControl;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            flags |= ModifierShift;
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            flags |= ModifierWin;
        }

        return flags;
    }

    /// <summary>把位掩码与虚拟键码还原成 “Ctrl+Alt+X” 形式的展示文本。</summary>
    public static string FormatHotkey(uint modifiers, uint key)
    {
        if (key == 0)
        {
            return "未设置";
        }

        var parts = new List<string>();
        if ((modifiers & ModifierControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModifierShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyInterop.KeyFromVirtualKey((int)key).ToString());
        return string.Join("+", parts);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // 保存前再校验一次：文本框内容可能来自旧版本 settings.json（无修饰键或两个动作撞车），
        // 捕获阶段的拦截只管本次会话新按下的组合。
        if (!ValidateCombination(_selectionModifiers, _selectionKey, _screenOcrModifiers, _screenOcrKey))
        {
            return;
        }

        if (!ValidateCombination(_screenOcrModifiers, _screenOcrKey, _selectionModifiers, _selectionKey))
        {
            return;
        }

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
