namespace TicTack;

public class UsnMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tictack-usn-" + Guid.NewGuid().ToString("N"));

    public UsnMonitorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // Reason bits mirror the monitor's internal constants.
    private const uint FileCreate = 0x00000100;
    private const uint FileDelete = 0x00000200;
    private const uint RenameOld = 0x00001000;
    private const uint RenameNew = 0x00002000;
    private const uint DataOverwrite = 0x00000001;
    private const uint Close = 0x80000000;

    private static byte[] BuildRecord(ulong fileRef, ulong parentRef, long usn,
        uint reason, uint attrs, string name)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        var buf = new byte[52 + nameBytes.Length];
        BitConverter.GetBytes((uint)buf.Length).CopyTo(buf, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(buf, 4);
        BitConverter.GetBytes(fileRef).CopyTo(buf, 8);
        BitConverter.GetBytes(parentRef).CopyTo(buf, 16);
        BitConverter.GetBytes(usn).CopyTo(buf, 24);
        BitConverter.GetBytes(reason).CopyTo(buf, 32);
        BitConverter.GetBytes(attrs).CopyTo(buf, 44);
        BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(buf, 48);
        BitConverter.GetBytes((ushort)52).CopyTo(buf, 50);
        nameBytes.CopyTo(buf, 52);
        return buf;
    }

    [Fact]
    public void TryParseRecord_ParsesV2Fields()
    {
        var buf = BuildRecord(0x1234, 0x5678, 99, FileCreate | Close, 0x80, "a.txt");

        Assert.True(UsnJournalMonitor.TryParseRecord(buf, 0, out var rec));
        Assert.NotNull(rec);
        Assert.Equal(0x1234UL, rec!.FileRef);
        Assert.Equal(0x5678UL, rec.ParentRef);
        Assert.Equal(99L, rec.Usn);
        Assert.Equal(FileCreate | Close, rec.Reason);
        Assert.Equal("a.txt", rec.FileName);
    }

    [Fact]
    public void TryParseRecord_RejectsShortBufferAndBadVersion()
    {
        Assert.False(UsnJournalMonitor.TryParseRecord(new byte[10], 0, out _));

        var buf = BuildRecord(1, 2, 3, FileCreate, 0, "a.txt");
        BitConverter.GetBytes((ushort)1).CopyTo(buf, 4); // major version 1
        Assert.False(UsnJournalMonitor.TryParseRecord(buf, 0, out _));
    }

    private RecordsForTest NewMonitor() => new(new UsnJournalMonitor(_dir));

    [Fact]
    public void Translate_CreateModifyDelete()
    {
        using var helper = NewMonitor();
        string Resolver(ulong fr) => Path.Combine(_dir, "f.txt");

        var created = helper.Translate(0x1, 0x0, 1, FileCreate | Close, 0x80, "f.txt", Resolver);
        Assert.NotNull(created);
        Assert.Equal(ChangeType.Created, created!.ChangeType);
        Assert.Equal(Path.Combine(_dir, "f.txt"), created.FullPath);

        var modified = helper.Translate(0x1, 0x0, 2, DataOverwrite | Close, 0x80, "f.txt", Resolver);
        Assert.NotNull(modified);
        Assert.Equal(ChangeType.Modified, modified!.ChangeType);

        // Deleted files cannot be opened by id: resolver fails for the file
        // ref, parent resolves, name comes from the record.
        string? DelResolver(ulong fr) => fr == 0x0 ? _dir : null;
        var deleted = helper.Translate(0x1, 0x0, 3, FileDelete | Close, 0x80, "f.txt", DelResolver);
        Assert.NotNull(deleted);
        Assert.Equal(ChangeType.Deleted, deleted!.ChangeType);
        Assert.Equal(Path.Combine(_dir, "f.txt"), deleted.FullPath);
    }

    [Fact]
    public void Translate_RenamePairsOldAndNew()
    {
        using var helper = NewMonitor();
        // Same FRN resolves to the old path before the rename and the new
        // path after — the resolver sees the live filesystem.
        var calls = 0;
        string Resolver(ulong fr) => ++calls == 1
            ? Path.Combine(_dir, "old.txt")
            : Path.Combine(_dir, "new.txt");

        var old = helper.Translate(0x1, 0x0, 1, RenameOld, 0x80, "old.txt", Resolver);
        Assert.Null(old); // old-name alone emits nothing

        var renamed = helper.Translate(0x1, 0x0, 2, RenameNew | Close, 0x80, "new.txt", Resolver);
        Assert.NotNull(renamed);
        Assert.Equal(ChangeType.Renamed, renamed!.ChangeType);
        Assert.Equal(Path.Combine(_dir, "new.txt"), renamed.FullPath);
        Assert.Equal(Path.Combine(_dir, "old.txt"), renamed.OldFullPath);
    }

    [Fact]
    public void Translate_RenameWithoutOldName_BecomesCreated()
    {
        using var helper = NewMonitor();
        string Resolver(ulong fr) => Path.Combine(_dir, "new.txt");

        var evt = helper.Translate(0x9, 0x0, 1, RenameNew | Close, 0x80, "new.txt", Resolver);
        Assert.NotNull(evt);
        Assert.Equal(ChangeType.Created, evt!.ChangeType);
    }

    [Fact]
    public void Translate_OutsideTree_IsFiltered()
    {
        using var helper = NewMonitor();
        string Resolver(ulong fr) => Path.Combine(Path.GetTempPath(), "elsewhere.txt");

        var evt = helper.Translate(0x1, 0x0, 1, FileCreate | Close, 0x80, "elsewhere.txt", Resolver);
        Assert.Null(evt);
    }

    [Fact]
    public void Translate_DirectoryModifiedSkipped_CloseOnlySkipped()
    {
        using var helper = NewMonitor();
        string Resolver(ulong fr) => Path.Combine(_dir, "sub");

        const uint dirAttr = 0x00000010;
        Assert.Null(helper.Translate(0x1, 0x0, 1, DataOverwrite | Close, dirAttr, "sub", Resolver));
        Assert.Null(helper.Translate(0x1, 0x0, 2, Close, 0x80, "f.txt", Resolver));

        // ...but directory create/delete still surface.
        var dirCreated = helper.Translate(0x1, 0x0, 3, FileCreate | Close, dirAttr, "sub", Resolver);
        Assert.NotNull(dirCreated);
        Assert.Equal(ChangeType.Created, dirCreated!.ChangeType);
    }

    [Fact]
    public void Config_AcceptsUsnMonitorType()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = "/tmp/tt-src", Destination = "/tmp/tt-dst" });
        cfg.Monitor = new MonitorConfig { Type = "usn" };
        var log = new RecordingLogger();

        Assert.True(Config.Validate(cfg, log));
    }

    [Fact]
    public void Config_AcceptsCompositeUsn()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = "/tmp/tt-src", Destination = "/tmp/tt-dst" });
        cfg.Monitor = new MonitorConfig { Type = "composite", Usn = true };
        var log = new RecordingLogger();

        Assert.True(Config.Validate(cfg, log));
    }

    [Fact]
    public void Config_WarnsUsnOnNonComposite()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = "/tmp/tt-src", Destination = "/tmp/tt-dst" });
        cfg.Monitor = new MonitorConfig { Type = "watcher", Usn = true };
        var log = new RecordingLogger();

        Assert.True(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.StartsWith("WRN:") && m.Contains("monitor.usn"));
    }

    [Fact]
    public void Composite_WithUsnMember_SurfacesFileOps()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var probe = new UsnJournalMonitor(_dir);
            probe.ProbeVolume();
        }
        catch
        {
            return; // unelevated
        }

        // Watcher + journal observe the same tree; the pipeline dedups by
        // path downstream, so delivery may double — assert presence, not count.
        using var composite = new CompositeMonitor(
            new FileWatcherMonitor(_dir),
            new UsnJournalMonitor(_dir));
        var events = new List<FileChangedEventArgs>();
        var gate = new object();
        composite.Changed += (_, e) => { lock (gate) events.Add(e); };
        composite.Start();
        try
        {
            for (var i = 0; i < 3; i++)
                File.WriteAllText(Path.Combine(_dir, "c" + i + ".txt"), "v1");
            WaitFor(events, gate, 3);
        }
        finally { composite.Stop(); }

        List<FileChangedEventArgs> snapshot;
        lock (gate) snapshot = events.ToList();
        for (var i = 0; i < 3; i++)
            Assert.Contains(snapshot, e => e.FullPath == Path.Combine(_dir, "c" + i + ".txt"));
    }

    [Fact]
    public void Live_DetectsFileOps()
    {
        if (!OperatingSystem.IsWindows()) return;
        UsnJournalMonitor probe;
        try
        {
            probe = new UsnJournalMonitor(_dir);
            probe.ProbeVolume();
        }
        catch
        {
            return; // unelevated: journal not openable, nothing to prove
        }

        using var monitor = new UsnJournalMonitor(_dir);
        var events = new List<FileChangedEventArgs>();
        var gate = new object();
        monitor.Changed += (_, e) => { lock (gate) events.Add(e); };
        monitor.Start();
        try
        {
            var f = Path.Combine(_dir, "live.txt");
            File.WriteAllText(f, "x");
            WaitFor(events, gate, 1);

            File.WriteAllText(f, "yy longer");
            WaitFor(events, gate, 2);

            var g = Path.Combine(_dir, "renamed.txt");
            File.Move(f, g);
            WaitFor(events, gate, 3);

            File.Delete(g);
            WaitFor(events, gate, 4);
        }
        finally { monitor.Stop(); }

        List<FileChangedEventArgs> snapshot;
        lock (gate) snapshot = events.ToList();
        Assert.Contains(snapshot, e => e.ChangeType == ChangeType.Created && e.FullPath == Path.Combine(_dir, "live.txt"));
        Assert.Contains(snapshot, e => e.ChangeType == ChangeType.Modified && e.FullPath == Path.Combine(_dir, "live.txt"));
        Assert.Contains(snapshot, e => e.ChangeType == ChangeType.Renamed && e.FullPath == Path.Combine(_dir, "renamed.txt"));
        Assert.Contains(snapshot, e => e.ChangeType == ChangeType.Deleted && e.FullPath == Path.Combine(_dir, "renamed.txt"));
    }

    [Fact]
    public void Parity_WithWatcher_FileEventsAgree()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var probe = new UsnJournalMonitor(_dir);
            probe.ProbeVolume();
        }
        catch
        {
            return; // unelevated
        }

        using var usn = new UsnJournalMonitor(_dir);
        using var watcher = new FileWatcherMonitor(_dir);
        var usnEvents = new List<FileChangedEventArgs>();
        var watcherEvents = new List<FileChangedEventArgs>();
        var gate = new object();
        usn.Changed += (_, e) => { lock (gate) usnEvents.Add(e); };
        watcher.Changed += (_, e) => { lock (gate) watcherEvents.Add(e); };
        usn.Start();
        watcher.Start();
        try
        {
            for (var i = 0; i < 3; i++)
                File.WriteAllText(Path.Combine(_dir, "p" + i + ".txt"), "v1");
            WaitFor(usnEvents, gate, 3);
            WaitFor(watcherEvents, gate, 3);

            File.WriteAllText(Path.Combine(_dir, "p0.txt"), "v2 longer");
            File.Move(Path.Combine(_dir, "p1.txt"), Path.Combine(_dir, "q1.txt"));
            File.Delete(Path.Combine(_dir, "p2.txt"));
            WaitFor(usnEvents, gate, 6);
            WaitFor(watcherEvents, gate, 6);
        }
        finally { usn.Stop(); watcher.Stop(); }

        // Directory-mtime noise differs by design (USN skips dir-Modified);
        // normalize it away, then the file-event multisets must agree.
        static List<string> Normalize(List<FileChangedEventArgs> evts) => evts
            .Where(e => !(e.ChangeType == ChangeType.Modified && Directory.Exists(e.FullPath)))
            .Select(e => e.ChangeType + "|" + e.FullPath + "|" + (e.OldFullPath ?? ""))
            .OrderBy(s => s).ToList();

        List<FileChangedEventArgs> usnSnap, watcherSnap;
        lock (gate) { usnSnap = usnEvents.ToList(); watcherSnap = watcherEvents.ToList(); }
        Assert.Equal(Normalize(watcherSnap), Normalize(usnSnap));
    }

    private static void WaitFor(List<FileChangedEventArgs> events, object gate, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
            {
                if (events.Count >= count) return;
            }
            Thread.Sleep(50);
        }
    }
}

// Thin construction + translation seam so the pure USN logic is testable
// without touching the volume (P/Invoke stays inside the monitor).
internal sealed class RecordsForTest : IDisposable
{
    private readonly UsnJournalMonitor _monitor;

    public RecordsForTest(UsnJournalMonitor monitor) => _monitor = monitor;

    public FileChangedEventArgs? Translate(ulong fileRef, ulong parentRef, long usn,
        uint reason, uint attrs, string name, Func<ulong, string?> resolve)
    {
        var rec = new UsnJournalMonitor.UsnRecord
        {
            FileRef = fileRef,
            ParentRef = parentRef,
            Usn = usn,
            Reason = reason,
            Attributes = attrs,
            FileName = name,
        };
        return _monitor.TranslateRecord(rec, resolve);
    }

    public void Dispose() => _monitor.Dispose();
}
