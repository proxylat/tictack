namespace Corpus;

using System.Text;
using DiscUtils.Vhdx;

// Whole-disk rung, store side (spec §7 tests 6–10). Lane A owns tests 1–5 in
// THIS file — append those methods only, never reorder/edit these.
public sealed class DiskLayoutTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public DiskLayoutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_disk_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Store NewStore(TestLogger? log = null) =>
        new(_storeRoot, _dbPath, 1, 3, log ?? new TestLogger());

    private SnapshotManifest LoadGen(int gen)
    {
        var manDir = Path.Combine(_storeRoot, "manifests");
        var paths = Directory.GetFiles(manDir, $"{gen:D6}-*.json");
        Assert.Single(paths);
        return ManifestStore.Load(paths[0], ChunkId.EnsureKey(_storeRoot));
    }

    private static string NewGuid() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    private static byte[] RandomChunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static void WriteFile(string path, params byte[][] chunks)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var c in chunks)
            f.Write(c, 0, c.Length);
    }

    private static PartitionImageInput Part(int number, string uniqueGuid, string file, string filesystem,
        IBlockSource? source, IChangeSource? change, bool skipped = false, string? skipReason = null)
    {
        var lengthBytes = new FileInfo(file).Length;
        var firstLba = 2048L + (number - 1) * 8192L;
        return new PartitionImageInput(number, $"part{number}", "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
            uniqueGuid, firstLba, firstLba + lengthBytes / 512 - 1, firstLba * 512, lengthBytes,
            filesystem, skipped, skipReason, source, change);
    }

    private static DiskLayout SampleLayout(int generation, int diskNumber = 0) => new()
    {
        SchemaVersion = 1,
        Generation = generation,
        DiskNumber = diskNumber,
        DiskGuid = NewGuid(),
        DiskSizeBytes = 1L << 40,
        SectorSizeBytes = 512,
        Style = "gpt",
        Partitions = new List<PartitionRecord>
        {
            new()
            {
                PartitionNumber = 1, Name = "EFI system partition",
                TypeGuid = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", UniqueGuid = NewGuid(),
                FirstLba = 2048, LastLba = 206847, OffsetBytes = 1048576, LengthBytes = 104857600,
                Filesystem = "fat32", PartitionKey = "disk0p1:esp",
            },
            new()
            {
                PartitionNumber = 2, Name = "Microsoft reserved partition",
                TypeGuid = "e3c9e316-0b5c-4db8-817d-f92df00215ae", UniqueGuid = NewGuid(),
                FirstLba = 206848, LastLba = 239615, OffsetBytes = 105906176, LengthBytes = 16777216,
                Filesystem = "none", Skipped = true, SkipReason = "msr-reserved", PartitionKey = "disk0p2:msr",
            },
        },
    };

    [Fact]
    public void LayoutStore_RoundTripHmac()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var layoutsDir = Path.Combine(_dir, "layouts");
        var layout = SampleLayout(7);

        var path = LayoutStore.Save(layoutsDir, layout, key);
        Assert.Equal(Path.Combine(layoutsDir, "000007-disk0.json"), path);

        var loaded = LayoutStore.Load(path, key);
        Assert.Equal(layout.Generation, loaded.Generation);
        Assert.Equal(layout.DiskNumber, loaded.DiskNumber);
        Assert.Equal(layout.DiskGuid, loaded.DiskGuid);
        Assert.Equal(layout.DiskSizeBytes, loaded.DiskSizeBytes);
        Assert.Equal(layout.SectorSizeBytes, loaded.SectorSizeBytes);
        Assert.Equal(layout.Style, loaded.Style);
        Assert.Equal(layout.Hmac, loaded.Hmac);
        Assert.Equal(layout.Partitions.Count, loaded.Partitions.Count);
        for (var i = 0; i < layout.Partitions.Count; i++)
        {
            var want = layout.Partitions[i];
            var got = loaded.Partitions[i];
            Assert.Equal(want.PartitionNumber, got.PartitionNumber);
            Assert.Equal(want.Name, got.Name);
            Assert.Equal(want.TypeGuid, got.TypeGuid);
            Assert.Equal(want.UniqueGuid, got.UniqueGuid);
            Assert.Equal(want.FirstLba, got.FirstLba);
            Assert.Equal(want.LastLba, got.LastLba);
            Assert.Equal(want.OffsetBytes, got.OffsetBytes);
            Assert.Equal(want.LengthBytes, got.LengthBytes);
            Assert.Equal(want.Filesystem, got.Filesystem);
            Assert.Equal(want.Skipped, got.Skipped);
            Assert.Equal(want.SkipReason, got.SkipReason);
            Assert.Equal(want.SnapshotGeneration, got.SnapshotGeneration);
            Assert.Equal(want.PartitionKey, got.PartitionKey);
        }

        // One flipped byte in a value breaks the seal (flip inside a value,
        // not a property name: renaming a default-valued property parses back
        // to the same object, which correctly still verifies).
        var json = File.ReadAllText(path);
        var nameIdx = json.IndexOf("EFI system partition", StringComparison.Ordinal);
        var tampered = json[..nameIdx] + "X" + json[(nameIdx + 1)..];
        File.WriteAllText(path, tampered);
        Assert.Throws<InvalidDataException>(() => LayoutStore.Load(path, key));
    }

    [Fact]
    public void LayoutStore_SchemaRejectsFuture()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var layoutsDir = Path.Combine(_dir, "layouts");
        var layout = SampleLayout(1);
        layout.SchemaVersion = 99;
        var path = LayoutStore.Save(layoutsDir, layout, key);
        Assert.Throws<InvalidDataException>(() => LayoutStore.Load(path, key));
    }

    [Fact]
    public void DiskImage_FakePartitions_IndependentChains()
    {
        var espPath = Path.Combine(_dir, "esp.img");
        var sysPath = Path.Combine(_dir, "sys.img");
        var dataPath = Path.Combine(_dir, "data.img");
        WriteFile(espPath, RandomChunk(11));
        WriteFile(sysPath, new byte[Mib], RandomChunk(12), new byte[Mib]);
        WriteFile(dataPath, RandomChunk(13), RandomChunk(14));
        var espGuid = NewGuid();
        var sysGuid = NewGuid();
        var dataGuid = NewGuid();
        var sysKey = DiskImager.PartitionKey(0, 2, sysGuid);

        DiskCapturePlan BuildPlan(IBlockSource esp, IBlockSource sys, IBlockSource data, IChangeSource? ntfsChange) =>
            new("disk-guid-8", 1L << 40, 512, new List<PartitionImageInput>
            {
                Part(1, espGuid, espPath, "fat32", esp, null), // no journal → always full
                Part(2, sysGuid, sysPath, "ntfs", sys, ntfsChange),
                Part(3, dataGuid, dataPath, "ntfs", data, ntfsChange),
            });

        using var store = NewStore();
        using (var esp1 = new FileBlockSource(espPath))
        using (var sys1 = new FileBlockSource(sysPath))
        using (var data1 = new FileBlockSource(dataPath))
        {
            var layout1 = store.ImageDisk(0, "disk0", new DiskImageOptions(), BuildPlan(esp1, sys1, data1, null));
            Assert.Equal(3, layout1.Generation);
            Assert.Equal(new[] { 1, 2, 3 }, layout1.Partitions.ConvertAll(p => p.SnapshotGeneration).ToArray());
            Assert.Equal(DiskImager.PartitionKey(0, 1, espGuid), layout1.Partitions[0].PartitionKey);
            Assert.Equal(sysKey, layout1.Partitions[1].PartitionKey);
        }

        // Mutate the middle partition's chunk 1 only.
        var mutated = RandomChunk(999);
        using (var f = new FileStream(sysPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            f.Seek(Mib, SeekOrigin.Begin);
            f.Write(mutated, 0, mutated.Length);
        }
        var fake = new FakeChangeSource((parent, _, _) => parent.PartitionKey == sysKey
            ? new IncrementalPlan(new HashSet<long> { 1 }, "fake:2")
            : new IncrementalPlan(new HashSet<long>(), "fake:2"));

        using var espCount = new CountingBlockSource(espPath, Mib);
        using var sysCount = new CountingBlockSource(sysPath, Mib);
        using var dataCount = new CountingBlockSource(dataPath, Mib);
        var layout2 = store.ImageDisk(0, "disk0", new DiskImageOptions(Incremental: true),
            BuildPlan(espCount, sysCount, dataCount, fake));

        Assert.Equal(6, layout2.Generation);
        Assert.Equal(new[] { 4, 5, 6 }, layout2.Partitions.ConvertAll(p => p.SnapshotGeneration).ToArray());

        var espChild = LoadGen(4);
        Assert.Equal(0, espChild.ParentGeneration); // FAT32: null cursor → full fallback
        Assert.Null(espChild.ChangeCursor);

        var sysChild = LoadGen(5);
        Assert.Equal(2, sysChild.ParentGeneration);
        Assert.Single(sysCount.ReadChunks);
        Assert.Contains(1L, sysCount.ReadChunks);

        var dataChild = LoadGen(6);
        Assert.Equal(3, dataChild.ParentGeneration);
        Assert.Empty(dataCount.ReadChunks); // clean: zero reads, digest reuse

        // Layouts live sealed beside the store, never inside manifests/.
        var layoutsFile = Path.Combine(_storeRoot, "layouts", "000006-disk0.json");
        Assert.True(File.Exists(layoutsFile));
        var reloaded = LayoutStore.Load(layoutsFile, ChunkId.EnsureKey(_storeRoot));
        Assert.Equal(6, reloaded.Generation);
        Assert.Equal(3, reloaded.Partitions.Count);
        Assert.Equal(6, Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "*.json").Length);

        // The chained child restores byte-identical.
        var outPath = Path.Combine(_dir, "sys-out.img");
        store.Export(5, outPath);
        Assert.Equal(File.ReadAllBytes(sysPath), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(5));
    }

    [Fact]
    public void TableChange_ResizedPartition_FullOnly()
    {
        var aPath = Path.Combine(_dir, "a.img");
        var bPath = Path.Combine(_dir, "b.img");
        WriteFile(aPath, RandomChunk(21), RandomChunk(22));
        WriteFile(bPath, RandomChunk(23), RandomChunk(24));
        var aGuid = NewGuid();
        var bGuid = NewGuid();

        using var store = NewStore();
        using (var a1 = new FileBlockSource(aPath))
        using (var b1 = new FileBlockSource(bPath))
        {
            var plan1 = new DiskCapturePlan("disk-guid-9", 1L << 40, 512, new List<PartitionImageInput>
            {
                Part(1, aGuid, aPath, "ntfs", a1, null),
                Part(2, bGuid, bPath, "ntfs", b1, null),
            });
            store.ImageDisk(0, "disk0", new DiskImageOptions(), plan1);
        }

        // Same GUID, new length → full for that partition only (length gate).
        WriteFile(aPath, RandomChunk(21), RandomChunk(22), RandomChunk(25));
        var clean = new FakeChangeSource((_, _, _) => new IncrementalPlan(new HashSet<long>(), "fake:2"));
        DiskCapturePlan plan2;
        using (var a2 = new FileBlockSource(aPath))
        using (var b2 = new FileBlockSource(bPath))
        {
            plan2 = new DiskCapturePlan("disk-guid-9", 1L << 40, 512, new List<PartitionImageInput>
            {
                Part(1, aGuid, aPath, "ntfs", a2, clean),
                Part(2, bGuid, bPath, "ntfs", b2, clean),
            });
            var layout2 = store.ImageDisk(0, "disk0", new DiskImageOptions(Incremental: true), plan2);
            Assert.Equal(4, layout2.Generation);

            var aChild = LoadGen(3);
            Assert.Equal(0, aChild.ParentGeneration);
            Assert.Equal(3, aChild.Chunks.Count);

            var bChild = LoadGen(4);
            Assert.Equal(2, bChild.ParentGeneration); // sibling chain intact
        }
        var outPath = Path.Combine(_dir, "a-out.img");
        store.Export(3, outPath);
        Assert.Equal(File.ReadAllBytes(aPath), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void TableChange_AddedRemoved()
    {
        var aPath = Path.Combine(_dir, "a.img");
        var bPath = Path.Combine(_dir, "b.img");
        var cPath = Path.Combine(_dir, "c.img");
        WriteFile(aPath, RandomChunk(31), RandomChunk(32));
        WriteFile(bPath, RandomChunk(33), RandomChunk(34));
        WriteFile(cPath, RandomChunk(35));
        var aGuid = NewGuid();
        var bGuid = NewGuid();
        var cGuid = NewGuid();
        var aKey = DiskImager.PartitionKey(0, 1, aGuid);

        using var store = NewStore();
        using (var a1 = new FileBlockSource(aPath))
        using (var b1 = new FileBlockSource(bPath))
        {
            var plan1 = new DiskCapturePlan("disk-guid-10", 1L << 40, 512, new List<PartitionImageInput>
            {
                Part(1, aGuid, aPath, "ntfs", a1, null),
                Part(2, bGuid, bPath, "ntfs", b1, null),
            });
            store.ImageDisk(0, "disk0", new DiskImageOptions(), plan1);
        }
        var manDir = Path.Combine(_storeRoot, "manifests");
        var aManifestPath = Assert.Single(Directory.GetFiles(manDir, "000001-*.json"));

        // A removed, C added (C takes the freed number; B's number is stable
        // so its key still resolves — a renumbered GUID would re-key and go
        // full per the remove+add rule).
        var clean = new FakeChangeSource((_, _, _) => new IncrementalPlan(new HashSet<long>(), "fake:2"));
        using (var b2 = new FileBlockSource(bPath))
        using (var c2 = new FileBlockSource(cPath))
        {
            var plan2 = new DiskCapturePlan("disk-guid-10", 1L << 40, 512, new List<PartitionImageInput>
            {
                Part(1, cGuid, cPath, "ntfs", c2, clean),
                Part(2, bGuid, bPath, "ntfs", b2, clean),
            });
            var layout2 = store.ImageDisk(0, "disk0", new DiskImageOptions(Incremental: true), plan2);

            Assert.Equal(2, layout2.Partitions.Count);
            Assert.DoesNotContain(layout2.Partitions, p => p.PartitionKey == aKey);
            Assert.Equal(4, layout2.Generation);

            var cChild = LoadGen(3);
            Assert.Equal(0, cChild.ParentGeneration); // added → full
            var bChild = LoadGen(4);
            Assert.Equal(2, bChild.ParentGeneration);
        }

        // Removed partition: absent from the new layout, old manifests untouched.
        Assert.True(File.Exists(aManifestPath));
        Assert.True(store.Verify(1));
    }

    // Capture-enumeration tests (spec §1.1, lane A tests 1–5). All pin pure
    // parsers — Linux-runnable, no disk I/O.
    private static readonly Guid EspTypeGuid = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    private static readonly Guid MsrTypeGuid = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    private static readonly Guid BasicTypeGuid = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    private static readonly Guid RecoveryTypeGuid = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
    private static readonly Guid LdmTypeGuid = new("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3");

    private static byte[] BuildEntry(Guid type, Guid unique, long first, long last, string name)
    {
        var e = new byte[128];
        Buffer.BlockCopy(type.ToByteArray(), 0, e, 0, 16);
        Buffer.BlockCopy(unique.ToByteArray(), 0, e, 16, 16);
        Buffer.BlockCopy(BitConverter.GetBytes(first), 0, e, 32, 8);
        Buffer.BlockCopy(BitConverter.GetBytes(last), 0, e, 40, 8);
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        Buffer.BlockCopy(nameBytes, 0, e, 56, Math.Min(nameBytes.Length, 72));
        return e;
    }

    private static byte[] BuildHeader(Guid diskGuid, int entryCount, byte[] entriesBytes)
    {
        var h = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("EFI PART").CopyTo(h, 0);
        Buffer.BlockCopy(BitConverter.GetBytes(0x00010000u), 0, h, 8, 4); // revision
        Buffer.BlockCopy(BitConverter.GetBytes(92u), 0, h, 12, 4); // header size
        Buffer.BlockCopy(BitConverter.GetBytes(1L), 0, h, 24, 8); // MyLBA
        Buffer.BlockCopy(diskGuid.ToByteArray(), 0, h, 56, 16);
        Buffer.BlockCopy(BitConverter.GetBytes(2L), 0, h, 72, 8); // entries LBA
        Buffer.BlockCopy(BitConverter.GetBytes(entryCount), 0, h, 80, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(128), 0, h, 84, 4); // entry size
        Buffer.BlockCopy(BitConverter.GetBytes(PartitionTable.Crc32(entriesBytes)), 0, h, 88, 4);
        var crc = new byte[92];
        Buffer.BlockCopy(h, 0, crc, 0, 92);
        Buffer.BlockCopy(BitConverter.GetBytes(PartitionTable.Crc32(crc)), 0, h, 16, 4);
        return h;
    }

    private static byte[] BuildProtectiveMbr()
    {
        var mbr = new byte[512];
        mbr[446 + 4] = 0xEE;
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        return mbr;
    }

    [Fact]
    public void GptParser_GoldenParses()
    {
        var espGuid = Guid.NewGuid();
        var msrGuid = Guid.NewGuid();
        var basicGuid = Guid.NewGuid();
        var recGuid = Guid.NewGuid();
        var entries = BuildEntry(EspTypeGuid, espGuid, 2048, 206847, "EFI system partition")
            .Concat(BuildEntry(MsrTypeGuid, msrGuid, 206848, 239615, "Microsoft reserved partition"))
            .Concat(BuildEntry(BasicTypeGuid, basicGuid, 239616, 500000, "Basic data partition"))
            .Concat(BuildEntry(RecoveryTypeGuid, recGuid, 500001, 600000, "Recovery"))
            .Concat(new byte[128]) // unused slot: free space, omitted
            .ToArray();
        PartitionTable.CheckHeader(BuildHeader(Guid.NewGuid(), 5, entries));
        PartitionTable.CheckProtectiveMbr(BuildProtectiveMbr());

        var parts = PartitionTable.ParseEntries(entries, 5);

        Assert.Equal(4, parts.Count);
        var esp = parts[0];
        Assert.Equal(1, esp.PartitionNumber);
        Assert.Equal("EFI system partition", esp.Name);
        Assert.Equal("c12a7328-f81f-11d2-ba4b-00a0c93ec93b", esp.TypeGuid);
        Assert.Equal(espGuid.ToString("d"), esp.UniqueGuid);
        Assert.Equal(2048, esp.FirstLba);
        Assert.Equal(206847, esp.LastLba);
        Assert.Equal(1048576, esp.OffsetBytes);
        Assert.Equal(104857600, esp.LengthBytes);
        Assert.Equal("fat32", esp.Filesystem);
        Assert.False(esp.Skipped);
        var msr = parts[1];
        Assert.Equal(2, msr.PartitionNumber);
        Assert.True(msr.Skipped);
        Assert.Equal("msr-reserved", msr.SkipReason);
        Assert.Equal(105906176, msr.OffsetBytes);
        Assert.Equal(16777216, msr.LengthBytes);
        Assert.Equal("none", msr.Filesystem);
        Assert.Equal(3, parts[2].PartitionNumber);
        Assert.Equal("unknown", parts[2].Filesystem);
        Assert.Equal(4, parts[3].PartitionNumber);
    }

    [Fact]
    public void GptParser_BadCrc_Throws()
    {
        var entries = BuildEntry(BasicTypeGuid, Guid.NewGuid(), 2048, 4095, "Basic data partition");
        var header = BuildHeader(Guid.NewGuid(), 1, entries);
        header[40] ^= 0xFF; // flip a covered byte after sealing
        Assert.Throws<InvalidDataException>(() => PartitionTable.CheckHeader(header));
    }

    [Fact]
    public void GptParser_MbrDisk_Throws()
    {
        var mbr = new byte[512];
        mbr[446 + 4] = 0x07; // NTFS, no 0xEE anywhere
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        var ex = Assert.Throws<InvalidDataException>(() => PartitionTable.CheckProtectiveMbr(mbr));
        Assert.Contains("mbr", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gpt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DynamicDisk_Refuses()
    {
        var entries = BuildEntry(BasicTypeGuid, Guid.NewGuid(), 2048, 4095, "Basic data partition")
            .Concat(BuildEntry(LdmTypeGuid, Guid.NewGuid(), 4096, 8191, "LDM metadata"))
            .ToArray();
        var ex = Assert.Throws<InvalidDataException>(() => PartitionTable.ParseEntries(entries, 2));
        Assert.Contains("ldm", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MsrPartition_SkippedWithRange()
    {
        var unique = Guid.NewGuid();
        var parts = PartitionTable.ParseEntries(
            BuildEntry(MsrTypeGuid, unique, 206848, 239615, "Microsoft reserved partition"), 1);
        var msr = Assert.Single(parts);
        Assert.True(msr.Skipped);
        Assert.Equal("msr-reserved", msr.SkipReason);
        Assert.Equal(unique.ToString("d"), msr.UniqueGuid);
        Assert.Equal(206848, msr.FirstLba);
        Assert.Equal(239615, msr.LastLba);
        Assert.Equal(105906176, msr.OffsetBytes);
        Assert.Equal(16777216, msr.LengthBytes);
    }

    [Fact]
    public void MsrGuid_MatchesLiveDisk_NotTestConst()
    {
        // Anti-typo pin: the literal GUID from a live disk0 (VM-proven)
        // must skip even if the shared fixture const ever drifts again.
        var parts = PartitionTable.ParseEntries(
            BuildEntry(new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae"), Guid.NewGuid(), 206848, 239615, "Microsoft reserved partition"), 1);
        var msr = Assert.Single(parts);
        Assert.True(msr.Skipped);
        Assert.Equal("msr-reserved", msr.SkipReason);
        Assert.Equal("none", msr.Filesystem);
    }

    [Fact]
    public void LdmGuid_MatchesUefiSpec_NotTestConst()
    {
        // Same pin for the dynamic-disk tripwire (spec §5.3.3: 85D2).
        var entries = BuildEntry(new Guid("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3"), Guid.NewGuid(), 4096, 8191, "LDM metadata");
        var ex = Assert.Throws<InvalidDataException>(() => PartitionTable.ParseEntries(entries, 1));
        Assert.Contains("ldm", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3, "NTFS    ", "ntfs")]
    [InlineData(3, "EXFAT   ", "exfat")]
    [InlineData(82, "FAT32   ", "fat32")]
    [InlineData(54, "FAT12   ", "fat12")]
    [InlineData(54, "FAT16   ", "fat16")]
    public void SniffFilesystem_NamesKnownVbrs(int offset, string magic, string expected)
    {
        var sector = new byte[512];
        Encoding.ASCII.GetBytes(magic, 0, magic.Length, sector, offset);
        Assert.Equal(expected, PartitionTable.SniffFilesystem(sector));
    }

    [Theory]
    [InlineData(3, "NTTFS   ")]   // near-miss at the NTFS offset
    [InlineData(4, "NTFS    ")]   // right magic, wrong offset
    [InlineData(3, "-FVE-FS-")]   // BitLocker: opaque, never guessed
    public void SniffFilesystem_Unknown_StaysUnknown(int offset, string magic)
    {
        var sector = new byte[512];
        Encoding.ASCII.GetBytes(magic, 0, magic.Length, sector, offset);
        Assert.Equal("unknown", PartitionTable.SniffFilesystem(sector));
    }

    [Fact]
    public void SniffFilesystem_ShortSector_StaysUnknown()
    {
        Assert.Equal("unknown", PartitionTable.SniffFilesystem(new byte[64]));
    }

    // Disk-restore tests (spec §7, lane D tests 12–15 + 17). All drive
    // RestoreDisk through FileBlockTarget file stand-ins (a prefilled .img
    // plays the disk); the MSR gap pins skipped-never-written.
    private sealed class ListProgress : IProgress<long>
    {
        public readonly List<long> Values = new();
        public void Report(long value) { lock (Values) Values.Add(value); }
    }

    private static void PreFill(string path, long length, byte fill = 0xAA)
    {
        var buf = new byte[Mib];
        Array.Fill(buf, fill);
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        for (long left = length; left > 0;)
        {
            var n = (int)Math.Min(buf.Length, left);
            f.Write(buf, 0, n);
            left -= n;
        }
    }

    private static string DiskTokenFor(string targetPath)
    {
        using var t = new FileBlockTarget(targetPath);
        return BootDiskGuard.TargetToken(t.DisplayName, t.Capacity);
    }

    private static int RunDiskRestore(Store store, TestLogger log, DiskRestoreOptions opts, IProgress<long>? progress = null)
    {
        var orch = new RestoreOrchestrator(store, log, path => new FileBlockTarget(path));
        return progress is null ? orch.RestoreDisk(opts) : orch.RestoreDisk(opts, progress);
    }

    private static void AssertRangeFill(byte[] got, long from, long to, byte fill)
    {
        for (var i = from; i < to; i++)
        {
            if (got[(int)i] != fill)
                Assert.Fail($"byte {i}: want 0x{fill:X2}, got 0x{got[(int)i]:X2}");
        }
    }

    [Fact]
    public void RestoreDisk_ExtentWindows_Exact()
    {
        // 3 extents + MSR gap → file restore: extents byte-match sources,
        // gaps keep the 0xAA prefill (skipped extents never written).
        const long diskSize = 32L * Mib;
        var aPath = Path.Combine(_dir, "ra.img");
        var bPath = Path.Combine(_dir, "rb.img");
        var cPath = Path.Combine(_dir, "rc.img");
        WriteFile(aPath, RandomChunk(501));
        WriteFile(bPath, RandomChunk(502), RandomChunk(503));
        WriteFile(cPath, RandomChunk(504));
        DiskLayout layout;
        using (var store = NewStore())
        using (var a = new FileBlockSource(aPath))
        using (var b = new FileBlockSource(bPath))
        using (var c = new FileBlockSource(cPath))
        {
            const long msrFirst = 2048L + 8192L;
            layout = store.ImageDisk(0, "disk0", new DiskImageOptions(), new DiskCapturePlan("disk-guid-restore", diskSize, 512,
                new List<PartitionImageInput>
                {
                    Part(1, NewGuid(), aPath, "fat32", a, null),
                    new PartitionImageInput(2, "Microsoft reserved partition", "e3c9e316-0b5c-4db8-817d-f92df00215ae",
                        NewGuid(), msrFirst, msrFirst + 32 - 1, msrFirst * 512, 32 * 512,
                        "none", true, "msr-reserved", null, null),
                    Part(3, NewGuid(), bPath, "ntfs", b, null),
                    Part(4, NewGuid(), cPath, "ntfs", c, null),
                }));
        }
        Assert.Equal(4, layout.Partitions.Count);
        Assert.True(layout.Partitions[1].Skipped);

        var target = Path.Combine(_dir, "disk.img");
        PreFill(target, diskSize);
        var log = new TestLogger();
        using (var store = NewStore(log))
        {
            Assert.Equal(0, RunDiskRestore(store, log, new DiskRestoreOptions(
                LayoutGen: layout.Generation, Target: target, StorePath: _storeRoot, IndexPath: _dbPath,
                ConfirmToken: DiskTokenFor(target), AllowBootDisk: false, SkipVerify: false, DryRun: false)));
        }

        var got = File.ReadAllBytes(target);
        Assert.Equal(diskSize, got.Length);
        var esp = layout.Partitions[0];
        var msr = layout.Partitions[1];
        var sys = layout.Partitions[2];
        var data = layout.Partitions[3];
        Assert.Equal(File.ReadAllBytes(aPath), got.AsSpan((int)esp.OffsetBytes, (int)esp.LengthBytes).ToArray());
        Assert.Equal(File.ReadAllBytes(bPath), got.AsSpan((int)sys.OffsetBytes, (int)sys.LengthBytes).ToArray());
        Assert.Equal(File.ReadAllBytes(cPath), got.AsSpan((int)data.OffsetBytes, (int)data.LengthBytes).ToArray());
        AssertRangeFill(got, 0, esp.OffsetBytes, 0xAA);
        AssertRangeFill(got, msr.OffsetBytes, msr.OffsetBytes + msr.LengthBytes, 0xAA);
        AssertRangeFill(got, data.OffsetBytes + data.LengthBytes, diskSize, 0xAA);
    }

    [Fact]
    public void RestoreDisk_SparseHole_PrefillSurvives_WarnsAndPasses()
    {
        // Free space is don't-care: holes seek (never written — thin
        // targets stay thin), and stale gap bytes on a reused target warn
        // instead of failing. Data chunks stay byte-strict.
        var vol = Path.Combine(_dir, "sparse-vol.img");
        WriteFile(vol, RandomChunk(42), RandomChunk(43), RandomChunk(44), RandomChunk(45), RandomChunk(46));
        DiskLayout layout;
        using (var store = NewStore())
        using (var sparse = new FakeSparseSource(vol, new (long Start, long Count)[] { (256, 256), (768, 512) }))
        {
            layout = store.ImageDisk(0, "disk0", new DiskImageOptions(), new DiskCapturePlan("disk-guid-sparse", 8L * Mib, 512,
                new List<PartitionImageInput> { Part(1, NewGuid(), vol, "ntfs", sparse, null) }));
        }
        var part = layout.Partitions[0];
        var target = Path.Combine(_dir, "sparse-disk.img");
        PreFill(target, 8L * Mib);
        var log = new TestLogger();
        using (var store = NewStore(log))
        {
            Assert.Equal(0, RunDiskRestore(store, log, new DiskRestoreOptions(
                LayoutGen: layout.Generation, Target: target, StorePath: _storeRoot, IndexPath: _dbPath,
                ConfirmToken: DiskTokenFor(target), AllowBootDisk: false, SkipVerify: false, DryRun: false)));
        }
        var got = File.ReadAllBytes(target);
        var want = File.ReadAllBytes(vol);
        // Runs cover chunks 1,3,4 — chunks 0+2 are holes: prefill survives.
        AssertRangeFill(got, part.OffsetBytes + 0L * Mib, part.OffsetBytes + 1L * Mib, 0xAA);
        AssertRangeFill(got, part.OffsetBytes + 2L * Mib, part.OffsetBytes + 3L * Mib, 0xAA);
        Assert.Equal(want.AsSpan(Mib, Mib).ToArray(), got.AsSpan((int)part.OffsetBytes + Mib, Mib).ToArray());
        Assert.Contains("stale bytes", string.Join("\n", log.Messages));
    }

    [Fact]
    public void RestoreDisk_ZeroChunk_PrefillSurvives_FailsStrict()
    {
        // Contrast: zero chunks are DATA (hash-verified), not holes. Seeked
        // like holes on write, but a dirty target fails strict — the
        // don't-care rule covers free space only, never stored bytes.
        var vol = Path.Combine(_dir, "zero-vol.img");
        WriteFile(vol, new byte[Mib], new byte[Mib]);
        DiskLayout layout;
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol))
        {
            layout = store.ImageDisk(0, "disk0", new DiskImageOptions(), new DiskCapturePlan("disk-guid-zero", 8L * Mib, 512,
                new List<PartitionImageInput> { Part(1, NewGuid(), vol, "ntfs", source, null) }));
        }
        // Premise pin: dense zeros are DATA, not holes.
        var m = LoadGen(layout.Partitions[0].SnapshotGeneration);
        Assert.Equal(2, m.Chunks.Count);
        Assert.All(m.Chunks, c => Assert.False(c.IsHole));
        var part = layout.Partitions[0];
        var target = Path.Combine(_dir, "zero-disk.img");
        PreFill(target, 8L * Mib);
        var log = new TestLogger();
        using (var store = NewStore(log))
        {
            Assert.Equal(1, RunDiskRestore(store, log, new DiskRestoreOptions(
                LayoutGen: layout.Generation, Target: target, StorePath: _storeRoot, IndexPath: _dbPath,
                ConfirmToken: DiskTokenFor(target), AllowBootDisk: false, SkipVerify: false, DryRun: false)));
        }
        var output = string.Join("\n", log.Messages);
        Assert.Contains("corrupt ranges", output);
        Assert.Contains("do not boot", output);
        AssertRangeFill(File.ReadAllBytes(target), part.OffsetBytes, part.OffsetBytes + part.LengthBytes, 0xAA);
    }

    [Fact]
    public void RestoreDisk_ShortTarget_RefusesBeforeWrite()
    {
        var aPath = Path.Combine(_dir, "sa.img");
        WriteFile(aPath, RandomChunk(511));
        DiskLayout layout;
        using (var store = NewStore())
        using (var a = new FileBlockSource(aPath))
        {
            layout = store.ImageDisk(0, "disk0", new DiskImageOptions(), new DiskCapturePlan("disk-guid-short", 32L * Mib, 512,
                new List<PartitionImageInput> { Part(1, NewGuid(), aPath, "ntfs", a, null) }));
        }

        // Need = 1 MiB offset + 1 MiB length; a 1 MiB file refuses.
        var target = Path.Combine(_dir, "short.img");
        PreFill(target, 1L * Mib);
        var before = File.ReadAllBytes(target);
        var log = new TestLogger();
        using (var store = NewStore(log))
        {
            Assert.Equal(1, RunDiskRestore(store, log, new DiskRestoreOptions(
                LayoutGen: layout.Generation, Target: target, StorePath: _storeRoot, IndexPath: _dbPath,
                ConfirmToken: DiskTokenFor(target), AllowBootDisk: false, SkipVerify: false, DryRun: false)));
        }
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void RestoreDisk_DryRun_PrintsDiskpartAndBcdboot()
    {
        const long diskSize = 32L * Mib;
        var espPath = Path.Combine(_dir, "da.img");
        var sysPath = Path.Combine(_dir, "ds.img");
        WriteFile(espPath, RandomChunk(521));
        WriteFile(sysPath, RandomChunk(522), RandomChunk(523));
        DiskLayout layout;
        using (var store = NewStore())
        using (var esp = new FileBlockSource(espPath))
        using (var sys = new FileBlockSource(sysPath))
        {
            layout = store.ImageDisk(0, "disk0", new DiskImageOptions(), new DiskCapturePlan("disk-guid-dry", diskSize, 512,
                new List<PartitionImageInput>
                {
                    Part(1, NewGuid(), espPath, "fat32", esp, null),
                    Part(2, NewGuid(), sysPath, "ntfs", sys, null),
                }));
        }

        var target = Path.Combine(_dir, "dry.img");
        PreFill(target, diskSize);
        var before = File.ReadAllBytes(target);
        var log = new TestLogger();
        using (var store = NewStore(log))
        {
            Assert.Equal(0, RunDiskRestore(store, log, new DiskRestoreOptions(
                LayoutGen: layout.Generation, Target: target, StorePath: _storeRoot, IndexPath: _dbPath,
                ConfirmToken: DiskTokenFor(target), AllowBootDisk: false, SkipVerify: false, DryRun: true)));
        }
        var output = string.Join("\n", log.Messages);
        Assert.Contains("create partition efi", output);
        Assert.Contains("bcdboot", output);
        Assert.Equal(before, File.ReadAllBytes(target));
        var scriptPath = target + ".diskpart.txt";
        Assert.True(File.Exists(scriptPath));
        Assert.Contains("create partition efi", File.ReadAllText(scriptPath));
    }

    [Fact]
    public void ExportVhdx_PerPartition_RoundTrips()
    {
        // No new exporter: an ImageDisk partition gen renders through the
        // existing ExportVhdx dynamic path and reads back byte-identical.
        var sysPath = Path.Combine(_dir, "va.img");
        WriteFile(sysPath, RandomChunk(531), RandomChunk(532));
        int gen;
        using (var store = NewStore())
        using (var sys = new FileBlockSource(sysPath))
        {
            var layout = store.ImageDisk(0, "disk0", new DiskImageOptions(), new DiskCapturePlan("disk-guid-vhdx", 32L * Mib, 512,
                new List<PartitionImageInput> { Part(1, NewGuid(), sysPath, "ntfs", sys, null) }));
            gen = layout.Partitions[0].SnapshotGeneration;
        }

        var vhdxPath = Path.Combine(_dir, "part.vhdx");
        using (var store = NewStore())
            store.ExportVhdx(gen, vhdxPath);

        using var disk = new Disk(vhdxPath, FileAccess.Read);
        var back = new byte[2L * Mib];
        disk.Content.Seek(0, SeekOrigin.Begin);
        var read = 0;
        while (read < back.Length)
        {
            var n = disk.Content.Read(back, read, back.Length - read);
            Assert.True(n > 0, "VHDX content ended early at " + read);
            read += n;
        }
        Assert.Equal(File.ReadAllBytes(sysPath), back);
    }

    [Fact]
    public void RestoreDisk_Progress_Monotonic()
    {
        const long diskSize = 32L * Mib;
        var aPath = Path.Combine(_dir, "pa.img");
        var bPath = Path.Combine(_dir, "pb.img");
        WriteFile(aPath, RandomChunk(541), RandomChunk(542));
        WriteFile(bPath, RandomChunk(543));
        DiskLayout layout;
        using (var store = NewStore())
        using (var a = new FileBlockSource(aPath))
        using (var b = new FileBlockSource(bPath))
        {
            layout = store.ImageDisk(0, "disk0", new DiskImageOptions(), new DiskCapturePlan("disk-guid-prog", diskSize, 512,
                new List<PartitionImageInput>
                {
                    Part(1, NewGuid(), aPath, "ntfs", a, null),
                    Part(2, NewGuid(), bPath, "ntfs", b, null),
                }));
        }

        var target = Path.Combine(_dir, "prog.img");
        PreFill(target, diskSize);
        var prog = new ListProgress();
        var log = new TestLogger();
        using (var store = NewStore(log))
        {
            Assert.Equal(0, RunDiskRestore(store, log, new DiskRestoreOptions(
                LayoutGen: layout.Generation, Target: target, StorePath: _storeRoot, IndexPath: _dbPath,
                ConfirmToken: DiskTokenFor(target), AllowBootDisk: false, SkipVerify: false, DryRun: false), prog));
        }

        Assert.NotEmpty(prog.Values);
        var prev = 0L;
        foreach (var v in prog.Values)
        {
            Assert.InRange(v, 0, diskSize);
            Assert.True(v >= prev, $"progress went backwards: {prev} -> {v}");
            prev = v;
        }
    }
}
