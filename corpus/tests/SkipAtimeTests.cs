namespace Corpus;

// B6AB: the SI last-access FILETIME steps between baseline and gate while
// nothing changed, vetoing every idle skip. NTFS $STANDARD_INFORMATION
// content layout: creation@+0, modified@+8, MFT-change@+16, accessed@+24 —
// so only content+24 (8B) is masked for non-root FRNs. Modified/created/
// change stay sensitive: every content write moves modified-time.
public sealed class SkipAtimeTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private const int Bpr = 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public SkipAtimeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_atime_" + Guid.NewGuid());
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

    private string SeedVolume(string name, params int[] seeds)
    {
        var vol = Path.Combine(_dir, name);
        using var f = new FileStream(vol, FileMode.Create, FileAccess.Write);
        foreach (var seed in seeds)
        {
            var c = new byte[Mib];
            new Random(seed).NextBytes(c);
            f.Write(c, 0, c.Length);
        }
        return vol;
    }

    private static Dictionary<long, byte[]> SysUserRecords()
    {
        var records = new Dictionary<long, byte[]>();
        for (var r = 0L; r < 16; r++)
            records[r] = MftRecordBuilder.Live(Bpr, 7,
                MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((8, 0))));
        records[16] = UserRec(BaseTimes());
        return records;
    }

    private static byte[] AllocBitmap() => new byte[] { 0xFF, 0xFF, 0x01 };

    private static FakeMftVolume DualVol(IBlockSource inner,
        Dictionary<long, byte[]> shadowRecs, Dictionary<long, byte[]> liveRecs,
        byte[]? bitmap, long recordCount = 24)
    {
        var v = new FakeMftVolume(records: shadowRecs,
            mftRuns: new (long, long)[] { (0, 100) }, inner: inner, recordCount: recordCount);
        v.MftBitmap = bitmap;
        v.LiveFactory = () =>
        {
            var live = new FakeMftVolume(records: liveRecs,
                mftRuns: new (long, long)[] { (0, 100) }, recordCount: recordCount);
            live.MftBitmap = bitmap;
            return live;
        };
        return v;
    }

    private static void MutateBytes(string path, long offset, int count)
    {
        var buf = new byte[count];
        new Random(9182).NextBytes(buf);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        f.Seek(offset, SeekOrigin.Begin);
        f.Write(buf, 0, buf.Length);
    }

    private static byte[] TouchLsn(byte[] record)
    {
        var stamped = (byte[])record.Clone();
        for (var i = 0x08; i < 0x10; i++)
            stamped[i] ^= 0xFF;
        return stamped;
    }

    private static bool HasLine(TestLogger log, string fragment) =>
        log.Messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));

    private int ManifestCount() =>
        Directory.Exists(Path.Combine(_storeRoot, "manifests"))
            ? Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "*.json").Length : 0;

    private void PinRecordVersion(int version)
    {
        var manDir = Path.Combine(_storeRoot, "manifests");
        var key = ChunkId.EnsureKey(_storeRoot);
        var path = Directory.GetFiles(manDir, "000001-*.json")[0];
        var m = ManifestStore.Load(path, key);
        m.MftRecordDigestVersion = version;
        ManifestStore.Save(manDir, m, key);
    }

    // Resident $STANDARD_INFORMATION with explicit FILETIMEs: creation,
    // modified, MFT-change, accessed — the NTFS content order.
    private static byte[] SiAttr(ulong ctime, ulong mtime, ulong chtime, ulong atime)
    {
        var si = new byte[24 + 48];
        BitConverter.GetBytes(0x10u).CopyTo(si, 0);
        BitConverter.GetBytes((uint)si.Length).CopyTo(si, 4);
        si[8] = 0; // resident
        BitConverter.GetBytes(48u).CopyTo(si, 16); // contentLen
        BitConverter.GetBytes((ushort)24).CopyTo(si, 20); // contentOff
        BitConverter.GetBytes(ctime).CopyTo(si, 24);
        BitConverter.GetBytes(mtime).CopyTo(si, 32);
        BitConverter.GetBytes(chtime).CopyTo(si, 40);
        BitConverter.GetBytes(atime).CopyTo(si, 48);
        return si;
    }

    private static (ulong C, ulong M, ulong H, ulong A) BaseTimes() =>
        (0x1DC0000000000000UL, 0x1DC0000000000010UL, 0x1DC0000000000020UL, 0x1DC0000000000030UL);

    // Non-root user record (FRN 16) carrying a resident SI + the standard
    // user data run (chunk 2). Record layout: firstAttr=48, SI at 48,
    // content at 48+24=72, atime at 72+24=96..104 — the fake twin of the
    // real FRN-38 @104-108 (firstAttr=56 there: 56+24+24=104).
    private static byte[] UserRec((ulong C, ulong M, ulong H, ulong A) t) =>
        MftRecordBuilder.Live(Bpr, 7, SiAttr(t.C, t.M, t.H, t.A),
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((256, 512))));

    // (a) atime-only churn is invisible: the whole 8B accessed field steps
    // (low and high bytes — the B6AB shape) with everything else identical.
    [Fact]
    public void AtimeOnlyFlip_IsInvisible()
    {
        var t = BaseTimes();
        var @base = UserRec(t);
        var stepped = UserRec((t.C, t.M, t.H, t.A + 0x1_0000_0005UL));
        Assert.Equal(MftDigest.SkipRecordDigest(@base), MftDigest.SkipRecordDigest(stepped));
    }

    // (b) SAFETY PIN: a modified-time flip alone still vetoes. Masking
    // content+8 instead of +24 would blind this — the one-line difference
    // between a fix and a wrong-skip.
    [Fact]
    public void MtimeFlipAlone_StillVetoes()
    {
        var t = BaseTimes();
        var @base = UserRec(t);
        var mtime = UserRec((t.C, t.M + 1, t.H, t.A));
        Assert.NotEqual(MftDigest.SkipRecordDigest(@base), MftDigest.SkipRecordDigest(mtime));
        var ctime = UserRec((t.C + 1, t.M, t.H, t.A));
        Assert.NotEqual(MftDigest.SkipRecordDigest(@base), MftDigest.SkipRecordDigest(ctime));
        var chtime = UserRec((t.C, t.M, t.H + 1, t.A));
        Assert.NotEqual(MftDigest.SkipRecordDigest(@base), MftDigest.SkipRecordDigest(chtime));
    }

    // (c) same-size overwrite shape at record-gate level: data-run bytes
    // flip AND mtime moves (NTFS always bumps modified on a content
    // write) — vetoes. The atime mask cannot hide this: mtime is outside
    // the mask.
    [Fact]
    public void SameSizeOverwriteShape_Vetoes()
    {
        var t = BaseTimes();
        using var before = new FakeMftVolume(
            records: new Dictionary<long, byte[]> { [16] = UserRec(t) },
            recordCount: 20, length: 4L * Mib);
        var beforeList = MftDigest.ComputeGroupsAndRecords(before, Mib).Records;
        var t2 = (t.C, t.M + 0x100, t.H + 0x100, t.A + 0x100);
        var after = UserRec(t2);
        after[200] ^= 0xFF; // same-size content change inside the data-run bytes
        using var afterVol = new FakeMftVolume(
            records: new Dictionary<long, byte[]> { [16] = after },
            recordCount: 20, length: 4L * Mib);
        var afterList = MftDigest.ComputeGroupsAndRecords(afterVol, Mib).Records;
        Assert.NotEqual(beforeList[16], afterList[16]);
    }

    // Walks the attribute list from firstAttr and flips one content byte
    // of the named attribute — no hardcoded offsets.
    private static void FlipAttrContentByte(byte[] record, uint type, int contentRel)
    {
        var first = BitConverter.ToUInt16(record, 0x14);
        var used = BitConverter.ToUInt32(record, 0x18);
        var off = (int)first;
        while (off + 8 <= (int)used)
        {
            var t = BitConverter.ToUInt32(record, off);
            if (t == 0xFFFFFFFF)
                break;
            var len = (int)BitConverter.ToUInt32(record, off + 4);
            if (len < 8 || off + len > record.Length)
                break;
            if (t == type)
            {
                var cOff = BitConverter.ToUInt16(record, off + 20);
                record[off + cOff + contentRel] ^= 0xFF;
                return;
            }
            off += len;
        }
        throw new InvalidOperationException($"attr 0x{type:X} not found");
    }

    // (d) FRN-38-shape dir index change still vetoes: one $INDEX_ROOT byte
    // flips on a record that also carries SI (atime stepped, masked) —
    // the index dirt is outside every mask.
    [Fact]
    public void DirIndexChange_Vetoes()
    {
        var t = BaseTimes();
        var idx = new byte[48];
        BitConverter.GetBytes(0x90u).CopyTo(idx, 0);
        BitConverter.GetBytes(48u).CopyTo(idx, 4);
        idx[8] = 0;
        BitConverter.GetBytes(24u).CopyTo(idx, 16);
        BitConverter.GetBytes((ushort)24).CopyTo(idx, 20);
        for (var i = 24; i < 48; i++)
            idx[i] = (byte)(i + 1);
        var dirBase = MftRecordBuilder.LiveFlags(Bpr, 7, 3,
            SiAttr(t.C, t.M, t.H, t.A),
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((256, 512))), idx);
        var dirAtime = (byte[])dirBase.Clone();
        FlipAttrContentByte(dirAtime, 0x10, 24); // SI accessed field: invisible
        FlipAttrContentByte(dirAtime, 0x10, 31); // last atime byte too
        Assert.Equal(MftDigest.SkipRecordDigest(dirBase), MftDigest.SkipRecordDigest(dirAtime));
        var dirIndex = (byte[])dirBase.Clone();
        FlipAttrContentByte(dirIndex, 0x90, 5); // inside the $INDEX_ROOT content: visible
        Assert.NotEqual(MftDigest.SkipRecordDigest(dirBase), MftDigest.SkipRecordDigest(dirIndex));
    }

    // (a) end-to-end (B6AC run2 shape): gen2 carries only an atime step on
    // the live user record; the shadow LSN flap forces the record gate to
    // decide. Pre-stream skip, zero bulk reads, run2 log shape.
    [Fact]
    public void AtimeOnlyIdle_SkipsPreStream()
    {
        var vol = SeedVolume("atimeidle.img", 701, 702, 703, 704);
        var log = new TestLogger();
        using var store = NewStore(log);
        var t = BaseTimes();
        var gen1Live = SysUserRecords();
        using (var v1 = DualVol(new FileBlockSource(vol), SysUserRecords(), gen1Live, AllocBitmap()))
            store.ImageFast(v1, "gen1");

        var gen2Live = SysUserRecords();
        gen2Live[16] = UserRec((t.C, t.M, t.H, t.A + 0x1_0000_0005UL)); // atime step only
        var shadow2 = SysUserRecords();
        shadow2[16] = TouchLsn(shadow2[16]); // masked-only flap: plan gate vetoes, record gate decides
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using (var v2 = DualVol(counting, shadow2, gen2Live, AllocBitmap()))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
            Assert.Equal("unchanged since gen 1, skipping", ex.Message);
        }
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-plan:"));
        Assert.True(HasLine(log, "skip-records: 0 changed of 17 records, outside-sys: []"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-records-retry:"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    // (e) v4→v5 refuse-once-then-heal: a v4 manifest refuses the record
    // gate once (no skip-records line — the version drift refuses before
    // comparing) and the written gen carries v5; the next run skips.
    // Pins both halves of the mechanism: RecordsMatchExceptSys drift
    // refusal + ManifestStore.Build stamping SkipRecordVersion.
    [Fact]
    public void VersionBumpV4_RefusesOnceThenSelfHeals()
    {
        var vol = SeedVolume("atimever.img", 721, 722, 723, 724);
        using (var store = NewStore())
        using (var v1 = DualVol(new FileBlockSource(vol), SysUserRecords(), SysUserRecords(), null))
            store.ImageFast(v1, "gen1");
        PinRecordVersion(4);

        var log2 = new TestLogger();
        using (var store = NewStore(log2))
        {
            var shadow = SysUserRecords();
            shadow[16][1000] ^= 0xFF;
            var live = SysUserRecords();
            live[16][1000] ^= 0xFF;
            MutateBytes(vol, 2L * Mib + 1000, 100);
            using var v2 = DualVol(new FileBlockSource(vol), shadow, live, null);
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
            Assert.Equal(MftDigest.SkipRecordVersion, m.MftRecordDigestVersion);
            Assert.Equal(5, m.MftRecordDigestVersion);
        }
        Assert.Equal(2, ManifestCount());
        Assert.DoesNotContain(log2.Messages, m => m.Contains("skip-records:"));

        var log3 = new TestLogger();
        using (var store = NewStore(log3))
        {
            var shadow = SysUserRecords();
            shadow[16][1000] ^= 0xFF;
            shadow[16] = TouchLsn(shadow[16]);
            var live = SysUserRecords();
            live[16][1000] ^= 0xFF;
            using var inner = new FileBlockSource(vol);
            using var counting = new CountingBlockSource(inner, Mib);
            using var v3 = DualVol(counting, shadow, live, null);
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v3, "gen3", skipIfUnchanged: true));
            Assert.Equal(2, ex.Generation);
            Assert.Empty(counting.ReadChunks);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log3, "skip-records: 0 changed of 17 records, outside-sys: []"));
        Assert.DoesNotContain(log3.Messages, m => m.Contains("skip-compare:"));
        Assert.True(NewStore().Verify(2));
    }
}
