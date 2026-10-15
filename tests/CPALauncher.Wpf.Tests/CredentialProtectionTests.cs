using CPALauncher.Services;
using Xunit;

namespace CPALauncher.Wpf.Tests;

public sealed class CredentialProtectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cpa-credentials-" + Guid.NewGuid().ToString("N"));
    private string Auth => Path.Combine(root, "auth");
    private string Target => Path.Combine(Auth, "token.json");
    private string Source => Path.Combine(root, "token.json");
    public CredentialProtectionTests() { Directory.CreateDirectory(Auth); File.WriteAllText(Target, "{\"token\":\"old\"}"); }

    [Theory]
    [InlineData("broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{} trailing")]
    [InlineData("")]
    public void InvalidCredential_PreservesOriginalAndBackup(string content)
    {
        File.WriteAllText(Source, content);
        File.WriteAllText(Target + ".bak", "earlier backup");
        var result = new TokenImportService().ImportJsonFiles(Auth, [Source]);
        Assert.Equal(TokenImportStatus.Failed, result.Status);
        Assert.Equal("{\"token\":\"old\"}", File.ReadAllText(Target));
        Assert.Equal("earlier backup", File.ReadAllText(Target + ".bak"));
        Assert.Empty(result.OverwrittenFiles);
    }

    [Fact]
    public void SuccessfulOverwrite_PreservesExactBytesAndPreviousVersionOnly()
    {
        var original = File.ReadAllBytes(Target);
        var bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(System.Text.Encoding.UTF8.GetBytes("{\"token\":\"新凭证\"}\r\n")).ToArray();
        File.WriteAllBytes(Source, bytes);
        var service = new TokenImportService();
        Assert.Equal(TokenImportStatus.Succeeded, service.ImportJsonFiles(Auth, [Source]).Status);
        Assert.Equal(bytes, File.ReadAllBytes(Target)); Assert.Equal(original, File.ReadAllBytes(Target + ".bak"));
        File.WriteAllText(Source, "{\"token\":\"third\"}");
        Assert.Equal(TokenImportStatus.Succeeded, service.ImportJsonFiles(Auth, [Source]).Status);
        Assert.Equal(bytes, File.ReadAllBytes(Target + ".bak"));
        Assert.Single(Directory.GetFiles(Auth, "*.json")); // Backups must not be loaded as active credentials.
        Assert.Equal(2, Directory.GetFiles(Auth).Length);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("backup")]
    [InlineData("concurrent")]
    public void LockedFileOrConcurrentWriter_DoesNotTruncateOriginal(string scenario)
    {
        File.WriteAllText(Source, "{\"token\":\"new\"}");
        File.WriteAllText(Target + ".bak", "earlier backup");
        var lockedPath = scenario switch { "backup" => Target + ".bak", "concurrent" => Target + ".import-lock", _ => Target };
        using (var locked = new FileStream(lockedPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            var result = new TokenImportService().ImportJsonFiles(Auth, [Source]);
            Assert.Equal(TokenImportStatus.Failed, result.Status);
            Assert.Empty(Directory.GetFiles(Auth, "*.tmp-*"));
        }
        Assert.Equal("{\"token\":\"old\"}", File.ReadAllText(Target));
        Assert.Equal(TokenImportStatus.Succeeded, new TokenImportService().ImportJsonFiles(Auth, [Source]).Status);
    }

    public void Dispose() => Directory.Delete(root, true);
}
