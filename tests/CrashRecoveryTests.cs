using System.Diagnostics;

namespace TicTack;

public class CrashRecoveryTests
{
    [Fact]
    [Trait("Category", "CrashRecovery")]
    public async Task TerminationAfterFlush_IsRecoveredByPowerGuard()
    {
        var root = Path.Combine(Path.GetTempPath(), "tictack-crash-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            var sourceFile = Path.Combine(source, "file.txt");
            var destinationFile = Path.Combine(destination, "file.txt");
            File.WriteAllText(sourceFile, new string('x', 4096));

            using var process = StartWorker("copy", sourceFile, destinationFile, "DataFlushed");
            await WaitForExitAsync(process, TimeSpan.FromSeconds(10));

            Assert.NotEqual(0, process.ExitCode);
            Assert.False(File.Exists(destinationFile));
            Assert.True(File.Exists(destinationFile + ".tictack.tmp"));

            var cfg = new TicTackConfig();
            cfg.Sources.Add(new SourceConfig { Path = source, Destination = destination });
            PowerGuard.Cleanup(cfg, new RecordingLogger());

            Assert.Equal(File.ReadAllText(sourceFile), File.ReadAllText(destinationFile));
            Assert.False(File.Exists(destinationFile + ".tictack.tmp"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    [Trait("Category", "LockContention")]
    public async Task LockHolder_BlocksSecondProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "tictack-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, ".tictack.lock");
        try
        {
            using var holder = StartWorker("lock", lockPath, "3000");
            await WaitForFileAsync(lockPath, TimeSpan.FromSeconds(5));

            using var contender = new SrcLock(lockPath, new RecordingLogger(), TimeSpan.Zero);
            Assert.False(contender.IsHeld);

            await WaitForExitAsync(holder, TimeSpan.FromSeconds(10));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static Process StartWorker(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var worker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../CrashWorker/bin/" + configuration + "/net10.0/CrashWorker.dll"));
        Assert.True(File.Exists(worker), "CrashWorker was not built: " + worker);
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = host,
            WorkingDirectory = Path.GetDirectoryName(worker)!,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add(worker);
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        var process = Process.Start(startInfo);
        Assert.NotNull(process);
        foreach (var arg in args) process!.StartInfo.ArgumentList.Add(arg);
        return process!;
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        await process.WaitForExitAsync().WaitAsync(timeout);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(File.Exists(path), "Timed out waiting for " + path);
    }
}
