using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using CPALauncher.Models;
using CPALauncher.Services;
using Xunit;

namespace CPALauncher.Wpf.Tests;

public sealed class CpaLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CPA 生命周期 " + Guid.NewGuid().ToString("N"));
    private readonly CpaProcessManager manager = new();
    public CpaLifecycleTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ActualService_UpdateValidatesNewProcess_OrRestoresOldProcess(bool running, bool healthy)
    {
        var exe = Path.Combine(root, "cli-proxy-api.exe");
        var replacement = Path.Combine(root, "replacement.exe");
        Compile(exe, "old", false); Compile(replacement, "new", !healthy);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var config = Path.Combine(root, "config.yaml");
        File.WriteAllText(config, port.ToString());
        var credential = Path.Combine(root, "credential.json");
        File.WriteAllText(credential, "fixture credential");
        var store = new LauncherSettingsStore(Path.Combine(root, "settings"));
        var settings = new LauncherSettings { ExecutablePath = exe, ConfigPath = config, LastInstalledCpaVersion = "v1" };
        store.Save(settings);
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        async Task<bool> Ready()
        {
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && manager.IsManagedProcessRunning)
            {
                try { if ((await http.GetAsync($"http://127.0.0.1:{port}/")).IsSuccessStatusCode) return true; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(50);
            }
            return false;
        }
        async Task Stop() { var result = await manager.StopAsync(); Assert.True(result.Success, result.Message); }
        if (running) { Assert.True((await manager.StartAsync(exe, config)).Success); Assert.True(await Ready()); }
        var originalPid = manager.ManagedProcessId;
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) zip.CreateEntryFromFile(replacement, "cli-proxy-api.exe");
        using var download = new HttpClient(new Handler(bytes.ToArray()));
        var info = new CpaUpdateInfo { TagName = "v2", NewVersion = new Version(2, 0), AssetDownloadUrl = "https://example.invalid/package.zip", ReleaseUrl = "https://example.invalid/release", ExpectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())), AssetSize = bytes.Length };
        var prepared = await new CpaUpdateService(download).PrepareUpdateAsync(info, exe, "v1", running);
        Assert.True(prepared.Success, prepared.Message);
        Assert.Equal(originalPid, manager.ManagedProcessId); // Preparation never stops the original service.
        var transaction = prepared.Transaction!;
        var apply = CpaUpdateCoordinator.ApplyAsync(transaction, Stop,
            async isNew =>
            {
                var start = await manager.StartAsync(exe, config);
                if (!start.Success) throw new IOException(start.Message);
                if (isNew) transaction.RecordProcess(manager.ManagedProcessId ?? throw new IOException("New process exited."));
            }, Ready, version => { settings.LastInstalledCpaVersion = version; store.Save(settings); });
        if (healthy) await apply; else await Assert.ThrowsAsync<IOException>(() => apply);
        Assert.Equal(running, manager.IsManagedProcessRunning);
        Assert.Equal(healthy ? "v2" : "v1", store.Load().LastInstalledCpaVersion);
        if (running) Assert.Equal(healthy ? "new" : "old", await http.GetStringAsync($"http://127.0.0.1:{port}/"));
        Assert.Equal(port.ToString(), File.ReadAllText(config));
        Assert.Equal("fixture credential", File.ReadAllText(credential));
        Assert.False(Directory.Exists(transaction.DirectoryPath));
        await Stop();
    }

    private void Compile(string path, string version, bool crash)
    {
        var source = path + ".cs";
        File.WriteAllText(source, """
            using System; using System.IO; using System.Net; using System.Net.Sockets; using System.Text;
            class Fixture { static int Main(string[] args) {
            """ + (crash ? "return 7;" : """
            var port=int.Parse(File.ReadAllText(args[1])); var server=new TcpListener(IPAddress.Loopback,port); server.Start();
            while(true) { using(var client=server.AcceptTcpClient()) using(var stream=client.GetStream()) {
            var input=new byte[4096]; stream.Read(input,0,input.Length);
            """ + $"var body=\"{version}\";" + """
            var output=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n"+body); stream.Write(output,0,output.Length);
            }}
            """) + "} }");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET/Framework64/v4.0.30319/csc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/nologo", "/target:exe", "/out:" + path, source }) start.ArgumentList.Add(arg);
        using var compiler = Process.Start(start)!;
        var output = compiler.StandardOutput.ReadToEnd() + compiler.StandardError.ReadToEnd(); compiler.WaitForExit();
        Assert.True(compiler.ExitCode == 0, output);
    }

    private sealed class Handler(byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
    }
    public void Dispose()
    {
        var stopped = manager.StopAsync().GetAwaiter().GetResult();
        Assert.True(stopped.Success, stopped.Message);
        Assert.False(manager.IsManagedProcessRunning);
        manager.Dispose();
        // Windows can briefly retain the executable image after process exit.
        // A persistent lock still fails the test instead of silently leaking its directory.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(root, true); return; }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
            { Thread.Sleep(100); }
        }
    }
}
