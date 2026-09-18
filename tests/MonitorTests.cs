namespace TicTack;

public class MonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tictack-mon-" + Guid.NewGuid().ToString("N"));

    public MonitorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void PollingMonitor_DetectsCreatedModifiedDeleted()
    {
        using var monitor = new PollingMonitor(_dir);
        var events = new List<FileChangedEventArgs>();
        monitor.Changed += (_, e) => events.Add(e);

        monitor.PollNow();
        Assert.Empty(events);

        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "x");
        monitor.PollNow();
        var created = Assert.Single(events);
        Assert.Equal(ChangeType.Created, created.ChangeType);
        Assert.Equal(file, created.FullPath);

        events.Clear();
        File.WriteAllText(file, "yy longer");
        monitor.PollNow();
        var modified = Assert.Single(events);
        Assert.Equal(ChangeType.Modified, modified.ChangeType);
        Assert.Equal(file, modified.FullPath);

        events.Clear();
        File.Delete(file);
        monitor.PollNow();
        var deleted = Assert.Single(events);
        Assert.Equal(ChangeType.Deleted, deleted.ChangeType);
        Assert.Equal(file, deleted.FullPath);
    }

    [Fact]
    public void PollingMonitor_SilentWhenNothingChanges()
    {
        File.WriteAllText(Path.Combine(_dir, "steady.txt"), "x");
        using var monitor = new PollingMonitor(_dir);
        var events = new List<FileChangedEventArgs>();
        monitor.Changed += (_, e) => events.Add(e);

        monitor.PollNow();
        monitor.PollNow();

        Assert.Empty(events);
    }

    [Fact]
    public void PollingMonitor_ThrowingChangedSubscriber_IsContained()
    {
        using var monitor = new PollingMonitor(_dir);
        var errors = new List<MonitorErrorEventArgs>();
        monitor.Changed += (_, _) => throw new InvalidOperationException("bad handler");
        monitor.Error += (_, e) => errors.Add(e);

        monitor.PollNow(); // empty baseline
        File.WriteAllText(Path.Combine(_dir, "boom1.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "boom2.txt"), "x");
        monitor.PollNow();

        // Containment must let the second event reach the handler too; an
        // unguarded subscriber would abort the scan after the first throw.
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal("bad handler", e.Exception.Message));
    }

    [Fact]
    public void CompositeMonitor_FansOutChangedAndError()
    {
        var a = new EventMonitor();
        var b = new EventMonitor();
        using var composite = new CompositeMonitor(a, b);
        var changed = new List<FileChangedEventArgs>();
        var errors = new List<MonitorErrorEventArgs>();
        composite.Changed += (_, e) => changed.Add(e);
        composite.Error += (_, e) => errors.Add(e);

        composite.Start();
        Assert.True(a.Started);
        Assert.True(b.Started);

        a.Fire(ChangeType.Created, Path.Combine(_dir, "f.txt"));
        Assert.Single(changed);

        b.FireError(new IOException("boom"));
        var err = Assert.Single(errors);
        Assert.Equal("boom", err.Exception.Message);

        composite.Stop();
        Assert.False(a.Started);
        Assert.False(b.Started);
    }

    [Fact]
    public void FileWatcherMonitor_RescanNow_ReportsExistingFiles()
    {
        var file = Path.Combine(_dir, "existing.txt");
        File.WriteAllText(file, "x");
        using var monitor = new FileWatcherMonitor(_dir);
        FileChangedEventArgs? seen = null;
        monitor.Changed += (_, e) =>
        {
            if (e.ChangeType == ChangeType.Modified && e.FullPath == file) seen = e;
        };

        monitor.RescanNow();

        Assert.NotNull(seen);
    }

    [Fact]
    public void FileWatcherMonitor_Start_ReturnsWhenTheDirectoryCannotBeOpened()
    {
        // The worker signals "armed" on its failure paths too, so Start() is
        // bounded: missing directory on Windows (CreateFile fails) and on
        // Linux (kernel32 P/Invoke throws) both return promptly.
        using var monitor = new FileWatcherMonitor(Path.Combine(_dir, "missing"));
        var start = DateTime.UtcNow;

        monitor.Start();

        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(9),
            "Start blocked waiting for a watcher that never armed");
    }
}
