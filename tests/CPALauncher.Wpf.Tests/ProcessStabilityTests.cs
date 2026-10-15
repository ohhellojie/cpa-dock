using System.Diagnostics;
using CPALauncher.Services;
using Xunit;

namespace CPALauncher.Wpf.Tests;

public sealed class ProcessStabilityTests
{
    [Fact]
    public async Task RepeatedStartStop_WithOutputFlood_KeepsLogsBoundedAndReleasesChildren()
    {
        var root = Path.Combine(Path.GetTempPath(), "cpa-stability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var manager = new CpaProcessManager();
        try
        {
            var source = Path.Combine(root, "probe.cs");
            var exe = Path.Combine(root, "probe.exe");
            var config = Path.Combine(root, "config.yaml");
            File.WriteAllText(config, "isolated fixture");
            File.WriteAllText(source, "using System; class Probe { static void Main() { for(int i=0;i<10000;i++) Console.WriteLine(\"burst \"+i); Console.WriteLine(\"READY\"); System.Threading.Thread.Sleep(-1); } }");
            var compile = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET/Framework64/v4.0.30319/csc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "/nologo", "/out:" + exe, source }) compile.ArgumentList.Add(arg);
            using (var compiler = Process.Start(compile)!)
            {
                var output = compiler.StandardOutput.ReadToEndAsync();
                await compiler.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(compiler.ExitCode == 0, await output);
            }
            for (var cycle = 0; cycle < 20; cycle++)
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<string> observe = (_, text) => { if (text.EndsWith("READY")) ready.TrySetResult(); };
                manager.OutputReceived += observe;
                try
                {
                    var start = await manager.StartAsync(exe, config);
                    Assert.True(start.Success, start.Message);
                    using var child = Process.GetProcessById(manager.ManagedProcessId!.Value);
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.InRange(manager.GetRecentOutput().Count, 1, 400);
                    var stop = await manager.StopAsync();
                    Assert.True(stop.Success, stop.Message);
                    Assert.False(manager.IsManagedProcessRunning);
                    Assert.Null(manager.ManagedProcessId);
                    Assert.True(child.HasExited);
                    Assert.True((await manager.StopAsync()).Success);
                }
                finally { manager.OutputReceived -= observe; }
            }
            Assert.Equal("isolated fixture", File.ReadAllText(config));
        }
        finally
        {
            await manager.StopAsync();
            manager.Dispose();
            // Windows can retain the executable mapping briefly after process exit.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
                { await Task.Delay(100); }
            }
        }
    }
}
