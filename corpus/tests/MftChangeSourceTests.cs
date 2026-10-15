namespace Corpus;

// MftDigestChangeSource: USN-hint delegation, fake-volume digest plans,
// bitmap-net union, Store end-to-end. Grid here: chunkBytes = bpc = 4096
// (chunk == LCN) except the Store test (chunkMib 1 → chunk 256 LCNs).
public sealed class MftChangeSourceTests : IDisposable
{
    private const int Bpr = 1024;
    private const int Bpc = 4096;
    private const int ChunkBytes = 4096;
    private readonly string _dir;

    public MftChangeSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_mftcs_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Dictionary<long, byte[]> StandardRecords()
    {
        var records = new Dictionary<long, byte[]>();
        for (var r = 0L; r < 16; r++)
            records[r] = MftRecordBuilder.Live(Bpr, 7,
                MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((1, 200 + r))));
        for (var r = 16L; r < 20; r++)
            records[r] = MftRecordBuilder.Live(Bpr, 7,
                MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 32 + ((r - 16) * 16)))));
        return records;
    }

    private static SnapshotManifest ParentWith(List<string>? digests, List<ManifestEntry> chunks,
        long length, string? cursor = "usn:0000000000000001:0000000000000002") =>
        ManifestStore.Build(1, "parent", length, 1, 3, "key-1", "", chunks,
            changeCursor: cursor, mftChunkDigests: digests, mftRecordBytes: Bpr);

    private static List<ManifestEntry> DataChunks(int n)
    {
        var chunks = new List<ManifestEntry>();
        for (var i = 0; i < n; i++)
            chunks.Add(new ManifestEntry(new string('a', 64), ChunkBytes));
        return chunks;
    }

    [Fact]
    public void NoMftPath_DelegatesToUsn_NullOnLinux()
    {
        var vol = Path.Combine(_dir, "vol.img");
        File.WriteAllBytes(vol, new byte[ChunkBytes]);
        using var source = new FileBlockSource(vol);
        var src = new MftDigestChangeSource();
        var withDigests = ParentWith(["00"], DataChunks(1), ChunkBytes);
        var withoutDigests = ParentWith(null, DataChunks(1), ChunkBytes);
        // No MFT path (plain file) and no journal on Linux → USN hint also abstains → full.
        Assert.Null(src.Plan(withDigests, source, ChunkBytes, new TestLogger()));
        Assert.Null(src.Plan(withoutDigests, source, ChunkBytes, new TestLogger()));
    }

    [Fact]
    public void FakeVolume_Clean_PassthroughCursorAndDigests()
    {
        using var vol = new FakeMftVolume(records: StandardRecords(), length: 512L * Bpc);
        var digests = MftDigest.ComputeGroups(vol, ChunkBytes);
        var parent = ParentWith(digests, DataChunks(512), 512L * ChunkBytes);
        var plan = new MftDigestChangeSource().Plan(parent, vol, ChunkBytes, new TestLogger());
        Assert.NotNull(plan);
        Assert.Equal("usn:0000000000000001:0000000000000002", plan!.NewCursor); // no journal on the fake → parent cursor
        Assert.Equal(digests, plan.NewMftDigests);
        Assert.Equal(Bpr, plan.NewMftRecordBytes);
        var want = new HashSet<long> { 0, 1, 2, 3 }; // sys record clusters + sys data runs, nothing else
        for (var c = 200L; c < 216; c++)
            want.Add(c);
        Assert.Equal(want, plan.DirtyChunks);
    }

    [Fact]
    public void FakeVolume_HoleFlip_UnionedIntoDirty()
    {
        using var vol = new FakeMftVolume(records: StandardRecords(), length: 2L * ChunkBytes,
            allocRuns: new List<(long, long)> { (0, 2) }); // both clusters allocated now...
        var digests = MftDigest.ComputeGroups(vol, ChunkBytes);
        var chunks = new List<ManifestEntry>
        {
            new(new string('a', 64), ChunkBytes),
            ManifestEntry.Hole(ChunkBytes), // ...but chunk 1 was a hole in the parent
        };
        var plan = new MftDigestChangeSource().Plan(ParentWith(digests, chunks, 2L * ChunkBytes),
            vol, ChunkBytes, new TestLogger());
        Assert.NotNull(plan);
        Assert.Contains(1L, plan!.DirtyChunks);
    }

    [Fact]
    public void FakeVolume_MftFault_FallsBackToFull()
    {
        using var before = new FakeMftVolume(records: StandardRecords(), length: 512L * Bpc);
        var digests = MftDigest.ComputeGroups(before, ChunkBytes);
        using var faulty = new FakeMftVolume(records: StandardRecords(), length: 512L * Bpc,
            faultOnRecord: _ => new InvalidDataException("ioctl gone"));
        var parent = ParentWith(digests, DataChunks(512), 512L * ChunkBytes);
        Assert.Null(new MftDigestChangeSource().Plan(parent, faulty, ChunkBytes, new TestLogger()));
    }

    [Fact]
    public void StoreIncremental_MftPlan_DrivesReRead()
    {
        const int mib = 1024 * 1024;
        var volPath = Path.Combine(_dir, "mftvol.img");
        var chunk0 = new byte[mib];
        new Random(7).NextBytes(chunk0);
        var chunk1 = new byte[mib];
        new Random(8).NextBytes(chunk1);
        File.WriteAllBytes(volPath, chunk0.Concat(chunk1).ToArray());

        var storeRoot = Path.Combine(_dir, "store");
        var dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        using var store = new Store(storeRoot, dbPath, 1, 3, new TestLogger());
        using (var file = new FileBlockSource(volPath))
            store.Image(file, "parent");

        // Parent gains digests (Store persistence is the wiring lane; here
        // the test seals them in exactly like Store will).
        var manDir = Path.Combine(storeRoot, "manifests");
        var key = ChunkId.EnsureKey(storeRoot);
        var parentPath = Directory.GetFiles(manDir, "000001-*.json")[0];
        var parent = ManifestStore.Load(parentPath, key);
        var before = StandardStoreRecords((30000, 1));
        using (var beforeVol = new FakeMftVolume(records: before, recordCount: 20,
                   mftRuns: new (long, long)[] { (10000, 100) }, length: 2L * mib,
                   allocRuns: new List<(long, long)> { (0, 512) }))
            parent.MftChunkDigests = MftDigest.ComputeGroups(beforeVol, mib);
        parent.MftRecordBytes = Bpr;
        ManifestStore.Save(manDir, parent, key);

        // Mutate chunk 1 on disk; file 16's record relocates onto it
        // (chunk 1 = LCNs 256..511 at 4 KiB clusters).
        var mutated = new byte[mib];
        new Random(999).NextBytes(mutated);
        using (var f = new FileStream(volPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            f.Seek(mib, SeekOrigin.Begin);
            f.Write(mutated, 0, mutated.Length);
        }
        var current = StandardStoreRecords((256, 256));
        using var fileSource = new FileBlockSource(volPath);
        using var mftSource = new FakeMftVolume(records: current, recordCount: 20,
            mftRuns: new (long, long)[] { (10000, 100) }, inner: fileSource,
            allocRuns: new List<(long, long)> { (0, 512) });
        var child = store.Image(mftSource, "child",
            new ImageOptions(Incremental: true, ChangeSource: new MftDigestChangeSource()));

        Assert.Equal(1, child.ParentGeneration);
        Assert.Equal(parent.Chunks[0].DigestHex, child.Chunks[0].DigestHex);
        Assert.NotEqual(parent.Chunks[1].DigestHex, child.Chunks[1].DigestHex);
        var outPath = Path.Combine(_dir, "mft-out.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(volPath), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    // Sys 0..15 parked past the 2-chunk grid (data LCN 20000+, record
    // clusters off mftRuns (10000,100) → chunks 39+); files 17..19 likewise
    // parked; file 16 takes the scripted run.
    private static Dictionary<long, byte[]> StandardStoreRecords((long Lcn, long Count) file16)
    {
        var records = new Dictionary<long, byte[]>();
        for (var r = 0L; r < 16; r++)
            records[r] = MftRecordBuilder.Live(Bpr, 7,
                MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((1, 20000 + r))));
        records[16] = MftRecordBuilder.Live(Bpr, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((file16.Count, file16.Lcn))));
        for (var r = 17L; r < 20; r++)
            records[r] = MftRecordBuilder.Live(Bpr, 7,
                MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((1, 30000 + r))));
        return records;
    }
}
