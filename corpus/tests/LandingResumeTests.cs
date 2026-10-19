namespace Corpus;

// §12.13 capture resume from landing: kill mid-capture → resume →
// byte-matches the uninterrupted baseline; stale fingerprints discard
// loud. Kill is simulated by a stream that dies after N bytes, then a
// new Store is constructed over the same dirs (the crashed process is
// gone — dispose-then-recreate is the faithful restart).
public sealed class LandingResumeTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;
    private readonly string _volPath;

    public LandingResumeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_resume_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        _volPath = Path.Combine(_dir, "vol.img");
        Directory.CreateDirectory(_dir);
        WriteChunks(_volPath, RandomChunk(101), RandomChunk(102), RandomChunk(103), RandomChunk(104));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Store NewStore(TestLogger? log = null) =>
        new(_storeRoot, _dbPath, 1, 3, log ?? new TestLogger());

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

    private string LandingDir() => Path.Combine(_storeRoot, "landing");

    private int LandedCount() =>
        Directory.Exists(LandingDir()) ? Directory.GetFiles(LandingDir(), "*.chunk").Length : 0;

    // Stream that dies after budget bytes (simulated SIGKILL mid-read).
    private sealed class KillerStream : Stream
    {
        private readonly Stream _inner;
        private long _budget;

        public KillerStream(Stream inner, long budget)
        {
            _inner = inner;
            _budget = budget;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_budget <= 0)
                throw new IOException("simulated kill");
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, _budget));
            _budget -= n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class KillerSource : IBlockSource
    {
        private readonly string _path;
        private readonly long _budget;
        private bool _disposed;

        public KillerSource(string path, long budget)
        {
            _path = path;
            _budget = budget;
        }

        public long Length => new FileInfo(_path).Length;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new KillerStream(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), _budget);
        }

        public void Dispose() => _disposed = true;
    }

    private sealed class FakeExclusions : IExclusionResolver
    {
        public IReadOnlyList<string> ExclusionNames { get; }
        public long BytesPerCluster => 4096;
        public FakeExclusions(params string[] names) => ExclusionNames = names;
        public IReadOnlyList<(long StartLcn, long ClusterCount)> ExcludedRuns() =>
            Array.Empty<(long, long)>();
    }

    // Memory-backed source with a layout fingerprint (the volume-id leg
    // FileBlockSource cannot exercise — it exposes no identity).
    private sealed class IdSource : IBlockSource, ILayoutFingerprintProvider
    {
        private readonly byte[] _data;
        private readonly string _id;
        private readonly long _killAfter;

        public IdSource(byte[] data, string id, long killAfter = long.MaxValue)
        {
            _data = data;
            _id = id;
            _killAfter = killAfter;
        }

        public long Length => _data.Length;
        public string? LayoutFingerprint() => _id;

        public Stream OpenRead() =>
            _killAfter == long.MaxValue
                ? new MemoryStream(_data, false)
                : new KillerStream(new MemoryStream(_data, false), _killAfter);

        public void Dispose() { }
    }

    private static byte[] ExportBytes(Store store, int gen, string outPath)
    {
        store.Export(gen, outPath);
        return File.ReadAllBytes(outPath);
    }

    private static void AssertHasLine(TestLogger log, string fragment)
    {
        Assert.Contains(log.Messages, m => m.Contains(fragment, StringComparison.Ordinal));
    }

    private static void AssertNoLine(TestLogger log, string fragment)
    {
        Assert.DoesNotContain(log.Messages, m => m.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void KillResume_ByteMatchesBaseline()
    {
        // Uninterrupted baseline in isolated dirs.
        var baseDir = Path.Combine(_dir, "baseline");
        var baseVol = Path.Combine(_dir, "vol.img");
        byte[] baseline;
        using (var store = new Store(Path.Combine(baseDir, "store"), Path.Combine(baseDir, "state", "corpus-chunks.db"), 1, 3, new TestLogger()))
        using (var source = new FileBlockSource(baseVol))
        {
            store.ImageFast(source, "fast1");
            baseline = ExportBytes(store, 1, Path.Combine(baseDir, "rt.img"));
        }

        // Kill after 2.5 chunks: 2 land, the third read dies.
        using (var store = NewStore())
        using (var killer = new KillerSource(_volPath, (2 * Mib) + (Mib / 2)))
            Assert.Throws<IOException>(() => store.ImageFast(killer, "fast1"));
        Assert.Equal(2, LandedCount());
        Assert.True(File.Exists(Path.Combine(LandingDir(), "resume.json")));
        Assert.Empty(Directory.GetFiles(LandingDir(), "*.tmp"));

        // Restart: new Store over the same dirs, healthy source.
        var log = new TestLogger();
        byte[] resumed;
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(_volPath))
        {
            var m = store.ImageFast(source, "fast1");
            Assert.Equal(1, m.Generation);
            Assert.True(store.Verify(1));
            resumed = ExportBytes(store, 1, Path.Combine(_dir, "resumed.img"));
        }
        AssertHasLine(log, "resuming: 2/4 chunks already landed");
        Assert.Equal(baseline, resumed);
    }

    [Fact]
    public void CommittedLanding_SurvivesExclusionChangeDiscard()
    {
        // BUG-A: default fast captures seal nothing (packs come at
        // optimize), so gens 1+ live in landing/ until then. An
        // exclusion change correctly discards the STALE resume — but
        // chunks committed manifests reference must survive, else
        // verify 1 fails `missing from index` with no GC ever run.
        using (var store = NewStore())
        using (var source = new FileBlockSource(_volPath))
            store.ImageFast(source, "gen1");
        var original = File.ReadAllBytes(_volPath);
        WriteChunks(_volPath, RandomChunk(201), RandomChunk(202), RandomChunk(203), RandomChunk(204));

        var log = new TestLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(_volPath))
            store.ImageFast(source, "gen2",
                new ImageOptions(Exclusions: new FakeExclusions("hiberfil.sys")));
        AssertHasLine(log, "landing resume discarded (exclusion set changed");
        using (var store = NewStore())
        {
            Assert.Equal(2, store.ListSnapshots().Count);
            Assert.True(store.Verify(1));
            Assert.True(store.Verify(2));
            Assert.Equal(original, ExportBytes(store, 1, Path.Combine(_dir, "kept1.img")));
            Assert.Equal(File.ReadAllBytes(_volPath), ExportBytes(store, 2, Path.Combine(_dir, "kept2.img")));
        }
    }

    [Fact]
    public void StaleFingerprint_Resize_Discards()
    {
        using (var store = NewStore())
        using (var killer = new KillerSource(_volPath, (2 * Mib) + (Mib / 2)))
            Assert.Throws<IOException>(() => store.ImageFast(killer, "fast1"));
        Assert.Equal(2, LandedCount());

        // Resize: one more chunk (length leg changes).
        using (var f = new FileStream(_volPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            f.Write(RandomChunk(105), 0, Mib);

        var log = new TestLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(_volPath))
        {
            store.ImageFast(source, "fast1");
            Assert.True(store.Verify(1));
            var back = ExportBytes(store, 1, Path.Combine(_dir, "grown.img"));
            Assert.Equal(File.ReadAllBytes(_volPath), back);
        }
        AssertHasLine(log, "landing resume discarded (volume length changed");
        AssertNoLine(log, "resuming:");
    }

    [Fact]
    public void StaleFingerprint_ExcludesChange_Discards()
    {
        using (var store = NewStore())
        using (var killer = new KillerSource(_volPath, (2 * Mib) + (Mib / 2)))
            Assert.Throws<IOException>(() => store.ImageFast(killer, "fast1",
                new ImageOptions(Exclusions: new FakeExclusions("pagefile.sys"))));
        Assert.Equal(2, LandedCount());

        var log = new TestLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(_volPath))
        {
            store.ImageFast(source, "fast1",
                new ImageOptions(Exclusions: new FakeExclusions("hiberfil.sys")));
            Assert.True(store.Verify(1));
            var back = ExportBytes(store, 1, Path.Combine(_dir, "excl.img"));
            Assert.Equal(File.ReadAllBytes(_volPath), back);
        }
        AssertHasLine(log, "landing resume discarded (exclusion set changed");
        AssertNoLine(log, "resuming:");
    }

    [Fact]
    public void StaleFingerprint_VolumeIdChange_Discards()
    {
        var data = File.ReadAllBytes(_volPath);
        using (var store = NewStore())
            Assert.Throws<IOException>(() => store.ImageFast(new IdSource(data, "vol-A", (2 * Mib) + (Mib / 2)), "fast1"));
        Assert.Equal(2, LandedCount());

        var log = new TestLogger();
        using (var store = NewStore(log))
        {
            store.ImageFast(new IdSource(data, "vol-B"), "fast1");
            Assert.True(store.Verify(1));
            var back = ExportBytes(store, 1, Path.Combine(_dir, "vid.img"));
            Assert.Equal(data, back);
        }
        AssertHasLine(log, "landing resume discarded (volume id changed (vol-A != vol-B)");
        AssertNoLine(log, "resuming:");
    }

    [Fact]
    public void TornChunk_Rehashed_NotTrusted()
    {
        using (var store = NewStore())
        using (var killer = new KillerSource(_volPath, (2 * Mib) + (Mib / 2)))
            Assert.Throws<IOException>(() => store.ImageFast(killer, "fast1"));
        Assert.Equal(2, LandedCount());

        // Flip a byte in one landed chunk (same name, wrong bytes).
        var victim = Directory.GetFiles(LandingDir(), "*.chunk")[0];
        var bytes = File.ReadAllBytes(victim);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(victim, bytes);

        var log = new TestLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(_volPath))
        {
            store.ImageFast(source, "fast1");
            Assert.True(store.Verify(1));
            var back = ExportBytes(store, 1, Path.Combine(_dir, "torn.img"));
            Assert.Equal(File.ReadAllBytes(_volPath), back);
        }
        // Only the intact chunk counted as already landed.
        AssertHasLine(log, "resuming: 1/4 chunks already landed");
    }

    [Fact]
    public void SkipWithStaleLanding_SkipsAndLaterResumes()
    {
        // Skip × resume coexistence: gen1 committed, run2 killed mid-stream
        // (partial landing, no manifest), run3 --skip-if-unchanged skips
        // without clobbering the resumable landing, run4 resumes to a
        // verifying gen. File sources have no MFT plan, so the skip comes
        // from the post-stream zero-new path after resume read-through.
        using (var store = NewStore())
        using (var source = new FileBlockSource(_volPath))
            store.ImageFast(source, "gen1");
        using (var store = NewStore())
        using (var killer = new KillerSource(_volPath, (2 * Mib) + (Mib / 2)))
            Assert.Throws<IOException>(() => store.ImageFast(killer, "gen2"));
        Assert.Equal(4, LandedCount()); // gen1's chunks; the kill landed only dupes

        var log = new TestLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(_volPath))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(source, "gen3", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        AssertHasLine(log, "unchanged since gen 1, skipping");
        Assert.Equal(4, LandedCount()); // the skip deleted nothing resumable

        using (var store = NewStore())
        using (var source = new FileBlockSource(_volPath))
        {
            var m = store.ImageFast(source, "gen2");
            Assert.Equal(2, m.Generation);
            Assert.True(store.Verify(2));
            Assert.Equal(File.ReadAllBytes(_volPath), ExportBytes(store, 2, Path.Combine(_dir, "skres.img")));
        }
    }
}
