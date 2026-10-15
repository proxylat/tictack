using TicTack;

namespace Corpus;

// DiskCapture: live-disk → DiskCapturePlan backend. All Linux-safe: fake
// layouts (GPT shapes mirrored from DiskLayoutTests), a fake snapshot-set
// factory (counts sets, serves scripted devices), a fake BitLocker
// controller (FakeBitLocker-style ledger), and FileBlockSource stand-ins.
// The VSS COM sequence + extent P/Invokes + DiskSnapshotSource ctor need
// on-box (Windows VM) proof — Linux pins the planning, not the ioctls.
public sealed class DiskCaptureTests : IDisposable
{
    private const string EspType = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";
    private const string MsrType = "e3c9e316-0b5c-4db8-817d-f92df00215ae";
    private const string BasicType = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7";
    private const string RecoveryType = "de94bba4-06d1-4d40-a16a-bfd50179d6ac";
    private const string KeyId = "{11111111-1111-1111-1111-111111111111}";

    private readonly string _dir;

    public DiskCaptureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_diskcapture_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static PartitionRecord Rec(int n, string type, string fs, long off, long len,
        bool skipped = false, string? reason = null) => new()
        {
            PartitionNumber = n,
            Name = $"p{n}",
            TypeGuid = type,
            UniqueGuid = Guid.NewGuid().ToString("d"),
            FirstLba = off / 512,
            LastLba = (off + len) / 512 - 1,
            OffsetBytes = off,
            LengthBytes = len,
            Filesystem = fs,
            Skipped = skipped,
            SkipReason = reason,
        };

    private static DiskLayout Layout(params PartitionRecord[] parts) => new()
    {
        DiskNumber = 0,
        DiskGuid = Guid.NewGuid().ToString("d"),
        DiskSizeBytes = 1L << 40,
        SectorSizeBytes = 512,
        Style = "gpt",
        Partitions = new List<PartitionRecord>(parts),
    };

    private string SeedFile(string name, int seed, int bytes = 8192)
    {
        var path = Path.Combine(_dir, name);
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    // Fake over mock: a working in-memory snapshot set with scripted devices
    // ("snap:" + volume) and a shared ledger for ordering asserts.
    private sealed class FakeSnapshotSet : IDiskSnapshotSet
    {
        private readonly List<string> _ledger;
        private readonly Dictionary<string, string> _devices;
        private bool _completed;
        private bool _disposed;
        public int Completes;

        public FakeSnapshotSet(List<string> ledger, IEnumerable<string> volumes)
        {
            _ledger = ledger;
            _devices = volumes.ToDictionary(v => v, v => "snap:" + v, StringComparer.OrdinalIgnoreCase);
        }

        public string DeviceFor(string volume) => _devices[volume];

        public void Complete()
        {
            if (_completed) return;
            _completed = true;
            Completes++;
            _ledger.Add("complete");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ledger.Add("set-dispose");
        }
    }

    private sealed class FakeSetFactory
    {
        public readonly List<string> Ledger;
        public int Creates;
        public List<string>? LastVolumes;
        public FakeSnapshotSet? Last;

        public FakeSetFactory(List<string> ledger)
        {
            Ledger = ledger;
        }

        public IDiskSnapshotSet Create(IReadOnlyList<string> volumes, IReadOnlySet<string> statics, ILogger log)
        {
            Creates++;
            LastVolumes = volumes.ToList();
            Ledger.Add("create-set:" + string.Join(",", volumes));
            Ledger.Add("create-statics:" + string.Join(",", statics.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)));
            Last = new FakeSnapshotSet(Ledger, volumes);
            return Last;
        }
    }

    // Same shape as BitLockerTests.FakeBitLocker, sharing the caller's ledger
    // so suspend/freeze/resume ordering asserts span both collaborators.
    private sealed class FakeBitLocker : IBitLockerController
    {
        private readonly List<string> _ledger;
        private readonly BitLockerState _state;

        public FakeBitLocker(List<string> ledger, BitLockerState state)
        {
            _ledger = ledger;
            _state = state;
        }

        public BitLockerInfo Query(string volume)
        {
            _ledger.Add("query:" + volume);
            return new BitLockerInfo(_state, _state == BitLockerState.Unprotected
                ? Array.Empty<string>() : new[] { KeyId });
        }

        public void Suspend(string volume) => _ledger.Add("suspend:" + volume);

        public void Resume(string volume) => _ledger.Add("resume:" + volume);
    }

    // Lifetime-honoring file stand-in: the plan sources the core builds would
    // be DiskSnapshotSource/LiveCaptureSource on Windows; on Linux this plays
    // both (AddRef/Release included, so teardown tests are real).
    private sealed class FakePartSource : IBlockSource
    {
        private readonly DiskCaptureLifetime _lifetime;
        private readonly IBlockSource _inner;
        private bool _disposed;

        public FakePartSource(DiskCaptureLifetime lifetime, IBlockSource inner)
        {
            _lifetime = lifetime;
            _inner = inner;
            lifetime.AddRef();
        }

        public long Length => _inner.Length;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _inner.OpenRead();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inner.Dispose();
            _lifetime.Release();
        }
    }

    private static void DisposePlan(DiskCapturePlan plan)
    {
        foreach (var p in plan.Partitions)
            p.Source?.Dispose();
    }

    [Fact]
    public void BuildPlan_OffWindows_ThrowsInvalidOperation_Loud()
    {
        if (OperatingSystem.IsWindows()) return;
        var ex = Assert.Throws<InvalidOperationException>(() => DiskCapture.BuildPlan(0, new TestLogger()));
        Assert.Equal("disk 0: live disk capture needs Windows (elevated GPT+VSS path; Linux disk verbs exit 1)", ex.Message);
    }

    [Fact]
    public void SnapshotSet_OffWindows_ThrowsPlatformNotSupported()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<PlatformNotSupportedException>(() => VssSnapshotSet.Create(new[] { "C:" }, new TestLogger()));
    }

    [Fact]
    public void TryFindVolume_OffWindows_Null()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Null(DiskCapture.TryFindVolume(0, 1048576, 104857600));
    }

    [Fact]
    public void Plan_MsrSkipped_EspLive_DataSnapshotted()
    {
        var esp = Rec(1, EspType, "fat32", 1L << 20, 100L << 20);
        var msr = Rec(2, MsrType, "none", 105906176, 16777216, skipped: true, reason: "msr-reserved");
        var sys = Rec(3, BasicType, "ntfs", 256L << 20, 512L << 20);
        var layout = Layout(esp, msr, sys);
        var espPath = SeedFile("esp.img", 1);
        var sysPath = SeedFile("sys.img", 2);
        var ledger = new List<string>();
        var factory = new FakeSetFactory(ledger);
        var kinds = new Dictionary<int, string>();

        var plan = DiskCapture.BuildPlanCore(0, layout,
            findVolume: (d, off, len) => off == esp.OffsetBytes ? "S:" : "C:",
            bitlocker: new FakeBitLocker(ledger, BitLockerState.ProtectedUnlocked),
            createSet: (v, s, l) => factory.Create(v, s, l),
            openData: (life, vol, dev, rec, scope) =>
            {
                kinds[rec.PartitionNumber] = $"data:{vol}@{dev}";
                return new FakePartSource(life, new FileBlockSource(sysPath));
            },
            openLive: (life, rec, scope) =>
            {
                kinds[rec.PartitionNumber] = "live";
                return new FakePartSource(life, new FileBlockSource(espPath));
            },
            new TestLogger());

        try
        {
            Assert.Equal(layout.DiskGuid, plan.DiskGuid);
            Assert.Equal(layout.DiskSizeBytes, plan.DiskSizeBytes);
            Assert.Equal(512, plan.SectorSizeBytes);
            Assert.Equal(3, plan.Partitions.Count);

            var e = plan.Partitions[0];
            Assert.False(e.Skipped);
            Assert.NotNull(e.Source);
            Assert.Null(e.ChangeSource); // ESP: no journal, always full
            Assert.Equal("live", kinds[1]);

            var m = plan.Partitions[1];
            Assert.True(m.Skipped);
            Assert.Equal("msr-reserved", m.SkipReason);
            Assert.Null(m.Source);
            Assert.Null(m.ChangeSource);
            Assert.Equal(msr.OffsetBytes, m.OffsetBytes); // range kept...
            Assert.Equal(msr.LengthBytes, m.LengthBytes);

            var s = plan.Partitions[2];
            Assert.False(s.Skipped);
            Assert.NotNull(s.Source);
            Assert.IsType<MftDigestChangeSource>(s.ChangeSource);
            Assert.Equal("data:C:@snap:C:", kinds[3]);

            // ...and never snapshotted: the set carries the data volume only.
            Assert.Equal(1, factory.Creates);
            Assert.Equal(new[] { "C:" }, factory.LastVolumes);
        }
        finally
        {
            DisposePlan(plan);
        }
    }

    [Fact]
    public void Plan_RecoveryVolume_MarkedStatic_BasicVolume_Not()
    {
        // On-box: a 735MB Recovery partition with 120MB free vetoed
        // whole-disk capture under the 320MB size tier; VSS itself
        // snapshots it fine, so Recovery clears the preflight as
        // known-static while ordinary data keeps the size tier.
        var sys = Rec(3, BasicType, "ntfs", 256L << 20, 512L << 20);
        var rec = Rec(4, RecoveryType, "ntfs", 768L << 20, 735L << 20);
        var img = SeedFile("r.img", 9);
        var ledger = new List<string>();
        var factory = new FakeSetFactory(ledger);

        var plan = DiskCapture.BuildPlanCore(0, Layout(sys, rec),
            findVolume: (d, off, len) => off == sys.OffsetBytes ? "C:" : "R:",
            bitlocker: new FakeBitLocker(ledger, BitLockerState.Unprotected),
            createSet: (v, s, l) => factory.Create(v, s, l),
            openData: (life, vol, dev, part, scope) => new FakePartSource(life, new FileBlockSource(img)),
            openLive: (life, part, scope) => new FakePartSource(life, new FileBlockSource(img)),
            new TestLogger());

        try
        {
            Assert.Equal(1, factory.Creates);
            Assert.Equal(new[] { "C:", "R:" }, factory.LastVolumes);
            Assert.Contains("create-statics:R:", ledger);
            Assert.DoesNotContain(ledger, e => e == "create-statics:C:" || e == "create-statics:C:,R:");
        }
        finally
        {
            DisposePlan(plan);
        }
    }

    [Fact]
    public void Plan_MultiPartition_UsesSingleSnapshotSet()
    {
        var esp = Rec(1, EspType, "fat32", 1L << 20, 100L << 20);
        var msr = Rec(2, MsrType, "none", 105906176, 16777216, skipped: true, reason: "msr-reserved");
        var c = Rec(3, BasicType, "ntfs", 256L << 20, 512L << 20);
        var d = Rec(4, BasicType, "ntfs", 768L << 20, 512L << 20);
        var e = Rec(5, BasicType, "ntfs", 1280L << 20, 256L << 20);
        var byOff = new Dictionary<long, string>
        {
            [esp.OffsetBytes] = "S:",
            [c.OffsetBytes] = "C:",
            [d.OffsetBytes] = "D:",
            [e.OffsetBytes] = "E:",
        };
        var img = SeedFile("v.img", 3);
        var ledger = new List<string>();
        var factory = new FakeSetFactory(ledger);

        var plan = DiskCapture.BuildPlanCore(0, Layout(esp, msr, c, d, e),
            findVolume: (disk, off, len) => byOff[off],
            bitlocker: new FakeBitLocker(ledger, BitLockerState.Unprotected),
            createSet: (v, s, l) => factory.Create(v, s, l),
            openData: (life, vol, dev, rec, scope) => new FakePartSource(life, new FileBlockSource(img)),
            openLive: (life, rec, scope) => new FakePartSource(life, new FileBlockSource(img)),
            new TestLogger());

        try
        {
            // One set for the whole disk — not one snapshot per partition.
            Assert.Equal(1, factory.Creates);
            Assert.Equal(new[] { "C:", "D:", "E:" }, factory.LastVolumes);
            Assert.Equal(5, plan.Partitions.Count);
        }
        finally
        {
            DisposePlan(plan);
        }
    }

    [Fact]
    public void Plan_BitLocker_SuspendsBeforeFreeze_ResumesAfterTeardown()
    {
        var esp = Rec(1, EspType, "fat32", 1L << 20, 100L << 20);
        var sys = Rec(2, BasicType, "ntfs", 256L << 20, 512L << 20);
        var img = SeedFile("b.img", 4);
        var ledger = new List<string>();
        var factory = new FakeSetFactory(ledger);

        var plan = DiskCapture.BuildPlanCore(0, Layout(esp, sys),
            findVolume: (d, off, len) => off == esp.OffsetBytes ? "S:" : "C:",
            bitlocker: new FakeBitLocker(ledger, BitLockerState.ProtectedUnlocked),
            createSet: (v, s, l) => factory.Create(v, s, l),
            openData: (life, vol, dev, rec, scope) => new FakePartSource(life, new FileBlockSource(img)),
            openLive: (life, rec, scope) => new FakePartSource(life, new FileBlockSource(img)),
            new TestLogger());

        // Suspend-then-freeze: every suspend precedes the set creation.
        var createIdx = ledger.FindIndex(m => m.StartsWith("create-set:", StringComparison.Ordinal));
        Assert.True(createIdx >= 0, "set was never created: " + string.Join(" | ", ledger));
        foreach (var v in new[] { "C:", "S:" })
        {
            Assert.Contains("query:" + v, ledger);
            Assert.True(ledger.IndexOf("suspend:" + v) is int i && i >= 0 && i < createIdx,
                $"suspend:{v} must precede freeze: " + string.Join(" | ", ledger));
        }
        Assert.DoesNotContain(ledger, m => m.StartsWith("resume:", StringComparison.Ordinal));

        DisposePlan(plan);

        // Resume-after-teardown: the set tears down first, then each scope.
        var disposeIdx = ledger.FindIndex(m => m == "set-dispose");
        Assert.True(disposeIdx >= 0, "set was never disposed: " + string.Join(" | ", ledger));
        foreach (var v in new[] { "C:", "S:" })
        {
            Assert.True(ledger.IndexOf("resume:" + v) is int i && i > disposeIdx,
                $"resume:{v} must follow teardown: " + string.Join(" | ", ledger));
        }
        Assert.Equal(1, factory.Creates);
    }

    [Fact]
    public void Plan_LetterlessData_FailsLoud_BeforeAnyScope()
    {
        var sys = Rec(3, BasicType, "ntfs", 256L << 20, 512L << 20);
        var ledger = new List<string>();
        var factory = new FakeSetFactory(ledger);

        var ex = Assert.Throws<InvalidDataException>(() => DiskCapture.BuildPlanCore(0, Layout(sys),
            findVolume: (d, off, len) => null,
            bitlocker: new FakeBitLocker(ledger, BitLockerState.ProtectedUnlocked),
            createSet: (v, s, l) => factory.Create(v, s, l),
            openData: (life, vol, dev, rec, scope) => throw new InvalidOperationException("unreachable"),
            openLive: (life, rec, scope) => throw new InvalidOperationException("unreachable"),
            new TestLogger()));

        Assert.Contains("partition 3", ex.Message);
        Assert.Contains("drive letter", ex.Message);
        Assert.Empty(ledger); // failed before a single query/suspend...
        Assert.Equal(0, factory.Creates); // ...and before any snapshot.
    }

    [Fact]
    public void Plan_FatData_UsesLiveRead()
    {
        // Basic-data FAT32 (not ESP-typed): VSS would refuse it, so it
        // live-reads like the ESP — always full, no change source.
        var fat = Rec(3, BasicType, "fat32", 256L << 20, 512L << 20);
        var img = SeedFile("f.img", 5);
        var ledger = new List<string>();
        var factory = new FakeSetFactory(ledger);
        var kinds = new Dictionary<int, string>();

        var plan = DiskCapture.BuildPlanCore(0, Layout(fat),
            findVolume: (d, off, len) => "F:",
            bitlocker: new FakeBitLocker(ledger, BitLockerState.Unprotected),
            createSet: (v, s, l) => factory.Create(v, s, l),
            openData: (life, vol, dev, rec, scope) =>
            {
                kinds[rec.PartitionNumber] = "data";
                return new FakePartSource(life, new FileBlockSource(img));
            },
            openLive: (life, rec, scope) =>
            {
                kinds[rec.PartitionNumber] = "live";
                return new FakePartSource(life, new FileBlockSource(img));
            },
            new TestLogger());

        try
        {
            Assert.Equal("live", kinds[3]);
            Assert.Null(plan.Partitions[0].ChangeSource);
            Assert.Equal(0, factory.Creates); // no data volumes → no set at all
        }
        finally
        {
            DisposePlan(plan);
        }
    }

    [Fact]
    public void Plan_DuplicateVolume_RefusesDoubleImage()
    {
        var a = Rec(3, BasicType, "ntfs", 256L << 20, 512L << 20);
        var b = Rec(4, BasicType, "ntfs", 768L << 20, 512L << 20);
        var ledger = new List<string>();
        var factory = new FakeSetFactory(ledger);

        var ex = Assert.Throws<InvalidDataException>(() => DiskCapture.BuildPlanCore(0, Layout(a, b),
            findVolume: (d, off, len) => "C:", // both resolve to one volume
            bitlocker: new FakeBitLocker(ledger, BitLockerState.Unprotected),
            createSet: (v, s, l) => factory.Create(v, s, l),
            openData: (life, vol, dev, rec, scope) => throw new InvalidOperationException("unreachable"),
            openLive: (life, rec, scope) => throw new InvalidOperationException("unreachable"),
            new TestLogger()));

        Assert.Contains("same volume", ex.Message);
        Assert.Empty(ledger);
        Assert.Equal(0, factory.Creates);
    }

    [Fact]
    public void Lifetime_CompletesSet_WhenAllDataVolumesFullyRead()
    {
        var ledger = new List<string>();
        var set = new FakeSnapshotSet(ledger, new[] { "C:", "D:" });
        var life = new DiskCaptureLifetime(set, new List<BitLockerCaptureScope>(), new[] { "C:", "D:" });

        life.ReportFullRead("C:");
        Assert.Equal(0, set.Completes);
        life.ReportFullRead("D:");
        Assert.Equal(1, set.Completes);
        life.ReportFullRead("D:"); // duplicate report harmless
        Assert.Equal(1, set.Completes);
    }

    [Fact]
    public void Lifetime_LastRelease_TearsDown_SetBeforeScopes()
    {
        var ledger = new List<string>();
        var controller = new FakeBitLocker(ledger, BitLockerState.ProtectedUnlocked);
        var log = new TestLogger();
        var scopes = new List<BitLockerCaptureScope>
        {
            BitLockerCaptureScope.Arm("C:", controller, log),
            BitLockerCaptureScope.Arm("D:", controller, log),
        };
        var set = new FakeSnapshotSet(ledger, new[] { "C:", "D:" });
        var life = new DiskCaptureLifetime(set, scopes, new[] { "C:", "D:" });

        life.AddRef();
        life.AddRef();
        life.Release();
        Assert.DoesNotContain("set-dispose", ledger);
        life.Release();

        Assert.Equal("set-dispose", ledger[ledger.IndexOf("set-dispose")]);
        Assert.True(ledger.IndexOf("set-dispose") < ledger.IndexOf("resume:C:"));
        Assert.True(ledger.IndexOf("set-dispose") < ledger.IndexOf("resume:D:"));
    }

    [Fact]
    public void LiveCaptureSource_ReportsScopeState_AndReleases()
    {
        // LiveCaptureSource is handle-free (wraps any IBlockSource), so the
        // wrapper behavior itself is Linux-pinnable; only the inner
        // EspLiveSource needs Windows.
        var path = SeedFile("esp-live.img", 6);
        var ledger = new List<string>();
        var scope = BitLockerCaptureScope.Arm("S:", new FakeBitLocker(ledger, BitLockerState.ProtectedUnlocked), new TestLogger());
        var set = new FakeSnapshotSet(ledger, Array.Empty<string>());
        var life = new DiskCaptureLifetime(set, new List<BitLockerCaptureScope> { scope }, Array.Empty<string>());

        var src = new LiveCaptureSource(life, new FileBlockSource(path), scope);
        Assert.Equal(new FileInfo(path).Length, src.Length);
        Assert.Equal("Suspended", ((IBitLockerVolume)src).BitLockerState);
        Assert.Equal(new[] { KeyId }, ((IBitLockerVolume)src).BitLockerKeyIds);
        using (var s = src.OpenRead())
        {
            var back = new byte[src.Length];
            Assert.Equal(back.Length, s.Read(back, 0, back.Length));
            Assert.Equal(File.ReadAllBytes(path), back);
        }

        src.Dispose();
        Assert.Contains("set-dispose", ledger);
        Assert.Contains("resume:S:", ledger);
        src.Dispose(); // idempotent: no duplicate resume
        Assert.Single(ledger, m => m == "resume:S:");
    }

    [Fact]
    public void Plan_FeedsImageDisk()
    {
        // The plan the core builds must be consumable by the store path it
        // was designed for: per-partition manifests + sealed layout sidecar.
        var esp = Rec(1, EspType, "fat32", 1L << 20, 100L << 20);
        var msr = Rec(2, MsrType, "none", 105906176, 16777216, skipped: true, reason: "msr-reserved");
        var sys = Rec(3, BasicType, "ntfs", 256L << 20, 512L << 20);
        var espPath = SeedFile("ie-esp.img", 7);
        var sysPath = SeedFile("ie-sys.img", 8);
        var ledger = new List<string>();
        var factory = new FakeSetFactory(ledger);
        var storeRoot = Path.Combine(_dir, "store");
        var dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");

        var plan = DiskCapture.BuildPlanCore(0, Layout(esp, msr, sys),
            findVolume: (d, off, len) => off == esp.OffsetBytes ? "S:" : "C:",
            bitlocker: new FakeBitLocker(ledger, BitLockerState.Unprotected),
            createSet: (v, s, l) => factory.Create(v, s, l),
            openData: (life, vol, dev, rec, scope) => new FakePartSource(life, new FileBlockSource(sysPath)),
            openLive: (life, rec, scope) => new FakePartSource(life, new FileBlockSource(espPath)),
            new TestLogger());

        try
        {
            DiskLayout layout;
            using (var store = new Store(storeRoot, dbPath, 1, 3, new TestLogger()))
            {
                layout = store.ImageDisk(0, "disk0", new DiskImageOptions(), plan);
                Assert.Equal(3, layout.Partitions.Count);
                Assert.True(layout.Partitions[1].Skipped);
                Assert.Equal(2, layout.Generation);
                var outPath = Path.Combine(_dir, "sys-out.img");
                store.Export(layout.Partitions[2].SnapshotGeneration, outPath);
                Assert.Equal(File.ReadAllBytes(sysPath), File.ReadAllBytes(outPath));
            }
        }
        finally
        {
            DisposePlan(plan);
        }
    }
}
