using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using CPALauncher.Models;

namespace CPALauncher.Services;

public sealed class CpaUpdateService
{
    private readonly HttpClient _httpClient;

    public CpaUpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CpaUpdateCheckResult> CheckForUpdateAsync(
        string repo,
        string? currentVersion,
        CancellationToken ct = default,
        bool requireWindowsAsset = true,
        string productName = "CPA",
        Func<string, bool>? assetNamePredicate = null,
        string assetDescription = "Windows amd64 安装包")
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{repo}/releases/latest");
            request.Headers.Add("User-Agent", "CPALauncher");
            request.Headers.Add("Accept", "application/vnd.github+json");

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return CpaUpdateCheckResult.CheckFailed(
                    $"获取最新发布信息失败（HTTP {(int)response.StatusCode} {response.ReasonPhrase ?? "Unknown"}）。");
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagNameElement))
                return CpaUpdateCheckResult.CheckFailed("最新发布信息缺少 tag_name，无法判断版本。");

            var tagName = tagNameElement.GetString();
            if (string.IsNullOrEmpty(tagName))
                return CpaUpdateCheckResult.CheckFailed("最新发布信息中的 tag_name 为空，无法判断版本。");

            var versionStr = tagName.TrimStart('v');
            if (!Version.TryParse(versionStr, out var newVersion))
                return CpaUpdateCheckResult.CheckFailed($"最新发布版本号格式无效：{tagName}。");

            if (!string.IsNullOrWhiteSpace(currentVersion))
            {
                var curVersionStr = currentVersion.TrimStart('v');
                if (!Version.TryParse(curVersionStr, out var curVersion))
                    return CpaUpdateCheckResult.CheckFailed($"当前 {productName} 版本号格式无效：{currentVersion}。");

                if (newVersion <= curVersion)
                    return CpaUpdateCheckResult.UpToDate(tagName, newVersion);
            }

            var releaseUrl = root.TryGetProperty("html_url", out var releaseUrlElement)
                ? releaseUrlElement.GetString() ?? string.Empty
                : string.Empty;

            var assetUrl = releaseUrl;
            long assetSize = 0;
            string? expectedSha256 = null;

            if (requireWindowsAsset)
            {
                assetUrl = null;
                assetNamePredicate ??= IsDefaultWindowsAsset;

                if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
                    return CpaUpdateCheckResult.CheckFailed($"最新发布信息缺少 assets 列表，无法定位 {assetDescription}。");

                foreach (var asset in assetsElement.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var nameElement))
                        continue;

                    var name = nameElement.GetString();
                    if (name is not null && assetNamePredicate(name))
                    {
                        assetUrl = asset.TryGetProperty("browser_download_url", out var assetUrlElement)
                            ? assetUrlElement.GetString()
                            : null;
                        assetSize = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var size)
                            ? size
                            : 0;
                        expectedSha256 = await ReleaseChecksum.ResolveAsync(_httpClient, asset, assetsElement, ct);
                        break;
                    }
                }

                if (string.IsNullOrEmpty(assetUrl))
                    return CpaUpdateCheckResult.CheckFailed($"最新发布中未找到可用的 {assetDescription}。");
            }

            return CpaUpdateCheckResult.UpdateAvailable(new CpaUpdateInfo
            {
                TagName = tagName,
                NewVersion = newVersion,
                AssetDownloadUrl = assetUrl,
                AssetSize = assetSize,
                ExpectedSha256 = expectedSha256,
                ReleaseUrl = releaseUrl,
            });
        }
        catch (InvalidDataException ex)
        {
            return CpaUpdateCheckResult.CheckFailed(ex.Message);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return CpaUpdateCheckResult.CheckFailed("检查更新时发生异常，无法获取最新发布信息。");
        }
    }

    public async Task<(bool Success, string Message, string? ExePath)> InstallLatestAsync(
        string targetDirectory, CpaUpdateInfo info,
        IProgress<(long downloaded, long total)>? progress = null, CancellationToken ct = default)
    {
        var target = Path.Combine(Path.GetFullPath(targetDirectory), "cli-proxy-api.exe");
        if (File.Exists(target)) return (false, "目录中已有 CPA，请使用更新功能。", null);
        var prepared = await PrepareUpdateAsync(info, target, progress: progress, ct: ct);
        if (prepared.Transaction is not { } transaction) return (false, prepared.Message, null);
        var example = Path.Combine(targetDirectory, "config.example.yaml");
        var addedExample = false;
        try
        {
            // Only the executable and optional example configuration belong to installation.
            using (var zip = ZipFile.OpenRead(Path.Combine(transaction.DirectoryPath, "package.zip")))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name == "config.example.yaml");
                if (entry is not null && !File.Exists(example))
                {
                    entry.ExtractToFile(example);
                    addedExample = true;
                }
            }
            ct.ThrowIfCancellationRequested();
            transaction.Apply();
            transaction.Commit();
            return (true, $"CPA {info.TagName} 安装成功。", target);
        }
        catch (Exception ex)
        {
            try
            {
                transaction.Rollback();
                transaction.FinishRollback();
                if (addedExample) TryDeleteFile(example);
            }
            catch (Exception recovery) { return (false, $"安装失败：{ex.Message}。恢复未完成：{recovery.Message}", null); }
            return (false, $"安装失败：{ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message, CpaUpdateTransaction? Transaction)> PrepareUpdateAsync(
        CpaUpdateInfo info, string targetExePath, string? previousVersion = null, bool wasRunning = false,
        IProgress<(long downloaded, long total)>? progress = null, CancellationToken ct = default)
    {
        CpaUpdateTransaction? transaction = null;
        try
        {
            transaction = CpaUpdateTransaction.Create(targetExePath, previousVersion, info.TagName, wasRunning);
            var zipPath = Path.Combine(transaction.DirectoryPath, "package.zip");
            await UpdatePackageDownloader.DownloadAsync(_httpClient, info, zipPath, progress, ct);
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.SingleOrDefault(e => e.Name.Equals("cli-proxy-api.exe", StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length == 0) throw new InvalidDataException("更新包中未找到有效的 cli-proxy-api.exe。");
            await Task.Run(() => entry.ExtractToFile(transaction.StagedPath), ct);
            ct.ThrowIfCancellationRequested();
            transaction.Prepare();
            return (true, "更新包已准备完成。", transaction);
        }
        catch (Exception ex)
        {
            transaction?.DiscardPreparation();
            return (false, ex is OperationCanceledException ? "更新下载已取消或超时。" : $"更新准备失败：{ex.Message}", null);
        }
    }

    // File-only entry point. The desktop flow retains the transaction until its health check completes.
    public async Task<(bool Success, string Message)> ApplyUpdateAsync(
        CpaUpdateInfo info, string targetExePath, IProgress<(long downloaded, long total)>? progress = null, CancellationToken ct = default)
    {
        var preparation = await PrepareUpdateAsync(info, targetExePath, progress: progress, ct: ct);
        if (preparation.Transaction is not { } transaction) return (false, preparation.Message);
        try
        {
            ct.ThrowIfCancellationRequested();
            transaction.Apply();
            transaction.Commit();
            return (true, $"CPA 已更新到 {info.TagName}。");
        }
        catch (Exception ex)
        {
            try { transaction.Rollback(); transaction.FinishRollback(); }
            catch (Exception recovery) { return (false, $"更新失败：{ex.Message}。恢复失败：{recovery.Message}。备份：{transaction.DirectoryPath}"); }
            return (false, $"更新失败：{ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static bool IsDefaultWindowsAsset(string name)
    {
        return name.Contains("windows", StringComparison.OrdinalIgnoreCase)
               && name.Contains("amd64", StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }
}
