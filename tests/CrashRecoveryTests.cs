using System.Diagnostics;

namespace TicTack;

[Collection("SerialWatcher")]
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
            var sentinel = Path.Combine(root, "checkpoint.reached");
            File.WriteAllText(sourceFile, new string('x', 4096));

            using var process = StartWorker("copy", sourceFile, destinationFile, "DataFlushed", sentinel);
            await WaitForFileAsync(sentinel, TimeSpan.FromSeconds(10));
            process.Kill();
            await WaitForExitAsync(process, TimeSpan.FromSeconds(10));

            Assert.True(process.HasExited);
            Assert.False(File.Exists(destinationFile));
            Assert.True(File.Exists(destinationFile + ".tictack.tmp"));

            var cfg = new TicTackConfig();
            cfg.Sources.Add(new SourceConfig { Path = source, Destination = destination });
            var log = new RecordingLogger();
            PowerGuard.Cleanup(cfg, log);

            // Pin the recovery path, not just the end state: a validated temp
            // that is deleted instead of moved still leaves no file, and the
            // logged line says which happened.
            Assert.Contains(log.Messages, m => m.Contains("from validated temp"));
            Assert.Equal(File.ReadAllText(sourceFile), File.ReadAllText(destinationFile));
            Assert.False(File.Exists(destinationFile + ".tictack.tmp"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    [Trait("Category", "CrashRecovery")]
    public async Task KillBeforeCheckpoint_WalReplaysOnReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "tictack-wal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "state.db");
            var sentinel = Path.Combine(root, "writes.reached");
            using var process = StartWorker("statedb", dbPath, sentinel);
            await WaitForFileAsync(sentinel, TimeSpan.FromSeconds(15));

            // Committed but uncheckpointed: the worker's connection never
            // closed, so the -wal file must hold frames (header alone is 32
            // bytes). A clean close would checkpoint and remove it, so this
            // asserts the crash path, not just durability.
            var wal = new FileInfo(dbPath + "-wal");
            Assert.True(wal.Exists && wal.Length > 32,
                "expected uncheckpointed WAL frames, wal length=" + (wal.Exists ? wal.Length : -1));

            process.Kill();
            await WaitForExitAsync(process, TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);

            using var db = new StateDb(dbPath);
            var all = db.LoadAll();
            Assert.Equal(2, all.Count);
            Assert.Equal((100L, 1000L), all["crash-a.txt"]);
            Assert.Equal((200L, 2000L), all["crash-b.txt"]);
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
