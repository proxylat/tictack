namespace Corpus;

// --exclude lane: merge policy is Linux-provable (pure names); extent
// zeroing + manifest recording go through the item-4 IExclusionResolver
// seam with a fake (same shape as CapturePolicyTests.FakeExcl).
public sealed class ExcludeTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private const long Bpc = 4096;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;
    private readonly TestLogger _log = new();

    public ExcludeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_excl_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
        var zeros = new byte[Mib];
        var rand = new byte[Mib];
        new Random(7).NextBytes(rand);
        using var f = new FileStream(_volPath, FileMode.Create, FileAccess.Write);
        f.Write(zeros, 0, zeros.Length);
        f.Write(rand, 0, rand.Length);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class FakeExcl : IExclusionResolver
    {
        private readonly IReadOnlyList<(long StartLcn, long ClusterCount)> _runs;

        public FakeExcl(IReadOnlyList<string> names, IReadOnlyList<(long, long)> runs)
        {
            ExclusionNames = names;
            _runs = runs;
        }

        public IReadOnlyList<string> ExclusionNames { get; }
        public long BytesPerCluster => Bpc;
        public IReadOnlyList<(long StartLcn, long ClusterCount)> ExcludedRuns() => _runs;
    }

    private Store NewStore() => new(_storeRoot, _dbPath, 1, 3, _log);

    private static FakeChangeSource Clean() =>
        new((_, _, _) => new IncrementalPlan(new HashSet<long>(), "fake:1"));

    [Fact]
    public void Merge_UnionsConfigAndCli_WithoutRegainingDefaults()
    {
        var merged = Store.MergeExclusionNames(["custom.sys"], ["other.sys"]);
        Assert.Equal(["custom.sys", "other.sys"], merged);
        Assert.DoesNotContain("pagefile.sys", merged);
    }

    [Fact]
    public void Merge_NullConfigKeepsDefaults()
    {
        var merged = Store.MergeExclusionNames(null, ["x.sys"]);
        Assert.Contains("pagefile.sys", merged);
        Assert.Contains("x.sys", merged);
    }

    [Fact]
    public void Merge_StripsLogFile_AndDedups()
    {
        var merged = Store.MergeExclusionNames(["Temp"], ["temp", "$LogFile", "pagefile.sys"]);
        Assert.DoesNotContain(merged, e => e.Equals("$LogFile", StringComparison.OrdinalIgnoreCase));
        Assert.Single(merged, e => e.Equals("Temp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("pagefile.sys", merged);
    }

    [Fact]
    public void Manifest_RecordsExclusions_AndZeroesRanges()
    {
        // Runs cover chunk 0 exactly (256 clusters x 4 KiB = 1 MiB).
        using var store = NewStore();
        SnapshotManifest m;
        using (var source = new FileBlockSource(_volPath))
            m = store.Image(source, "excl", new ImageOptions(Exclusions: new FakeExcl(["pagefile.sys"], [(0, 256)])));
        Assert.Equal(["pagefile.sys"], m.SkippedExclusions);

        var outPath = Path.Combine(_dir, "out.img");
        store.Export(1, outPath);
        var got = File.ReadAllBytes(outPath);
        Assert.Equal(new byte[Mib], got[..Mib]);
        Assert.Equal(File.ReadAllBytes(_volPath)[Mib..], got[Mib..]);
    }

    [Fact]
    public void ExclusionSetChange_ForcesFull()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent", new ImageOptions(Exclusions: new FakeExcl(["a.sys"], [])));

        using var counting = new CountingBlockSource(_volPath, Mib);
        var child = store.Image(counting, "child",
            new ImageOptions(Incremental: true, ChangeSource: Clean(), Exclusions: new FakeExcl(["b.sys"], [])));

        Assert.Equal(0, child.ParentGeneration);
        Assert.Equal([0L, 1L], counting.ReadChunks.OrderBy(c => c).ToList());
    }

    [Fact]
    public void SameExclusionSet_AllowsIncremental()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent", new ImageOptions(Exclusions: new FakeExcl(["a.sys"], [])));

        using var counting = new CountingBlockSource(_volPath, Mib);
        var child = store.Image(counting, "child",
            new ImageOptions(Incremental: true, ChangeSource: Clean(), Exclusions: new FakeExcl(["a.sys"], [])));

        Assert.Equal(1, child.ParentGeneration);
        Assert.Empty(counting.ReadChunks);
    }
}
