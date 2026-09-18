namespace TicTack;

// GC-behavior soak tests: Maoni's leak proof inside xUnit.
// A managed leak keeps growing even after the hardest full blocking GC;
// churn/fragmentation do not. Both tests force a full compacting GC and
// then assert bounded growth — generous margins, no external tools, so
// they run in the normal CI matrix (Linux + Windows).
//
// Process-wide GC readings, so serialize against every other collection
// (SerialPerf is defined in PerfTests.cs): concurrent allocations from a
// parallel test class would move the baselines.
[Collection("SerialPerf")]
public class GcSoakTests
{
    static string TestDir() =>
        Path.Combine(Path.GetTempPath(), "TicTackGcSoak_" + Guid.NewGuid());

    static SourceConfig MakeConfig(string src, string dst) => new()
    {
        Path = src,
        Destination = dst,
        DebounceSeconds = 0.05,
        Filter = new FilterConfig(),
        Sync = new SyncConfig { Verification = "size" }
    };

    static SyncPipeline MakePipeline(string src, string dst, EventMonitor monitor) => new(
        MakeConfig(src, dst),
        monitor,
        new DateSizeComparer(),
        new CopyAction(new FileAccessor()),
        new RenameAction(),
        new ExponentialBackoffRetry(1, 0, 1),
        new SizeValidator(),
        new NoVersioning(),
        new MirrorDeletion(),
        new RecordingLogger()
    );

    // Full compacting GC + finalizer drain, then measure. Callers must have
    // released everything they want collected (pipelines disposed).
    static long FullGcHeapBytes()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        return GC.GetTotalMemory(false);
    }

    // Returns false on timeout so callers can Assert: measuring a heap after
    // a sync that never happened would pass vacuously.
    static async Task<bool> WaitForFilesAsync(string dst, int n, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        bool synced;
        do
        {
            synced = true;
            for (int i = 0; i < n; i++)
            {
                if (!File.Exists(Path.Combine(dst, $"s{i}.txt"))) { synced = false; break; }
            }
            if (!synced) await Task.Delay(250);
        } while (!synced && DateTime.UtcNow < deadline);
        return synced;
    }

    // Content-based wait: destination files already exist from earlier cycles,
    // so existence alone would return immediately without proving this cycle
    // synced. The per-cycle unique prefix proves fresh content arrived.
    static async Task<bool> WaitForContentAsync(string dst, int n, string prefix, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        bool synced;
        do
        {
            synced = true;
            for (int i = 0; i < n; i++)
            {
                var p = Path.Combine(dst, $"s{i}.txt");
                if (!File.Exists(p)) { synced = false; break; }
                try
                {
                    using var s = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var r = new StreamReader(s);
                    var buf = new char[prefix.Length + 1];
                    var read = await r.ReadAsync(buf, 0, buf.Length);
                    if (read < prefix.Length || new string(buf, 0, prefix.Length) != prefix) { synced = false; break; }
                }
                catch (IOException) { synced = false; break; }
            }
            if (!synced) await Task.Delay(250);
        } while (!synced && DateTime.UtcNow < deadline);
        return synced;
    }

    // ── 1. Repeated sync cycles must not grow the post-full-GC heap ──
    [Fact]
    public async Task Pipeline_RepeatedCycles_HeapStableAfterFullGC()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            // Warmup cycle so JIT/caches settle before the baseline.
            for (int i = 0; i < 20; i++)
                File.WriteAllText(Path.Combine(src, $"s{i}.txt"), $"warm{i}");
            using (var warm = MakePipeline(src, dst, new EventMonitor()))
            {
                warm.Start();
                Assert.True(await WaitForFilesAsync(dst, 20), "warmup files not synced");
            }
            var baseline = FullGcHeapBytes();

            // Three measured cycles: rewrite every file, sync, dispose.
            const int cycles = 3, n = 50;
            for (int c = 0; c < cycles; c++)
            {
                for (int i = 0; i < n; i++)
                    File.WriteAllText(Path.Combine(src, $"s{i}.txt"), $"cycle{c}-content{i}-{new string('x', 200)}");
                using (var pipeline = MakePipeline(src, dst, new EventMonitor()))
                {
                    pipeline.Start();
                    Assert.True(await WaitForContentAsync(dst, n, $"cycle{c}-"), $"cycle {c} content not synced");
                }
            }
            var after = FullGcHeapBytes();

            // Generous: 150 files synced 3x through fsync must not retain
            // more than 8 MB past a full compacting GC. A real per-file
            // leak (even 64 KB/file/cycle) blows this by 2x+.
            Assert.True(after - baseline < 8 * 1024 * 1024,
                $"Heap grew {(after - baseline) / 1024} KB after full GC (baseline {baseline / 1024} KB, after {after / 1024} KB)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 2. Bulk medium files (LOH-sized) must not force gen2 churn ──
    [Fact]
    public async Task CopyAction_BulkMediumFiles_BoundedGen2Collections()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            // 40 x 256 KB files: each is LOH-sized as file DATA, but the
            // copy loop must stream through sub-LOH buffers (no AllocLarge).
            const int n = 40;
            var rnd = new Random(7);
            for (int i = 0; i < n; i++)
            {
                var data = new byte[256 * 1024];
                rnd.NextBytes(data);
                File.WriteAllBytes(Path.Combine(src, $"m{i}.bin"), data);
            }

            var gen2Before = GC.CollectionCount(2);
            var action = new CopyAction(new FileAccessor());
            for (int i = 0; i < n; i++)
            {
                var f = Path.Combine(src, $"m{i}.bin");
                var result = await action.ExecuteAsync(new FileActionArgs(
                    new FileChangedEventArgs(ChangeType.Created, f), src, dst), CancellationToken.None);
                Assert.True(result.Success);
            }
            var gen2Delta = GC.CollectionCount(2) - gen2Before;

            // Streaming 10 MB through 80 KB buffers should cost ~zero gen2s.
            // Bound is deliberately loose (10) — per-file LOH buffers would
            // force ~40 AllocLarge gen2s and fail loudly.
            Assert.True(gen2Delta <= 10, $"Bulk copy forced {gen2Delta} gen2 collections");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
