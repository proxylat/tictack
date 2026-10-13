using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Corpus;

// GC contract tests (store-rungs-spec §C.7). Pins the §C member shapes:
// every cross-type call below is an exact-name assertion.
public sealed class GcTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public GcTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_gc_" + Guid.NewGuid());
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

    private GarbageCollector NewGc(CorpusRetentionConfig retention, TestLogger log) =>
        new(_storeRoot, _dbPath, retention, log);

    private static CorpusRetentionConfig Keep(int last, int withinDays = 0, params int[] pins) =>
        new() { KeepLast = last, KeepWithinDays = withinDays, Pin = new List<int>(pins) };

    // DropManifests carries full manifest paths (code-lane shape) — compare
    // by generation filename prefix, order-agnostic.
    private static void AssertDrops(GcPlan plan, params int[] gens)
    {
        var got = plan.DropManifests.Select(p => Path.GetFileName(p)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(gens.Length, got.Count);
        foreach (var g in gens)
            Assert.Contains(got, f => f.StartsWith($"{g:D6}-", StringComparison.Ordinal));
    }

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

    // One 2 MiB volume per generation, distinct seeds → disjoint digests,
    // one fresh pack per image (dedup never fires across gens).
    private string SeedGenVolume(int gen, int seedBase = 1000)
    {
        var vol = Path.Combine(_dir, $"vol{gen}.img");
        WriteChunks(vol, RandomChunk(seedBase + gen * 2), RandomChunk(seedBase + gen * 2 + 1));
        return vol;
    }

    private void ImageGen(int gen, string? vol = null, ImageOptions? opts = null)
    {
        vol ??= SeedGenVolume(gen);
        using var store = NewStore();
        using var source = new FileBlockSource(vol);
        var m = opts == null ? store.Image(source, "gen" + gen) : store.Image(source, "gen" + gen, opts);
        Assert.Equal(gen, m.Generation);
    }

    private void ImageGens(int n)
    {
        for (var g = 1; g <= n; g++)
            ImageGen(g);
    }

    private SnapshotManifest LoadGen(int gen)
    {
        var manDir = Path.Combine(_storeRoot, "manifests");
        var paths = Directory.GetFiles(manDir, $"{gen:D6}-*.json");
        Assert.Single(paths);
        return ManifestStore.Load(paths[0], ChunkId.EnsureKey(_storeRoot));
    }

    private long IndexCount()
    {
        using var idx = new ChunkIndex(_dbPath);
        return idx.Count();
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

    private string StoreTreeHash() => TreeHash(_storeRoot);

    // Backdate CreatedUtc + re-HMAC (replicates ManifestStore.Save's seal:
    // HMAC-SHA256 over default-STJ JSON with Hmac="").
    private void ReDateManifest(int gen, DateTime utc)
    {
        var manDir = Path.Combine(_storeRoot, "manifests");
        var path = Assert.Single(Directory.GetFiles(manDir, $"{gen:D6}-*.json"));
        var key = ChunkId.EnsureKey(_storeRoot);
        var m = ManifestStore.Load(path, key);
        m.CreatedUtc = utc.ToString("o");
        m.Hmac = "";
        var json = JsonSerializer.Serialize(m);
        using var hmac = new HMACSHA256(key);
        m.Hmac = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        File.WriteAllText(path, JsonSerializer.Serialize(m));
    }

    [Fact]
    public void DryRunMutatesNothing()
    {
        ImageGens(3);
        var before = StoreTreeHash();
        var beforeCount = IndexCount();
        var plan = NewGc(Keep(1), new TestLogger()).Plan();
        AssertDrops(plan, 1, 2);
        Assert.Equal(before, StoreTreeHash());
        Assert.Equal(beforeCount, IndexCount());
        foreach (var g in new[] { 1, 2, 3 })
            Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), $"{g:D6}-*.json"));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_storeRoot, "packs"), "*.pack").Length);
    }

    [Fact]
    public void BareGcNeedsConfirm()
    {
        ImageGens(3);
        var cfg = WriteCliConfig();
        var (exit, stdout, stderr) = RunCorpus("--config", cfg, "--gc");
        Assert.Equal(1, exit);
        Assert.Contains("re-run with --confirm", stdout + stderr);
    }

    [Fact]
    public void KeepLast()
    {
        // Config-shape pin (§C.1): retention YAML keys + Validate rules.
        var yaml = Path.Combine(_dir, "ret.yaml");
        File.WriteAllText(yaml,
            "store_path: '" + _storeRoot + "'\n" +
            "state_db_path: '" + Path.GetDirectoryName(_dbPath) + "'\n" +
            "chunk_mib: 1\n" +
            "retention:\n  keep_last: 2\n  keep_within_days: 0\n  pin: []\n");
        var cfg = CorpusConfig.Load(yaml);
        Assert.NotNull(cfg);
        Assert.NotNull(cfg.Retention);
        Assert.Equal(2, cfg.Retention.KeepLast);
        Assert.Equal(0, cfg.Retention.KeepWithinDays);
        Assert.Empty(cfg.Retention.Pin);
        Assert.True(CorpusConfig.Validate(cfg, new TestLogger()));
        var bad = new CorpusConfig { StorePath = _storeRoot, Retention = new CorpusRetentionConfig { KeepLast = 0 } };
        Assert.False(CorpusConfig.Validate(bad, new TestLogger()));

        ImageGens(3);
        var gc = NewGc(new CorpusRetentionConfig { KeepLast = 2 }, new TestLogger());
        var plan = gc.Plan();
        AssertDrops(plan, 1);
        gc.Execute(plan);
        Assert.Empty(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000001-*.json"));
        using var store = NewStore();
        foreach (var g in new[] { 2, 3 })
        {
            Assert.True(store.Verify(g));
            var outPath = Path.Combine(_dir, $"keep{g}.img");
            store.Export(g, outPath);
            Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, $"vol{g}.img")), File.ReadAllBytes(outPath));
        }
    }

    [Fact]
    public void KeepWithin()
    {
        ImageGens(3);
        ReDateManifest(1, DateTime.UtcNow.AddDays(-60));
        // keep-last 1 + 30-day window: gen 2 kept by age, gen 1 pruned.
        var plan = NewGc(Keep(1, 30), new TestLogger()).Plan();
        AssertDrops(plan, 1);
        // Window off (0): only the last gen survives.
        var off = NewGc(Keep(1), new TestLogger()).Plan();
        AssertDrops(off, 1, 2);
    }

    [Fact]
    public void Pin()
    {
        ImageGens(3);
        var gc = NewGc(Keep(1, 0, 1), new TestLogger());
        var plan = gc.Plan();
        AssertDrops(plan, 2);
        gc.Execute(plan);
        Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000001-*.json"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000002-*.json"));
        Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000003-*.json"));
    }

    [Fact]
    public void UnknownPin()
    {
        ImageGens(3);
        var log = new TestLogger();
        var plan = NewGc(Keep(2, 0, 99), log).Plan();
        AssertDrops(plan, 1);
        Assert.Contains(plan.Notes, n => n.Contains("99"));
    }

    [Fact]
    public void WholeDeadPackUnlinked()
    {
        ImageGens(3);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_storeRoot, "packs"), "*.pack").Length);
        string pack1;
        using (var idx = new ChunkIndex(_dbPath))
        {
            var gen1 = LoadGen(1);
            Assert.True(idx.TryGet(Convert.FromHexString(gen1.Chunks[0].DigestHex), out var row));
            pack1 = row.Pack;
        }
        var gc = NewGc(Keep(1), new TestLogger());
        var plan = gc.Plan();
        Assert.Contains(plan.DropPacks, p => p == Path.Combine(_storeRoot, "packs", pack1));
        gc.Execute(plan);
        Assert.False(File.Exists(Path.Combine(_storeRoot, "packs", pack1)));
    }

    [Fact]
    public void PartialPackSurvives()
    {
        var vol = SeedGenVolume(1);
        ImageGen(1, vol);
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol))
            Assert.Equal(2, store.Image(source, "gen2").Generation);
        Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "packs"), "*.pack"));
        var gc = NewGc(Keep(1), new TestLogger());
        var plan = gc.Plan();
        AssertDrops(plan, 1);
        Assert.Empty(plan.DropPacks);
        gc.Execute(plan);
        Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "packs"), "*.pack"));
        using var store2 = NewStore();
        Assert.True(store2.Verify(2));
        var outPath = Path.Combine(_dir, "partial.img");
        store2.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void SweepRemovesDeadRows()
    {
        ImageGens(3);
        Assert.Equal(6, IndexCount());
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in new[] { 2, 3 })
            foreach (var e in LoadGen(g).Chunks)
                if (!e.IsHole)
                    retained.Add(e.DigestHex);
        Assert.Equal(4, retained.Count);

        // Index-level contract (§C.3): mark batch → live packs → unmarked stats.
        using (var idx = new ChunkIndex(_dbPath))
        {
            idx.SetAllMarks(0);
            idx.MarkBatch(retained.Select(h => Convert.FromHexString(h)).ToList());
            Assert.NotEmpty(idx.LivePacks());
            var stats = idx.UnmarkedStats();
            Assert.Equal(2, stats.Item1);
            Assert.True(stats.Item2 > 0);
            idx.SetAllMarks(1);
        }

        var gc = NewGc(Keep(2), new TestLogger());
        var plan = gc.Plan();
        Assert.True(plan.DeadChunks == 2);
        gc.Execute(plan);
        Assert.Equal(4, IndexCount());
    }

    [Fact]
    public void KeepsParents_ChildUnaffected()
    {
        var vol = SeedGenVolume(1);
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol))
            store.Image(source, "gen1");
        var clean = new FakeChangeSource((_, _, _) => new IncrementalPlan(new HashSet<long>(), "fake:1"));
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol))
            Assert.Equal(1, store.Image(source, "gen2",
                new ImageOptions(Incremental: true, ChangeSource: clean)).ParentGeneration);

        var gc = NewGc(Keep(1), new TestLogger());
        var plan = gc.Plan();
        AssertDrops(plan, 1);
        Assert.Contains(plan.Notes, n => n.Contains("provenance"));
        gc.Execute(plan);

        using var store2 = NewStore();
        Assert.True(store2.Verify(2));
        var outPath = Path.Combine(_dir, "child.img");
        store2.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void SkipsEncryptedScope()
    {
        KeyStore.Generate(_storeRoot);
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger(), new KeyStore(_storeRoot)))
        using (var source = new FileBlockSource(SeedGenVolume(1)))
            store.Image(source, "enc1");
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger(), new KeyStore(_storeRoot)))
        using (var source = new FileBlockSource(SeedGenVolume(2)))
            store.Image(source, "enc2");
        Directory.Delete(Path.Combine(_storeRoot, "keys"), true); // GC needs no keys
        var gc = NewGc(Keep(1), new TestLogger());
        var plan = gc.Plan();
        AssertDrops(plan, 1);
        gc.Execute(plan);
        Assert.Empty(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000001-*.json"));
        Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000002-*.json"));
    }

    [Fact]
    public void CorruptAbortsClean()
    {
        ImageGens(2);
        var manDir = Path.Combine(_storeRoot, "manifests");
        var victim = Assert.Single(Directory.GetFiles(manDir, "000001-*.json"));
        var json = File.ReadAllText(victim);
        var chars = json.ToCharArray();
        chars[json.Length / 2] = chars[json.Length / 2] == 'a' ? 'b' : 'a';
        File.WriteAllText(victim, new string(chars));
        var before = StoreTreeHash();
        var beforeCount = IndexCount();
        Assert.Throws<InvalidDataException>(() => NewGc(Keep(1), new TestLogger()).Plan());
        Assert.Equal(before, StoreTreeHash());
        Assert.Equal(beforeCount, IndexCount());
        Assert.Equal(2, Directory.GetFiles(manDir, "*.json").Length);
    }

    [Fact]
    public void TmpUntouched()
    {
        ImageGens(2);
        var tmp = Path.Combine(_storeRoot, "packs", "pack-deadbeef.pack.tmp");
        var body = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(tmp, body);
        var gc = NewGc(Keep(1), new TestLogger());
        gc.Execute(gc.Plan());
        Assert.Equal(body, File.ReadAllBytes(tmp));
    }

    [Fact]
    public void ExportsUntouched()
    {
        ImageGens(2);
        var exportPath = Path.Combine(_dir, "outside.img");
        using (var store = NewStore())
            store.Export(1, exportPath);
        var before = File.ReadAllBytes(exportPath);
        var gc = NewGc(Keep(1), new TestLogger());
        gc.Execute(gc.Plan()); // drops gen 1's manifest, never the export
        Assert.Equal(before, File.ReadAllBytes(exportPath));
    }

    [Fact]
    public void RerunConverges()
    {
        ImageGens(2);
        var gc = NewGc(Keep(1), new TestLogger());
        gc.Execute(gc.Plan());
        var again = NewGc(Keep(1), new TestLogger()).Plan();
        Assert.Empty(again.DropManifests);
        Assert.True(again.DeadChunks == 0);
    }

    [Fact]
    public void Cli_DryRunAndConfirm_Excluded()
    {
        ImageGens(1);
        var cfg = WriteCliConfig();
        var before = StoreTreeHash();
        var (exit, _, _) = RunCorpus("--config", cfg, "--gc", "--dry-run", "--confirm");
        Assert.Equal(1, exit);
        Assert.Equal(before, StoreTreeHash()); // refused run deletes nothing
    }

    private string WriteCliConfig()
    {
        var cfg = Path.Combine(_dir, "corpus.yaml");
        File.WriteAllText(cfg,
            "store_path: '" + _storeRoot + "'\n" +
            "state_db_path: '" + Path.GetDirectoryName(_dbPath) + "'\n" +
            "chunk_mib: 1\n" +
            "retention:\n  keep_last: 1\n" + // non-empty plan over the seeded gens
            "compression:\n  level: 3\n" +
            "logging:\n  level: info\n  path: '" + Path.Combine(_dir, "corpus.log") + "'\n  console: false\n");
        return cfg;
    }

    private static string CorpusDll()
    {
        var baseDir = AppContext.BaseDirectory;
        var configuration = new DirectoryInfo(baseDir).Parent!.Name;
        var tfm = new DirectoryInfo(baseDir).Name;
        var dll = Path.GetFullPath(Path.Combine(baseDir,
            "../../../../bin/" + configuration + "/" + tfm + "/Corpus.dll"));
        Assert.True(File.Exists(dll), "Corpus was not built: " + dll);
        return dll;
    }

    private static (int Exit, string Stdout, string Stderr) RunCorpus(params string[] args)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = host,
            WorkingDirectory = Path.GetDirectoryName(CorpusDll())!,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add(CorpusDll());
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        using var proc = Process.Start(startInfo)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(120_000), "Corpus CLI timed out: " + string.Join(' ', args));
        return (proc.ExitCode, stdout, stderr);
    }
}
