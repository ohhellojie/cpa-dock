using System.IO.Compression;
using System.Net;
using CPALauncher.Models;
using CPALauncher.Services;
using Xunit;

namespace CPALauncher.Wpf.Tests;

public sealed class CpaUpdateInstallationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cpa-update-" + Guid.NewGuid().ToString("N"));
    public CpaUpdateInstallationTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Install_ExtractsCompletePackage_AndFindsExecutable()
    {
        using var client = CreateClient();
        var result = await new CpaUpdateService(client).InstallLatestAsync(directory, Info);
        Assert.True(result.Success, result.Message);
        Assert.Equal("new binary", File.ReadAllText(result.ExePath!));
        Assert.True(File.Exists(Path.Combine(directory, "config.example.yaml")));
    }

    [Fact]
    public async Task Install_PreservesExistingConfigurationAndCredentials()
    {
        using var client = CreateClient();
        File.WriteAllText(Path.Combine(directory, "config.yaml"), "existing config");
        File.WriteAllText(Path.Combine(directory, "credential.json"), "existing credential");
        var result = await new CpaUpdateService(client).InstallLatestAsync(directory, Info);
        Assert.True(result.Success, result.Message);
        Assert.Equal("existing config", File.ReadAllText(Path.Combine(directory, "config.yaml")));
        Assert.Equal("existing credential", File.ReadAllText(Path.Combine(directory, "credential.json")));
    }

    [Fact]
    public async Task Install_RejectsExistingExecutable_InsteadOfOverwritingIt()
    {
        using var client = CreateClient();
        var path = Path.Combine(directory, "cli-proxy-api.exe");
        File.WriteAllText(path, "existing binary");
        var result = await new CpaUpdateService(client).InstallLatestAsync(directory, Info);
        Assert.False(result.Success);
        Assert.Equal("existing binary", File.ReadAllText(path));
    }

    [Fact]
    public async Task Update_ReplacesOnlyExecutable_PreservesConfig_AndRemovesTemporaryFiles()
    {
        using var client = CreateClient();
        var exe = Path.Combine(directory, "cli-proxy-api.exe");
        var config = Path.Combine(directory, "config.yaml");
        File.WriteAllText(exe, "old binary"); File.WriteAllText(config, "custom config");
        var result = await new CpaUpdateService(client).ApplyUpdateAsync(Info, exe);
        Assert.True(result.Success, result.Message);
        Assert.Equal("new binary", File.ReadAllText(exe));
        Assert.Equal("custom config", File.ReadAllText(config));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public async Task CancelledUpdate_PreservesExistingExecutable()
    {
        using var client = CreateClient();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var exe = Path.Combine(directory, "cli-proxy-api.exe"); File.WriteAllText(exe, "old binary");
        var result = await new CpaUpdateService(client).ApplyUpdateAsync(Info, exe, ct: cancellation.Token);
        Assert.False(result.Success);
        Assert.Equal("old binary", File.ReadAllText(exe));
        Assert.Single(Directory.GetFiles(directory));
    }

    private static CpaUpdateInfo Info => new()
    {
        TagName = "v1.2.3", NewVersion = new Version(1, 2, 3),
        AssetDownloadUrl = "https://example.invalid/fixture.zip", ReleaseUrl = "https://example.invalid/release",
        AssetSize = 0, ExpectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(PackageBytes())),
    };

    private static HttpClient CreateClient() => new(new ZipHandler(PackageBytes()));

    private static byte[] PackageBytes()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(Entry(zip, "cli-proxy-api.exe").Open())) writer.Write("new binary");
            using (var writer = new StreamWriter(Entry(zip, "config.example.yaml").Open())) writer.Write("sample");
            using (var writer = new StreamWriter(Entry(zip, "config.yaml").Open())) writer.Write("package config");
            using (var writer = new StreamWriter(Entry(zip, "credential.json").Open())) writer.Write("package credential");
        }
        return bytes.ToArray();
    }

    private static ZipArchiveEntry Entry(ZipArchive zip, string name) { var entry = zip.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero); return entry; }

    private sealed class ZipHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
