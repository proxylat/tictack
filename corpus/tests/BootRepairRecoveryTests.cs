namespace Corpus;

// Item 5 tests: orchestrated layout-recreate + bootloader reinstall. All drive
// the fake runner (Linux-green); the real runner's Windows gate is pinned by
// the Linux-throws test, the on-box run is a VM step (see lane summary).
public sealed class BootRepairRecoveryTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public BootRepairRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_bootrepair_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static DiskLayout GoodLayout() => new()
    {
        SchemaVersion = 1,
        Generation = 3,
        DiskNumber = 0,
        DiskGuid = Guid.NewGuid().ToString("D"),
        DiskSizeBytes = 64L * Mib,
        SectorSizeBytes = 512,
        Style = "gpt",
        Partitions = new List<PartitionRecord>
        {
            new()
            {
                PartitionNumber = 1, Name = "EFI system partition",
                TypeGuid = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", UniqueGuid = Guid.NewGuid().ToString("D"),
                FirstLba = 2048, LastLba = 206847, OffsetBytes = Mib, LengthBytes = 260L * Mib,
                Filesystem = "fat32", PartitionKey = "disk0p1:esp",
            },
            new()
            {
                PartitionNumber = 2, Name = "Microsoft reserved partition",
                TypeGuid = "e3c9e316-0b5c-4db8-817d-f92df00215ae", UniqueGuid = Guid.NewGuid().ToString("D"),
                FirstLba = 206848, LastLba = 239615, OffsetBytes = 261L * Mib, LengthBytes = 16L * Mib,
                Filesystem = "none", Skipped = true, SkipReason = "msr-reserved", PartitionKey = "disk0p2:msr",
            },
            new()
            {
                PartitionNumber = 3, Name = "Basic data partition",
                TypeGuid = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", UniqueGuid = Guid.NewGuid().ToString("D"),
                FirstLba = 239616, LastLba = 251903, OffsetBytes = 277L * Mib, LengthBytes = 6L * Mib,
                Filesystem = "ntfs", PartitionKey = "disk0p3:os",
            },
        },
    };

    [Fact]
    public void Repair_Fake_RecreatesLayoutAndBootloader()
    {
        var log = new TestLogger();
        var fake = new FakeBootRepairRunner();

        Assert.Equal(0, BootRepairOrchestrator.Repair(GoodLayout(), fake, log));

        Assert.Contains(fake.Calls, c => c.StartsWith("ApplyLayout:", StringComparison.Ordinal));
        Assert.Contains("LocateVolumes", fake.Calls);
        Assert.Contains("FirmwareType", fake.Calls);
        Assert.Contains(fake.Calls, c => c.StartsWith("InstallBootloader:", StringComparison.Ordinal));
        Assert.Contains(fake.Calls, c => c.StartsWith("CheckBootFiles:", StringComparison.Ordinal));
        var output = string.Join("\n", log.Messages);
        Assert.Contains("boot files verified", output);
    }

    [Fact]
    public void Repair_RefusesMissingEsp_BeforeAnyCall()
    {
        var layout = GoodLayout();
        layout.Partitions.RemoveAt(0);
        var log = new TestLogger();
        var fake = new FakeBootRepairRunner();

        Assert.Equal(1, BootRepairOrchestrator.Repair(layout, fake, log));
        Assert.Empty(fake.Calls);
        Assert.Contains("ESP", string.Join("\n", log.Messages));
    }

    [Fact]
    public void Repair_RefusesLegacyFirmware_BeforeApply()
    {
        var log = new TestLogger();
        var fake = new FakeBootRepairRunner { Firmware = "Legacy" };

        Assert.Equal(1, BootRepairOrchestrator.Repair(GoodLayout(), fake, log));
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("ApplyLayout:", StringComparison.Ordinal));
        Assert.Contains("Legacy", string.Join("\n", log.Messages));
    }

    [Fact]
    public void Repair_RunnerFailure_Returns1()
    {
        var log = new TestLogger();
        var fake = new FakeBootRepairRunner { ThrowOnApply = true };

        Assert.Equal(1, BootRepairOrchestrator.Repair(GoodLayout(), fake, log));
        Assert.Contains("boot repair failed", string.Join("\n", log.Messages));
    }

    [Fact]
    public void Plan_ContainsDiskpartAndBcdboot()
    {
        var plan = BootRepairOrchestrator.Plan(GoodLayout());
        Assert.Contains("create partition efi", plan);
        Assert.Contains("bcdboot", plan);
    }

    [Fact]
    public void RestoreDisk_RepairBoot_Fake_Orchestrates()
    {
        // End-to-end through RestoreDisk with a file stand-in target: data
        // restores, then the fake repair runs (no manual printout needed).
        var espPath = Path.Combine(_dir, "esp.img");
        var sysPath = Path.Combine(_dir, "sys.img");
        File.WriteAllBytes(espPath, new byte[1 * Mib]);
        File.WriteAllBytes(sysPath, new byte[2 * Mib]);
        DiskLayout layout;
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger()))
        using (var esp = new FileBlockSource(espPath))
        using (var sys = new FileBlockSource(sysPath))
        {
            // Compact real-scale layout ( extents end at 219 MiB): ESP at the
            // 200 MiB floor, MSR 16 MiB, OS 2 MiB; FirstLba*512 == OffsetBytes.
            layout = store.ImageDisk(0, "disk0", new DiskImageOptions(), new DiskCapturePlan("guid-repair", 232L * Mib, 512,
                new List<PartitionImageInput>
                {
                    new(1, "EFI system partition", "c12a7328-f81f-11d2-ba4b-00a0c93ec93b",
                        Guid.NewGuid().ToString("D"), 2048, 411647, Mib, 200L * Mib, "fat32", false, null, esp, null),
                    new(2, "Microsoft reserved partition", "e3c9e316-0b5c-4db8-817d-f92df00215ae",
                        Guid.NewGuid().ToString("D"), 411648, 444415, 201L * Mib, 16L * Mib, "none", true, "msr-reserved", null, null),
                    new(3, "Basic data partition", "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
                        Guid.NewGuid().ToString("D"), 444416, 448511, 217L * Mib, 2L * Mib, "ntfs", false, null, sys, null),
                }));
        }

        // ESP source is 1 MiB but the layout claims 200 MiB: restore streams
        // the 1 MiB manifest and logs the length mismatch (Warn); verify is
        // skipped — this test pins the repair tail, not extent bytes.
        var target = Path.Combine(_dir, "disk.img");
        using (var f = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
            f.SetLength(232L * Mib);
        var log = new TestLogger();
        var fake = new FakeBootRepairRunner();
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, log))
        {
            var orch = new RestoreOrchestrator(store, log, path => new FileBlockTarget(path));
            using var t = new FileBlockTarget(target);
            var rc = orch.RestoreDisk(new DiskRestoreOptions(
                LayoutGen: layout.Generation, Target: target, StorePath: _storeRoot, IndexPath: _dbPath,
                ConfirmToken: BootDiskGuard.TargetToken(t.DisplayName, t.Capacity),
                AllowBootDisk: false, SkipVerify: true, DryRun: false, RepairBoot: true, BootRunner: fake));
            Assert.Equal(0, rc);
        }
        Assert.Contains(fake.Calls, c => c.StartsWith("ApplyLayout:", StringComparison.Ordinal));
        Assert.Contains("boot repair: boot files verified", string.Join("\n", log.Messages));
    }

    [Fact]
    public void Generate_NoFormat_SkipsFormatKeepsExactOffsets()
    {
        var script = DiskpartScript.Generate(GoodLayout(), formatPartitions: false);
        Assert.Contains("create partition efi size=260 offset=1024", script);
        Assert.Contains("create partition msr size=16 offset=267264", script);
        Assert.Contains("create partition primary size=6 offset=283648", script);
        Assert.DoesNotContain("format", script);
    }

    [Fact]
    public void Generate_Default_FormatsPrimaries()
    {
        var script = DiskpartScript.Generate(GoodLayout());
        Assert.Contains("format fs=fat32 quick", script);
        Assert.Contains("format fs=ntfs quick", script);
    }

    [Fact]
    public void Generate_NonAlignedRefusesLoud()
    {
        var oddLen = GoodLayout();
        oddLen.Partitions[2].LengthBytes = 6L * Mib + 512;
        var ex = Assert.Throws<InvalidDataException>(() => DiskpartScript.Generate(oddLen));
        Assert.Contains("MiB-exact", ex.Message);
        var oddOff = GoodLayout();
        oddOff.Partitions[2].OffsetBytes = 277L * Mib + 512;
        var ex2 = Assert.Throws<InvalidDataException>(() => DiskpartScript.Generate(oddOff));
        Assert.Contains("KiB-exact", ex2.Message);
    }

    // Shared event list pins the load-bearing order: diskpart clean
    // rewrites the table, so ApplyLayout MUST precede the first byte
    // streamed (streaming first would be destroyed by the recreate).
    private sealed class OrderRunner : IBootRepairRunner
    {
        private readonly List<string> _events;
        public OrderRunner(List<string> events) => _events = events;
        public void ApplyLayout(string diskpartScript) => _events.Add("apply");
        public (string WindowsDir, string EspDir) LocateVolumes() => ("D:\\Windows", "S:");
        public void InstallBootloader(string windowsDir, string espDir) { }
        public string FirmwareType() => "UEFI";
        public void CheckBootFiles(string espDir) { }
    }

    private sealed class OrderTarget : IBlockTarget
    {
        private readonly FileBlockTarget _inner;
        private readonly List<string> _events;
        public OrderTarget(string path, List<string> events) { _inner = new FileBlockTarget(path); _events = events; }
        public string DisplayName => _inner.DisplayName;
        public long Capacity => _inner.Capacity;
        public Stream OpenWrite() { _events.Add("openwrite"); return _inner.OpenWrite(); }
        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public void RestoreDisk_RepairBoot_AppliesLayoutBeforeFirstByte()
    {
        var espPath = Path.Combine(_dir, "oesp.img");
        var sysPath = Path.Combine(_dir, "osys.img");
        File.WriteAllBytes(espPath, new byte[1 * Mib]);
        File.WriteAllBytes(sysPath, new byte[2 * Mib]);
        DiskLayout layout;
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger()))
        using (var esp = new FileBlockSource(espPath))
        using (var sys = new FileBlockSource(sysPath))
        {
            layout = store.ImageDisk(0, "disk0", new DiskImageOptions(), new DiskCapturePlan("guid-order", 232L * Mib, 512,
                new List<PartitionImageInput>
                {
                    new(1, "EFI system partition", "c12a7328-f81f-11d2-ba4b-00a0c93ec93b",
                        Guid.NewGuid().ToString("D"), 2048, 411647, Mib, 200L * Mib, "fat32", false, null, esp, null),
                    new(2, "Microsoft reserved partition", "e3c9e316-0b5c-4db8-817d-f92df00215ae",
                        Guid.NewGuid().ToString("D"), 411648, 444415, 201L * Mib, 16L * Mib, "none", true, "msr-reserved", null, null),
                    new(3, "Basic data partition", "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
                        Guid.NewGuid().ToString("D"), 444416, 448511, 217L * Mib, 2L * Mib, "ntfs", false, null, sys, null),
                }));
        }

        var target = Path.Combine(_dir, "odisk.img");
        using (var f = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
            f.SetLength(232L * Mib);
        var log = new TestLogger();
        var events = new List<string>();
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, log))
        {
            var orch = new RestoreOrchestrator(store, log, path => new OrderTarget(path, events));
            using var t = new FileBlockTarget(target);
            var rc = orch.RestoreDisk(new DiskRestoreOptions(
                LayoutGen: layout.Generation, Target: target, StorePath: _storeRoot, IndexPath: _dbPath,
                ConfirmToken: BootDiskGuard.TargetToken(t.DisplayName, t.Capacity),
                AllowBootDisk: false, SkipVerify: true, DryRun: false, RepairBoot: true, BootRunner: new OrderRunner(events)));
            Assert.Equal(0, rc);
        }
        var apply = events.IndexOf("apply");
        var firstWrite = events.FindIndex(e => e == "openwrite");
        Assert.True(apply >= 0, "ApplyLayout never ran: " + string.Join(",", events));
        Assert.True(firstWrite >= 0, "no bytes streamed: " + string.Join(",", events));
        Assert.True(apply < firstWrite, "layout applied after streaming started: " + string.Join(",", events));
    }

    [Fact]
    public void WindowsRunner_Linux_ThrowsPlatform()
    {
        if (OperatingSystem.IsWindows())
            return; // On-box run is a VM step, not CI.
        var runner = new WindowsBootRepairRunner(new TestLogger());
        Assert.Throws<PlatformNotSupportedException>(() => runner.ApplyLayout("select disk 0"));
        Assert.Throws<PlatformNotSupportedException>(() => runner.LocateVolumes());
        Assert.Throws<PlatformNotSupportedException>(() => runner.InstallBootloader("D:\\Windows", "S:"));
        Assert.Throws<PlatformNotSupportedException>(() => runner.FirmwareType());
        Assert.Throws<PlatformNotSupportedException>(() => runner.CheckBootFiles("S:"));
    }
}
