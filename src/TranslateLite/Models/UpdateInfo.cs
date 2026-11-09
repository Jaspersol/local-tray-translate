namespace TranslateLite.Models;

/// <summary>
/// 一次检查更新的结果。安装只使用 <see cref="Services.UpdateService"/> 缓存的这份数据，
/// UI 不能把任意下载地址或校验值交给安装流程。
/// </summary>
public sealed class UpdateInfo
{
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public bool HasUpdate { get; set; }
    public string? ReleaseName { get; set; }
    public string? Notes { get; set; }
    public string? PublishedAt { get; set; }
    public string? ReleaseUrl { get; set; }
    public string? AssetName { get; set; }
    public long? AssetSize { get; set; }
    /// <summary>主下载地址：release 资产的 browser_download_url。</summary>
    public string? DownloadUrl { get; set; }
    /// <summary>备用地址：API 资产地址，需带 Accept: application/octet-stream。</summary>
    public string? MirrorUrl { get; set; }
    public string? Sha256 { get; set; }
    public long CheckedAt { get; set; }
    /// <summary>本次结果来自不消耗 API 配额的直链兜底路径（无发布说明、时间与体积）。</summary>
    public bool FromFallback { get; set; }
}

/// <summary>下载进度；Total 为 null 时表示服务器未给出 Content-Length。</summary>
public readonly record struct UpdateProgress(long Downloaded, long? Total);
