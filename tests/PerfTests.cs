namespace TicTack;

[CollectionDefinition("SerialPerf", DisableParallelization = true)]
public sealed class SerialPerfCollection { }

[Collection("SerialPerf")]
[Trait("Category", "Perf")]
public sealed class PerfTests
{
    static string TestDir() => Path.Combine(Path.GetTempPath(), "TicTackPerf_" + Guid.NewGuid().ToString("N"));

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    static SourceConfig MakeConfig(string src, string dst, string maxFileSizeMb = "no-limit") => new()
    {
        Path = src,
        Destination = dst,
        DebounceSeconds = 0.05,
        Filter = new FilterConfig { MaxFileSizeMb = maxFileSizeMb },
        Sync = new SyncConfig { Verification = "size" }
    };

    static SyncPipeline MakePipeline(SourceConfig cfg, EventMonitor monitor, IFileComparer comparer,
        IValidator validator, IFileAction copy, RecordingLogger log, StateDb? stateDb = null) =>
        new(cfg, monitor, comparer, copy, new RenameAction(), new ExponentialBackoffRetry(1, 0, 1),
            validator, new NoVersioning(), new MirrorDeletion(), log, stateDb);

    static void WriteFiles(string dir, int count, int size, string ext = ".bin")
    {
        Directory.CreateDirectory(dir);
        var buffer = new byte[size];
        for (int i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(dir, $"file{i:D5}{ext}"), buffer);
    }

    static void WriteZeroFile(string path, long bytes)
    {
        var buffer = new byte[1024 * 1024];
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        for (long written = 0; written < bytes; written += buffer.Length)
            fs.Write(buffer, 0, buffer.Length);
    }

    static async Task WaitForAsync(Func<bool> condition, string what, int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(100);
        Assert.True(condition(), "Timed out waiting for " + what);
    }

    [Fact]
    public async Task WarmResync_CopiesNothing()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        try
        {
            WriteFiles(src, 200, 512, ".txt");
            var copy = new RecordingAction(new CopyAction(new FileAccessor()));
            var statePath = Path.Combine(dir, "state.db");

            using (var db = new StateDb(statePath))
            using (var pipeline = MakePipeline(MakeConfig(src, dst), new EventMonitor(),
                new DateSizeComparer(), new SizeValidator(), copy, new RecordingLogger(), db))
            {
                Assert.True(await pipeline.RunOnceAsync());
                Assert.Equal(200, copy.Calls.Count);
            }

            var allocBefore = GC.GetTotalAllocatedBytes(true);
            using (var db = new StateDb(statePath))
            using (var pipeline = MakePipeline(MakeConfig(src, dst), new EventMonitor(),
                new DateSizeComparer(), new SizeValidator(), copy, new RecordingLogger(), db))
            {
                Assert.True(await pipeline.RunOnceAsync());
            }
            var alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;

            Assert.Equal(200, copy.Calls.Count);
            Assert.True(alloc < 12 * 1024 * 1024, $"Warm resync allocated {alloc} bytes");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task InitialSync_ManySmallFiles_AllocationPerFileBounded()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        try
        {
            WriteFiles(src, 1000, 1024);
            var log = new RecordingLogger();
            var copy = new RecordingAction(new CopyAction(new FileAccessor()));
            using var pipeline = MakePipeline(MakeConfig(src, dst), new EventMonitor(),
                new DateSizeComparer(), new SizeValidator(), copy, log);

            var allocBefore = GC.GetTotalAllocatedBytes(true);
            Assert.True(await pipeline.RunOnceAsync());
            var perFile = (GC.GetTotalAllocatedBytes(true) - allocBefore) / 1000.0;

            Assert.Equal(1000, copy.Calls.Count);
            Assert.True(perFile < 64 * 1024, $"{perFile:F0} bytes allocated per file");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task LargeFile_AllocationStaysBuffered()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        try
        {
            Directory.CreateDirectory(src);
            WriteZeroFile(Path.Combine(src, "big.bin"), 64L * 1024 * 1024);
            var log = new RecordingLogger();
            var copy = new RecordingAction(new CopyAction(new FileAccessor()));
            using var pipeline = MakePipeline(MakeConfig(src, dst), new EventMonitor(),
                new DateSizeComparer(), new SizeValidator(), copy, log);

            var allocBefore = GC.GetTotalAllocatedBytes(true);
            Assert.True(await pipeline.RunOnceAsync());
            var alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;

            Assert.Single(copy.Calls);
            Assert.True(alloc < 8 * 1024 * 1024, $"64 MB copy allocated {alloc} bytes");
        }
        finally { TryDelete(dir); }
    }

    static async Task<long> SyncAllocAsync(int fileCount, int fileSize, bool hash)
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        try
        {
            WriteFiles(src, fileCount, fileSize);
            var accessor = new FileAccessor();
            var log = new RecordingLogger();
            var copy = new RecordingAction(new CopyAction(accessor));
            IFileComparer comparer = hash ? new HashComparer(accessor) : new DateSizeComparer();
            IValidator validator = hash ? new HashValidator() : new SizeValidator();
            using var pipeline = MakePipeline(MakeConfig(src, dst), new EventMonitor(),
                comparer, validator, copy, log);

            var allocBefore = GC.GetTotalAllocatedBytes(true);
            Assert.True(await pipeline.RunOnceAsync());
            Assert.Equal(fileCount, copy.Calls.Count);
            return GC.GetTotalAllocatedBytes(true) - allocBefore;
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task HashVerification_AllocationStaysNearSizeMode()
    {
        var sizeAlloc = await SyncAllocAsync(4, 8 * 1024 * 1024, hash: false);
        var hashAlloc = await SyncAllocAsync(4, 8 * 1024 * 1024, hash: true);

        Assert.True(hashAlloc <= sizeAlloc + 16 * 1024 * 1024,
            $"hash mode allocated {hashAlloc} bytes vs size mode {sizeAlloc}");
    }

    [Fact]
    public async Task Watcher_SingleChange_CopiesExactlyOnce()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        try
        {
            WriteFiles(src, 5, 512, ".txt");
            var monitor = new EventMonitor();
            var copy = new RecordingAction(new CopyAction(new FileAccessor()));
            using var pipeline = MakePipeline(MakeConfig(src, dst), monitor,
                new DateSizeComparer(), new SizeValidator(), copy, new RecordingLogger());

            pipeline.Start();
            await WaitForAsync(() => copy.Calls.Count >= 5, "initial copies");
            var before = copy.Calls.Count;

            var target = Path.Combine(src, "file00002.txt");
            File.WriteAllText(target, "changed");
            monitor.Fire(ChangeType.Modified, target);

            await WaitForAsync(() => copy.Calls.Count > before, "the changed file");
            Assert.Equal(before + 1, copy.Calls.Count);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task SizeFilter_SkipsOversizedFiles()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        try
        {
            WriteFiles(src, 3, 10 * 1024, ".small");
            WriteFiles(src, 3, 2 * 1024 * 1024, ".big");
            var copy = new RecordingAction(new CopyAction(new FileAccessor()));
            using var pipeline = MakePipeline(MakeConfig(src, dst, maxFileSizeMb: "1"), new EventMonitor(),
                new DateSizeComparer(), new SizeValidator(), copy, new RecordingLogger());

            Assert.True(await pipeline.RunOnceAsync());

            Assert.Equal(3, copy.Calls.Count);
            var copied = Directory.GetFiles(dst).Select(f => Path.GetFileName(f)).ToList();
            Assert.Equal(3, copied.Count(f => f!.EndsWith(".small")));
            Assert.DoesNotContain(copied, f => f!.EndsWith(".big"));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void StateDb_LoadAll151kRows_AllocationPerRowBounded()
    {
        var dir = TestDir();
        try
        {
            using var db = new StateDb(Path.Combine(dir, "state.db"));
            var rows = new List<(string path, long size, long mtime)>(151_000);
            for (int i = 0; i < 151_000; i++)
                rows.Add(($"/p/file{i:D6}.txt", i * 1024L, 637000000000000000L + i));
            db.UpsertBatch(rows);

            var allocBefore = GC.GetTotalAllocatedBytes(true);
            var all = db.LoadAll();
            var alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;

            Assert.Equal(151_000, all.Count);
            Assert.Equal(5 * 1024L, db.GetSize("/p/file000005.txt"));
            Assert.True(alloc < 32 * 1024 * 1024, $"LoadAll allocated {alloc} bytes");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void FileAccessor_OpenRead_AllocationPerOpenBounded()
    {
        var dir = TestDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "data.bin");
            WriteZeroFile(path, 4L * 1024 * 1024);
            var accessor = new FileAccessor();
            using (var warm = accessor.OpenRead(path)) warm.ReadByte();

            var allocBefore = GC.GetTotalAllocatedBytes(true);
            for (int i = 0; i < 200; i++)
            {
                using var stream = accessor.OpenRead(path);
                Assert.NotEqual(-1, stream.ReadByte());
            }
            var perOpen = (GC.GetTotalAllocatedBytes(true) - allocBefore) / 200.0;
            Assert.True(perOpen < 32 * 1024, $"{perOpen:F0} bytes allocated per OpenRead");
        }
        finally { TryDelete(dir); }
    }
}
