namespace Corpus;

public sealed class IncrementalTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public IncrementalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_incr_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
        SeedVolume();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // 5 MiB, chunkMib 1: zeros | random | zeros | same random | text.
    private void SeedVolume() => WriteChunks(_volPath, new byte[Mib], RandomChunk(42), new byte[Mib], RandomChunk(42), TextChunk());

    private Store NewStore(TestLogger? log = null) =>
        new(_storeRoot, _dbPath, 1, 3, log ?? new TestLogger());

    private static FakeChangeSource Dirty(params long[] chunks) =>
        new FakeChangeSource((_, _, _) => new IncrementalPlan(new HashSet<long>(chunks), "fake:1"));

    private SnapshotManifest LoadGen(int gen)
    {
        var manDir = Path.Combine(_storeRoot, "manifests");
        var paths = Directory.GetFiles(manDir, $"{gen:D6}-*.json");
        Assert.Single(paths);
        return ManifestStore.Load(paths[0], ChunkId.EnsureKey(_storeRoot));
    }

    private static byte[] RandomChunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static byte[] TextChunk()
    {
        var text = new byte[Mib];
        var pattern = "The quick brown fox jumps over the lazy dog. "u8;
        for (var i = 0; i < text.Length; i++)
            text[i] = pattern[i % pattern.Length];
        return text;
    }

    private static void WriteChunks(string path, params byte[][] chunks)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var c in chunks)
            f.Write(c, 0, c.Length);
    }

    [Fact]
    public void Incremental_SkipsCleanReads()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent");
        var parent = LoadGen(1);

        using var counting = new CountingBlockSource(_volPath, Mib);
        var child = store.Image(counting, "child",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(2)));

        Assert.Equal(1, child.ParentGeneration);
        Assert.Equal("fake:1", child.ChangeCursor);
        Assert.Equal(parent.Chunks.Count, child.Chunks.Count);
        for (var i = 0; i < parent.Chunks.Count; i++)
            if (i != 2)
                Assert.Equal(parent.Chunks[i].DigestHex, child.Chunks[i].DigestHex);
        Assert.Single(counting.ReadChunks);
        Assert.Contains(2L, counting.ReadChunks);
    }

    [Fact]
    public void Incremental_CleanReuseExportEqual()
    {
        var vol = Path.Combine(_dir, "mut.img");
        WriteChunks(vol, RandomChunk(1), RandomChunk(2), RandomChunk(3), RandomChunk(4));
        using var store = NewStore();
        using (var source = new FileBlockSource(vol))
            store.Image(source, "parent");
        var parent = LoadGen(1);

        // Mutate chunk 1 on disk.
        var mutated = RandomChunk(999);
        using (var f = new FileStream(vol, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            f.Seek(Mib, SeekOrigin.Begin);
            f.Write(mutated, 0, mutated.Length);
        }

        using var counting = new CountingBlockSource(vol, Mib);
        var child = store.Image(counting, "child",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(1)));

        Assert.Equal(1, child.ParentGeneration);
        Assert.Equal(4, child.Chunks.Count);
        Assert.NotEqual(parent.Chunks[1].DigestHex, child.Chunks[1].DigestHex);
        foreach (var i in new[] { 0, 2, 3 })
            Assert.Equal(parent.Chunks[i].DigestHex, child.Chunks[i].DigestHex);

        var outPath = Path.Combine(_dir, "mut-out.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    [Fact]
    public void Incremental_NoParent_FallsBackToFull()
    {
        using var store = NewStore();
        using var source = new FileBlockSource(_volPath);
        var m = store.Image(source, "first", new ImageOptions(Incremental: true));
        Assert.Equal(0, m.ParentGeneration);
        Assert.Null(m.ChangeCursor);
        Assert.Equal(5, m.Chunks.Count);
        var outPath = Path.Combine(_dir, "first-out.img");
        store.Export(1, outPath);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void Incremental_NullPlan_FallsBackToFull()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent");
        var nullPlan = new FakeChangeSource((_, _, _) => null);
        using var source2 = new FileBlockSource(_volPath);
        var m = store.Image(source2, "child",
            new ImageOptions(Incremental: true, ChangeSource: nullPlan));
        Assert.Equal(0, m.ParentGeneration);
        Assert.Equal(5, m.Chunks.Count);
        Assert.True(store.Verify(2));
    }

    [Fact]
    public void Incremental_ExplicitParentMissing_Throws()
    {
        using var store = NewStore();
        using var source = new FileBlockSource(_volPath);
        Assert.Throws<InvalidDataException>(() =>
            store.Image(source, "orphan", new ImageOptions(ParentGeneration: 99)));
        Assert.Empty(store.ListSnapshots());
    }

    [Fact]
    public void Incremental_ResizedVolume_Full()
    {
        using (var store = NewStore())
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent");

        var grown = Path.Combine(_dir, "grown.img");
        WriteChunks(grown, new byte[Mib], RandomChunk(42), new byte[Mib], RandomChunk(42), TextChunk(), TextChunk());
        var log = new TestLogger();
        using var store2 = NewStore(log);
        using var source2 = new FileBlockSource(grown);
        var m = store2.Image(source2, "grown",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        Assert.Equal(0, m.ParentGeneration);
        Assert.Equal(6, m.Chunks.Count);
        Assert.True(log.Messages.Count >= 2); // fallback reason + imaged line
        var outPath = Path.Combine(_dir, "grown-out.img");
        store2.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(grown), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void Incremental_GridMismatch_Full()
    {
        using (var store = NewStore())
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent"); // chunkMib 1 → 5 chunks

        // Same bytes, different chunk grid: must not reuse parent entries.
        using var wide = new Store(_storeRoot, _dbPath, 2, 3, new TestLogger());
        using var source2 = new FileBlockSource(_volPath);
        var m = wide.Image(source2, "wide",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        Assert.Equal(0, m.ParentGeneration);
        Assert.Equal(3, m.Chunks.Count); // ceil(5 / 2)
        Assert.True(wide.Verify(2));
    }

    [Fact]
    public void Incremental_CorruptParent_Throws()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent");

        var manDir = Path.Combine(_storeRoot, "manifests");
        var paths = Directory.GetFiles(manDir, "000001-*.json");
        Assert.Single(paths);
        var json = File.ReadAllText(paths[0]);
        var chars = json.ToCharArray();
        chars[json.Length / 2] = chars[json.Length / 2] == 'a' ? 'b' : 'a';
        File.WriteAllText(paths[0], new string(chars));

        using var source2 = new FileBlockSource(_volPath);
        Assert.Throws<InvalidDataException>(() =>
            store.Image(source2, "child",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(0))));
        Assert.Single(store.ListSnapshots());
    }

    [Fact]
    public void Incremental_ChildSelfContained()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent");
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "child",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(2)));
        Assert.Equal(1, LoadGen(2).ParentGeneration);

        var manDir = Path.Combine(_storeRoot, "manifests");
        File.Delete(Directory.GetFiles(manDir, "000001-*.json")[0]);

        var outPath = Path.Combine(_dir, "self-out.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    [Fact]
    public void Incremental_HoleStaysHole()
    {
        var vol = Path.Combine(_dir, "hole.img");
        WriteChunks(vol, RandomChunk(42), new byte[Mib], TextChunk());
        var runs = new (long Start, long Count)[] { (0, 256), (512, 256) };
        using var store = NewStore();
        using (var sparse = new FakeSparseSource(vol, runs))
            store.Image(sparse, "parent");
        var parent = LoadGen(1);
        Assert.True(parent.Chunks[1].IsHole);

        // Dirty set includes the hole: hole wins, zero reads for it.
        using var counting = new CountingBlockSource(new FakeSparseSource(vol, runs), Mib);
        var child = store.Image(counting, "child",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0, 1)));

        Assert.Equal(1, child.ParentGeneration);
        Assert.True(child.Chunks[1].IsHole);
        Assert.Equal("", child.Chunks[1].DigestHex);
        Assert.Equal(parent.Chunks[0].DigestHex, child.Chunks[0].DigestHex);
        Assert.Equal(parent.Chunks[2].DigestHex, child.Chunks[2].DigestHex);
        Assert.Single(counting.ReadChunks);
        Assert.Contains(0L, counting.ReadChunks);

        var outPath = Path.Combine(_dir, "hole-out.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    [Fact]
    public void VerifyChain_Ok()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "full");
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "incr1",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "incr2",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(4)));
        Assert.Equal(1, LoadGen(2).ParentGeneration);
        Assert.Equal(2, LoadGen(3).ParentGeneration);
        Assert.True(store.Verify(3, chain: true));
        Assert.True(store.Verify(3));
    }

    [Fact]
    public void VerifyChain_OlderParent_SkipsPrevDigestCheck()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "full");
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "other");
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "child",
                new ImageOptions(ParentGeneration: 1, ChangeSource: Dirty(0)));
        // Gen 3 seals gen 2's HMAC but points at gen 1: linkage is only
        // enforced across adjacent gens; both manifests still HMAC-verify.
        Assert.Equal(1, LoadGen(3).ParentGeneration);
        Assert.True(store.Verify(3, chain: true));
    }

    [Fact]
    public void SystemFrns_BypassSequenceCheck()
    {
        // Canned MFT record: live (seq 7, in-use), one non-resident $DATA
        // run (LCN 32 +16) plus a small resident $DATA (len 24 < 34:
        // skipped, must not throw). Synthetic FRN carries seq 0.
        var record = new byte[1024];
        record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';
        BitConverter.GetBytes((ushort)7).CopyTo(record, 0x10);
        BitConverter.GetBytes((ushort)48).CopyTo(record, 0x14);
        BitConverter.GetBytes((ushort)1).CopyTo(record, 0x16);
        BitConverter.GetBytes(144u).CopyTo(record, 0x18);
        BitConverter.GetBytes(0x80u).CopyTo(record, 48);
        BitConverter.GetBytes(64u).CopyTo(record, 52);
        record[56] = 1; // non-resident
        BitConverter.GetBytes((ushort)48).CopyTo(record, 80);
        record[96] = 0x21; // lenBytes=1, offBytes=2
        record[97] = 0x10; // 16 clusters
        record[98] = 0x20; record[99] = 0x00; // LCN delta 32
        record[100] = 0x00; // terminator
        BitConverter.GetBytes(0x80u).CopyTo(record, 112);
        BitConverter.GetBytes(24u).CopyTo(record, 116);
        record[120] = 0; // resident $DATA: short attr, skipped via record cluster
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(record, 136);
        var mftRuns = new List<(long, long)> { (1000, 10) };

        Assert.Null(MftExtentReader.ParseRecordExtents(record, 5UL, 4096, 1024, mftRuns));
        // Bypass resolves: data extent + own record cluster (record 5 @byte
        // 5120 → VCN 1 → LCN 1001 through the canned MFT runs).
        var ext = MftExtentReader.ParseRecordExtents(record, 5UL, 4096, 1024, mftRuns, skipSequenceCheck: true);
        Assert.NotNull(ext);
        Assert.Equal([(32L, 16L), (1001L, 1L)], ext);
    }

    [Fact]
    public void FullImage_NoCursorWithoutVolumeHandle()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "full");
        var m = LoadGen(1);
        Assert.Equal(0, m.ParentGeneration);
        Assert.Null(m.ChangeCursor); // file sources carry no journal: no cursor, incrementals fall back
    }

    [Fact]
    public void VerifyChain_BrokenPrevDigest_Fails()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "full");
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "middle",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "tip",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(1)));

        var key = ChunkId.EnsureKey(_storeRoot);
        var manDir = Path.Combine(_storeRoot, "manifests");
        var middle = LoadGen(2);
        middle.PrevDigest = new string('0', 64);
        ManifestStore.Save(manDir, middle, key); // re-HMAC: standalone stays valid

        Assert.False(store.Verify(3, chain: true));
        Assert.True(store.Verify(2));
    }

    [Fact]
    public void VerifyChain_MissingLink_Fails()
    {
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "full");
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "middle",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "tip",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(1)));

        var manDir = Path.Combine(_storeRoot, "manifests");
        File.Delete(Directory.GetFiles(manDir, "000002-*.json")[0]);

        Assert.False(store.Verify(3, chain: true));
        Assert.Contains(log.Messages, m => m.Contains("provenance gap"));
        Assert.True(store.Verify(3));
    }

    [Fact]
    public void Cursor_ParseRoundTrip()
    {
        const long journalId = 0x1234ABCD5678EF09;
        const long nextUsn = 0x1000;
        var cursor = UsnChangeSource.FormatCursor(journalId, nextUsn);
        Assert.StartsWith("usn:", cursor);
        var parsed = UsnChangeSource.ParseCursor(cursor);
        Assert.NotNull(parsed);
        // Item1/Item2 (not element names) so any code-lane tuple naming compiles.
        Assert.Equal(journalId, parsed!.Value.Item1);
        Assert.Equal(nextUsn, parsed!.Value.Item2);

        Assert.Null(UsnChangeSource.ParseCursor(null));
        Assert.Null(UsnChangeSource.ParseCursor(""));
        Assert.Null(UsnChangeSource.ParseCursor("garbage"));
        Assert.Null(UsnChangeSource.ParseCursor("usn:xyz"));
        Assert.Null(UsnChangeSource.ParseCursor("usn2:e30=")); // unknown prefix → reject (future-proof)
    }
}
