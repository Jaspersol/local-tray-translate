namespace TranslateLite.Models;

public class AppSettings
{
    public HotkeyConfig SelectionHotkey { get; set; } = new() { Modifiers = 0x0003, Key = 0x58 };
    public HotkeyConfig ScreenOcrHotkey { get; set; } = new() { Modifiers = 0x0003, Key = 0x53 };
    /// <summary>是否随 Windows 登录自动启动；默认关闭，由 App 启动时对齐到注册表。</summary>
    public bool AutoStart { get; set; }
    /// <summary>启动时自动检查 GitHub Releases 更新（每 6 小时最多一次）；只提示，不自动下载。</summary>
    public bool AutoCheckUpdate { get; set; } = true;
    /// <summary>上次检查更新的 UTC 毫秒时间戳，用于自动检查节流。</summary>
    public long LastUpdateCheckMs { get; set; }
}

public class HotkeyConfig
{
    public uint Modifiers { get; set; }
    public uint Key { get; set; }
}
