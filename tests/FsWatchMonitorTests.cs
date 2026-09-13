namespace TicTack;

public class FsWatchMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tictack-fsw-" + Guid.NewGuid().ToString("N"));
    private readonly FsWatchMonitor _monitor;
    private readonly object _gate = new();
    private readonly List<FileChangedEventArgs> _events = new();

    public FsWatchMonitorTests()
    {
        Directory.CreateDirectory(_dir);
        _monitor = new FsWatchMonitor(_dir);
        _monitor.Changed += (s, e) => { lock (_gate) _events.Add(e); };
        _monitor.Start();
    }

    public void Dispose()
    {
        _monitor.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private FileChangedEventArgs WaitEvent(ChangeType type, Func<FileChangedEventArgs, bool>? match = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                var found = _events.FirstOrDefault(e => e.ChangeType == type && (match == null || match(e)));
                if (found != null) return found;
            }
            Thread.Sleep(50);
        }
        throw new TimeoutException("No " + type + " event observed");
    }

    [Fact]
    public void FiresCreatedAndDeleted()
    {
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "x");
        WaitEvent(ChangeType.Created, e => e.FullPath == file);

        File.Delete(file);
        WaitEvent(ChangeType.Deleted, e => e.FullPath == file);
    }

    [Fact]
    public void FiresModifiedOnWrite()
    {
        var file = Path.Combine(_dir, "b.txt");
        File.WriteAllText(file, "x");
        WaitEvent(ChangeType.Created, e => e.FullPath == file);

        File.WriteAllText(file, "yy");
        WaitEvent(ChangeType.Modified, e => e.FullPath == file);
    }

    [Fact]
    public void FiresRenamedWithOldFullPath()
    {
        var oldPath = Path.Combine(_dir, "old.txt");
        var newPath = Path.Combine(_dir, "new.txt");
        File.WriteAllText(oldPath, "x");
        WaitEvent(ChangeType.Created, e => e.FullPath == oldPath);

        File.Move(oldPath, newPath);

        var renamed = WaitEvent(ChangeType.Renamed, e => e.FullPath == newPath);
        Assert.Equal(oldPath, renamed.OldFullPath);
    }

    [Fact]
    public void RescanEmitsModifiedForExistingFiles()
    {
        var file = Path.Combine(_dir, "rescan.txt");
        File.WriteAllText(file, "x");
        _monitor.RescanNow();

        WaitEvent(ChangeType.Modified, e => e.FullPath == file);
    }
}
