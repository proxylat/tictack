namespace TicTack;

public class UsnMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tictack-usn-" + Guid.NewGuid().ToString("N"));
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public UsnMonitorTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

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

    // True USN_RECORD_V2 layout (winioctl.h): 60-byte fixed prefix,
    // Reason@40, Attributes@52, FileNameLength@56, FileNameOffset@58.
    private static byte[] BuildRecord(ulong fileRef, ulong parentRef, long usn,
        uint reason, uint attrs, string name)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        var buf = new byte[60 + nameBytes.Length];
        BitConverter.GetBytes((uint)buf.Length).CopyTo(buf, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(buf, 4);
        BitConverter.GetBytes(fileRef).CopyTo(buf, 8);
        BitConverter.GetBytes(parentRef).CopyTo(buf, 16);
        BitConverter.GetBytes(usn).CopyTo(buf, 24);
        BitConverter.GetBytes(reason).CopyTo(buf, 40);
        BitConverter.GetBytes(attrs).CopyTo(buf, 52);
        BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(buf, 56);
        BitConverter.GetBytes((ushort)60).CopyTo(buf, 58);
        nameBytes.CopyTo(buf, 60);
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
        // Parent ref resolves to the directory; the record name supplies
        // the file (event-time path, not by-id current path).
        string Resolver(ulong fr) => fr == 0x0 ? _dir : Path.Combine(_dir, "f.txt");

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
    public void ReasonWatchMask_CoversAllMappedReasons()
    {
        uint mask = UsnJournalMonitor.ReasonWatchMask;
        // Exact value pins the contract: any newly mapped reason must
        // extend the mask deliberately, not silently stay filtered.
        Assert.Equal(0x00FFFF77u, mask);
        // Every reason TranslateRecord maps must survive NTFS-side filtering.
        foreach (var bit in new uint[] { FileCreate, FileDelete, RenameOld, RenameNew,
            0x00000001, 0x00000002, 0x00000004, 0x00000010, 0x00000020, 0x00000040,
            0x00000400, 0x00000800, 0x00004000, 0x00008000, 0x00010000, 0x00020000,
            0x00040000, 0x00080000, 0x00100000, 0x00200000, 0x00400000, 0x00800000 })
            Assert.True((mask & bit) != 0, "mask drops mapped bit 0x" + bit.ToString("X8"));
        // Reasons mapping to null today stay filtered (CLOSE-alone
        // included: every record carries it, alone it means nothing changed).
        foreach (var bit in new uint[] { Close, 0x01000000 })
            Assert.True((mask & bit) == 0, "mask passes unmapped bit 0x" + bit.ToString("X8"));
    }

    [Fact]
    public void Prefilter_SeedSkipsOutsideTree()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        var m = new UsnJournalMonitor(_dir);
        try
        {
            m.FrnOfPath = p => p == _dir ? 0xA0 : (p == sub ? (ulong?)0xA1 : null);
            m.SeedWatchDirs();
            Assert.True(m.IsWatchedParent(0xA0));
            Assert.True(m.IsWatchedParent(0xA1));
            Assert.False(m.IsWatchedParent(0xB0)); // outsider (Spotify-like)
        }
        finally { m.Dispose(); }
    }

    [Fact]
    public void Prefilter_DispatchSkipsOutsideParent()
    {
        var m = new UsnJournalMonitor(_dir);
        try
        {
            m.FrnOfPath = p => p == _dir ? (ulong?)0xA0 : null;
            m.SeedWatchDirs();
            var rec = new UsnJournalMonitor.UsnRecord
            {
                FileRef = 0xF1, ParentRef = 0xB0, Usn = 1,
                Reason = FileCreate | Close, Attributes = 0x80, FileName = "x.txt",
            };
            m.Dispatch(rec);
            Assert.Equal(1, m.PrefilteredSkips);
        }
        finally { m.Dispose(); }
    }

    [Fact]
    public void Prefilter_OffDisablesSkipping()
    {
        var m = new UsnJournalMonitor(_dir, 10, 200, false);
        try
        {
            m.SeedWatchDirs();
            var rec = new UsnJournalMonitor.UsnRecord
            {
                FileRef = 0xF1, ParentRef = 0xB0, Usn = 1,
                Reason = FileCreate | Close, Attributes = 0x80, FileName = "x.txt",
            };
            m.Dispatch(rec);
            Assert.Equal(0, m.PrefilteredSkips);
        }
        finally { m.Dispose(); }
    }

    [Fact]
    public void Prefilter_DirCreateAndDeleteMaintainSet()
    {
        var m = new UsnJournalMonitor(_dir);
        try
        {
            m.FrnOfPath = p => p == _dir ? (ulong?)0xA0 : null;
            m.SeedWatchDirs();
            Assert.False(m.IsWatchedParent(0xD0));
            var helper = new RecordsForTest(m);
            string? Resolver(ulong fr) => fr == 0xA0 ? _dir : null;
            var created = helper.Translate(0xD0, 0xA0, 1, FileCreate | Close, 0x10, "newdir", Resolver);
            Assert.NotNull(created);
            Assert.True(m.IsWatchedParent(0xD0));
            var deleted = helper.Translate(0xD0, 0xA0, 2, FileDelete | Close, 0x10, "newdir", Resolver);
            Assert.NotNull(deleted);
            Assert.False(m.IsWatchedParent(0xD0));
        }
        finally { m.Dispose(); }
    }

    [Fact]
    public void Translate_RenamePairsOldAndNew()
    {
        using var helper = NewMonitor();
        // The record arrives after the move: opening the file by id yields
        // its new path, so the old name must come from the record itself.
        string Resolver(ulong fr) => fr == 0x1
            ? Path.Combine(_dir, "new.txt")
            : _dir;

        var old = helper.Translate(0x1, 0x0, 1, RenameOld, 0x80, "old.txt", Resolver);
        Assert.Null(old); // old-name alone emits nothing

        var renamed = helper.Translate(0x1, 0x0, 2, RenameNew | Close, 0x80, "new.txt", Resolver);
        Assert.NotNull(renamed);
        Assert.Equal(ChangeType.Renamed, renamed!.ChangeType);
        Assert.Equal(Path.Combine(_dir, "new.txt"), renamed.FullPath);
        Assert.Equal(Path.Combine(_dir, "old.txt"), renamed.OldFullPath);
    }

    [Fact]
    public void Translate_CreateAfterMove_UsesRecordName()
    {
        using var helper = NewMonitor();
        // Journal read after the move: by-id yields the NEW path, but the
        // create happened at the record's parent + name (the Parity
        // mechanism: p1's create resolving to q1).
        string Resolver(ulong fr) => fr == 0x0 ? _dir : Path.Combine(_dir, "new.txt");

        var created = helper.Translate(0x1, 0x0, 1, FileCreate | Close, 0x80, "old.txt", Resolver);
        Assert.NotNull(created);
        Assert.Equal(ChangeType.Created, created!.ChangeType);
        Assert.Equal(Path.Combine(_dir, "old.txt"), created.FullPath);
    }

    [Fact]
    public void Translate_ParentGone_FallsBackToById()
    {
        using var helper = NewMonitor();
        // Parent unresolvable (deleted too): by-id still answers.
        string? Resolver(ulong fr) => fr == 0x1 ? Path.Combine(_dir, "f.txt") : null;

        var created = helper.Translate(0x1, 0x0, 1, FileCreate | Close, 0x80, "f.txt", Resolver);
        Assert.NotNull(created);
        Assert.Equal(Path.Combine(_dir, "f.txt"), created!.FullPath);
    }

    [Fact]
    public void Translate_RenameWithoutOldName_BecomesCreated()
    {
        using var helper = NewMonitor();
        string Resolver(ulong fr) => fr == 0x0 ? _dir : Path.Combine(_dir, "new.txt");

        var evt = helper.Translate(0x9, 0x0, 1, RenameNew | Close, 0x80, "new.txt", Resolver);
        Assert.NotNull(evt);
        Assert.Equal(ChangeType.Created, evt!.ChangeType);
    }

    [Fact]
    public void Translate_DuplicateRenameDelivery_StaysSilent()
    {
        using var helper = NewMonitor();
        // Parity mechanism: the journal delivered the new name twice
        // (duplicate rename-new, or a create record for the new link).
        // The paired rename already reported it; a second delivery for
        // the same FRN must not become a phantom Created.
        string Resolver(ulong fr) => fr == 0x0 ? _dir : Path.Combine(_dir, "new.txt");

        Assert.Null(helper.Translate(0x1, 0x0, 1, RenameOld, 0x80, "old.txt", Resolver));
        var renamed = helper.Translate(0x1, 0x0, 2, RenameNew | Close, 0x80, "new.txt", Resolver);
        Assert.NotNull(renamed);
        Assert.Equal(ChangeType.Renamed, renamed!.ChangeType);

        // Duplicate rename-new for the same FRN: silent.
        Assert.Null(helper.Translate(0x1, 0x0, 3, RenameNew | Close, 0x80, "new.txt", Resolver));
        // Create record for the same FRN (new link): silent too.
        Assert.Null(helper.Translate(0x1, 0x0, 4, FileCreate | Close, 0x80, "new.txt", Resolver));

        // After delete the FRN lifecycle ends: a later create is real.
        string? DelResolver(ulong fr) => fr == 0x0 ? _dir : null;
        var deleted = helper.Translate(0x1, 0x0, 5, FileDelete | Close, 0x80, "new.txt", DelResolver);
        Assert.NotNull(deleted);
        Assert.Equal(ChangeType.Deleted, deleted!.ChangeType);
        var recreated = helper.Translate(0x1, 0x0, 6, FileCreate | Close, 0x80, "new.txt", Resolver);
        Assert.NotNull(recreated);
        Assert.Equal(ChangeType.Created, recreated!.ChangeType);
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
        string Resolver(ulong fr) => fr == 0x0 ? _dir : Path.Combine(_dir, "sub");

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

    [WindowsUsnFact]
    public void Composite_WithUsnMember_SurfacesFileOps()
    {
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

    [WindowsUsnFact]
    public void Live_DetectsFileOps()
    {
        using var monitor = new UsnJournalMonitor(_dir);
        var events = new List<FileChangedEventArgs>();
        var errors = new List<string>();
        var gate = new object();
        monitor.Changed += (_, e) => { lock (gate) events.Add(e); };
        monitor.Error += (_, e) => { lock (gate) errors.Add(e.Exception.ToString()); };
        monitor.Start();
        try
        {
            var f = Path.Combine(_dir, "live.txt");
            var g = Path.Combine(_dir, "renamed.txt");

            // Wait on the event predicate, not a count: a count barrier
            // desyncs when a transport emits an extra (or coalesced) event.
            File.WriteAllText(f, "x");
            WaitForEvent(events, gate, e => e.ChangeType == ChangeType.Created && e.FullPath == f);

            File.WriteAllText(f, "yy longer");
            WaitForEvent(events, gate, e => e.ChangeType == ChangeType.Modified && e.FullPath == f);

            File.Move(f, g);
            WaitForEvent(events, gate, e => e.ChangeType == ChangeType.Renamed && e.FullPath == g);

            File.Delete(g);
            WaitForEvent(events, gate, e => e.ChangeType == ChangeType.Deleted && e.FullPath == g);
        }
        finally { monitor.Stop(); }

        List<FileChangedEventArgs> snapshot;
        lock (gate) snapshot = events.ToList();
        List<string> errorSnap;
        lock (gate) errorSnap = errors.ToList();
        // Worker errors are otherwise invisible: surface them first so a
        // failing live test says WHY the collection is empty.
        Assert.Empty(errorSnap);
        // Full strings, not truncated: the assert messages cut paths off.
        _output.WriteLine("Live resolveFailures=" + monitor.ResolveFailures +
            " prefiltered=" + monitor.PrefilteredSkips + " seen=" + monitor._recordsSeen +
            " active=" + monitor._prefilterActive +
            " events=" + string.Join(";", snapshot.Select(e => e.ChangeType + "|" + e.FullPath + "|" + (e.OldFullPath ?? ""))));
        Assert.Contains(snapshot, e => e.ChangeType == ChangeType.Created && e.FullPath == Path.Combine(_dir, "live.txt"));
        Assert.Contains(snapshot, e => e.ChangeType == ChangeType.Modified && e.FullPath == Path.Combine(_dir, "live.txt"));
        Assert.Contains(snapshot, e => e.ChangeType == ChangeType.Renamed && e.FullPath == Path.Combine(_dir, "renamed.txt"));
        Assert.Contains(snapshot, e => e.ChangeType == ChangeType.Deleted && e.FullPath == Path.Combine(_dir, "renamed.txt"));
    }

    [WindowsUsnFact]
    public void Parity_WithWatcher_FileEventsAgree()
    {
        using var usn = new UsnJournalMonitor(_dir);
        using var watcher = new FileWatcherMonitor(_dir);
        var usnEvents = new List<FileChangedEventArgs>();
        var watcherEvents = new List<FileChangedEventArgs>();
        var usnErrors = new List<string>();
        var gate = new object();
        usn.Changed += (_, e) => { lock (gate) usnEvents.Add(e); };
        usn.Error += (_, e) => { lock (gate) usnErrors.Add(e.Exception.ToString()); };
        watcher.Changed += (_, e) => { lock (gate) watcherEvents.Add(e); };
        usn.Start();
        watcher.Start();
        try
        {
            // Gate: FileSystemWatcher arms asynchronously — file ops fired
            // before it listens are lost (USN replays from its floor, so it
            // never misses them). Wait for a sentinel round-trip through the
            // watcher, then clear both lists so sentinel ops don't pollute
            // the parity comparison.
            var sentinel = Path.Combine(_dir, "ready.txt");
            File.WriteAllText(sentinel, "ready");
            WaitForEvent(watcherEvents, gate, e => e.FullPath == sentinel);
            File.Delete(sentinel);
            lock (gate) { usnEvents.Clear(); watcherEvents.Clear(); }

            var p0 = Path.Combine(_dir, "p0.txt");
            var p1 = Path.Combine(_dir, "p1.txt");
            var p2 = Path.Combine(_dir, "p2.txt");
            var q1 = Path.Combine(_dir, "q1.txt");

            for (var i = 0; i < 3; i++)
                File.WriteAllText(Path.Combine(_dir, "p" + i + ".txt"), "v1");
            File.WriteAllText(p0, "v2 longer");
            File.Move(p1, q1);
            File.Delete(p2);

            // Structural events only, ordinal-sorted: Modified differs by
            // transport design (USN coalesces a write into the create or
            // overwrite record; the watcher splits ADDED + MODIFIED) and
            // Live_DetectsFileOps covers it. The ready.txt sentinel is
            // filtered: USN's poll cycle can deliver its delete after the
            // clear above, and it is test scaffolding, not product signal.
            var expected = new List<string>
            {
                "Created|" + p0 + "|",
                "Created|" + p1 + "|",
                "Created|" + p2 + "|",
                "Deleted|" + p2 + "|",
                "Renamed|" + q1 + "|" + p1,
            };

            List<string> Normalize(List<FileChangedEventArgs> evts) => evts
                .Where(e => e.ChangeType != ChangeType.Modified)
                .Where(e => e.FullPath != sentinel && e.OldFullPath != sentinel)
                .Select(e => e.ChangeType + "|" + e.FullPath + "|" + (e.OldFullPath ?? ""))
                .Distinct()
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            WaitUntil(usnEvents, gate, evts => expected.SequenceEqual(Normalize(evts)));
            WaitUntil(watcherEvents, gate, evts => expected.SequenceEqual(Normalize(evts)));

            List<FileChangedEventArgs> usnSnap, watcherSnap;
            List<string> usnErrSnap;
            lock (gate) { usnSnap = usnEvents.ToList(); watcherSnap = watcherEvents.ToList(); usnErrSnap = usnErrors.ToList(); }
            Assert.Empty(usnErrSnap);
            _output.WriteLine("Parity resolveFailures=" + usn.ResolveFailures +
                " prefiltered=" + usn.PrefilteredSkips + " seen=" + usn._recordsSeen +
                " active=" + usn._prefilterActive +
                " usn=" + string.Join(";", Normalize(usnSnap)) +
                " watcher=" + string.Join(";", Normalize(watcherSnap)));
            Assert.Equal(Normalize(watcherSnap), Normalize(usnSnap));
        }
        finally { usn.Stop(); watcher.Stop(); }
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

    private static void WaitForEvent(List<FileChangedEventArgs> events, object gate,
        Func<FileChangedEventArgs, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
            {
                if (events.Any(predicate)) return;
            }
            Thread.Sleep(50);
        }
    }

    private static void WaitUntil(List<FileChangedEventArgs> events, object gate,
        Func<List<FileChangedEventArgs>, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
            {
                if (predicate(events)) return;
            }
            Thread.Sleep(50);
        }
    }

    // FSCTL codes from winioctl.h via the CTL_CODE formula
    // (DeviceType 9 << 16 | Function << 2 | Method). QUERY regressed
    // investigation 2026-09-22: 0x900FB (function 62, undefined) here
    // would fail the first journal read after a good probe. MOUNTED had
    // the same class of bug: 0x90028 is function 10 (FSCTL_UNLOCK_VOLUME),
    // correct is function 42 (IS_VOLUME_MOUNTED) = 0x900A8.
    [Fact]
    public void FsctlCodes_MatchWinioctl()
    {
        Assert.Equal(0x000900f4u, UsnJournalMonitor.FsctlQueryUsnJournal);
        Assert.Equal(0x000900bbu, UsnJournalMonitor.FsctlReadUsnJournal);
        Assert.Equal(0x000900a8u, UsnJournalMonitor.FsctlIsVolumeMounted);
    }

    // dwSize must be sizeof(FILE_ID_DESCRIPTOR) = 4 + 4 + 16 (union sized
    // by FILE_ID_128), not the 16 bytes of the Type=0 variant alone: a
    // wrong size makes OpenFileById fail with ERROR_INVALID_PARAMETER on
    // every resolve and silently drops every event.
    [Fact]
    public void FileIdDescriptor_MarshalsToNativeSize()
    {
        Assert.Equal(24, UsnJournalMonitor.FileIdDescriptorSize);
    }

    // BY_HANDLE_FILE_INFORMATION is 52 bytes with FileIndexHigh at 44 and
    // FileIndexLow at 48. Default Pack=8 inserts 4 pad bytes after the
    // leading uint and shifts the index reads onto bytes the OS never
    // wrote: every seeded FRN came out garbage, the prefilter matched
    // nothing, and USN delivery went totally (and silently) dark.
    [Fact]
    public void ByHandleFileInfo_MatchesNativeLayout()
    {
        var t = typeof(UsnJournalMonitor.ByHandleFileInfo);
        Assert.Equal(52, System.Runtime.InteropServices.Marshal.SizeOf(t));
        Assert.Equal(44, (int)System.Runtime.InteropServices.Marshal.OffsetOf(t, nameof(UsnJournalMonitor.ByHandleFileInfo.FileIndexHigh)));
        Assert.Equal(48, (int)System.Runtime.InteropServices.Marshal.OffsetOf(t, nameof(UsnJournalMonitor.ByHandleFileInfo.FileIndexLow)));
    }

    // Privilege adjustment is Windows-only; elsewhere it must decline
    // cleanly without throwing (no advapi32, no token).
    [Fact]
    public void EnableVolumePrivileges_ReportsUnsupportedOffWindows()
    {
        if (OperatingSystem.IsWindows()) return; // exercised live on Windows
        Assert.Equal("unsupported", UsnJournalMonitor.EnableVolumePrivileges());
    }
}

// Discovery-time gate: static Skip is understood by every runner, so a
// USN test that can't run is always reported as Skipped — never a silent
// pass, never a mapping-dependent runtime signal. The probe runs at
// discovery on the same box, so elevation state matches execution.
internal sealed class WindowsUsnFactAttribute : FactAttribute
{
    public WindowsUsnFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows/NTFS.";
            return;
        }
        try
        {
            using var probe = new UsnJournalMonitor(Path.GetTempPath());
            probe.ProbeVolume();
        }
        catch (Exception ex)
        {
            Skip = "USN journal not openable: " + ex.Message;
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
