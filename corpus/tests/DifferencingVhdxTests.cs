using System.Text.Json;
using DiscUtils.Streams;
using DiscUtils.Vhdx;

namespace Corpus;

// §12.6 tests. Synthetic parents come from SingleFileBackup.BackupVhdx
// (the §12.1 writer — reused, never forked); the differencing round trip
// is fully Linux-provable on DiscUtils. Mount-level proof is a VM step.
// NOTE (wiring): this file needs `<Compile Include="../DifferencingVhdx.cs"
// Link="corpus/DifferencingVhdx.cs" />` added to Corpus.Tests.csproj.

public sealed class DifferencingVhdxTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly TestLogger _log = new();

    public DifferencingVhdxTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_diffvhdx_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void RoundTrip_DirtyChildVerifiesAndFallsThroughToParent()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var volB = Path.Combine(_dir, "b.img");
        File.Copy(volA, volB);
        FlipChunk(volB, 1);
        ZeroChunk(volB, 3);

        var child = Path.Combine(_dir, "incr.vhdx");
        using (var src = new FileBlockSource(volB))
            DifferencingVhdx.CreateDiff(src, "T:", child, parent, new HashSet<long> { 1, 3 }, Mib, _log);

        Assert.True(File.Exists(child));
        var sidecar = DifferencingVhdx.SidecarPath(child);
        Assert.True(File.Exists(sidecar));
        // Dirty-only child: 2 of 5 MiB plus metadata, well under the full.
        Assert.True(new FileInfo(child).Length < new FileInfo(parent).Length,
            $"child {new FileInfo(child).Length} vs parent {new FileInfo(parent).Length}");

        using (var src = new FileBlockSource(volB))
            DifferencingVhdx.VerifyDiff(child, src, _log);

        // Full pair read == mutated source: chunks 0,2,4 fall through to
        // the parent, 1 and 3 come from the child.
        Assert.Equal(File.ReadAllBytes(volB), ReadPair(child, 5L * Mib));
        // Zero-trap: chunk 3 was non-zero in the parent, zeros in the
        // child — fall-through must NOT resurrect the old bytes.
        var pair = ReadPair(child, 5L * Mib);
        Assert.All(pair.Skip(3 * Mib).Take(Mib), b => Assert.Equal(0, b));

        var record = JsonSerializer.Deserialize<DifferencingParentRecord>(File.ReadAllText(sidecar))!;
        Assert.Equal(Path.GetFullPath(parent), record.ParentPath);
        Assert.Equal(new FileInfo(parent).Length, record.ParentLength);
        Assert.Equal(5L * Mib, record.Capacity);
        Assert.Equal(new List<long> { 1, 3 }, record.DirtyChunks);
    }

    [Fact]
    public void Create_DirtyIndexOutsideSource_Refuses()
    {
        var parent = MakeParent(SeedVolume("a.img", seed: 7), "base.vhdx");
        using var src = new FileBlockSource(Path.Combine(_dir, "a.img"));
        var ex = Assert.Throws<DifferencingVhdxException>(() =>
            DifferencingVhdx.CreateDiff(src, "T:", Path.Combine(_dir, "c.vhdx"), parent, new HashSet<long> { 5 }, Mib, _log));
        Assert.Contains("chunk 5", ex.Message);
    }

    [Fact]
    public void Depth2_ParentIsDiff_RefusesNamingGrandparent()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var child1 = Path.Combine(_dir, "c1.vhdx");
        using (var src = new FileBlockSource(volA))
            DifferencingVhdx.CreateDiff(src, "T:", child1, parent, new HashSet<long> { 0 }, Mib, _log);

        using var src2 = new FileBlockSource(volA);
        var ex = Assert.Throws<DifferencingDepthException>(() =>
            DifferencingVhdx.CreateDiff(src2, "T:", Path.Combine(_dir, "c2.vhdx"), child1, new HashSet<long> { 0 }, Mib, _log));
        Assert.Contains(parent, ex.Message); // grandparent named
    }

    [Fact]
    public void MissingParent_RefusesNamingPath()
    {
        var missing = Path.Combine(_dir, "nope.vhdx");
        using var src = new FileBlockSource(SeedVolume("a.img", seed: 7));
        var ex = Assert.Throws<DifferencingParentMissingException>(() =>
            DifferencingVhdx.CreateDiff(src, "T:", Path.Combine(_dir, "c.vhdx"), missing, new HashSet<long> { 0 }, Mib, _log));
        Assert.Contains("nope.vhdx", ex.Message);
    }

    [Fact]
    public void Orphan_ParentMoved_VerifyRefusesNamingRecordedPath()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var child = Path.Combine(_dir, "c.vhdx");
        using (var src = new FileBlockSource(volA))
            DifferencingVhdx.CreateDiff(src, "T:", child, parent, new HashSet<long> { 0 }, Mib, _log);
        File.Move(parent, parent + ".moved");

        using var src2 = new FileBlockSource(volA);
        var ex = Assert.Throws<DifferencingOrphanException>(() =>
            DifferencingVhdx.VerifyDiff(child, src2, _log));
        Assert.Contains(parent, ex.Message);
        Assert.Contains("Set-VHD", ex.Message);
    }

    [Fact]
    public void ParentChanged_ByteFlip_VerifyRefusesOnHash()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var child = Path.Combine(_dir, "c.vhdx");
        using (var src = new FileBlockSource(volA))
            DifferencingVhdx.CreateDiff(src, "T:", child, parent, new HashSet<long> { 0 }, Mib, _log);

        var record = JsonSerializer.Deserialize<DifferencingParentRecord>(
            File.ReadAllText(DifferencingVhdx.SidecarPath(child)))!;
        using (var f = new FileStream(parent, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            f.Seek(1, SeekOrigin.Begin);
            f.WriteByte((byte)(ReadByte(parent, 1) ^ 0xFF));
        }
        // Same size+mtime, different bytes: only the hash leg can catch it.
        File.SetLastWriteTimeUtc(parent, new DateTime(record.ParentMtimeUtcTicks, DateTimeKind.Utc));

        using var src2 = new FileBlockSource(volA);
        var ex = Assert.Throws<DifferencingParentChangedException>(() =>
            DifferencingVhdx.VerifyDiff(child, src2, _log));
        Assert.Contains("SHA-256", ex.Message);
    }

    [Fact]
    public void ParentChanged_SizeChange_VerifyRefuses()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var child = Path.Combine(_dir, "c.vhdx");
        using (var src = new FileBlockSource(volA))
            DifferencingVhdx.CreateDiff(src, "T:", child, parent, new HashSet<long> { 0 }, Mib, _log);
        using (var f = new FileStream(parent, FileMode.Append, FileAccess.Write, FileShare.Read))
            f.Write(new byte[16], 0, 16);

        using var src2 = new FileBlockSource(volA);
        var ex = Assert.Throws<DifferencingParentChangedException>(() =>
            DifferencingVhdx.VerifyDiff(child, src2, _log));
        Assert.Contains("size mismatch", ex.Message);
    }

    [Fact]
    public void ParentChanged_MtimeOnly_VerifyRefuses()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var child = Path.Combine(_dir, "c.vhdx");
        using (var src = new FileBlockSource(volA))
            DifferencingVhdx.CreateDiff(src, "T:", child, parent, new HashSet<long> { 0 }, Mib, _log);
        File.SetLastWriteTimeUtc(parent, File.GetLastWriteTimeUtc(parent).AddHours(1));

        using var src2 = new FileBlockSource(volA);
        var ex = Assert.Throws<DifferencingParentChangedException>(() =>
            DifferencingVhdx.VerifyDiff(child, src2, _log));
        Assert.Contains("mtime mismatch", ex.Message);
    }

    [Fact]
    public void Create_CapacityMismatch_Refuses()
    {
        var parent = MakeParent(SeedVolume("a.img", seed: 7), "base.vhdx");
        var small = Path.Combine(_dir, "small.img");
        File.WriteAllBytes(small, new byte[3 * Mib]);
        using var src = new FileBlockSource(small);
        var ex = Assert.Throws<DifferencingParentChangedException>(() =>
            DifferencingVhdx.CreateDiff(src, "T:", Path.Combine(_dir, "c.vhdx"), parent, new HashSet<long> { 0 }, Mib, _log));
        Assert.Contains("size mismatch", ex.Message);
    }

    [Fact]
    public void FixedParent_RefusesAsNonFull()
    {
        var fixedPath = Path.Combine(_dir, "fixed.vhdx");
        using (var f = new FileStream(fixedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
        using (var disk = Disk.InitializeFixed(f, Ownership.None, 5L * Mib))
            disk.Content.Flush();
        using var src = new FileBlockSource(SeedVolume("a.img", seed: 7));
        var ex = Assert.Throws<DifferencingDepthException>(() =>
            DifferencingVhdx.CreateDiff(src, "T:", Path.Combine(_dir, "c.vhdx"), fixedPath, new HashSet<long> { 0 }, Mib, _log));
        Assert.Contains("fixed", ex.Message);
    }

    [Fact]
    public void DirtyPlan_OnlyTrueDirty()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var volB = Path.Combine(_dir, "b.img");
        File.Copy(volA, volB);
        FlipChunk(volB, 1);
        ZeroChunk(volB, 3);

        using (var src = new FileBlockSource(volB))
            Assert.Equal(new HashSet<long> { 1, 3 }, DifferencingVhdx.ComputeDirtyVsParent(src, parent, Mib, _log));
        // Identical live: empty plan, not hundreds of false dirty.
        using (var src = new FileBlockSource(volA))
            Assert.Empty(DifferencingVhdx.ComputeDirtyVsParent(src, parent, Mib, _log));
    }

    [Fact]
    public void DirtyPlan_MissingParent_Curated()
    {
        var missing = Path.Combine(_dir, "no-such-base.vhdx");
        using var src = new FileBlockSource(SeedVolume("a.img", seed: 7));
        // The dirty plan gates BEFORE opening: curated, never raw
        // FileNotFoundException + DiscUtils stack (Wave-2 B5 §7(b)).
        var ex = Assert.Throws<DifferencingParentMissingException>(() =>
            DifferencingVhdx.ComputeDirtyVsParent(src, missing, Mib, _log));
        Assert.Contains("no-such-base.vhdx", ex.Message);
    }

    [Fact]
    public void DirtyPlan_ForeignFile_Loud()
    {
        var junk = Path.Combine(_dir, "junk.vhdx");
        File.WriteAllText(junk, "not a vhdx");
        using var src = new FileBlockSource(SeedVolume("a.img", seed: 7));
        Assert.Throws<DifferencingVhdxException>(() =>
            DifferencingVhdx.ComputeDirtyVsParent(src, junk, Mib, _log));
    }

    [Fact]
    public void DirtyPlan_FirstPassGarbage_DroppedOnConfirm()
    {
        // 8 chunks: 0-1 data, 2-7 holes. Live truth adds data in hole 6
        // (true dirty); the first pass additionally sees garbage in 3-5.
        var truth = new byte[8 * Mib];
        new Random(7).NextBytes(truth.AsSpan(0, 2 * Mib));
        new Random(11).NextBytes(truth.AsSpan(6 * Mib, Mib));
        var volA = Path.Combine(_dir, "a.img");
        var baseBytes = new byte[8 * Mib];
        Array.Copy(truth, 0, baseBytes, 0, 2 * Mib);
        File.WriteAllBytes(volA, baseBytes);
        var parent = MakeParent(volA, "base.vhdx");
        var pass1 = (byte[])truth.Clone();
        for (var c = 3; c <= 5; c++)
            Array.Fill(pass1, (byte)0xAB, c * Mib, Mib);
        using var src = new ScriptedSource(pass1, truth);
        Assert.Equal(new HashSet<long> { 6 }, DifferencingVhdx.ComputeDirtyVsParent(src, parent, Mib, _log));
    }

    [Fact]
    public void DirtyPlan_UnstableLive_Refuses()
    {
        // Same candidate, different bytes per open: truth unknown, so the
        // plan refuses instead of risking a poisoned child.
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var a = File.ReadAllBytes(volA);
        var b = (byte[])a.Clone();
        Array.Fill(b, (byte)0x11, Mib, Mib);
        var c = (byte[])a.Clone();
        Array.Fill(c, (byte)0x22, Mib, Mib);
        using var src = new ScriptedSource(b, c);
        var ex = Assert.Throws<DifferencingVhdxException>(() =>
            DifferencingVhdx.ComputeDirtyVsParent(src, parent, Mib, _log));
        Assert.Contains("unstable", ex.Message);
    }

    [Fact]
    public void Verify_Mismatch_NamesParentByte()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var volB = Path.Combine(_dir, "b.img");
        File.Copy(volA, volB);
        FillChunk(volB, 1, 0x11);
        var child = Path.Combine(_dir, "c.vhdx");
        using (var src = new FileBlockSource(volB))
            DifferencingVhdx.CreateDiff(src, "T:", child, parent, new HashSet<long> { 1 }, Mib, _log);
        // Live moved on: the pair still holds 0x11, the source now 0x22.
        FillChunk(volB, 1, 0x22);
        using (var src = new FileBlockSource(volB))
        {
            var ex = Assert.Throws<DifferencingVhdxException>(() =>
                DifferencingVhdx.VerifyDiff(child, src, _log));
            Assert.Contains("parent 0x", ex.Message);
            Assert.Contains("IS present", ex.Message);
        }
    }

    [Fact]
    public void Create_Failure_RemovesPartialChild()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var b1 = File.ReadAllBytes(volA);
        var b2 = (byte[])b1.Clone();
        Array.Fill(b1, (byte)0x11, Mib, Mib);
        Array.Fill(b2, (byte)0x22, Mib, Mib);
        // Write pass stores 0x11, verify re-reads 0x22: refusal, and no
        // half-written child (or sidecar) may linger — it still mounts.
        var child = Path.Combine(_dir, "c.vhdx");
        using var src = new ScriptedSource(b1, b2);
        Assert.Throws<DifferencingVhdxException>(() =>
            DifferencingVhdx.CreateDiff(src, "T:", child, parent, new HashSet<long> { 1 }, Mib, _log));
        Assert.False(File.Exists(child));
        Assert.False(File.Exists(DifferencingVhdx.SidecarPath(child)));
    }

    [Fact]
    public void DirtyPlan_SparseHoles_NeverRead_PlanIsTrueDirty()
    {
        // B7d replay: 8 chunks, only chunks 0-1 allocated; hole reads
        // return per-open-varying garbage (same range, disagreeing bytes
        // across opens — the frozen-snapshot double-read disagreement).
        // Pre-fix the plan traverses the holes and refuses loud
        // (unstable); post-fix it never reads a hole byte and reports
        // exactly the true dirty chunk.
        var truth = new byte[8 * Mib];
        new Random(7).NextBytes(truth.AsSpan(0, 2 * Mib));
        var baseBytes = new byte[8 * Mib];
        Array.Copy(truth, 0, baseBytes, 0, Mib);
        var volA = Path.Combine(_dir, "a.img");
        File.WriteAllBytes(volA, baseBytes);
        var parent = MakeParent(volA, "base.vhdx");
        var runs = new (long Start, long Count)[] { (0, 256), (256, 256) };
        using var src = new GarbageHoleSource(truth, runs, 4096);
        Assert.Equal(new HashSet<long> { 1 }, DifferencingVhdx.ComputeDirtyVsParent(src, parent, Mib, _log));
        foreach (var (pos, len) in src.Reads)
            Assert.True(pos >= 0 && pos + len <= 2L * Mib, $"hole traversed at {pos}+{len}");

        // Write + verify ride the same extent-aware reads: the child round
        // trips through the pair even though holes were never read.
        var child = Path.Combine(_dir, "sparse.vhdx");
        DifferencingVhdx.CreateDiff(src, "T:", child, parent, new HashSet<long> { 1 }, Mib, _log);
        DifferencingVhdx.VerifyDiff(child, src, _log);
        foreach (var (pos, len) in src.Reads)
            Assert.True(pos >= 0 && pos + len <= 2L * Mib, $"hole traversed at {pos}+{len}");
    }

    [Fact]
    public void DiffPlan_LogsDirtyIndexesAndConfirmTrace()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var volB = Path.Combine(_dir, "b.img");
        File.Copy(volA, volB);
        FlipChunk(volB, 1);
        ZeroChunk(volB, 3);

        using (var src = new FileBlockSource(volB))
            Assert.Equal(new HashSet<long> { 1, 3 }, DifferencingVhdx.ComputeDirtyVsParent(src, parent, Mib, _log));
        var plan = Assert.Single(_log.Messages, m => m.StartsWith("INF:diff plan vs ", StringComparison.Ordinal));
        Assert.Contains("2 dirty of 5 chunks [1, 3]", plan);
        var t1 = Assert.Single(_log.Messages, m => m.StartsWith("INF:diff confirm chunk 1: ", StringComparison.Ordinal));
        Assert.Matches(@"^INF:diff confirm chunk 1: pass1=[0-9A-F]{64} pass2=[0-9A-F]{64} parentEqual=false decision=kept$", t1);
        var t3 = Assert.Single(_log.Messages, m => m.StartsWith("INF:diff confirm chunk 3: ", StringComparison.Ordinal));
        Assert.Contains("parentEqual=false decision=kept", t3);
    }

    [Fact]
    public void DiffPlan_LargeDirtySet_CapsIndexList()
    {
        const int chunks = 40;
        var volA = Path.Combine(_dir, "a40.img");
        using (var f = new FileStream(volA, FileMode.Create, FileAccess.Write))
        {
            var rng = new Random(7);
            var buf = new byte[Mib];
            for (var c = 0; c < chunks; c++)
            {
                rng.NextBytes(buf);
                buf[0] = (byte)(c + 1);
                f.Write(buf, 0, buf.Length);
            }
        }
        var parent = MakeParent(volA, "base40.vhdx");
        var volB = Path.Combine(_dir, "b40.img");
        using (var f = new FileStream(volB, FileMode.Create, FileAccess.Write))
        {
            var rng = new Random(8);
            var buf = new byte[Mib];
            for (var c = 0; c < chunks; c++)
            {
                rng.NextBytes(buf);
                buf[0] = (byte)(255 - c); // never equals volA's first byte (c+1) for c < 127
                f.Write(buf, 0, buf.Length);
            }
        }

        using (var src = new FileBlockSource(volB))
            Assert.Equal(chunks, DifferencingVhdx.ComputeDirtyVsParent(src, parent, Mib, _log).Count);
        var plan = Assert.Single(_log.Messages, m => m.StartsWith("INF:diff plan vs ", StringComparison.Ordinal));
        Assert.Contains($"{chunks} dirty of {chunks} chunks [0, 1, 2", plan);
        Assert.Contains("(+8 more)]", plan);
    }

    [Fact]
    public void DirtyPlan_UnstableLive_NamesUnstableIndexes()
    {
        var volA = SeedVolume("a.img", seed: 7);
        var parent = MakeParent(volA, "base.vhdx");
        var a = File.ReadAllBytes(volA);
        var b = (byte[])a.Clone();
        Array.Fill(b, (byte)0x11, Mib, Mib);
        var c = (byte[])a.Clone();
        Array.Fill(c, (byte)0x22, Mib, Mib);
        using var src = new ScriptedSource(b, c);
        var ex = Assert.Throws<DifferencingVhdxException>(() =>
            DifferencingVhdx.ComputeDirtyVsParent(src, parent, Mib, _log));
        Assert.Contains("unstable", ex.Message);
        Assert.Contains("unstable chunks: [1]", ex.Message);
        // The 3-way trace lands BEFORE the throw, so a refused diff still
        // leaves its candidate indexes in the log.
        Assert.Contains(_log.Messages, m => m.StartsWith("INF:diff confirm chunk 1: ", StringComparison.Ordinal)
            && m.Contains("decision=unstable", StringComparison.Ordinal));
    }

    [Fact]
    public void ParentGate_AbsentKey_Equals_ExplicitEmpty()
    {
        // The --parent/exclusions gate input: an absent `exclude` key
        // (null) and an explicit `exclude: []` gate identically (both
        // empty — the refusal is reached only for explicit names).
        Assert.Empty(Store.ExplicitExclusionNames(null, []));
        Assert.Empty(Store.ExplicitExclusionNames([], []));
        Assert.Equal(Store.ExplicitExclusionNames(null, []), Store.ExplicitExclusionNames([], []));
        // Explicit names on either side still refuse; $LogFile never counts.
        Assert.Equal(new[] { "pagefile.sys" }, Store.ExplicitExclusionNames(["pagefile.sys"], []));
        Assert.Equal(new[] { "custom.sys" }, Store.ExplicitExclusionNames(null, ["custom.sys"]));
        Assert.Empty(Store.ExplicitExclusionNames(["$LogFile"], []));
        // Sanity: the MERGED set still restores defaults for an absent key
        // (capture-time policy is untouched — only the --parent gate changed).
        Assert.NotEmpty(Store.MergeExclusionNames(null, []));
    }

    // IBlockSource with scripted bytes per open (first open = pass 1,
    // later opens = confirm/write/verify passes).
    private sealed class ScriptedSource : IBlockSource
    {
        private readonly byte[][] _opens;
        private int _n;

        public ScriptedSource(params byte[][] opens)
        {
            _opens = opens;
        }

        public long Length => _opens[0].Length;

        public Stream OpenRead()
        {
            var i = Math.Min(_n++, _opens.Length - 1);
            return new MemoryStream(_opens[i], writable: false);
        }

        public void Dispose()
        {
        }
    }

    // B7d shadow replay: allocated ranges read stable truth bytes, hole
    // clusters return per-open-varying garbage (two reads of one range
    // disagree). Every served read range is recorded for the never-
    // traverse-holes assertion.
    private sealed class GarbageHoleSource : ISparseBlockSource
    {
        private readonly byte[] _truth;
        private readonly IReadOnlyList<(long Start, long Count)> _runs;
        private readonly long _bpc;
        private int _opens;

        public List<(long Pos, int Len)> Reads { get; } = new();

        public GarbageHoleSource(byte[] truth, IReadOnlyList<(long Start, long Count)> runs, long bpc)
        {
            _truth = truth;
            _runs = runs;
            _bpc = bpc;
        }

        public long Length => _truth.Length;

        public long BytesPerCluster => _bpc;

        public IReadOnlyList<(long Start, long Count)> GetAllocatedRuns() => _runs;

        public Stream OpenRead()
        {
            var gen = _opens++;
            var buf = (byte[])_truth.Clone();
            for (long cluster = 0; cluster * _bpc < buf.Length; cluster++)
            {
                if (IsHole(cluster))
                    Array.Fill(buf, (byte)(0xA0 + gen), (int)(cluster * _bpc), (int)Math.Min(_bpc, buf.Length - cluster * _bpc));
            }
            return new RecordingStream(buf, Reads);
        }

        private bool IsHole(long cluster)
        {
            foreach (var (start, count) in _runs)
            {
                if (cluster >= start && cluster < start + count)
                    return false;
            }
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly List<(long Pos, int Len)> _reads;

        public RecordingStream(byte[] data, List<(long Pos, int Len)> reads)
        {
            _inner = new MemoryStream(data, writable: false);
            _reads = reads;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var pos = _inner.Position;
            var n = _inner.Read(buffer, offset, count);
            if (n > 0)
                _reads.Add((pos, n));
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static void FillChunk(string volPath, int chunk, byte fill)
    {
        using var f = new FileStream(volPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        f.Seek(chunk * Mib, SeekOrigin.Begin);
        var buf = new byte[Mib];
        Array.Fill(buf, fill);
        f.Write(buf, 0, buf.Length);
    }

    private string SeedVolume(string name, int seed)
    {
        var path = Path.Combine(_dir, name);
        var rng = new Random(seed);
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        var buf = new byte[Mib];
        for (var c = 0; c < 5; c++)
        {
            rng.NextBytes(buf);
            buf[0] = (byte)(c + 1); // every chunk non-zero: the zero-trap test needs it
            f.Write(buf, 0, buf.Length);
        }
        return path;
    }

    // The full parent is a §12.1 single-file backup — the house writer.
    private string MakeParent(string volPath, string name)
    {
        using var src = new FileBlockSource(volPath);
        return SingleFileBackup.BackupVhdx(src, "T:", Path.Combine(_dir, name),
            VhdxWriterKind.DiscUtils, null, _log);
    }

    private static void FlipChunk(string volPath, int chunk)
    {
        var data = File.ReadAllBytes(volPath);
        for (var i = chunk * Mib; i < (chunk + 1) * Mib; i++)
            data[i] = (byte)(data[i] ^ 0xFF);
        // XOR of 0xFF could still hold zeros only if input held 0xFFs —
        // force the first byte non-zero either way.
        if (data[chunk * Mib] == 0) data[chunk * Mib] = 0x5A;
        File.WriteAllBytes(volPath, data);
    }

    private static void ZeroChunk(string volPath, int chunk)
    {
        using var f = new FileStream(volPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        f.Seek(chunk * Mib, SeekOrigin.Begin);
        f.Write(new byte[Mib], 0, Mib);
    }

    private static byte ReadByte(string path, long offset)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        f.Seek(offset, SeekOrigin.Begin);
        var b = f.ReadByte();
        Assert.True(b >= 0, "short parent read");
        return (byte)b;
    }

    private static byte[] ReadPair(string childPath, long length)
    {
        using var disk = new Disk(childPath, FileAccess.Read);
        var back = new byte[length];
        disk.Content.Seek(0, SeekOrigin.Begin);
        var read = 0;
        while (read < back.Length)
        {
            var n = disk.Content.Read(back, read, back.Length - read);
            Assert.True(n > 0, "pair content ended early at " + read);
            read += n;
        }
        return back;
    }
}
