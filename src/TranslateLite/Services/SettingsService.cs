using System.IO;
using System.Text.Json;
using TranslateLite.Models;

namespace TranslateLite.Services;

/// <summary>settings.json 的读写；读取失败一律回落默认设置，保证应用永远能启动。</summary>
public sealed class SettingsService
{
    private readonly string _settingsPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // 配置文件可能被手改坏（非法 JSON）或读到一半被中断。设置项只影响快捷键等偏好，
            // 回落默认值比弹错误框、让应用起不来更合适；文件会在下次 Save 时被正常内容覆盖。
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}
