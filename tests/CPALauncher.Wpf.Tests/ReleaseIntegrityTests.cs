using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CPALauncher.Models;
using CPALauncher.Services;
using Xunit;

namespace CPALauncher.Wpf.Tests;

public sealed class ReleaseIntegrityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cpa-integrity-" + Guid.NewGuid().ToString("N"));
    public ReleaseIntegrityTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("cpa", "match")]
    [InlineData("launcher", "match")]
    [InlineData("cpa", "mismatch")]
    [InlineData("launcher", "mismatch")]
    [InlineData("cpa", "missing")]
    [InlineData("launcher", "missing")]
    [InlineData("cpa", "invalid")]
    [InlineData("launcher", "invalid")]
    public async Task PackageIntegrity_IsRequiredBeforeEitherProductCanReplaceFiles(string product, string integrity)
    {
        var name = product == "cpa" ? "cli-proxy-api.exe" : "CPA Dock.exe";
        var target = Path.Combine(root, name);
        File.WriteAllText(target, "original");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("replacement");
        var bytes = stream.ToArray();
        var requests = 0;
        using var client = new HttpClient(new Handler(_ => { requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }));
        var info = new CpaUpdateInfo
        {
            TagName = "v9.0.0", NewVersion = new(9, 0), AssetDownloadUrl = "https://example.invalid/package.zip",
            AssetSize = bytes.Length, ReleaseUrl = "https://example.invalid/release",
            ExpectedSha256 = integrity switch { "match" => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                "mismatch" => new string('0', 64), "invalid" => "not-a-hash", _ => null },
        };
        bool success; string message;
        if (product == "cpa")
        {
            var result = await new CpaUpdateService(client).ApplyUpdateAsync(info, target);
            (success, message) = result;
        }
        else
        {
            var result = await new LauncherHotUpdateService(client).PrepareHotUpdateAsync(info, target);
            success = result.Success; message = result.Message;
            if (result.Package is { } package) Directory.Delete(Path.GetDirectoryName(package.ApplyScriptPath)!, true);
        }
        Assert.Equal(integrity == "match", success);
        Assert.Equal(success && product == "cpa" ? "replacement" : "original", File.ReadAllText(target));
        Assert.Equal(integrity is "missing" or "invalid" ? 0 : 1, requests);
        if (!success) Assert.Contains("SHA256", message);
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Theory]
    [InlineData("digest", true)]
    [InlineData("uppercase", true)]
    [InlineData("manifest", true)]
    [InlineData("star", true)]
    [InlineData("bad-digest", false)]
    [InlineData("missing", false)]
    [InlineData("wrong-name", false)]
    [InlineData("duplicate", false)]
    [InlineData("oversized", false)]
    [InlineData("http", false)]
    public async Task ReleaseMetadata_RequiresAnUnambiguousPublishedHash(string scenario, bool expected)
    {
        var hash = new string('a', 64);
        var manifest = scenario switch
        {
            "star" => $"\ufeff{hash} *windows-amd64.zip\r\n",
            "wrong-name" => $"{hash}  another.zip",
            "duplicate" => $"{hash}  windows-amd64.zip\n{hash}  windows-amd64.zip",
            "oversized" => new string(' ', 65537),
            _ => $"{hash}  windows-amd64.zip\n",
        };
        var assets = new List<object> { new { name = "windows-amd64.zip", size = 100,
            browser_download_url = "https://example.invalid/package.zip",
            digest = scenario switch { "digest" => "sha256:" + hash, "uppercase" => "SHA256:" + hash.ToUpperInvariant(), "bad-digest" => "sha256:xyz", _ => (string?)null } } };
        if (scenario != "missing") assets.Add(new { name = "SHA256SUMS.txt", browser_download_url = "https://example.invalid/checksums" });
        var json = JsonSerializer.Serialize(new { tag_name = "v1.2.3", html_url = "https://example.invalid/release", assets });
        using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
            ? new(HttpStatusCode.OK) { Content = new StringContent(json) }
            : new(scenario == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(manifest, Encoding.UTF8) }));
        var result = await new CpaUpdateService(client).CheckForUpdateAsync("owner/repo", "v1.0.0");
        Assert.Equal(expected ? CpaUpdateCheckStatus.UpdateAvailable : CpaUpdateCheckStatus.CheckFailed, result.Status);
        if (expected) Assert.Equal(hash, result.UpdateInfo!.ExpectedSha256!.ToLowerInvariant());
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
    public void Dispose() => Directory.Delete(root, true);
}
