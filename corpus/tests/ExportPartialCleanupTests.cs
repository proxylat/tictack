namespace Corpus;

// BUG-B: a mid-write export failure left a corrupt partial output behind,
// so the next --clone-from built on corruption. Every file-creating export
// flavor deletes its partial output on failure (best-effort) and names the
// deletion in the log. Fault model here: the store is damaged (packs wiped),
// so ExportToStream throws mid-write like the missing-chunk case on-box.
public sealed class ExportPartialCleanupTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public ExportPartialCleanupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_exportclean_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
        var data = new byte[2 * Mib];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(_volPath, data);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void DamagePacks(string storeRoot)
    {
        var packs = Path.Combine(storeRoot, "packs");
        foreach (var f in Directory.EnumerateFiles(packs))
            File.Delete(f);
    }

    [Fact]
    public void Export_MidWriteFailure_DeletesPartialOutput()
    {
        var log = new TestLogger();
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, log))
        {
            using (var s = new FileBlockSource(_volPath))
                store.Image(s, "g1");
            DamagePacks(_storeRoot);
            var outPath = Path.Combine(_dir, "out.img");
            Assert.ThrowsAny<Exception>(() => store.Export(1, outPath));
            Assert.False(File.Exists(outPath));
        }
        Assert.Contains(log.Messages, m => m.Contains("deleted partial export", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportVhdx_MidWriteFailure_DeletesPartialOutput()
    {
        var log = new TestLogger();
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, log))
        {
            using (var s = new FileBlockSource(_volPath))
                store.Image(s, "g1");
            DamagePacks(_storeRoot);
            var outPath = Path.Combine(_dir, "out.vhdx");
            Assert.ThrowsAny<Exception>(() => store.ExportVhdx(1, outPath));
            Assert.False(File.Exists(outPath));
        }
        Assert.Contains(log.Messages, m => m.Contains("deleted partial export", StringComparison.Ordinal));
    }

    [Fact]
    public void CloneFrom_MidWriteFailure_DeletesPartialOutput()
    {
        var log = new TestLogger();
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, log))
        {
            using (var s = new FileBlockSource(_volPath))
                store.Image(s, "g1");
            var baseVhdx = Path.Combine(_dir, "g1.vhdx");
            store.ExportVhdx(1, baseVhdx);
            using (var s = new FileBlockSource(_volPath))
                store.Image(s, "g2");
            DamagePacks(_storeRoot);
            var dest = Path.Combine(_dir, "g2.vhdx");
            Assert.ThrowsAny<Exception>(() =>
                ReflinkExport.ExportVhdxFromClone(store, 2, dest, baseVhdx, log, new NoCloneFallback()));
            Assert.False(File.Exists(dest));
        }
        Assert.Contains(log.Messages, m => m.Contains("deleted partial reflink export", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_GreenPath_KeepsFile()
    {
        using var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger());
        using (var s = new FileBlockSource(_volPath))
            store.Image(s, "g1");
        var outPath = Path.Combine(_dir, "out.img");
        store.Export(1, outPath);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
    }

    // Plain-copy fallback without a block-clone API (Linux has none).
    private sealed class NoCloneFallback : IFileCloner
    {
        public bool TryClone(string sourcePath, string destPath, out string reason)
        {
            File.Copy(sourcePath, destPath, true);
            reason = "";
            return true;
        }
    }
}
