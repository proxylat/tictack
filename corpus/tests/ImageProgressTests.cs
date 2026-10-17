namespace Corpus;

using System.Globalization;
using System.Text.RegularExpressions;
using TicTack;

// §12.10 image progress lines: `--image` (volume, landing, disk) logs
// restore-shaped `image N% (M MB/s)` at 2 Hz. Seeded fake-source tests:
// lines appear, monotonic, end at 100%, completion line unchanged.
public sealed class ImageProgressTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public ImageProgressTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_imgprog_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Store NewStore(OrderedLogger log) =>
        new(_storeRoot, _dbPath, 1, 3, log);

    // TestLogger is an unordered bag — progress monotonicity needs order.
    private sealed class OrderedLogger : ILogger
    {
        private readonly object _gate = new();
        public readonly List<string> Lines = new();
        private void Add(string line) { lock (_gate) Lines.Add(line); }
        public void Debug(string msg) => Add("DBG:" + msg);
        public void Info(string msg) => Add("INF:" + msg);
        public void Warn(string msg) => Add("WRN:" + msg);
        public void Error(string msg, Exception? ex = null) => Add("ERR:" + msg);
    }

    private static readonly Regex ProgressLine =
        new(@"^INF:image (\d+)% \(([0-9.]+) MB/s\)$", RegexOptions.Compiled);

    // Returns the parsed percentages in log order.
    private static List<int> ProgressPcts(OrderedLogger log)
    {
        var pcts = new List<int>();
        foreach (var line in log.Lines)
        {
            var m = ProgressLine.Match(line);
            if (m.Success)
                pcts.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        return pcts;
    }

    private static void AssertProgressMonotonic100(OrderedLogger log)
    {
        var pcts = ProgressPcts(log);
        Assert.NotEmpty(pcts);
        var prev = 0;
        foreach (var p in pcts)
        {
            Assert.InRange(p, 0, 100);
            Assert.True(p >= prev, $"progress went backwards: {prev} -> {p}");
            prev = p;
        }
        Assert.Equal(100, pcts[pcts.Count - 1]);
    }

    private static void AssertNoCarriageReturn(OrderedLogger log)
    {
        Assert.DoesNotContain(log.Lines, l => l.Contains('\r'));
    }

    private static byte[] RandomChunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private string SeedVolume(string name, params byte[][] chunks)
    {
        var vol = Path.Combine(_dir, name);
        using var f = new FileStream(vol, FileMode.Create, FileAccess.Write, FileShare.Read);
        foreach (var c in chunks)
            f.Write(c, 0, c.Length);
        return vol;
    }

    private static PartitionImageInput Part(int number, string uniqueGuid, string file, string filesystem,
        IBlockSource? source, IChangeSource? change)
    {
        var lengthBytes = new FileInfo(file).Length;
        var firstLba = 2048L + (number - 1) * 8192L;
        return new PartitionImageInput(number, $"part{number}", "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
            uniqueGuid, firstLba, firstLba + lengthBytes / 512 - 1, firstLba * 512, lengthBytes,
            filesystem, false, null, source, change);
    }

    private static string NewGuid() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    [Fact]
    public void FastCapture_Progress_Monotonic_EndsAt100()
    {
        var vol = SeedVolume("vol.img", RandomChunk(201), RandomChunk(202), RandomChunk(203), RandomChunk(204));
        var log = new OrderedLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(vol))
            store.ImageFast(source, "fast1");
        AssertProgressMonotonic100(log);
        AssertNoCarriageReturn(log);
        Assert.Contains(log.Lines, l => l.Contains("fast-imaged 'fast1' gen 1"));
    }

    [Fact]
    public void ClassicFull_Progress_Monotonic_EndsAt100()
    {
        var vol = SeedVolume("vol.img", RandomChunk(211), RandomChunk(212), RandomChunk(213), RandomChunk(214));
        var log = new OrderedLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(vol))
            store.Image(source, "gen1");
        AssertProgressMonotonic100(log);
        AssertNoCarriageReturn(log);
        Assert.Contains(log.Lines, l => l.Contains("imaged 'gen1' gen 1"));
    }

    [Fact]
    public void Incremental_Progress_EndsAt100()
    {
        var vol = SeedVolume("vol.img", RandomChunk(221), RandomChunk(222), RandomChunk(223), RandomChunk(224));
        using (var store = NewStore(new OrderedLogger()))
        using (var source = new FileBlockSource(vol))
            store.Image(source, "gen1");
        using (var f = new FileStream(vol, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            f.Seek(2 * Mib, SeekOrigin.Begin);
            f.Write(RandomChunk(225), 0, Mib);
        }
        var log = new OrderedLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(vol))
            store.Image(source, "gen2", new ImageOptions(Incremental: true,
                ChangeSource: new FakeChangeSource((_, _, _) =>
                    new IncrementalPlan(new HashSet<long> { 2 }, "fake:1"))));
        AssertProgressMonotonic100(log);
        Assert.Contains(log.Lines, l => l.Contains("incremental gen 2 from parent 1"));
    }

    [Fact]
    public void Disk_Progress_Aggregated_Monotonic_EndsAt100()
    {
        var aPath = Path.Combine(_dir, "pa.img");
        var bPath = Path.Combine(_dir, "pb.img");
        SeedVolume("pa.img", RandomChunk(231), RandomChunk(232));
        SeedVolume("pb.img", RandomChunk(233));
        const long diskSize = 32L * Mib;
        var log = new OrderedLogger();
        using (var store = NewStore(log))
        using (var a = new FileBlockSource(aPath))
        using (var b = new FileBlockSource(bPath))
        {
            store.ImageDisk(0, "disk0", new DiskImageOptions(), new DiskCapturePlan("disk-guid-imgprog", diskSize, 512,
                new List<PartitionImageInput>
                {
                    Part(1, NewGuid(), aPath, "ntfs", a, null),
                    Part(2, NewGuid(), bPath, "ntfs", b, null),
                }));
        }
        // One aggregated series over 3 MiB: monotonic, ends at 100%.
        AssertProgressMonotonic100(log);
        AssertNoCarriageReturn(log);
        Assert.Contains(log.Lines, l => l.Contains("imaged disk 0 'disk0'"));
    }

    [Fact]
    public void SparseFast_Progress_EndsAt100()
    {
        // 4-chunk file, only the first half allocated: denominator =
        // allocated bytes, holes advance nothing, series still ends 100%.
        var vol = SeedVolume("vol.img", RandomChunk(241), RandomChunk(242), RandomChunk(243), RandomChunk(244));
        var log = new OrderedLogger();
        using (var store = NewStore(log))
        using (var file = new FileBlockSource(vol))
        using (var sparse = new FakeMftVolume(inner: file,
                   allocRuns: new List<(long Start, long Count)> { (0, 512) }))
            store.ImageFast(sparse, "fast1");
        AssertProgressMonotonic100(log);
        using (var store = NewStore(new OrderedLogger()))
            Assert.True(store.Verify(1));
    }
}
