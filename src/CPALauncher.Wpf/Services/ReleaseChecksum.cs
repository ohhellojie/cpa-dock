using System.Net.Http;
using System.Text.Json;

namespace CPALauncher.Services;

internal static class ReleaseChecksum
{
    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static async Task<string> ResolveAsync(HttpClient client, JsonElement asset,
        JsonElement assets, CancellationToken cancellationToken)
    {
        if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(digest.GetString()))
        {
            var value = digest.GetString()!;
            if (!value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !IsSha256(value[7..]))
                throw new InvalidDataException("发布信息中的 SHA256 无效，已停止更新。");
            return value[7..];
        }

        // Older releases and mirrors may not expose GitHub's asset digest.
        var manifests = assets.EnumerateArray().Where(item => item.TryGetProperty("name", out var name)
            && name.GetString() is { } text && (text.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)
                || text.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (manifests.Length != 1 || !manifests[0].TryGetProperty("browser_download_url", out var url)
            || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("更新包缺少有效的 SHA256，已停止更新。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("User-Agent", "CPALauncher");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[64 * 1024 + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
            if (count == 0) break;
            length += count;
        }
        if (length == buffer.Length) throw new InvalidDataException("更新校验文件过大，已停止更新。");
        var content = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        var assetName = asset.GetProperty("name").GetString();
        var matches = new List<string>();
        foreach (var line in content.Split('\n'))
        {
            var entry = line.Trim().TrimStart('\ufeff');
            if (entry.Length < 66 || !IsSha256(entry[..64]) || !char.IsWhiteSpace(entry[64])) continue;
            var name = entry[64..].TrimStart().TrimStart('*');
            if (name.Equals(assetName, StringComparison.Ordinal)) matches.Add(entry[..64]);
        }
        if (matches.Count != 1) throw new InvalidDataException("校验文件中没有唯一匹配的更新包 SHA256，已停止更新。");
        return matches[0];
    }
}
