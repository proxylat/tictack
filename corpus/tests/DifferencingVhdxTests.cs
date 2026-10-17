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
