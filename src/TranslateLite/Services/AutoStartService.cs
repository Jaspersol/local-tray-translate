using Microsoft.Win32;

namespace TranslateLite.Services;

/// <summary>
/// 通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 控制开机自启。
/// 写当前用户键不需要管理员权限；读写失败只返回 false，不让设置或启动流程中断。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LocalTrayTranslate";

    /// <summary>把启动项对齐到 enabled，返回是否写入成功。</summary>
    public static bool Apply(bool enabled)
    {
        // 每次都用当前进程路径重写：用户把程序挪到别的目录后，旧的自启命令会在下次启动时自动修正。
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key.SetValue(ValueName, $"\"{executable}\"");
            }
            else
            {
                // 值不存在时 DeleteValue(throwOnMissingValue: false) 直接返回，不会抛。
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
