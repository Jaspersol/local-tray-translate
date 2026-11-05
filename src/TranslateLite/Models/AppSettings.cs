namespace TranslateLite.Models;

public class AppSettings
{
    public HotkeyConfig SelectionHotkey { get; set; } = new() { Modifiers = 0x0003, Key = 0x58 };
    public HotkeyConfig ScreenOcrHotkey { get; set; } = new() { Modifiers = 0x0003, Key = 0x53 };
}

public class HotkeyConfig
{
    public uint Modifiers { get; set; }
    public uint Key { get; set; }
}
