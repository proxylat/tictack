namespace TicTack;

// Native-watcher and child-process tests share kernel/process resources and
// contend badly on small CI runners: run them serially, never in parallel.
[CollectionDefinition("SerialWatcher", DisableParallelization = true)]
public class SerialWatcherCollection { }

[Collection("SerialWatcher")]
public class WindowsIntegrationTests
{
    [Fact]
    [Trait("Category", "Windows")]
    public void FileWatcherMonitor_ReportsNativeFileChanges()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "tictack-win-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var monitor = new FileWatcherMonitor(root, 32, 1);
        var changed = new ManualResetEventSlim();
        monitor.Changed += (_, e) =>
        {
            if (e.ChangeType == ChangeType.Created && e.FullPath.EndsWith("native.txt", StringComparison.OrdinalIgnoreCase))
                changed.Set();
        };
        try
        {
            monitor.Start();
            File.WriteAllText(Path.Combine(root, "native.txt"), "native");
            Assert.True(changed.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            monitor.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void FileAccessor_ReadsFileHeldOpenWithSharing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), "tictack-win-lock-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "readable");
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            using var read = new FileAccessor().OpenRead(path);
            Assert.Equal('r', (char)read.ReadByte());
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void FileWatcherMonitor_RescanReportsExistingFiles()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "tictack-win-rescan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var monitor = new FileWatcherMonitor(root);
        var changed = new ManualResetEventSlim();
        monitor.Changed += (_, e) =>
        {
            if (e.ChangeType == ChangeType.Modified && e.FullPath.EndsWith("existing.txt", StringComparison.OrdinalIgnoreCase))
                changed.Set();
        };
        try
        {
            File.WriteAllText(Path.Combine(root, "existing.txt"), "existing");
            monitor.RescanNow();
            Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
