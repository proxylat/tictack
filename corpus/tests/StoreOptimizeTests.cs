using System.Security.Cryptography;
using System.Text;

namespace Corpus;

// --optimize-to-fit contract tests (system-image.md §12.2): compact
// reclaims dead bytes, verify greens after, headroom refusal fires before
// touching anything, the budget loop stops at the right gen, and pinned
// gens that block the budget fail loud.
public sealed class StoreOptimizeTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public StoreOptimizeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_opt_" + Guid.NewGuid());
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

    private StoreOptimizer NewOptimizer(CorpusRetentionConfig? retention = null, TestLogger? log = null) =>
        new(_storeRoot, _dbPath, retention ?? new CorpusRetentionConfig(), log ?? new TestLogger(), 1, 3);

    private static byte[] RandomChunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static void WriteChunks(string path, params byte[][] chunks)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        foreach (var c in chunks)
            f.Write(c, 0, c.Length);
    }

    private string SeedVolume(string name, params byte[][] chunks)
    {
        var vol = Path.Combine(_dir, name);
        WriteChunks(vol, chunks);
        return vol;
    }

    // One 2 MiB volume per generation, distinct seeds → disjoint digests,
    // one fresh pack per image.
    private void ImageDisjointGen(int gen)
    {
        var vol = SeedVolume($"vol{gen}.img", RandomChunk(1000 + gen * 2), RandomChunk(1000 + gen * 2 + 1));
        using var store = NewStore();
        using var source = new FileBlockSource(vol);
        Assert.Equal(gen, store.Image(source, "gen" + gen).Generation);
    }

    private static string TreeHash(string root)
    {
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToList();
        using var sha = SHA256.Create();
        foreach (var f in files)
        {
            var relBytes = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, f));
            sha.TransformBlock(relBytes, 0, relBytes.Length, null, 0);
            var body = File.ReadAllBytes(f);
            if (body.Length > 0)
                sha.TransformBlock(body, 0, body.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private string PackOfGenChunk(int gen, int chunkIndex)
    {
        var manDir = Path.Combine(_storeRoot, "manifests");
        var path = Assert.Single(Directory.GetFiles(manDir, $"{gen:D6}-*.json"));
        var manifest = ManifestStore.Load(path, ChunkId.EnsureKey(_storeRoot));
        using var index = new ChunkIndex(_dbPath);
        Assert.True(index.TryGet(Convert.FromHexString(manifest.Chunks[chunkIndex].DigestHex), out var row));
        return row.Pack;
    }

    private long StoreSize()
    {
        var total = 0L;
        foreach (var path in Directory.EnumerateFiles(_storeRoot, "*", SearchOption.AllDirectories))
            total += new FileInfo(path).Length;
        return total;
    }

    [Fact]
    public void CompactReclaimsDeadBytes_VerifyGreens_ExportIdentical()
    {
        // gen1 = A+B, gen2 = C+B: B dedups into gen1's pack. Pruning gen1
        // leaves that pack half-dead (A dead, B live) — compact rewrites it.
        var chunkA = RandomChunk(11);
        var chunkB = RandomChunk(12);
        var chunkC = RandomChunk(13);
        var vol1 = SeedVolume("vol1.img", chunkA, chunkB);
        var vol2 = SeedVolume("vol2.img", chunkC, chunkB);
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol1))
            Assert.Equal(1, store.Image(source, "gen1").Generation);
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol2))
            Assert.Equal(2, store.Image(source, "gen2").Generation);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_storeRoot, "packs"), "*.pack").Length);
        var gc = new GarbageCollector(_storeRoot, _dbPath, new CorpusRetentionConfig { KeepLast = 1 }, new TestLogger());
        gc.Execute(gc.Plan()); // pack survives (B live), A is dead bytes inside it

        var before = StoreSize();
        var result = NewOptimizer().Optimize();
        var after = StoreSize();

        Assert.True(result.CompactFreedBytes > 0);
        Assert.Equal(before - after, result.CompactFreedBytes);
        Assert.Empty(result.PrunedGenerations);
        Assert.Contains("compact freed", result.Report);
        using var store2 = NewStore();
        Assert.True(store2.Verify(2));
        var outPath = Path.Combine(_dir, "compact.img");
        store2.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol2), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void CompactIsIdempotent_SecondRunFreesZero()
    {
        ImageDisjointGen(1);
        var first = NewOptimizer().Optimize();
        Assert.Equal(0, first.CompactFreedBytes); // disjoint single gen is already dense
        var second = NewOptimizer().Optimize();
        Assert.Equal(0, second.CompactFreedBytes);
        Assert.Equal(first.AfterBytes, second.AfterBytes);
    }

    [Fact]
    public void OrphanPack_CleanedOnNextRun()
    {
        ImageDisjointGen(1);
        var packsDir = Path.Combine(_storeRoot, "packs");
        var victim = Assert.Single(Directory.GetFiles(packsDir, "*.pack"));
        // Crash orphan: sealed pack with no index rows (seal landed, upsert lost).
        var orphan = Path.Combine(packsDir, "pack-orphan00000000.pack");
        File.Copy(victim, orphan);
        var orphanLen = new FileInfo(orphan).Length;
        var before = StoreSize();

        var result = NewOptimizer().Optimize();

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(victim));
        Assert.True(result.CompactFreedBytes > 0);
        Assert.Equal(orphanLen, before - StoreSize()); // size dropped by exactly the orphan
        using var store = NewStore();
        Assert.True(store.Verify(1));
    }

    // Windows denies plain reads on files another handle holds (the
    // optimizer's pooled sqlite connection can outlive the throw).
    private static byte[] ReadDbBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void HeadroomRefusal_FiresBeforeMutation()
    {
        ImageDisjointGen(1);
        ImageDisjointGen(2);
        var beforeTree = TreeHash(_storeRoot);
        var beforeDb = ReadDbBytes(_dbPath);

        var optimizer = NewOptimizer();
        optimizer.FreeSpaceOverride = _ => 0;
        var ex = Assert.Throws<StoreOptimizeHeadroomException>(() => optimizer.Optimize());

        Assert.Contains(_storeRoot, ex.Message); // names the store path
        Assert.Contains("pack-", ex.Message); // names the blocking pack
        Assert.Equal(beforeTree, TreeHash(_storeRoot)); // nothing touched
        Assert.Equal(beforeDb, ReadDbBytes(_dbPath)); // index untouched too
    }

    [Fact]
    public void HeadroomGuard_PassesWhenFreeEqualsLargest()
    {
        ImageDisjointGen(1);
        var packsDir = Path.Combine(_storeRoot, "packs");
        var largest = Directory.GetFiles(packsDir, "*.pack").Max(p => new FileInfo(p).Length);

        var optimizer = NewOptimizer();
        optimizer.FreeSpaceOverride = _ => largest; // boundary: free == largest fits
        var result = optimizer.Optimize();

        Assert.Equal(0, result.CompactFreedBytes);
    }

    [Fact]
    public void BudgetLoop_StopsAtRightGen()
    {
        ImageDisjointGen(1);
        ImageDisjointGen(2);
        ImageDisjointGen(3);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_storeRoot, "packs"), "*.pack").Length);
        var total = StoreSize();
        var pack1Len = new FileInfo(Path.Combine(_storeRoot, "packs", PackOfGenChunk(1, 0))).Length;
        var man1Len = new FileInfo(Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000001-*.json"))).Length;
        var budget = total - pack1Len - man1Len; // pruning exactly gen 1 fits

        var result = NewOptimizer().Optimize(budget);

        Assert.Equal(new[] { 1 }, result.PrunedGenerations);
        Assert.True(StoreSize() <= budget);
        Assert.Contains("pruned gens 1", result.Report);
        Assert.Contains("compact freed", result.Report);
        using var store = NewStore();
        Assert.True(store.Verify(2));
        Assert.True(store.Verify(3));
        var outPath = Path.Combine(_dir, "kept2.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "vol2.img")), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void BudgetLoop_PrunesMultiple_WhenNeeded()
    {
        ImageDisjointGen(1);
        ImageDisjointGen(2);
        ImageDisjointGen(3);
        var total = StoreSize();
        var packLen = 0L;
        var manLen = 0L;
        foreach (var gen in new[] { 1, 2 })
        {
            packLen += new FileInfo(Path.Combine(_storeRoot, "packs", PackOfGenChunk(gen, 0))).Length;
            manLen += new FileInfo(Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), $"{gen:D6}-*.json"))).Length;
        }
        var budget = total - packLen - manLen; // only gen 3 fits

        var result = NewOptimizer().Optimize(budget);

        Assert.Equal(new[] { 1, 2 }, result.PrunedGenerations);
        Assert.True(StoreSize() <= budget);
        Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000003-*.json"));
        using var store = NewStore();
        Assert.True(store.Verify(3));
    }

    [Fact]
    public void BudgetBlocked_PinnedGen_FailsLoud_NeverDeletesPinned()
    {
        ImageDisjointGen(1);
        ImageDisjointGen(2);
        ImageDisjointGen(3);
        var total = StoreSize();
        var pack2Len = new FileInfo(Path.Combine(_storeRoot, "packs", PackOfGenChunk(2, 0))).Length;
        var man2Len = new FileInfo(Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000002-*.json"))).Length;
        // Forces gen 1's removal too — but gen 1 is pinned, so this must fail.
        var budget = total - pack2Len - man2Len - 1;
        var retention = new CorpusRetentionConfig { KeepLast = 1, Pin = new List<int> { 1 } };

        var ex = Assert.Throws<StoreOptimizeBudgetException>(() => NewOptimizer(retention).Optimize(budget));

        Assert.Contains("gen 1", ex.Message);
        Assert.Contains("pinned", ex.Message);
        Assert.Contains(budget.ToString(), ex.Message);
        Assert.Contains(_storeRoot, ex.Message);
        Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000001-*.json")); // pinned survives
        Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000003-*.json")); // newest survives
        Assert.Empty(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000002-*.json")); // prunable gen still pruned
        using var store = NewStore();
        Assert.True(store.Verify(1));
        Assert.True(store.Verify(3));
    }

    [Fact]
    public void BudgetAlreadyMet_PrunesNothing()
    {
        ImageDisjointGen(1);
        ImageDisjointGen(2);
        var total = StoreSize();

        var result = NewOptimizer().Optimize(total);

        Assert.Empty(result.PrunedGenerations);
        Assert.Equal(0, result.PruneFreedBytes);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "*.json").Length);
    }

    [Fact]
    public void InvalidBudget_Refuses()
    {
        ImageDisjointGen(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => NewOptimizer().Optimize(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewOptimizer().Optimize(-5));
    }

    [Fact]
    public void EmptyStore_Succeeds_NoCrash()
    {
        using (NewStore()) { } // skeleton only (id.key + dirs), no snapshots
        var result = NewOptimizer().Optimize();
        Assert.Equal(0, result.CompactFreedBytes);
        Assert.Empty(result.PrunedGenerations);
        Assert.Contains("store", result.Report);
    }
}
