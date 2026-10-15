using System.Security.Cryptography;
using System.Text;

namespace Corpus;

// Item-13 truth core: digest stability, relocation detection (item 1-full),
// group growth/shrink policy, dead-slot/resident/sparse handling, manifest
// additive fields. Grid: chunkBytes = bpc = 4096, so chunk index == LCN and
// MFT record r sits in chunk r/4 (mftRuns (0,100)).
public sealed class MftDigestTests
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

    private static FakeMftVolume StandardVolume(Dictionary<long, byte[]>? records = null,
        long recordCount = 20, Func<ulong, Exception?>? fault = null) =>
        new(records: records ?? StandardRecords(), recordCount: recordCount,
            length: GridClusters * Bpc, faultOnRecord: fault);

    private static SnapshotManifest ParentWith(List<string> digests, int recordBytes = Bpr,
        int grid = (int)GridClusters, string cursor = "usn:0000000000000001:0000000000000002")
    {
        var chunks = new List<ManifestEntry>();
        for (var i = 0; i < grid; i++)
            chunks.Add(new ManifestEntry(new string('a', 64), ChunkBytes));
        return ManifestStore.Build(1, "parent", (long)grid * ChunkBytes, 1, 3, "key-1", "",
            chunks, changeCursor: cursor, mftChunkDigests: digests, mftRecordBytes: recordBytes);
    }

    private static HashSet<long> SysChunks()
    {
        // Sys data runs (200..215) + record clusters (r/4 → 0..3).
        var want = new HashSet<long> { 0, 1, 2, 3 };
        for (var c = 200L; c < 216; c++)
            want.Add(c);
        return want;
    }

    [Fact]
    public void RunCodec_RoundTripsSparseAndNegativeDeltas()
    {
        var enc = MftRecordBuilder.EncodeRuns((4, null), (2, 10), (3, -4), (1, 500000));
        Assert.Equal([(10L, 2L), (6L, 3L), (500006L, 1L)], MftExtentReader.DecodeDataRuns(enc));
    }

    [Fact]
    public void RecordDigest_StableAndRunSensitive()
    {
        var a = MftRecordBuilder.Live(Bpr, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 32))));
        var b = MftRecordBuilder.Live(Bpr, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 32))));
        var moved = MftRecordBuilder.Live(Bpr, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 64))));
        Assert.Equal(MftDigest.RecordDigest(a), MftDigest.RecordDigest(b));
        Assert.NotEqual(MftDigest.RecordDigest(a), MftDigest.RecordDigest(moved)); // relocation flips the digest
    }

    [Fact]
    public void ComputeGroups_BandedAndStable()
    {
        using var vol = StandardVolume();
        var g1 = MftDigest.ComputeGroups(vol, ChunkBytes); // 4 records/group → 5 groups
        var g2 = MftDigest.ComputeGroups(vol, ChunkBytes);
        Assert.Equal(5, g1.Count);
        Assert.Equal(g1, g2);
        Assert.All(g1, g => Assert.Matches("^[0-9a-f]{64}$", g));
    }

    [Fact]
    public void DiffGroups_EqualGrowthShrink()
    {
        Assert.Empty(MftDigest.DiffGroups(["a", "b"], ["a", "b"])!);
        Assert.Equal([1], MftDigest.DiffGroups(["a", "b"], ["a", "X"]));
        Assert.Equal([2, 3], MftDigest.DiffGroups(["a", "b"], ["a", "b", "c", "d"])); // growth dirties extras
        Assert.Null(MftDigest.DiffGroups(["a", "b", "c"], ["a", "b"])); // shrink → full
    }

    [Fact]
    public void Planner_CleanMft_YieldsSysOnly()
    {
        using var vol = StandardVolume();
        var parent = ParentWith(MftDigest.ComputeGroups(vol, ChunkBytes));
        var plan = MftDigestPlanner.PlanDirty(vol, parent, ChunkBytes, new TestLogger());
        Assert.NotNull(plan);
        Assert.Equal(SysChunks(), plan!.Value.Dirty);
        Assert.Equal(parent.MftChunkDigests, plan.Value.Groups);
    }

    [Fact]
    public void Planner_Relocation_DirtiesNewAbandonsOld()
    {
        using var before = StandardVolume();
        var digests = MftDigest.ComputeGroups(before, ChunkBytes);
        var records = StandardRecords();
        records[17] = MftRecordBuilder.Live(Bpr, 7, // defrag moved file 17: (48,16) → (96,16)
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 96))));
        using var current = StandardVolume(records);
        var plan = MftDigestPlanner.PlanDirty(current, ParentWith(digests), ChunkBytes, new TestLogger());
        Assert.NotNull(plan);
        var dirty = plan!.Value.Dirty;
        for (var c = 96L; c < 112; c++)
            Assert.Contains(c, dirty); // new location re-captured
        for (var c = 48L; c < 64; c++)
            Assert.DoesNotContain(c, dirty); // abandoned clusters left to their next owner / bitmap net
        Assert.Contains(4L, dirty); // file 17's own record cluster (17/4)
        Assert.Superset(SysChunks(), dirty);
    }

    [Fact]
    public void Planner_GroupCoarseness_ResolvesWholeBand()
    {
        // Record 17 shares group 4 with 16/18/19: one move dirties the
        // band-mates' extents too (over-capture, never under).
        using var before = StandardVolume();
        var digests = MftDigest.ComputeGroups(before, ChunkBytes);
        var records = StandardRecords();
        records[17] = MftRecordBuilder.Live(Bpr, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 96))));
        using var current = StandardVolume(records);
        var dirty = MftDigestPlanner.PlanDirty(current, ParentWith(digests), ChunkBytes, new TestLogger())!.Value.Dirty;
        Assert.Contains(32L, dirty); // file 16's run, dragged along by the band
        Assert.Contains(80L, dirty); // file 19's run, same band
    }

    [Fact]
    public void Planner_DeadSlot_Skipped()
    {
        using var before = StandardVolume();
        var digests = MftDigest.ComputeGroups(before, ChunkBytes);
        var records = StandardRecords();
        records[18] = new byte[Bpr]; // deleted: zeroed slot flips the group digest but resolves to nothing
        using var current = StandardVolume(records);
        var plan = MftDigestPlanner.PlanDirty(current, ParentWith(digests), ChunkBytes, new TestLogger());
        Assert.NotNull(plan); // no throw, no phantom dirty from the dead slot itself
        for (var c = 64L; c < 80; c++)
            Assert.DoesNotContain(c, plan!.Value.Dirty); // file 18's old runs gone with its record
        Assert.Contains(4L, plan.Value.Dirty); // band-mates 16/17/19 still resolve through cluster 4
    }

    [Fact]
    public void Planner_ResidentOnly_RecordClusterDirty()
    {
        using var before = StandardVolume();
        var digests = MftDigest.ComputeGroups(before, ChunkBytes);
        var records = StandardRecords();
        records[16] = MftRecordBuilder.Live(Bpr, 7, MftRecordBuilder.DataAttr(false, [])); // shrunk to resident
        using var current = StandardVolume(records);
        var dirty = MftDigestPlanner.PlanDirty(current, ParentWith(digests), ChunkBytes, new TestLogger())!.Value.Dirty;
        Assert.Contains(4L, dirty); // record 16's cluster (16/4)
        for (var c = 32L; c < 48; c++)
            Assert.DoesNotContain(c, dirty); // old non-resident runs gone from the record
    }

    [Fact]
    public void Planner_SparseRun_DirtiesNothing()
    {
        using var before = StandardVolume();
        var digests = MftDigest.ComputeGroups(before, ChunkBytes);
        var records = StandardRecords();
        records[19] = MftRecordBuilder.Live(Bpr, 7, MftRecordBuilder.DataAttr(true,
            MftRecordBuilder.EncodeRuns((8, null), (2, 400)))); // sparse head + real tail
        using var current = StandardVolume(records);
        var dirty = MftDigestPlanner.PlanDirty(current, ParentWith(digests), ChunkBytes, new TestLogger())!.Value.Dirty;
        Assert.Contains(400L, dirty);
        Assert.Contains(401L, dirty);
        Assert.DoesNotContain(399L, dirty);
    }

    [Fact]
    public void Planner_RecordBytesMismatch_Full()
    {
        using var vol = StandardVolume();
        var parent = ParentWith(MftDigest.ComputeGroups(vol, ChunkBytes), recordBytes: 512);
        Assert.Null(MftDigestPlanner.PlanDirty(vol, parent, ChunkBytes, new TestLogger()));
    }

    [Fact]
    public void Planner_Growth_EngagesShrink_FallsBack()
    {
        using var small = StandardVolume(recordCount: 20);
        var smallDigests = MftDigest.ComputeGroups(small, ChunkBytes);
        var grown = StandardRecords();
        for (var r = 20L; r < 24; r++)
            grown[r] = MftRecordBuilder.Live(Bpr, 7,
                MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((1, 300 + r))));
        using var big = StandardVolume(grown, recordCount: 24);
        var plan = MftDigestPlanner.PlanDirty(big, ParentWith(smallDigests), ChunkBytes, new TestLogger());
        Assert.NotNull(plan);
        for (var c = 320L; c < 324; c++)
            Assert.Contains(c, plan!.Value.Dirty);

        var bigDigests = MftDigest.ComputeGroups(big, ChunkBytes);
        using var shrunk = StandardVolume(recordCount: 20);
        Assert.Null(MftDigestPlanner.PlanDirty(shrunk, ParentWith(bigDigests), ChunkBytes, new TestLogger()));
    }

    [Fact]
    public void Planner_LiveButUnparseable_Full()
    {
        using var before = StandardVolume();
        var digests = MftDigest.ComputeGroups(before, ChunkBytes);
        var records = StandardRecords();
        var bad = MftRecordBuilder.Live(Bpr, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((1, 50))));
        BitConverter.GetBytes((ushort)9999).CopyTo(bad, 0x14); // live + in-use, corrupt attr offset
        records[16] = bad;
        using var current = StandardVolume(records);
        var log = new TestLogger();
        Assert.Null(MftDigestPlanner.PlanDirty(current, ParentWith(digests), ChunkBytes, log));
        Assert.Contains(log.Messages, m => m.Contains("unparseable"));
    }

    [Fact]
    public void Planner_RecordFault_MapsToFullOrThrows()
    {
        using var before = StandardVolume();
        var digests = MftDigest.ComputeGroups(before, ChunkBytes);
        using var unavailable = StandardVolume(fault: _ => new InvalidDataException("ioctl gone"));
        Assert.Null(MftDigestPlanner.PlanDirty(unavailable, ParentWith(digests), ChunkBytes, new TestLogger()));
        using var broken = StandardVolume(fault: _ => new IOException("disk fault"));
        Assert.Throws<IOException>(() =>
            MftDigestPlanner.PlanDirty(broken, ParentWith(digests), ChunkBytes, new TestLogger()));
    }

    [Fact]
    public void Manifest_MftFields_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CorpusTest_mftman_" + Guid.NewGuid());
        try
        {
            var key = new byte[32];
            Random.Shared.NextBytes(key);
            var digests = new List<string> { new('e', 64), new('f', 64) };
            var m = ManifestStore.Build(1, "mft", ChunkBytes, 1, 3, "key-1", "",
                new List<ManifestEntry> { new(new string('a', 64), ChunkBytes) },
                mftChunkDigests: digests, mftRecordBytes: Bpr);
            var loaded = ManifestStore.Load(ManifestStore.Save(dir, m, key), key);
            Assert.Equal(digests, loaded.MftChunkDigests);
            Assert.Equal(Bpr, loaded.MftRecordBytes);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Manifest_OldJson_LoadsNullMft()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CorpusTest_mftold_" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(dir);
            var key = new byte[32];
            Random.Shared.NextBytes(key);
            var digest = new string('c', 64);
            var body = $$"""{"Generation":1,"Name":"old","CreatedUtc":"2026-01-01T00:00:00.0000000Z","VolumeLength":4096,"ChunkMib":1,"CompressionLevel":3,"KeyId":"key-1","PrevDigest":"","Chunks":[{"DigestHex":"{{digest}}","Size":4096}],"Hmac":""}""";
            using var hmac = new HMACSHA256(key);
            var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var path = Path.Combine(dir, "000001-old.json");
            File.WriteAllText(path, body.Replace("\"Hmac\":\"\"", "\"Hmac\":\"" + hex + "\"", StringComparison.Ordinal));
            var loaded = ManifestStore.Load(path, key);
            Assert.Null(loaded.MftChunkDigests);
            Assert.Equal(0, loaded.MftRecordBytes);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
