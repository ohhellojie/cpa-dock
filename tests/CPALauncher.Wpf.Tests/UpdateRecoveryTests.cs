using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using CPALauncher.Models;
using CPALauncher.Services;
using Xunit;

namespace CPALauncher.Wpf.Tests;

public sealed class UpdateRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CPA 恢复测试 " + Guid.NewGuid().ToString("N"));
    private string Install => Path.Combine(root, "安装目录");
    private string Package => Path.Combine(root, "package");
    private string Launcher => Path.Combine(Install, "CPA Dock.exe");

    public UpdateRecoveryTests()
    {
        Directory.CreateDirectory(Install);
        Directory.CreateDirectory(Package);
        File.WriteAllText(Path.Combine(Install, "config.yaml"), "user config");
        File.WriteAllText(Path.Combine(Install, "credential.json"), "user credential");
    }

    [Fact]
    public async Task CpaUpdate_LockedExecutable_PreservesOldFilesAndCleansStaging()
    {
        var exe = Path.Combine(Install, "cli-proxy-api.exe");
        File.WriteAllText(exe, "old");
        using var locked = File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var client = Client(Zip(("cli-proxy-api.exe", "new")));
        var result = await new CpaUpdateService(client).ApplyUpdateAsync(Info(Zip((Path.GetFileName(exe), "new"))), exe);
        Assert.False(result.Success);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.Equal(3, Directory.GetFiles(Install).Length);
        AssertUserFiles();
    }

    [Fact]
    public async Task LauncherApply_PartialCopyFailure_RestoresAlreadyChangedFiles()
    {
        File.WriteAllText(Launcher, "old launcher");
        File.WriteAllText(Path.Combine(Install, "README.txt"), "old readme");
        File.WriteAllText(Path.Combine(Package, "CPA Dock.exe"), "new launcher");
        File.WriteAllText(Path.Combine(Package, "README.txt"), "new readme");
        File.WriteAllText(Path.Combine(Package, "added.txt"), "new file");
        using var locked = File.Open(Launcher, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await RunUpdater();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("old launcher", File.ReadAllText(Launcher));
        Assert.Equal("old readme", File.ReadAllText(Path.Combine(Install, "README.txt")));
        Assert.False(File.Exists(Path.Combine(Install, "added.txt")));
        AssertUserFiles();
    }

    [Theory]
    [InlineData("cpa")]
    [InlineData("launcher")]
    public async Task Download_LengthMismatch_IsRejectedBeforeReplacement(string product)
    {
        var exe = product == "cpa" ? Path.Combine(Install, "cli-proxy-api.exe") : Launcher;
        File.WriteAllText(exe, "old");
        var zip = Zip((Path.GetFileName(exe), "new"));
        using var client = Client(zip, zip.Length + 50);
        var success = product == "cpa"
            ? (await new CpaUpdateService(client).ApplyUpdateAsync(Info(Zip((Path.GetFileName(exe), "new"))), exe)).Success
            : (await new LauncherHotUpdateService(client).PrepareHotUpdateAsync(Info(Zip((Path.GetFileName(exe), "new"))), exe)).Success;
        Assert.False(success);
        Assert.Equal("old", File.ReadAllText(exe));
        AssertUserFiles();
    }

    [Theory]
    [InlineData("http", "cpa")]
    [InlineData("http", "launcher")]
    [InlineData("disconnect", "cpa")]
    [InlineData("disconnect", "launcher")]
    [InlineData("corrupt", "cpa")]
    [InlineData("corrupt", "launcher")]
    [InlineData("missing", "cpa")]
    [InlineData("missing", "launcher")]
    [InlineData("cancel", "cpa")]
    [InlineData("cancel", "launcher")]
    public async Task FailedDownload_LeavesInstalledFilesUsable_AndRetrySucceeds(string fault, string product)
    {
        var exe = product == "cpa" ? Path.Combine(Install, "cli-proxy-api.exe") : Launcher;
        File.WriteAllText(exe, "old");
        using var client = new HttpClient(new Handler(_ => fault switch
        {
            "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "disconnect" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream()) },
            "corrupt" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 1, 2, 3]) },
            "missing" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(("README.txt", "notes"))) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip((Path.GetFileName(exe), "new"))) }
        }));
        using var cancellation = new CancellationTokenSource();
        var faultBytes = fault == "corrupt" ? new byte[] { 0, 1, 2, 3 } : fault == "missing" ? Zip(("README.txt", "notes")) : Zip((Path.GetFileName(exe), "new"));
        var progress = new InlineProgress(_ => { if (fault == "cancel") cancellation.Cancel(); });
        var success = product == "cpa"
            ? (await new CpaUpdateService(client).ApplyUpdateAsync(Info(faultBytes), exe, progress, cancellation.Token)).Success
            : (await new LauncherHotUpdateService(client).PrepareHotUpdateAsync(Info(faultBytes), exe, progress, cancellation.Token)).Success;
        Assert.False(success);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.Equal(3, Directory.GetFiles(Install).Length);
        AssertUserFiles();

        using var retry = Client(Zip((Path.GetFileName(exe), "new")));
        if (product == "cpa")
        {
            var result = await new CpaUpdateService(retry).ApplyUpdateAsync(Info(Zip((Path.GetFileName(exe), "new"))), exe);
            Assert.True(result.Success, result.Message);
            Assert.Equal("new", File.ReadAllText(exe));
        }
        else
        {
            var result = await new LauncherHotUpdateService(retry).PrepareHotUpdateAsync(Info(Zip((Path.GetFileName(exe), "new"))), exe);
            Assert.True(result.Success, result.Message);
            Directory.Delete(Path.GetDirectoryName(result.Package!.ApplyScriptPath)!, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LauncherApply_StartsNewVersion_OrRestoresOldVersionAfterEarlyExit(bool crash)
    {
        CompileProbe(Launcher, "old", false);
        CompileProbe(Path.Combine(Package, "CPALauncher.exe"), "new", crash);
        File.WriteAllText(Path.Combine(Install, "README.txt"), "old readme");
        File.WriteAllText(Path.Combine(Package, "README.txt"), "new readme");
        Directory.CreateDirectory(Path.Combine(Package, "extra"));
        File.WriteAllText(Path.Combine(Package, "extra", "added.txt"), "new file");
        File.WriteAllText(Path.Combine(Package, "config.yaml"), "package must not overwrite config");
        try
        {
            var result = await RunUpdater();
            Assert.True(crash ? result.ExitCode != 0 : result.ExitCode == 0, result.Output);
            Assert.Equal(crash ? "old" : "new", File.ReadAllText(Path.Combine(Install, "started.txt")));
            Assert.Equal(crash ? "old readme" : "new readme", File.ReadAllText(Path.Combine(Install, "README.txt")));
            Assert.False(File.Exists(Path.Combine(Install, "extra", "added.txt")));
            Assert.Empty(Directory.GetDirectories(Install, ".cpa-update-*"));
            AssertUserFiles();
        }
        finally { StopProbe(); }
    }

    [Fact]
    public async Task LauncherApply_WriteDenied_LeavesOriginalFilesAndRestartsOldVersion()
    {
        CompileProbe(Launcher, "old", false);
        CompileProbe(Path.Combine(Package, "CPA Dock.exe"), "new", false);
        var originalAcl = new DirectoryInfo(Install).GetAccessControl();
        var denied = new DirectoryInfo(Install).GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateDirectories, AccessControlType.Deny));
        try
        {
            new DirectoryInfo(Install).SetAccessControl(denied);
            var result = await RunUpdater();
            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("old", File.ReadAllText(Path.Combine(Install, "started.txt")));
            AssertUserFiles();
        }
        finally { new DirectoryInfo(Install).SetAccessControl(originalAcl); StopProbe(); }
    }

    [Fact]
    public async Task LauncherApply_OldProcessStillRunning_ChangesNothing()
    {
        CompileProbe(Launcher, "old", false);
        CompileProbe(Path.Combine(Package, "CPA Dock.exe"), "new", false);
        using var old = Process.Start(new ProcessStartInfo(Launcher) { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var result = await RunUpdater(old.Id);
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(old.HasExited);
            Assert.Equal("old", File.ReadAllText(Path.Combine(Install, "started.txt")));
            Assert.Empty(Directory.GetDirectories(Install, ".cpa-update-*"));
            AssertUserFiles();
        }
        finally { if (!old.HasExited) old.Kill(); old.WaitForExit(); }
    }

    private void CompileProbe(string destination, string version, bool crash)
    {
        var source = Path.Combine(root, version + ".cs");
        File.WriteAllText(source, "using System; using System.IO; using System.Threading; class Probe { static int Main(string[] args) { var d=AppDomain.CurrentDomain.BaseDirectory; File.WriteAllText(Path.Combine(d,\"started.txt\"),\"" + version + "\"); File.WriteAllText(Path.Combine(d,\"probe.pid\"),System.Diagnostics.Process.GetCurrentProcess().Id.ToString()); " + (crash ? "Thread.Sleep(1500); return 7;" : "if(args.Length == 2 && args[0] == \"--update-apply\") File.WriteAllText(Path.Combine(args[1],\"ready\"),System.Diagnostics.Process.GetCurrentProcess().Id.ToString()); Thread.Sleep(60000); return 0;") + " } }");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/nologo", "/target:winexe", "/out:" + destination, source }) start.ArgumentList.Add(arg);
        using var compiler = Process.Start(start)!;
        var output = compiler.StandardOutput.ReadToEnd() + compiler.StandardError.ReadToEnd();
        compiler.WaitForExit();
        Assert.True(compiler.ExitCode == 0, output);
    }

    private void StopProbe()
    {
        var pidFile = Path.Combine(Install, "probe.pid");
        if (!File.Exists(pidFile)) return;
        try
        {
            using var process = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
            if (string.Equals(process.MainModule!.FileName, Launcher, StringComparison.OrdinalIgnoreCase))
            { process.Kill(); process.WaitForExit(); }
        }
        catch (ArgumentException) { }
    }

    [Fact]
    public async Task LauncherApply_RestoreBlocked_RetainsBackupAndReportsItsLocation()
    {
        CompileProbe(Launcher, "old", false);
        CompileProbe(Path.Combine(Package, "CPA Dock.exe"), "new", true);
        var readme = Path.Combine(Install, "README.txt");
        File.WriteAllText(readme, "old readme");
        File.WriteAllText(Path.Combine(Package, "README.txt"), "new readme");
        var update = RunUpdater();
        var marker = Path.Combine(Install, "started.txt");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker)) await Task.Delay(10, deadline.Token);
        using (File.Open(readme, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await update;
            Assert.NotEqual(0, result.ExitCode);
            var backup = Assert.Single(Directory.GetDirectories(Install, ".cpa-update-*"));
            Assert.Contains(Directory.GetFiles(backup), p => Path.GetFileName(p).StartsWith("old-") && File.ReadAllText(p) == "old readme");
            Assert.Contains("Recovery files retained at " + backup, result.Output);
            AssertUserFiles();
        }
    }

    private sealed class InlineProgress(Action<(long, long)> action) : IProgress<(long, long)>
    { public void Report((long, long) value) => action(value); }
    private sealed class InterruptedStream : MemoryStream
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) throw new IOException("Connection reset during transfer");
            read = true; buffer.Span[0] = 80; return ValueTask.FromResult(1);
        }
    }

    private void AssertUserFiles()
    {
        Assert.Equal("user config", File.ReadAllText(Path.Combine(Install, "config.yaml")));
        Assert.Equal("user credential", File.ReadAllText(Path.Combine(Install, "credential.json")));
    }

    [Theory]
    [InlineData("cpa")]
    [InlineData("launcher")]
    public async Task Update_ReadOnlyDirectory_FailsBeforeReplacingFiles(string product)
    {
        var exe = product == "cpa" ? Path.Combine(Install, "cli-proxy-api.exe") : Launcher;
        File.WriteAllText(exe, "old");
        var original = new DirectoryInfo(Install).GetAccessControl();
        var denied = new DirectoryInfo(Install).GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles, AccessControlType.Deny));
        using var client = Client(Zip((Path.GetFileName(exe), "new")));
        try
        {
            new DirectoryInfo(Install).SetAccessControl(denied);
            var success = product == "cpa"
                ? (await new CpaUpdateService(client).ApplyUpdateAsync(Info(Zip((Path.GetFileName(exe), "new"))), exe)).Success
                : (await new LauncherHotUpdateService(client).PrepareHotUpdateAsync(Info(Zip((Path.GetFileName(exe), "new"))), exe)).Success;
            Assert.False(success);
            Assert.Equal("old", File.ReadAllText(exe));
            Assert.Equal(3, Directory.GetFiles(Install).Length);
            AssertUserFiles();
        }
        finally { new DirectoryInfo(Install).SetAccessControl(original); }
    }

    [Fact]
    public async Task LauncherApply_KilledHelper_ReplaysJournalAndRestoresOldVersion()
    {
        CompileProbe(Launcher, "old", false);
        CompileProbe(Path.Combine(Package, "CPA Dock.exe"), "new", false);
        File.WriteAllText(Path.Combine(Install, "README.txt"), "old readme");
        File.WriteAllText(Path.Combine(Package, "README.txt"), "new readme");
        Process? helper = null;
        var update = RunUpdater(onStarted: process => helper = process);
        string? recovery = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                recovery = Directory.GetDirectories(Install, ".cpa-update-*").FirstOrDefault();
                var path = recovery is null ? "" : Path.Combine(recovery, "state.json");
                if (File.Exists(path))
                {
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var state = System.Text.Json.JsonDocument.Parse(stream);
                        if (state.RootElement.GetProperty("ProcessId").GetInt32() > 0) break;
                    }
                    catch (IOException) { } // The journal can be atomically replaced while observed.
                }
                await Task.Delay(10, deadline.Token);
            }
        }
        finally
        {
            if (helper is { HasExited: false }) helper.Kill(); // Recovery must identify its child.
            await update;
        }
        Assert.True(File.Exists(Path.Combine(recovery!, "恢复旧版.cmd")));
        Assert.Equal("new readme", File.ReadAllText(Path.Combine(Install, "README.txt")));
        var result = await RunUpdater(recoveryDirectory: recovery);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Install, "started.txt")));
        Assert.Equal("old readme", File.ReadAllText(Path.Combine(Install, "README.txt")));
        Assert.Empty(Directory.GetDirectories(Install, ".cpa-update-*"));
        AssertUserFiles();
    }

    private async Task<(int ExitCode, string Output)> RunUpdater(int processId = 2147483647,
        string? recoveryDirectory = null, Action<Process>? onStarted = null)
    {
        var script = Path.Combine(root, "apply.ps1");
        var content = (string)typeof(LauncherHotUpdateService).GetMethod("BuildApplyScriptContent", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
        await File.WriteAllTextAsync(script, content, new System.Text.UTF8Encoding(true));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-ProcessId", processId.ToString(), "-PackageDirectory", Package, "-InstallDirectory", Install, "-LauncherPath", Launcher, "-WaitSeconds", "1" }) start.ArgumentList.Add(arg);
        if (recoveryDirectory is not null) { start.ArgumentList.Add("-RecoverDirectory"); start.ArgumentList.Add(recoveryDirectory); }
        using var process = Process.Start(start)!;
        onStarted?.Invoke(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var log = Path.Combine(root, "apply-launcher-update.log");
        return (process.ExitCode, await stdout + await stderr + (File.Exists(log) ? File.ReadAllText(log) : ""));
    }

    private static CpaUpdateInfo Info(byte[] bytes) => new() { TagName = "v9.0.0", NewVersion = new Version(9, 0, 0), AssetDownloadUrl = "https://example.invalid/update.zip", ReleaseUrl = "https://example.invalid/release", ExpectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), AssetSize = 0 };
    private static HttpClient Client(byte[] bytes, long? length = null) => new(new Handler(_ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentLength = length ?? bytes.Length;
        return response;
    }));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries) { var entry = archive.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero); using var writer = new StreamWriter(entry.Open()); writer.Write(text); }
        return stream.ToArray();
    }
    public void Dispose() { StopProbe(); Directory.Delete(root, true); }
}
