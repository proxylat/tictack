using DiscUtils.Vhdx;

namespace Corpus;

// §12.5 tests. The clone itself is faked (Linux has no block-clone API;
// real ReFS proof is a VM step): the fake records clone-vs-write ops,
// dirty-only writes are pinned at unit level (filter) and behaviorally
// (a dirty zero-chunk leaves cloned bytes behind; tampering a clean
// range fails the verify).
public sealed class ReflinkExportTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;
    private readonly byte[] _random;

    public ReflinkExportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_reflink_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
        _random = new byte[Mib];
        new Random(42).NextBytes(_random);
        SeedVolume();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // 5 MiB, chunkMib 1: zeros | random | zeros | random | text.
    private void SeedVolume()
    {
        var text = new byte[Mib];
        var pattern = "The quick brown fox jumps over the lazy dog. "u8;
        for (var i = 0; i < text.Length; i++)
            text[i] = pattern[i % pattern.Length];
        using var f = new FileStream(_volPath, FileMode.Create, FileAccess.Write);
        f.Write(new byte[Mib], 0, Mib);
        f.Write(_random, 0, Mib);
        f.Write(new byte[Mib], 0, Mib);
        f.Write(_random, 0, Mib);
        f.Write(text, 0, Mib);
    }

    private void MutateVolume(long offset, byte[] bytes)
    {
        using var f = new FileStream(_volPath, FileMode.Open, FileAccess.Write);
        f.Seek(offset, SeekOrigin.Begin);
        f.Write(bytes, 0, bytes.Length);
    }

    private Store OpenStore() => new(_storeRoot, _dbPath, 1, 3, new TestLogger());

    private static byte[] ExportBytes(Store store, int gen)
    {
        using var ms = new MemoryStream();
        store.ExportToStream(gen, ms);
        return ms.ToArray();
    }

    private static byte[] ReadAllContent(string vhdxPath, long length)
    {
        using var disk = new Disk(vhdxPath, FileAccess.Read);
        disk.Content.Seek(0, SeekOrigin.Begin);
        var back = new byte[length];
        var read = 0;
        while (read < back.Length)
        {
            var n = disk.Content.Read(back, read, back.Length - read);
            Assert.True(n > 0, "VHDX content ended early at " + read);
            read += n;
        }
        return back;
    }

    // Recording clone seam: Result=true simulates a clone via File.Copy.
    internal sealed class FakeCloner : IFileCloner
    {
        public int Calls;
        public string? LastSource;
        public string? LastDest;
        public bool Result = true;
        public string Reason = "";
        public Action<string, string>? InsteadOfCopy;

        public bool TryClone(string sourcePath, string destPath, out string reason)
        {
            Calls++;
            LastSource = sourcePath;
            LastDest = destPath;
            if (Result)
            {
                if (InsteadOfCopy != null)
                    InsteadOfCopy(sourcePath, destPath);
                else
                    File.Copy(sourcePath, destPath, true);
            }
            reason = Reason;
            return Result;
        }
    }

    [Fact]
    public void BlockCloner_NullArgs_Throw()
    {
        var c = new BlockCloner();
        Assert.Throws<ArgumentNullException>(() => c.TryClone(null!, "d", out _));
        Assert.Throws<ArgumentNullException>(() => c.TryClone("s", null!, out _));
    }

    [Fact]
    public void BlockCloner_OffWindows_ReturnsFalse_NeverThrows()
    {
        var src = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
        var ok = new BlockCloner().TryClone(src, Path.Combine(_dir, "b.bin"), out var reason);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(ok);
            Assert.Contains("Windows", reason, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotNull(reason); // ReFS-dependent; only no-throw is pinned on Windows
        }
    }

    [Fact]
    public void DiffDirtyChunks_IdenticalManifests_Empty()
    {
        var chunks = new List<ManifestEntry> { new("aa", 10), ManifestEntry.Hole(5) };
        var a = ManifestStore.Build(1, "a", 15, 1, 3, "k", "", chunks);
        var b = ManifestStore.Build(2, "b", 15, 1, 3, "k", "", new List<ManifestEntry>(chunks));
        Assert.Empty(ReflinkExport.DiffDirtyChunks(a, b));
    }

    [Fact]
    public void DiffDirtyChunks_FlagsDigestSizeAndHoleChangesOnly()
    {
        var a = ManifestStore.Build(1, "a", 40, 1, 3, "k", "", new List<ManifestEntry>
            { new("aa", 10), new("bb", 10), new("cc", 10), ManifestEntry.Hole(10) });
        var b = ManifestStore.Build(2, "b", 40, 1, 3, "k", "", new List<ManifestEntry>
            { new("aa", 10), new("XX", 10), new("cc", 11), new("dd", 10) });
        Assert.Equal(new[] { 1, 2, 3 }, ReflinkExport.DiffDirtyChunks(a, b));
    }

    [Fact]
    public void DiffDirtyChunks_CountMismatch_Throws()
    {
        var a = ManifestStore.Build(1, "a", 10, 1, 3, "k", "", new List<ManifestEntry> { new("aa", 10) });
        var b = ManifestStore.Build(2, "b", 20, 1, 3, "k", "", new List<ManifestEntry> { new("aa", 10), new("bb", 10) });
        Assert.Throws<InvalidDataException>(() => ReflinkExport.DiffDirtyChunks(a, b));
    }

    [Fact]
    public void DirtyRangeWriteFilter_DropsCleanWrites_AdvancesPosition()
    {
        var manifest = ManifestStore.Build(1, "a", 30, 1, 3, "k", "", new List<ManifestEntry>
            { new("aa", 10), new("bb", 10), new("cc", 10) });
        var inner = new MemoryStream(new byte[30]);
        using var filter = new DirtyRangeWriteFilter(inner, manifest, new[] { 1 });
        var buf = new byte[30];
        for (var i = 0; i < 30; i++)
            buf[i] = (byte)(i / 5 + 1); // groups of 5: 1,1,..,2,2,..,3,3..
        filter.Write(buf, 0, 30);
        Assert.Equal(30, filter.Position);
        Assert.Equal(10, filter.WrittenBytes);
        Assert.Equal(20, filter.SkippedBytes);
        var got = inner.ToArray();
        Assert.True(got.Take(10).All(b => b == 0));
        Assert.Equal(buf.Skip(10).Take(10).ToArray(), got.Skip(10).Take(10).ToArray());
        Assert.True(got.Skip(20).All(b => b == 0));
        // Backward seek then write still hits dirty ranges (cursor reset).
        filter.Seek(12, SeekOrigin.Begin);
        filter.Write(new byte[] { 9, 9, 9 }, 0, 3);
        Assert.Equal(13, filter.WrittenBytes);
        Assert.Equal(new byte[] { 9, 9, 9 }, inner.ToArray().Skip(12).Take(3).ToArray());
    }

    // gen1 imaged + exported, chunk 1 mutated, gen2 imaged. Dirty = [1].
    private string SeedTwoGens(Store store)
    {
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g1");
        var baseVhdx = Path.Combine(_dir, "g1.vhdx");
        store.ExportVhdx(1, baseVhdx);
        MutateVolume(Mib + 100, new byte[] { 9, 9, 9, 9 });
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g2");
        return baseVhdx;
    }

    [Fact]
    public void CloneExport_ByteIdenticalToPlainExport()
    {
        using var store = OpenStore();
        var baseVhdx = SeedTwoGens(store);
        var log = new TestLogger();
        var clone = new FakeCloner();
        var dest = Path.Combine(_dir, "g2.vhdx");
        ReflinkExport.ExportVhdxFromClone(store, 2, dest, baseVhdx, log, clone);
        Assert.Equal(1, clone.Calls);
        Assert.Equal(baseVhdx, clone.LastSource);
        Assert.Equal(dest, clone.LastDest);
        Assert.Equal(ExportBytes(store, 2), ReadAllContent(dest, 5L * Mib));
        Assert.Contains(log.Messages, m => m.Contains("1 dirty chunks", StringComparison.Ordinal));
        Assert.Contains(log.Messages, m => m.Contains("COW-shared", StringComparison.Ordinal));
    }

    [Fact]
    public void CloneExport_IdenticalGens_PureClone_Verifies()
    {
        using var store = OpenStore();
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g1");
        var baseVhdx = Path.Combine(_dir, "g1.vhdx");
        store.ExportVhdx(1, baseVhdx);
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g2"); // unchanged: zero dirty chunks
        var log = new TestLogger();
        var dest = Path.Combine(_dir, "g2.vhdx");
        ReflinkExport.ExportVhdxFromClone(store, 2, dest, baseVhdx, log, new FakeCloner());
        Assert.Equal(ExportBytes(store, 2), ReadAllContent(dest, 5L * Mib));
        Assert.Contains(log.Messages, m => m.Contains("0 dirty chunks", StringComparison.Ordinal));
    }

    [Fact]
    public void CloneExport_Fallback_WarnsAndStillCorrect()
    {
        using var store = OpenStore();
        var baseVhdx = SeedTwoGens(store);
        var log = new TestLogger();
        var clone = new FakeCloner { Result = false, Reason = "filesystem is NTFS, not ReFS (block clone is ReFS-only)" };
        var dest = Path.Combine(_dir, "g2.vhdx");
        ReflinkExport.ExportVhdxFromClone(store, 2, dest, baseVhdx, log, clone);
        Assert.Equal(1, clone.Calls);
        Assert.Contains(log.Messages, m => m.StartsWith("WRN:", StringComparison.Ordinal) && m.Contains("NTFS", StringComparison.Ordinal));
        Assert.Equal(ExportBytes(store, 2), ReadAllContent(dest, 5L * Mib));
    }

    [Fact]
    public void CloneExport_MissingCloneFrom_FailsLoud()
    {
        using var store = OpenStore();
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g1");
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g2");
        var fake = new FakeCloner();
        var ex = Assert.Throws<FileNotFoundException>(() =>
            ReflinkExport.ExportVhdxFromClone(store, 2, Path.Combine(_dir, "x.vhdx"), Path.Combine(_dir, "nope.vhdx"), new TestLogger(), fake));
        Assert.Contains("nope.vhdx", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void CloneExport_FirstGen_RefusesLoud_BeforeCloning()
    {
        using var store = OpenStore();
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g1");
        var dummy = Path.Combine(_dir, "dummy.vhdx");
        File.WriteAllBytes(dummy, new byte[] { 1 });
        var fake = new FakeCloner();
        var ex = Assert.Throws<InvalidDataException>(() =>
            ReflinkExport.ExportVhdxFromClone(store, 1, Path.Combine(_dir, "x.vhdx"), dummy, new TestLogger(), fake));
        Assert.Contains("gen 0", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void CloneExport_MissingBaseGen_RefusesLoud()
    {
        using var store = OpenStore();
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g1");
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g2");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(_storeRoot, "manifests"), "000001-*.json"))
            File.Delete(f);
        var dummy = Path.Combine(_dir, "dummy.vhdx");
        File.WriteAllBytes(dummy, new byte[] { 1 });
        var ex = Assert.Throws<InvalidDataException>(() =>
            ReflinkExport.ExportVhdxFromClone(store, 2, Path.Combine(_dir, "x.vhdx"), dummy, new TestLogger(), new FakeCloner()));
        Assert.Contains("gen 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CloneExport_GridMismatch_RefusesLoud_BeforeCloning()
    {
        using var store = OpenStore();
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g1");
        var big = Path.Combine(_dir, "big.img");
        File.WriteAllBytes(big, new byte[6 * Mib]);
        using (var s = new FileBlockSource(big))
            store.Image(s, "g2");
        var dummy = Path.Combine(_dir, "dummy.vhdx");
        File.WriteAllBytes(dummy, new byte[] { 1 });
        var fake = new FakeCloner();
        var ex = Assert.Throws<InvalidDataException>(() =>
            ReflinkExport.ExportVhdxFromClone(store, 2, Path.Combine(_dir, "x.vhdx"), dummy, new TestLogger(), fake));
        Assert.Contains("differs", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void CloneExport_WrongBaseCapacity_RefusesLoud_BeforeCloning()
    {
        using var store = OpenStore();
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g1");
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g2");
        // Foreign base: valid VHDX, different capacity, from a throwaway store.
        var foreign = Path.Combine(_dir, "foreign.vhdx");
        using (var other = new Store(Path.Combine(_dir, "other"), Path.Combine(_dir, "otherdb", "c.db"), 1, 3, new TestLogger()))
        {
            var big = Path.Combine(_dir, "big.img");
            File.WriteAllBytes(big, new byte[6 * Mib]);
            using var src = new FileBlockSource(big);
            other.Image(src, "o1");
            other.ExportVhdx(1, foreign);
        }
        var fake = new FakeCloner();
        var ex = Assert.Throws<InvalidDataException>(() =>
            ReflinkExport.ExportVhdxFromClone(store, 2, Path.Combine(_dir, "x.vhdx"), foreign, new TestLogger(), fake));
        Assert.Contains("capacity", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void CloneExport_TamperedClone_FailsVerifyAndDeletesDest()
    {
        using var store = OpenStore();
        var baseVhdx = SeedTwoGens(store);
        var clone = new FakeCloner
        {
            InsteadOfCopy = (s, d) =>
            {
                File.Copy(s, d, true);
                // Tamper a CLEAN data range (chunk 4 text, starts with 'T').
                using var disk = new Disk(d, FileAccess.ReadWrite);
                disk.Content.Seek(4L * Mib, SeekOrigin.Begin);
                disk.Content.WriteByte(0xFF);
            },
        };
        var dest = Path.Combine(_dir, "g2.vhdx");
        var ex = Assert.Throws<InvalidDataException>(() =>
            ReflinkExport.ExportVhdxFromClone(store, 2, dest, baseVhdx, new TestLogger(), clone));
        Assert.Contains("reflink-verify FAILED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("offset " + (4L * Mib), ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void CloneExport_DirtyZeroChunk_LeavesClonedBytesAndLogsGaps()
    {
        using var store = OpenStore();
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g1");
        var baseVhdx = Path.Combine(_dir, "g1.vhdx");
        store.ExportVhdx(1, baseVhdx);
        // Zero out chunk 1 (random data): gen2 chunk 1 is a dirty zero-chunk
        // (seeked, never written, never verified).
        using (var f = new FileStream(_volPath, FileMode.Open, FileAccess.Write))
        {
            f.Seek(Mib, SeekOrigin.Begin);
            f.Write(new byte[Mib], 0, Mib);
        }
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g2");
        var log = new TestLogger();
        var dest = Path.Combine(_dir, "g2.vhdx");
        ReflinkExport.ExportVhdxFromClone(store, 2, dest, baseVhdx, log, new FakeCloner());
        // Cloned stale bytes survive (don't-care free space):
        Assert.Equal(_random, ReadAllContent(dest, 5L * Mib).Skip(Mib).Take(Mib).ToArray());
        Assert.Contains(log.Messages, m => m.Contains("gap bytes left as cloned", StringComparison.Ordinal));
    }
}
