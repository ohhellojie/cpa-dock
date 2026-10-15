using System.Net.Http;
using System.Security.Cryptography;
using CPALauncher.Models;

namespace CPALauncher.Services;

internal static class UpdatePackageDownloader
{
    internal static async Task DownloadAsync(HttpClient client, CpaUpdateInfo info, string destination,
        IProgress<(long downloaded, long total)>? progress, CancellationToken ct)
    {
        if (!ReleaseChecksum.IsSha256(info.ExpectedSha256))
            throw new InvalidDataException("更新包缺少有效的 SHA256，已停止更新。");
        // ResponseHeadersRead only applies HttpClient.Timeout through receipt of the headers.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, info.AssetDownloadUrl);
        request.Headers.Add("User-Agent", "CPALauncher");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var expectedLength = response.Content.Headers.ContentLength;
        var total = expectedLength ?? info.AssetSize;
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        long downloaded = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            hash.AppendData(buffer, 0, count);
            downloaded += count;
            progress?.Report((downloaded, total));
        }
        token.ThrowIfCancellationRequested();
        if ((expectedLength.HasValue && downloaded != expectedLength.Value) ||
            (info.AssetSize > 0 && downloaded != info.AssetSize))
            throw new InvalidDataException("更新包下载不完整，请重试。");
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(info.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包 SHA256 校验失败，已停止更新。请重新下载。");
    }
}
