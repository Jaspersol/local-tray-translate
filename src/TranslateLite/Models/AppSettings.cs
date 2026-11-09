namespace TranslateLite.Models;

public class AppSettings
{
    public HotkeyConfig SelectionHotkey { get; set; } = new() { Modifiers = 0x0003, Key = 0x58 };
    public HotkeyConfig ScreenOcrHotkey { get; set; } = new() { Modifiers = 0x0003, Key = 0x53 };
    /// <summary>是否随 Windows 登录自动启动；默认关闭，由 App 启动时对齐到注册表。</summary>
    public bool AutoStart { get; set; }
}

public class HotkeyConfig
{
    public uint Modifiers { get; set; }
    public uint Key { get; set; }
}
