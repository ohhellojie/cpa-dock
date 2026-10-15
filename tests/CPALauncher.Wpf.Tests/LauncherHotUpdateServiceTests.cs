using System.IO.Compression;
using System.Net;
using CPALauncher.Models;
using CPALauncher.Services;
using Xunit;

namespace CPALauncher.Wpf.Tests;

public sealed class LauncherHotUpdateServiceTests
{
    [Theory]
    [InlineData("CPALauncher.exe", "CPALauncher.exe")]
    [InlineData("CPA Dock.exe", "CPA Dock.exe")]
    [InlineData("CPALauncher.exe", "CPA Dock.exe")]
    [InlineData("CPA Dock.exe", "CPALauncher.exe")]
    public async Task PrepareHotUpdateAsync_WhenPackageContainsLauncherExe_StagesPackageAndScript(string installedName, string packageName)
    {
        var tempRoot = CreateTempRoot();
        string? stagingRoot = null;
        try
        {
            var installDirectory = Path.Combine(tempRoot, "install");
            Directory.CreateDirectory(installDirectory);

            var currentExecutablePath = Path.Combine(installDirectory, installedName);
            await File.WriteAllTextAsync(currentExecutablePath, "old launcher");

            var packageBytes = CreateZip((packageName, "new launcher"), ("README.txt", "notes"));
            using var httpClient = new HttpClient(new BinaryStubHttpMessageHandler(packageBytes));
            var service = new LauncherHotUpdateService(httpClient);
            var progress = new CaptureProgress();

            var result = await service.PrepareHotUpdateAsync(
                CreateUpdateInfo(packageBytes),
                currentExecutablePath,
                progress);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Package);
            stagingRoot = Path.GetDirectoryName(result.Package.ApplyScriptPath);
            Assert.Equal("v0.3.0", result.Package.VersionTag);
            Assert.Equal(installDirectory, result.Package.InstallDirectory);
            Assert.Equal(Path.GetFullPath(currentExecutablePath), result.Package.LauncherExecutablePath);
            Assert.True(File.Exists(Path.Combine(result.Package.PackageDirectory, packageName)));
            Assert.True(File.Exists(result.Package.ApplyScriptPath));

            Assert.Contains((packageBytes.Length, packageBytes.Length), progress.Reports);
        }
        finally
        {
            if (stagingRoot is not null) TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task PrepareHotUpdateAsync_WhenPackageDoesNotContainLauncherExe_ReturnsFailure()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var installDirectory = Path.Combine(tempRoot, "install");
            Directory.CreateDirectory(installDirectory);

            var currentExecutablePath = Path.Combine(installDirectory, "CPALauncher.exe");
            await File.WriteAllTextAsync(currentExecutablePath, "old launcher");

            var packageBytes = CreateZip(("README.txt", "notes"));
            using var httpClient = new HttpClient(new BinaryStubHttpMessageHandler(packageBytes));
            var service = new LauncherHotUpdateService(httpClient);

            var result = await service.PrepareHotUpdateAsync(
                CreateUpdateInfo(packageBytes),
                currentExecutablePath);

            Assert.False(result.Success);
            Assert.Null(result.Package);
            Assert.Contains("CPALauncher.exe", result.Message);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static CpaUpdateInfo CreateUpdateInfo(byte[] package)
    {
        return new CpaUpdateInfo
        {
            TagName = "v0.3.0",
            NewVersion = new Version(0, 3, 0),
            AssetDownloadUrl = "https://example.com/downloads/CPALauncher-win-x64-self-contained.zip",
            AssetSize = package.Length,
            ExpectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package)),
            ReleaseUrl = "https://example.com/releases/v0.3.0",
        };
    }

    private static byte[] CreateZip(params (string path, string content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "cpa-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    private sealed class CaptureProgress : IProgress<(long downloaded, long total)>
    {
        public List<(long downloaded, long total)> Reports { get; } = [];

        public void Report((long downloaded, long total) value)
        {
            Reports.Add(value);
        }
    }

    private sealed class BinaryStubHttpMessageHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
            response.Content.Headers.ContentLength = content.Length;
            return Task.FromResult(response);
        }
    }
}
