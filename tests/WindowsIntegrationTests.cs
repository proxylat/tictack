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

    [Fact]
    [Trait("Category", "Windows")]
    public void FileWatcherMonitor_ReportsRenameWithOldPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "tictack-win-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var monitor = new FileWatcherMonitor(root, 32, 1);
        var created = new ManualResetEventSlim();
        var renamed = new ManualResetEventSlim();
        string? oldPath = null;
        monitor.Changed += (_, e) =>
        {
            if (e.ChangeType == ChangeType.Created && e.FullPath.EndsWith("old.txt", StringComparison.OrdinalIgnoreCase))
                created.Set();
            if (e.ChangeType == ChangeType.Renamed && e.FullPath.EndsWith("new.txt", StringComparison.OrdinalIgnoreCase))
            {
                oldPath = e.OldFullPath;
                renamed.Set();
            }
        };
        try
        {
            monitor.Start();
            var oldFile = Path.Combine(root, "old.txt");
            File.WriteAllText(oldFile, "rename");
            Assert.True(created.Wait(TimeSpan.FromSeconds(10)), "watcher never saw the create");

            File.Move(oldFile, Path.Combine(root, "new.txt"));

            Assert.True(renamed.Wait(TimeSpan.FromSeconds(10)), "watcher never paired the rename");
            Assert.EndsWith("old.txt", oldPath);
        }
        finally
        {
            monitor.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task LongPath_CopyAction_RoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "tictack-win-long-" + Guid.NewGuid().ToString("N"));
        var srcRoot = Path.Combine(root, "src");
        var dstRoot = Path.Combine(root, "dst");
        var segment = new string('a', 40);
        var relDir = string.Join(Path.DirectorySeparatorChar.ToString(), Enumerable.Repeat(segment, 8));
        var srcDir = Path.Combine(srcRoot, relDir);
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstRoot);
        try
        {
            var src = Path.Combine(srcDir, "long.txt");
            File.WriteAllText(src, "long-path");
            Assert.True(src.Length > 260, "test path is not long enough: " + src.Length);

            var args = new FileActionArgs(new FileChangedEventArgs(ChangeType.Created, src), srcRoot, dstRoot);
            var result = await new CopyAction(new FileAccessor()).ExecuteAsync(args, CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("long-path", File.ReadAllText(args.DestPath));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void VolumeResolver_ResolvesRealDriveLabel()
    {
        if (!OperatingSystem.IsWindows()) return;

        var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && !string.IsNullOrEmpty(d.VolumeLabel));
        if (drive == null) return; // no labelled volume on this machine; nothing to prove

        var root = drive.RootDirectory.FullName.TrimEnd('\\');
        var resolved = VolumeResolver.Resolve("[" + drive.VolumeLabel + "]\\probe\\file.txt");

        Assert.Equal(root + "\\probe\\file.txt", resolved);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ExternalDrives_DefaultCommand_IsWindowsShape()
    {
        if (!OperatingSystem.IsWindows()) return;

        var command = new ExternalDrivesConfig().Command;

        Assert.Contains("restic.exe", command);
        Assert.Contains("{drive}\\restic-repo", command);
    }
}
