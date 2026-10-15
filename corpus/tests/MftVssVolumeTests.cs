namespace Corpus;

// VssBlockSource as IMftVolume: baseline digests flow through the existing
// planner path, Dispose releases the lazy reader exactly once, and a dead
// snapshot device degrades (baseline omitted, planner falls back to full)
// instead of faulting the image. Grid: chunkBytes = bpc = 4096, so chunk
// index == LCN and MFT record r sits in chunk r/4 (mftRuns (0,100)).
public sealed class MftVssVolumeTests
{
    private const int Bpr = 1024;
    private const int Bpc = 4096;
    private const int ChunkBytes = 4096;
    private const long GridClusters = 512;

    // Sys 0..15: live, data run (200+r,1). Files 16..19: live, (32+(r-16)*16,16).
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

    private static VssBlockSource SeamVolume(IMftVolume fake) =>
        new("SNAPSHOT", GridClusters * Bpc, Bpc, _ => fake);

    private static SnapshotManifest ParentWith(List<string> digests)
    {
        var chunks = new List<ManifestEntry>();
        for (var i = 0; i < (int)GridClusters; i++)
            chunks.Add(new ManifestEntry(new string('a', 64), ChunkBytes));
        return ManifestStore.Build(1, "parent", GridClusters * ChunkBytes, 1, 3, "key-1", "",
            chunks, changeCursor: "usn:0000000000000001:0000000000000002",
            mftChunkDigests: digests, mftRecordBytes: Bpr);
    }

    [Fact]
    public void BaselineDigests_MatchUnderlyingVolume()
    {
        using var fake = new FakeMftVolume(records: StandardRecords(), length: GridClusters * Bpc);
        using var vss = SeamVolume(fake);
        Assert.IsAssignableFrom<IMftVolume>(vss); // Store.TryMftBaseline's cast now hits
        // Exactly what TryMftBaseline persists: group digests + record bytes.
        var baseline = MftDigest.ComputeGroups(vss, ChunkBytes);
        Assert.Equal(MftDigest.ComputeGroups(fake, ChunkBytes), baseline);
        Assert.Equal(5, baseline.Count); // 20 records, 4 per group
        Assert.All(baseline, g => Assert.Matches("^[0-9a-f]{64}$", g));
        Assert.Equal(Bpr, vss.BytesPerRecord);
        Assert.Equal(Bpc, ((IMftVolume)vss).BytesPerCluster);
    }

    [Fact]
    public void PlannerPath_CleanMft_YieldsSysOnlyAndGroups()
    {
        using var fake = new FakeMftVolume(records: StandardRecords(), length: GridClusters * Bpc);
        using var vss = SeamVolume(fake);
        var digests = MftDigest.ComputeGroups(vss, ChunkBytes);
        var plan = MftDigestPlanner.PlanDirty(vss, ParentWith(digests), ChunkBytes, new TestLogger());
        Assert.NotNull(plan);
        var want = new HashSet<long> { 0, 1, 2, 3 }; // sys record clusters + sys data runs, nothing else
        for (var c = 200L; c < 216; c++)
            want.Add(c);
        Assert.Equal(want, plan!.Value.Dirty);
        Assert.Equal(digests, plan.Value.Groups);
    }

    [Fact]
    public void Dispose_ReleasesReaderOnce()
    {
        var tracking = new TrackingMftVolume(new FakeMftVolume(records: StandardRecords()));
        var vss = new VssBlockSource("SNAPSHOT", GridClusters * Bpc, Bpc, _ => tracking);
        _ = vss.GetRecordBytes(0); // force the lazy open
        vss.Dispose();
        Assert.Equal(1, tracking.DisposeCount);
        vss.Dispose();
        Assert.Equal(1, tracking.DisposeCount);
    }

    [Fact]
    public void Dispose_WithoutMftAccess_NeverOpens()
    {
        var opens = 0;
        var vss = new VssBlockSource("SNAPSHOT", GridClusters * Bpc, Bpc, device =>
        {
            opens++;
            return new FakeMftVolume(records: StandardRecords());
        });
        vss.Dispose();
        Assert.Equal(0, opens);
    }

    [Fact]
    public void SnapshotUnavailable_PlannerFallsBackToFull()
    {
        using var vss = new VssBlockSource("SNAPSHOT", GridClusters * Bpc, Bpc,
            _ => throw new InvalidDataException("snapshot gone"));
        var log = new TestLogger();
        using var fake = new FakeMftVolume(records: StandardRecords(), length: GridClusters * Bpc);
        Assert.Null(MftDigestPlanner.PlanDirty(vss,
            ParentWith(MftDigest.ComputeGroups(fake, ChunkBytes)), ChunkBytes, log));
        Assert.Contains(log.Messages, m => m.Contains("MFT unreadable"));
    }

    [Fact]
    public void SnapshotOpenIoFault_NormalizesToInvalidData()
    {
        // Store.TryMftBaseline catches InvalidDataException only: an open
        // fault must surface as that (or the image faults instead of
        // degrading to a digest-less manifest).
        using var vss = new VssBlockSource("SNAPSHOT", GridClusters * Bpc, Bpc,
            _ => throw new IOException("device gone"));
        var ex = Assert.Throws<InvalidDataException>(() => MftDigest.ComputeGroups(vss, ChunkBytes));
        Assert.Contains("SNAPSHOT", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public void MftAccessAfterDispose_Throws()
    {
        var vss = SeamVolume(new FakeMftVolume(records: StandardRecords()));
        vss.Dispose();
        Assert.Throws<ObjectDisposedException>(() => vss.GetRecordBytes(0));
        Assert.Throws<ObjectDisposedException>(() => vss.BytesPerRecord);
    }

    private sealed class TrackingMftVolume : IMftVolume
    {
        private readonly IMftVolume _inner;
        public int DisposeCount;

        public TrackingMftVolume(IMftVolume inner) => _inner = inner;

        public int BytesPerRecord => _inner.BytesPerRecord;
        public int BytesPerCluster => _inner.BytesPerCluster;
        public long RecordCount => _inner.RecordCount;
        public IReadOnlyList<(long StartLcn, long ClusterCount)> MftRuns => _inner.MftRuns;
        public IReadOnlyList<MftEnumEntry> Enumerate() => _inner.Enumerate();
        public byte[] GetRecordBytes(ulong fileRef) => _inner.GetRecordBytes(fileRef);

        public void Dispose()
        {
            DisposeCount++;
            _inner.Dispose();
        }
    }
}
