using System.Net;
using System.Net.Http;
using System.Text;
using CPALauncher.Models;
using CPALauncher.Services;
using Xunit;

namespace CPALauncher.Wpf.Tests;

public sealed class CpaUpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdateAsync_WhenNewerReleaseExists_ReturnsUpdateAvailable()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {
              "tag_name": "v1.2.4",
              "html_url": "https://example.com/releases/v1.2.4",
              "assets": [
                {
                  "name": "cli-proxy-api-windows-amd64.zip",
                  "browser_download_url": "https://example.com/downloads/v1.2.4.zip",
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 1048576
                }
              ]
            }
            """);
        var service = new CpaUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync("router-for-me/CLIProxyAPI", "v1.2.3");

        Assert.Equal(CpaUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.UpdateInfo);
        Assert.Equal("v1.2.4", result.UpdateInfo.TagName);
        Assert.Equal(new Version(1, 2, 4), result.UpdateInfo.NewVersion);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenCurrentVersionIsLatest_ReturnsUpToDate()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {
              "tag_name": "v1.2.3",
              "html_url": "https://example.com/releases/v1.2.3",
              "assets": [
                {
                  "name": "cli-proxy-api-windows-amd64.zip",
                  "browser_download_url": "https://example.com/downloads/v1.2.3.zip",
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 2048
                }
              ]
            }
            """);
        var service = new CpaUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync("router-for-me/CLIProxyAPI", "v1.2.3");

        Assert.Equal(CpaUpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.UpdateInfo);
        Assert.Equal("v1.2.3", result.LatestTagName);
        Assert.Equal(new Version(1, 2, 3), result.LatestVersion);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenAssetIsNotRequired_ReturnsReleaseUrl()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {
              "tag_name": "v0.3.0",
              "html_url": "https://example.com/releases/v0.3.0",
              "assets": []
            }
            """);
        var service = new CpaUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(
            "elenaaasky/CPALauncher",
            "v0.2.5",
            requireWindowsAsset: false,
            productName: "CPALauncher");

        Assert.Equal(CpaUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.UpdateInfo);
        Assert.Equal("https://example.com/releases/v0.3.0", result.UpdateInfo.ReleaseUrl);
        Assert.Equal("https://example.com/releases/v0.3.0", result.UpdateInfo.AssetDownloadUrl);
        Assert.Equal(0, result.UpdateInfo.AssetSize);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WithCustomAssetMatcher_ReturnsMatchingAsset()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {
              "tag_name": "v0.3.0",
              "html_url": "https://example.com/releases/v0.3.0",
              "assets": [
                {
                  "name": "CPALauncher-win-x64-framework-dependent.zip",
                  "browser_download_url": "https://example.com/downloads/framework.zip",
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 1024
                },
                {
                  "name": "CPALauncher-win-x64-self-contained.zip",
                  "browser_download_url": "https://example.com/downloads/self-contained.zip",
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 4096
                }
              ]
            }
            """);
        var service = new CpaUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(
            "elenaaasky/CPALauncher",
            "v0.2.6",
            requireWindowsAsset: true,
            productName: "CPALauncher",
            assetNamePredicate: name =>
                name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                && name.Contains("self-contained", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
            assetDescription: "CPALauncher win-x64 self-contained 更新包");

        Assert.Equal(CpaUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.UpdateInfo);
        Assert.Equal("https://example.com/downloads/self-contained.zip", result.UpdateInfo.AssetDownloadUrl);
        Assert.Equal(4096, result.UpdateInfo.AssetSize);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenReleaseRequestFails_ReturnsCheckFailed()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.Forbidden, """
            {
              "message": "API rate limit exceeded"
            }
            """);
        var service = new CpaUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync("router-for-me/CLIProxyAPI", "v1.2.3");

        Assert.Equal(CpaUpdateCheckStatus.CheckFailed, result.Status);
        Assert.Null(result.UpdateInfo);
        Assert.Null(result.LatestTagName);
        Assert.Null(result.LatestVersion);
        Assert.Contains("403", result.FailureReason);
    }

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, string json)
        => new(new StubHttpMessageHandler(statusCode, json));

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
