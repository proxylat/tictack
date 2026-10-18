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

    // B6E miss 1: every record rewrite bumps the LSN + fixup USN, so raw
    // digests flip 217/256 on a no-op mount. The skip digest masks exactly
    // those two per-write counters (LSN@0x08, USA words); stamps a realistic
    // USA ([42,48), ahead of firstAttr 48 — the parser never reads it).
    private static byte[] StampLsnUsa(byte[] record, long lsn, ushort usn = 1)
    {
        var stamped = (byte[])record.Clone();
        BitConverter.GetBytes(lsn).CopyTo(stamped, 0x08);
        BitConverter.GetBytes((ushort)42).CopyTo(stamped, 0x04);
        BitConverter.GetBytes((ushort)3).CopyTo(stamped, 0x06);
        for (var w = 0; w < 3; w++)
            BitConverter.GetBytes(usn).CopyTo(stamped, 42 + 2 * w);
        return stamped;
    }

    [Fact]
    public void SkipDigest_LsnAndUsaBumps_AreInvisible()
    {
        var attrs = MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 32)));
        var a = StampLsnUsa(MftRecordBuilder.Live(Bpr, 7, attrs), 100);
        var b = StampLsnUsa(MftRecordBuilder.Live(Bpr, 7, attrs), 200, usn: 9);
        Assert.Equal(MftDigest.SkipRecordDigest(a), MftDigest.SkipRecordDigest(b));
        Assert.NotEqual(MftDigest.RecordDigest(a), MftDigest.RecordDigest(b)); // capture truth stays write-sensitive
    }

    [Fact]
    public void SkipDigest_SeqAndContentBumps_AreVisible()
    {
        var attrs = MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 32)));
        var a = StampLsnUsa(MftRecordBuilder.Live(Bpr, 7, attrs), 100);
        var reseeded = StampLsnUsa(MftRecordBuilder.Live(Bpr, 8, attrs), 100); // slot reuse: seq 7→8
        Assert.NotEqual(MftDigest.SkipRecordDigest(a), MftDigest.SkipRecordDigest(reseeded));
        var moved = StampLsnUsa(MftRecordBuilder.Live(Bpr, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 64)))), 100);
        Assert.NotEqual(MftDigest.SkipRecordDigest(a), MftDigest.SkipRecordDigest(moved));
    }

    // USA audit: the skip mask follows usaOff/usaCount from each record
    // header (MftDigest.SkipRecordDigest reads them dynamically), not a
    // fixed offset. Churn inside the DECLARED usa words is invisible; the
    // same byte churn with no USA declared stays visible.
    private static byte[] UsaAt(byte[] record, ushort off, ushort count, ushort w0, ushort w1)
    {
        var r = (byte[])record.Clone();
        BitConverter.GetBytes(off).CopyTo(r, 0x04);
        BitConverter.GetBytes(count).CopyTo(r, 0x06);
        BitConverter.GetBytes(w0).CopyTo(r, off);
        BitConverter.GetBytes(w1).CopyTo(r, off + 2);
        return r;
    }

    [Fact]
    public void SkipDigest_RelocatedUsa_IsMaskedDynamically()
    {
        var attrs = MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 32)));
        var live = MftRecordBuilder.Live(Bpr, 7, attrs);
        Assert.Equal(MftDigest.SkipRecordDigest(UsaAt(live, 60, 2, 7, 8)),
            MftDigest.SkipRecordDigest(UsaAt(live, 60, 2, 9, 10)));
        var c = (byte[])live.Clone();
        BitConverter.GetBytes((ushort)7).CopyTo(c, 60);
        var d = (byte[])live.Clone();
        BitConverter.GetBytes((ushort)9).CopyTo(d, 60);
        Assert.NotEqual(MftDigest.SkipRecordDigest(c), MftDigest.SkipRecordDigest(d));
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

    [Fact]
    public void SkipDigest_SectorTails_AreMaskedByUsaStride()
    {
        // B6K: USA re-stamps move the sector-end fixup tails too
        // ([510:512)/[1022:1024) for the 1024B/usaCount-3 shape). The mask
        // derives the stride from the USA count (sectors = count-1), so tails
        // are invisible but adjacent content stays sensitive.
        var attrs = MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 32)));
        var live = MftRecordBuilder.Live(Bpr, 7, attrs);
        var tails = (byte[])live.Clone();
        BitConverter.GetBytes((ushort)42).CopyTo(tails, 0x04);
        BitConverter.GetBytes((ushort)3).CopyTo(tails, 0x06);
        var flipA = (byte[])tails.Clone();
        flipA[510] ^= 0xFF; flipA[511] ^= 0xFF;
        flipA[1022] ^= 0xFF; flipA[1023] ^= 0xFF;
        Assert.Equal(MftDigest.SkipRecordDigest(tails), MftDigest.SkipRecordDigest(flipA));
        var content = (byte[])tails.Clone();
        content[509] ^= 0xFF; // adjacent byte is NOT fixup: stays visible
        Assert.NotEqual(MftDigest.SkipRecordDigest(tails), MftDigest.SkipRecordDigest(content));
    }

    [Fact]
    public void SkipDigest_Non512Stride_MasksOnlyItsOwnTails()
    {
        // 4096B record, usaCount 2 (one 4K sector): only [4094:4096) is
        // fixup. A 512-boundary word mid-record is content — stays visible.
        var big = new byte[4096];
        MftRecordBuilder.Live(Bpr, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 32)))).CopyTo(big, 0);
        BitConverter.GetBytes((ushort)42).CopyTo(big, 0x04);
        BitConverter.GetBytes((ushort)2).CopyTo(big, 0x06);
        var tail = (byte[])big.Clone();
        tail[4094] ^= 0xFF; tail[4095] ^= 0xFF;
        Assert.Equal(MftDigest.SkipRecordDigest(big), MftDigest.SkipRecordDigest(tail));
        var mid = (byte[])big.Clone();
        mid[510] ^= 0xFF; mid[511] ^= 0xFF;
        Assert.NotEqual(MftDigest.SkipRecordDigest(big), MftDigest.SkipRecordDigest(mid));
    }

    [Fact]
    public void SkipDigest_OddUsaShape_StaysStrict()
    {
        // usaCount that doesn't divide the record (sectors=3, 1024%3 != 0):
        // no stride to trust — tails stay sensitive, never blind.
        var attrs = MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 32)));
        var live = MftRecordBuilder.Live(Bpr, 7, attrs);
        BitConverter.GetBytes((ushort)42).CopyTo(live, 0x04);
        BitConverter.GetBytes((ushort)4).CopyTo(live, 0x06);
        var flip = (byte[])live.Clone();
        flip[510] ^= 0xFF; flip[511] ^= 0xFF;
        Assert.NotEqual(MftDigest.SkipRecordDigest(live), MftDigest.SkipRecordDigest(flip));
    }

    // FRN-5-only SI masking: timestamp churn inside the resident
    // $STANDARD_INFORMATION is invisible, index bytes and other records'
    // timestamps stay sensitive.
    private static byte[] RootSiRecord(ulong mtime)
    {
        var si = new byte[24 + 48];
        BitConverter.GetBytes(0x10u).CopyTo(si, 0);
        BitConverter.GetBytes((uint)si.Length).CopyTo(si, 4);
        si[8] = 0;
        BitConverter.GetBytes(48u).CopyTo(si, 16);
        BitConverter.GetBytes((ushort)24).CopyTo(si, 20);
        for (var i = 0; i < 4; i++)
            BitConverter.GetBytes(0x1D0000000000000UL + mtime + (ulong)i).CopyTo(si, 24 + 8 * i);
        var idx = MftRecordBuilder.DataAttr(false, [], type: 0x90);
        var rec = MftRecordBuilder.Live(Bpr, 7, si, idx);
        // USA ahead of firstAttr (the fake convention, cf. TouchCounters):
        // usaOff must never overlap the attribute area it precedes.
        BitConverter.GetBytes((ushort)42).CopyTo(rec, 0x04);
        BitConverter.GetBytes((ushort)3).CopyTo(rec, 0x06);
        return rec;
    }

    private static List<string> RecordsOf(Dictionary<long, byte[]> records, long count)
    {
        using var vol = new FakeMftVolume(records: records, recordCount: count,
            length: GridClusters * Bpc);
        return MftDigest.ComputeGroupsAndRecords(vol, ChunkBytes).Records;
    }

    [Fact]
    public void RootSiMask_TimestampsInvisibleIndexSensitive()
    {
        var @base = StandardRecords();
        @base[5] = RootSiRecord(7);
        var before = RecordsOf(@base, 20);
        var tsFlip = StandardRecords();
        tsFlip[5] = RootSiRecord(9); // timestamps bumped, index identical
        Assert.Equal(before, RecordsOf(tsFlip, 20));
        var idxFlip = StandardRecords();
        idxFlip[5] = RootSiRecord(7);
        idxFlip[5][130] ^= 0xFF; // inside the $INDEX_ROOT stub
        Assert.NotEqual(before, RecordsOf(idxFlip, 20));
        var otherTs = StandardRecords();
        otherTs[5] = RootSiRecord(7);
        otherTs[6] = (byte[])otherTs[6].Clone();
        otherTs[6][100] ^= 0xFF; // non-root content: stays sensitive
        Assert.NotEqual(before, RecordsOf(otherTs, 20));
    }

    [Theory]
    [InlineData("{3808876b-c176-4d9f-9d4e-000000000001}", true)]
    [InlineData("{497AF6F4-0000-4D9F-9D4E-000000000001}{3808876b-c176-4d9f-9d4e-000000000001}", true)]
    [InlineData("TxfLogContainer00000000000000000001", false)]
    [InlineData("{short}", false)]
    [InlineData("{not-a-guid!}", false)]
    [InlineData("WPSettings.dat", false)]
    [InlineData("", false)]
    public void ShadowDiffName_MatchesOnlyBraceGuids(string name, bool expected) =>
        Assert.Equal(expected, MftSkipMask.IsShadowDiffName(name));

    // B6O ground truth, synthetic rebuild: FRN 38 (U:\big, in-use dir)
    // reads usaOff=48/count=3/firstAttr=56/flags=0x3/used=464, and a VSS
    // create flips EXACTLY bytes 48,510,1022 (USN 0x5B->0x5C + its two
    // sector-trailer copies; LSN/seq/index bit-identical). The record
    // gate must not veto on that triple. Existing USA/tail tests use
    // the fake off=42 shape; this pins the long-header off=48 shape the
    // real directory record has. No digest change: version stays 4, so
    // same-view manifests compare equal (no refuse-to-backstop cycle).
    private static byte[] Frn38StyleRecord()
    {
        var record = new byte[Bpr];
        record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';
        BitConverter.GetBytes((ushort)48).CopyTo(record, 0x04);
        BitConverter.GetBytes((ushort)3).CopyTo(record, 0x06);
        BitConverter.GetBytes(86001839L).CopyTo(record, 0x08);
        BitConverter.GetBytes((ushort)1).CopyTo(record, 0x10);
        BitConverter.GetBytes((ushort)1).CopyTo(record, 0x12);
        BitConverter.GetBytes((ushort)56).CopyTo(record, 0x14);
        BitConverter.GetBytes((ushort)0x3).CopyTo(record, 0x16);
        BitConverter.GetBytes(464u).CopyTo(record, 0x18);
        BitConverter.GetBytes((uint)Bpr).CopyTo(record, 0x1C);
        for (var i = 56; i < 464; i++)
            record[i] = (byte)(i * 7 + 3); // plausible index content: sensitive
        for (var i = 464; i < Bpr; i++)
            record[i] = (byte)(i * 13 + 1); // record slack: stable, sensitive
        record[48] = 0x5B; record[510] = 0x5B; record[1022] = 0x5B; // one USN epoch
        return record;
    }

    [Fact]
    public void Frn38UsnTriple_Invisible()
    {
        Assert.Equal(5, MftDigest.SkipRecordVersion); // v5: +SI last-access masking, digests moved once
        var live = Frn38StyleRecord();
        var restamped = (byte[])live.Clone();
        restamped[48] = 0x5C; restamped[510] = 0x5C; restamped[1022] = 0x5C; // B6O triple
        Assert.Equal(MftDigest.SkipRecordDigest(live), MftDigest.SkipRecordDigest(restamped));
    }

    [Fact]
    public void Frn38IndexSeqAndHeader_Visible()
    {
        var live = Frn38StyleRecord();
        var indexFlip = (byte[])live.Clone();
        indexFlip[100] ^= 0xFF; // inside the attribute area: a real child-add rewrites index bytes
        Assert.NotEqual(MftDigest.SkipRecordDigest(live), MftDigest.SkipRecordDigest(indexFlip));
        var seqFlip = (byte[])live.Clone();
        seqFlip[0x10] ^= 0xFF; // slot reuse signal: stays sensitive
        Assert.NotEqual(MftDigest.SkipRecordDigest(live), MftDigest.SkipRecordDigest(seqFlip));
        var usaPtrFlip = (byte[])live.Clone();
        usaPtrFlip[0x04] ^= 0xFF; // fixup offset/count: structural, stays sensitive
        Assert.NotEqual(MftDigest.SkipRecordDigest(live), MftDigest.SkipRecordDigest(usaPtrFlip));
        var lsnFlip = (byte[])live.Clone();
        lsnFlip[0x08] ^= 0xFF; // $LogFile cursor: void for content, stays masked
        Assert.Equal(MftDigest.SkipRecordDigest(live), MftDigest.SkipRecordDigest(lsnFlip));
    }

    [Fact]
    public void LargeRecord_TailsMaskedByCount()
    {
        // Standard 4K-record geometry (count=9, eight 512B sectors): every
        // sector tail is fixup, the rest is content.
        const int big = 4096;
        var record = new byte[big];
        record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';
        BitConverter.GetBytes((ushort)48).CopyTo(record, 0x04);
        BitConverter.GetBytes((ushort)9).CopyTo(record, 0x06);
        for (var i = 56; i < big; i++)
            record[i] = (byte)(i * 7 + 3);
        var tails = (byte[])record.Clone();
        for (var s = 1; s <= 8; s++)
            tails[s * 512 - 2] ^= 0xFF; // one fixup byte per sector tail
        Assert.Equal(MftDigest.SkipRecordDigest(record), MftDigest.SkipRecordDigest(tails));
        var content = (byte[])record.Clone();
        content[1000] ^= 0xFF; // interior content byte: stays visible
        Assert.NotEqual(MftDigest.SkipRecordDigest(record), MftDigest.SkipRecordDigest(content));
    }
}
