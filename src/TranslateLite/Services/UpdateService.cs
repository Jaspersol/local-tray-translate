using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TranslateLite.Models;

namespace TranslateLite.Services;

/// <summary>
/// 检查与安装更新。检查以 GitHub REST API 为主路径；API 被限流或不可达时切到不消耗配额的
/// 直链方案（releases/latest 重定向取版本号 + SHA256SUMS.txt 取资产名与校验值）。
/// 下载同样是主地址优先、直链 releases/latest/download/&lt;name&gt; 兜底。
/// 安装只接受 <see cref="LastResult"/> 缓存的结果，UI 无法传入任意地址。
/// </summary>
public sealed class UpdateService
{
    private const string Repo = "Jaspersol/local-tray-translate";
    private const string UserAgent = "LocalTrayTranslate";
    private const string ExeName = "LocalTrayTranslate.exe";
    private const long AutoCheckIntervalMs = 6 * 60 * 60 * 1000;
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private const long ProgressStepBytes = 128 * 1024;
    private const int MaxRedirects = 10;
    private const string ChecksumsAsset = "SHA256SUMS.txt";

    // asset 下载会重定向到这些 GitHub 域名；逐跳校验，禁止跳到外部主机。
    private static readonly string[] DownloadHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "githubusercontent.com"
    };

    private static readonly char[] DigestSeparators = { ' ', '\t', '\r', '\n', '*' };
    private static readonly object CacheLock = new();
    private static UpdateInfo? _lastCheck;

    private readonly HttpClient _apiClient;
    private readonly HttpClient _downloadClient;
    private readonly SettingsService _settings;
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<Action>? _marshalToUi;

    /// <summary>当前程序版本，来自程序集版本（0.1.4）。</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    public UpdateService(SettingsService settings, Func<AppSettings> getSettings,
        Action<Action>? marshalToUi = null)
    {
        _settings = settings;
        _getSettings = getSettings;
        _marshalToUi = marshalToUi;

        var ua = $"{UserAgent}/{CurrentVersion} (+https://github.com/{Repo})";
        _apiClient = CreateClient(TimeSpan.FromSeconds(30), ua, decompress: true);
        // 下载不设整体超时：每次读取单独用 60s 超时，避免 180MB 的更新包被 30s 掐断。
        _downloadClient = CreateClient(Timeout.InfiniteTimeSpan, ua, decompress: false);
    }

    /// <summary>最近一次检查结果；新开的设置窗口可以据此直接显示，无需联网。</summary>
    public static UpdateInfo? LastResult()
    {
        lock (CacheLock)
        {
            return _lastCheck;
        }
    }

    private static void Cache(UpdateInfo info)
    {
        lock (CacheLock)
        {
            _lastCheck = info;
        }
    }

    /// <summary>手动检查：主路径失败自动切备用方案，成功后记录时间戳并缓存结果。</summary>
    public async Task<UpdateInfo> CheckAsync()
    {
        var info = await FetchLatestWithFallbackAsync().ConfigureAwait(false);
        Cache(info);
        RecordCheck();
        return info;
    }

    /// <summary>启动时的自动检查：受设置开关与 6 小时间隔约束；失败也记录时间，避免反复重试。</summary>
    public async Task<UpdateInfo?> AutoCheckAsync()
    {
        var settings = _getSettings();
        if (!settings.AutoCheckUpdate || NowMs() - settings.LastUpdateCheckMs < AutoCheckIntervalMs)
        {
            return null;
        }

        try
        {
            var info = await FetchLatestWithFallbackAsync().ConfigureAwait(false);
            Cache(info);
            RecordCheck();
            return info.HasUpdate ? info : null;
        }
        catch
        {
            RecordCheck();
            return null;
        }
    }

    private void RecordCheck()
    {
        void Save()
        {
            try
            {
                var settings = _getSettings();
                settings.LastUpdateCheckMs = NowMs();
                _settings.Save(settings);
            }
            catch
            {
                // 时间戳只影响节流；写失败不应让检查失败。
            }
        }

        if (_marshalToUi is null)
        {
            Save();
            return;
        }

        try
        {
            _marshalToUi(Save);
        }
        catch
        {
            // 应用正在退出、Dispatcher 已停止时退回同步保存。
            Save();
        }
    }

    // ---- 检查：主路径（REST API）与直链兜底 ----

    private async Task<UpdateInfo> FetchLatestWithFallbackAsync()
    {
        try
        {
            return await FetchLatestApiAsync().ConfigureAwait(false);
        }
        catch (Exception primary)
        {
            try
            {
                var info = await FetchLatestFallbackAsync().ConfigureAwait(false);
                info.FromFallback = true;
                return info;
            }
            catch (Exception fallback)
            {
                throw new InvalidOperationException(
                    $"{fallback.Message}（API 主路径：{primary.Message}）", fallback);
            }
        }
    }

    private async Task<UpdateInfo> FetchLatestApiAsync()
    {
        using var response = await SendAsync(_apiClient, LatestReleaseApi(),
            "application/vnd.github+json", HttpCompletionOption.ResponseContentRead,
            CancellationToken.None).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("GitHub 仓库还没有发布任何版本");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden
            && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && string.Equals(remaining.FirstOrDefault()?.Trim(), "0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GitHub API 访问次数已用完，请稍后再试");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"检查更新失败：HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var info = ParseRelease(root);

        // 校验值优先用 GitHub 资产自带的 digest；老 release 没有该字段时退回 <asset>.sha256。
        if (info.Sha256 is null && info.AssetName is { } name)
        {
            var checksumUrl = ChecksumAssetUrl(root, name);
            if (checksumUrl is not null && ValidateUrlOrNull(checksumUrl))
            {
                info.Sha256 = await FetchDigestAsync(checksumUrl).ConfigureAwait(false);
            }
        }

        return info;
    }

    /// <summary>
    /// 直链兜底：完全不碰 api.github.com。releases/latest 会 302 到 releases/tag/&lt;tag&gt;，
    /// 最终 URL 即版本号；资产名与 SHA256 来自同版本的 SHA256SUMS.txt，下载地址直接拼
    /// releases/latest/download/&lt;name&gt;，避免 API 限流导致检查整体不可用。
    /// </summary>
    private async Task<UpdateInfo> FetchLatestFallbackAsync()
    {
        using var page = await SendAsync(_apiClient, ReleasePageUrl(), null,
            HttpCompletionOption.ResponseHeadersRead, CancellationToken.None).ConfigureAwait(false);
        var finalUrl = page.RequestMessage?.RequestUri?.ToString() ?? ReleasePageUrl();
        var tag = TagFromReleaseUrl(finalUrl);
        if (tag is null)
        {
            if (page.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException("GitHub 仓库还没有发布任何版本");
            }

            throw new InvalidOperationException("最新发布没有可识别的版本号");
        }

        var latest = NormalizeVersion(tag);
        var info = new UpdateInfo
        {
            CurrentVersion = CurrentVersion,
            LatestVersion = latest.Length == 0 ? tag : latest,
            HasUpdate = IsNewer(CurrentVersion, latest),
            ReleaseName = tag,
            ReleaseUrl = $"https://github.com/{Repo}/releases/tag/{tag}",
            CheckedAt = NowMs()
        };
        if (!info.HasUpdate)
        {
            return info;
        }

        using var manifest = await SendAsync(_apiClient, LatestAssetUrl() + ChecksumsAsset, null,
            HttpCompletionOption.ResponseContentRead, CancellationToken.None).ConfigureAwait(false);
        if (!manifest.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"读取校验清单失败：HTTP {(int)manifest.StatusCode}");
        }

        var text = await manifest.Content.ReadAsStringAsync().ConfigureAwait(false);
        var (name, sha256) = InstallerFromChecksums(text)
            ?? throw new InvalidOperationException("该版本没有提供更新包校验清单");

        info.AssetName = name;
        info.Sha256 = sha256;
        info.DownloadUrl = LatestAssetUrl() + name;
        info.MirrorUrl = $"https://github.com/{Repo}/releases/download/{tag}/{name}";
        return info;
    }

    // ---- 下载与安装 ----

    /// <summary>下载最后一次检查宣布的更新包，校验后交给外部脚本替换并重启。</summary>
    public async Task InstallLatestAsync(IProgress<UpdateProgress> progress, Action exitAction)
    {
        var info = LastResult() ?? throw new InvalidOperationException("请先检查更新");
        if (!info.HasUpdate)
        {
            throw new InvalidOperationException("当前已是最新版本");
        }

        var expected = RequiredSha256(info);
        var name = info.AssetName ?? throw new InvalidOperationException("该版本没有提供安装包");
        var primary = info.DownloadUrl ?? throw new InvalidOperationException("该版本没有提供安装包");

        var candidates = new List<string> { primary };
        if (!string.IsNullOrWhiteSpace(info.MirrorUrl))
        {
            candidates.Add(info.MirrorUrl!);
        }

        // 直链兜底放在最后：只有主地址与 API 资产地址都失败时才会用到。
        var partner = PartnerDownloadUrl(name);
        if (partner is not null && !candidates.Contains(partner))
        {
            candidates.Add(partner);
        }

        // 不在这里一次性校验全部候选：坏地址交给 DownloadToFileAsync 逐个跳过，
        // 不能让一个非法备用地址把可用的主地址一起拖垮。

        var dir = UpdateDir();
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, SafeFileName(name));
        TryDeleteFile(target);

        var (downloaded, digest) = await DownloadToFileAsync(candidates, target, progress)
            .ConfigureAwait(false);
        if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(target);
            throw new InvalidOperationException("更新包校验失败：SHA256 不匹配");
        }

        progress.Report(new UpdateProgress(downloaded, downloaded));
        await StageAndLaunchAsync(target, exitAction).ConfigureAwait(false);
    }

    private async Task<(long Downloaded, string Sha256)> DownloadToFileAsync(
        IReadOnlyList<string> candidates, string target, IProgress<UpdateProgress> progress)
    {
        var errors = new List<string>();
        foreach (var url in candidates)
        {
            if (!ValidateUrlOrNull(url))
            {
                errors.Add($"{url}（域名不在允许范围内）");
                continue;
            }

            var isAssetApi = url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase)
                && url.Contains("/releases/assets/", StringComparison.Ordinal);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var headerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var response = await SendAsync(_downloadClient, url,
                        isAssetApi ? "application/octet-stream" : null,
                        HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add($"HTTP {(int)response.StatusCode}");
                        break;
                    }

                    var total = response.Content.Headers.ContentLength;
                    if (total > MaxPackageBytes)
                    {
                        throw new InvalidOperationException("更新包体积异常，已停止下载");
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    var result = await StreamToFileAsync(stream, target, total, progress).ConfigureAwait(false);
                    return result;
                }
                catch (Exception error)
                {
                    TryDeleteFile(target);
                    errors.Add(Describe(error));
                    if (attempt == 0 && error is HttpRequestException or IOException or OperationCanceledException)
                    {
                        await Task.Delay(700).ConfigureAwait(false);
                        continue;
                    }

                    break;
                }
            }
        }

        throw new InvalidOperationException($"下载更新失败：{string.Join("；", errors)}");
    }

    private static async Task<(long Downloaded, string Sha256)> StreamToFileAsync(
        Stream stream, string target, long? total, IProgress<UpdateProgress> progress)
    {
        using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, useAsync: true);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        var head = new byte[2];
        var headLength = 0;
        long downloaded = 0;
        long reported = 0;

        while (true)
        {
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("下载中断：读取超时");
            }

            if (read == 0)
            {
                break;
            }

            if (headLength < 2)
            {
                var take = Math.Min(2 - headLength, read);
                Array.Copy(buffer, 0, head, headLength, take);
                headLength += take;
            }

            await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            hasher.AppendData(buffer, 0, read);
            downloaded += read;
            if (downloaded > MaxPackageBytes)
            {
                throw new InvalidOperationException("更新包体积异常，已停止下载");
            }

            if (downloaded - reported >= ProgressStepBytes || (total.HasValue && downloaded == total.Value))
            {
                reported = downloaded;
                progress.Report(new UpdateProgress(downloaded, total));
            }
        }

        await file.FlushAsync().ConfigureAwait(false);
        if (downloaded == 0)
        {
            throw new InvalidOperationException("下载的更新包为空");
        }

        if (total.HasValue && downloaded != total.Value)
        {
            throw new InvalidOperationException($"下载不完整：{downloaded}/{total.Value} 字节");
        }

        // zip 以 "PK" 开头；误下到 HTML 错误页时不会走到解压/替换。
        if (headLength < 2 || head[0] != (byte)'P' || head[1] != (byte)'K')
        {
            throw new InvalidOperationException("下载的文件不是有效的 zip 更新包");
        }

        return (downloaded, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>
    /// 便携版没有安装程序：校验通过后把新版解压到程序目录旁的 .new，交给一个退出后运行的
    /// PowerShell 脚本做目录级原子替换并重启。旧目录先改名保留，替换失败可以回滚。
    /// </summary>
    private async Task StageAndLaunchAsync(string zipPath, Action exitAction)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(appDir);
        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidOperationException("无法定位程序目录");
        }

        var dirName = Path.GetFileName(appDir);
        var staged = Path.Combine(parent, dirName + ".new");
        var backup = appDir + ".old";

        if (Directory.Exists(staged))
        {
            Directory.Delete(staged, recursive: true);
        }

        Directory.CreateDirectory(staged);
        ExtractPackage(zipPath, staged);
        if (!File.Exists(Path.Combine(staged, ExeName)))
        {
            throw new InvalidOperationException($"更新包里缺少 {ExeName}");
        }

        var dir = UpdateDir();
        var script = Path.Combine(dir, "apply-update.ps1");
        File.WriteAllText(script, ApplyScript, new UTF8Encoding(false));

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-AppDir");
        startInfo.ArgumentList.Add(appDir);
        startInfo.ArgumentList.Add("-Staged");
        startInfo.ArgumentList.Add(staged);
        startInfo.ArgumentList.Add("-Backup");
        startInfo.ArgumentList.Add(backup);
        startInfo.ArgumentList.Add("-Exe");
        startInfo.ArgumentList.Add(ExeName);
        startInfo.ArgumentList.Add("-OldPid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("启动更新脚本失败");
        }

        // 脚本在等旧进程退出前不应结束；若 400ms 内就退出，说明解析/启动失败。
        // 此时绝不能让应用也退出，否则托盘程序会「消失」。
        try
        {
            await Task.Delay(400).ConfigureAwait(false);
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "更新脚本启动后立即退出，已取消本次更新（应用保持运行）。请重试或从 GitHub 手动下载。");
            }
        }
        finally
        {
            process.Dispose();
        }

        // 命令先成功返回（UI 才能提示「正在安装」），随后再退出让脚本替换文件。
        _ = Task.Run(async () =>
        {
            await Task.Delay(400).ConfigureAwait(false);
            if (_marshalToUi is not null)
            {
                _marshalToUi(exitAction);
            }
            else
            {
                exitAction();
            }
        });
    }

    private static void ExtractPackage(string zipPath, string staged)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.FullName))
            .ToList();
        // 压缩包可能带一层顶层目录，也可能直接是发布目录内容：有根级文件时不剥离。
        var hasRootFiles = entries.Any(entry => !entry.FullName.Contains('/'));
        var topSegments = entries
            .Select(entry => entry.FullName.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var stripRoot = !hasRootFiles && topSegments.Count == 1;

        foreach (var entry in entries)
        {
            var parts = entry.FullName.Split('/');
            var relative = stripRoot ? string.Join(Path.DirectorySeparatorChar, parts.Skip(1)) : entry.FullName;
            relative = relative.Replace('/', Path.DirectorySeparatorChar);
            if (relative.Length == 0)
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(staged, relative));
            if (!destination.StartsWith(staged + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("更新包包含非法路径");
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>只允许在 github.com 打开发布页，避免 UI 传入任意链接。</summary>
    public static void OpenReleasePage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只允许打开 GitHub 链接");
        }

        using var opened = Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    // ---- HTTP ----

    private static HttpClient CreateClient(TimeSpan timeout, string userAgent, bool decompress)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // 下载二进制资产时不能自动解压：否则 Content-Length 是压缩后的字节数，
            // 而流里读到的是解压后的字节，长度校验会误判、SHA256 也会对不上。
            AutomaticDecompression = decompress
                ? DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
                : DecompressionMethods.None
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>手动跟随重定向，逐跳用域名白名单校验目标，最多 10 跳。</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string url,
        string? accept, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            if (hop > MaxRedirects)
            {
                throw new InvalidOperationException("更新请求重定向次数过多");
            }

            ValidateUrl(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (accept is not null)
            {
                request.Headers.TryAddWithoutValidation("Accept", accept);
            }

            var response = await client.SendAsync(request, completion, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400
                && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(url), location).ToString();
                response.Dispose();
                url = next;
                continue;
            }

            return response;
        }
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException("更新地址必须是 HTTPS 且不含用户信息");
        }

        var host = uri.Host.ToLowerInvariant();
        if (!DownloadHosts.Any(allowed =>
                host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("更新地址不在允许的 GitHub 域名内");
        }
    }

    private static bool ValidateUrlOrNull(string url)
    {
        try
        {
            ValidateUrl(url);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<string?> FetchDigestAsync(string url)
    {
        try
        {
            using var response = await SendAsync(_apiClient, url, null,
                HttpCompletionOption.ResponseContentRead, CancellationToken.None).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseDigest(text);
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(Exception error)
    {
        var message = error.Message;
        for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
        {
            message += $"（{inner.Message}）";
        }

        return message;
    }

    // ---- 解析助手 ----

    private static UpdateInfo ParseRelease(JsonElement root)
    {
        var tag = TextField(root, "tag_name") ?? "";
        var latest = NormalizeVersion(tag);
        var asset = PickInstaller(root);
        return new UpdateInfo
        {
            CurrentVersion = CurrentVersion,
            LatestVersion = latest.Length == 0 ? tag : latest,
            HasUpdate = IsNewer(CurrentVersion, latest),
            ReleaseName = TextField(root, "name") ?? (tag.Length == 0 ? null : tag),
            Notes = TextField(root, "body"),
            PublishedAt = TextField(root, "published_at"),
            ReleaseUrl = TextField(root, "html_url"),
            AssetName = asset?.Name,
            AssetSize = asset?.Size,
            DownloadUrl = asset?.Url,
            MirrorUrl = asset?.Mirror,
            Sha256 = NormalizeDigest(asset?.Digest),
            CheckedAt = NowMs()
        };
    }

    private sealed record ReleaseAsset(string Name, long Size, string Url, string? Mirror, string? Digest);

    private static ReleaseAsset? PickInstaller(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = new List<ReleaseAsset>();
        foreach (var item in assets.EnumerateArray())
        {
            var name = TextField(item, "name");
            var url = TextField(item, "browser_download_url");
            if (name is null || url is null)
            {
                continue;
            }

            candidates.Add(new ReleaseAsset(
                name,
                item.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : 0,
                url,
                TextField(item, "url"),
                TextField(item, "digest")));
        }

        static bool IsZip(ReleaseAsset asset) =>
            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && !asset.Name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase);

        return candidates.FirstOrDefault(asset =>
                   IsZip(asset) && asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(asset =>
                   IsZip(asset) && asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(IsZip);
    }

    private static string? ChecksumAssetUrl(JsonElement root, string assetName)
    {
        var wanted = assetName + ".sha256";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in assets.EnumerateArray())
        {
            var name = TextField(item, "name");
            if (name is not null && string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return TextField(item, "browser_download_url");
            }
        }

        return null;
    }

    private static string? TextField(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item)
        && item.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()!.Trim()
            : null;

    private static string? ParseDigest(string text) => text
        .Split(DigestSeparators, StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token.Length == 64 && token.All(Uri.IsHexDigit))
        .Select(token => token.ToLowerInvariant())
        .FirstOrDefault();

    private static string? NormalizeDigest(string? digest)
    {
        if (digest is null)
        {
            return null;
        }

        var value = digest.Trim();
        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            if (!value[..colon].Equals("sha256", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            value = value[(colon + 1)..].Trim();
        }

        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    /// <summary>从 SHA256SUMS.txt 里找出 zip 资产名与哈希，兼容 "&lt;hash&gt;  name" 和 "&lt;hash&gt; *name"。</summary>
    private static (string Name, string Sha256)? InstallerFromChecksums(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            var split = line.IndexOfAny(new[] { ' ', '\t' });
            if (split <= 0)
            {
                continue;
            }

            var hash = line[..split].Trim();
            var name = line[split..].Trim().TrimStart('*').Trim();
            var validName = name.Length > 0
                && !name.Contains('/') && !name.Contains('\\')
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase);
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit) && validName)
            {
                return (name, hash.ToLowerInvariant());
            }
        }

        return null;
    }

    private static string? TagFromReleaseUrl(string url)
    {
        const string marker = "/releases/tag/";
        var path = url.Split('?', '#')[0];
        var index = path.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var tag = path[(index + marker.Length)..].TrimEnd('/');
        return tag.Length == 0 ? null : Uri.UnescapeDataString(tag);
    }

    private static string NormalizeVersion(string tag) =>
        tag.Trim().TrimStart('v', 'V').Trim();

    /// <summary>解析 1.2.3 / v1.2 / 1.2.3-rc.1；无法识别的版本不触发更新。</summary>
    private static Version? ParseVersion(string raw)
    {
        var cleaned = NormalizeVersion(raw);
        var suffix = cleaned.IndexOfAny(new[] { '-', '+' });
        if (suffix >= 0)
        {
            cleaned = cleaned[..suffix];
        }

        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 3)
        {
            return null;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    private static bool IsNewer(string current, string latest) =>
        ParseVersion(current) is { } currentVersion
        && ParseVersion(latest) is { } latestVersion
        && latestVersion.CompareTo(currentVersion) > 0;

    private static string RequiredSha256(UpdateInfo info) =>
        info.Sha256 is { } value
        && value.Trim().Length == 64
        && value.Trim().All(Uri.IsHexDigit)
            ? value.Trim().ToLowerInvariant()
            : throw new InvalidOperationException(
                "该版本没有提供 SHA256 校验值，已取消安装（无法验证更新包完整性）");

    private static string SafeFileName(string name)
    {
        var cleaned = new string(Path.GetFileName(name)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_')
            .ToArray()).Trim('.').Trim();
        return cleaned.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? cleaned
            : "LocalTrayTranslate-update.zip";
    }

    private static string? PartnerDownloadUrl(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\')
            ? null
            : LatestAssetUrl() + name;

    private static string LatestReleaseApi() => $"https://api.github.com/repos/{Repo}/releases/latest";
    private static string ReleasePageUrl() => $"https://github.com/{Repo}/releases/latest";
    private static string LatestAssetUrl() => $"https://github.com/{Repo}/releases/latest/download/";
    private static string UpdateDir() => Path.Combine(Path.GetTempPath(), "TranslateLite-update");
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件删不掉不影响本次下载（下一次会先覆盖）。
        }
    }

    private static string ReadCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null
            ? "0.0.0"
            : new Version(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0)).ToString(3);
    }

    private const string ApplyScript = @"
param(
    [string]$AppDir,
    [string]$Staged,
    [string]$Backup,
    [string]$Exe,
    [int]$OldPid
)
$ErrorActionPreference = 'Stop'
$renamed = $false
try {
    # Bounded wait so a reused PID cannot hang the updater forever.
    $deadline = (Get-Date).AddSeconds(120)
    while (Get-Process -Id $OldPid -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) { throw ""Old process did not exit within 120 seconds"" }
        Start-Sleep -Milliseconds 300
    }
    Start-Sleep -Milliseconds 600
    if (Test-Path -LiteralPath $Backup) {
        Remove-Item -LiteralPath $Backup -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Staged $Exe))) {
        throw ""Staged folder is missing $Exe""
    }
    Rename-Item -LiteralPath $AppDir -NewName (Split-Path -Path $Backup -Leaf) -ErrorAction Stop
    $renamed = $true
    Rename-Item -LiteralPath $Staged -NewName (Split-Path -Path $AppDir -Leaf) -ErrorAction Stop
    $renamed = $false
    $newProc = Start-Process -FilePath (Join-Path $AppDir $Exe) -PassThru
    Start-Sleep -Seconds 3
    if ($newProc.HasExited) { throw ""New version exited immediately; old version kept at $Backup"" }
    Remove-Item -LiteralPath $Backup -Recurse -Force -ErrorAction SilentlyContinue
} catch {
    if ($renamed -and (Test-Path -LiteralPath $Backup) -and -not (Test-Path -LiteralPath $AppDir)) {
        Rename-Item -LiteralPath $Backup -NewName (Split-Path -Path $AppDir -Leaf) -ErrorAction SilentlyContinue
    }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            ""Update failed.`n`n$($_.Exception.Message)`n`nNew folder: $AppDir`nBackup: $Backup`nStaged: $Staged"",
            'LocalTrayTranslate Update Failed', 0, 48) | Out-Null
    } catch {}
}
";
}
