using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace TicTack.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class FilterBenchmarks
{
    private static readonly string[] Patterns =
    {
        ".dotnet/*", "HuntingLab/*", "node_modules/*", ".venv/*", "venv/*", "__pycache__/*",
        "bin/*", "obj/*", "*.tmp", "*.db-wal", "*.db-shm", "*.lock", ".git/*", ".opencode/*",
        ".code-review-graph/*"
    };

    private PatternFilter _filter = null!;
    private string[] _paths = null!;

    [GlobalSetup]
    public void Setup()
    {
        _filter = new PatternFilter(Patterns);
        var paths = new List<string>(7000);
        const string root = "/data/repo";
        for (var i = 0; i < 1000; i++)
        {
            paths.Add($"{root}/src/File{i:D4}.cs");
            paths.Add($"{root}/docs/note{i:D4}.md");
            paths.Add($"{root}/bin/out{i:D4}.dll");
            paths.Add($"{root}/.git/objects/ab/cdef{i:D4}");
            paths.Add($"{root}/obj/Debug/net10.0/file{i:D4}.cs");
            paths.Add($"{root}/Pictures/photo{i:D4}.jpg");
            paths.Add($"{root}/cache/temp{i:D4}.tmp");
        }
        _paths = paths.ToArray();
    }

    [Benchmark]
    public int ShouldProcess()
    {
        var kept = 0;
        foreach (var path in _paths)
        {
            if (_filter.ShouldProcess(path)) kept++;
        }
        return kept;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class FileActionArgsBenchmarks
{
    private const string SourceBase = "/data/src";
    private const string DestBase = "/data/dst";
    private FileChangedEventArgs[] _events = null!;

    [GlobalSetup]
    public void Setup()
    {
        var events = new List<FileChangedEventArgs>(1000);
        for (var i = 0; i < 1000; i++)
        {
            events.Add(new FileChangedEventArgs(
                ChangeType.Created,
                $"/data/src/docs/note{i:D4}.md"));
        }
        _events = events.ToArray();
    }

    [Benchmark]
    public int Construct()
    {
        var n = 0;
        foreach (var e in _events)
        {
            var args = new FileActionArgs(e, SourceBase, DestBase);
            if (args.DestPath.Length > 0) n++;
        }
        return n;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class HashBenchmarks
{
    private byte[] _hash = null!;

    [GlobalSetup]
    public void Setup() => _hash = SHA256.HashData(new byte[1024]);

    [Benchmark(Baseline = true)]
    public string LegacyHex() => BitConverter.ToString(_hash).Replace("-", "").ToLowerInvariant();

    [Benchmark]
    public string HexStringLower() => Convert.ToHexStringLower(_hash);
}

[MemoryDiagnoser]
[ShortRunJob]
public class SnapshotBenchmarks
{
    private const int Count = 10_000;

    private FileSnapshot[] _prev = null!;
    private FileSnapshot[] _cur = null!;

    [GlobalSetup]
    public void Setup()
    {
        _prev = new FileSnapshot[Count];
        _cur = new FileSnapshot[Count];
        for (var i = 0; i < Count; i++)
        {
            _prev[i] = new FileSnapshot(i * 1024L, 637000000000000000L + i);
            // 1% changed, like a quiet poll scan.
            _cur[i] = new FileSnapshot(i * 1024L, 637000000000000000L + i + (i % 100 == 0 ? 1 : 0));
        }
    }

    // The PollingMonitor hot path: one Equals per file per scan.
    [Benchmark(OperationsPerInvoke = Count)]
    public int ScanChanged()
    {
        var n = 0;
        for (var i = 0; i < Count; i++)
        {
            if (!_prev[i].Equals(_cur[i])) n++;
        }
        return n;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class StateDbBenchmarks
{
    private const int Rows = 151_000;

    private string _dir = null!;
    private StateDb _db = null!;
    private List<(string path, long size, long mtime)> _batch = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tictack-bench-" + Guid.NewGuid().ToString("N"));
        _db = new StateDb(Path.Combine(_dir, "state.db"));

        var seed = new List<(string path, long size, long mtime)>(Rows);
        for (var i = 0; i < Rows; i++)
        {
            seed.Add(($"/data/file{i:D6}.txt", i * 1024L, 637000000000000000L + i));
        }
        _db.UpsertBatch(seed);

        _batch = new List<(string path, long size, long mtime)>(1000);
        for (var i = 0; i < 1000; i++)
        {
            _batch.Add(($"/data/file{i:D6}.txt", i * 2048L, 637000000000000001L + i));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Benchmark]
    public int LoadAll() => _db.LoadAll().Count;

    [Benchmark]
    public void UpsertBatch1000() => _db.UpsertBatch(_batch);

    // Production initial-sync path: indexed point lookup per file,
    // 90% hits + 10% misses like a warm restart scan.
    [Benchmark(OperationsPerInvoke = 1000)]
    public async Task<int> TryGetHit1000()
    {
        var n = 0;
        for (var i = 0; i < 1000; i++)
        {
            var key = i % 10 == 9 ? $"/data/missing{i:D6}.txt" : $"/data/file{i:D6}.txt";
            if (await _db.TryGetStateAsync(key).ConfigureAwait(false) != null) n++;
        }
        return n;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class DriveGuardBenchmarks
{
    private List<(string Root, bool Ready)> _mounts = null!;
    private string _path = null!;
    private Func<DriveInfo[]> _probe = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mounts = new List<(string, bool)>(41) { ("/", true) };
        for (var i = 0; i < 40; i++) _mounts.Add(("/mnt/drive" + i, true));
        _path = "/mnt/drive17/folder/file.txt";
        _probe = static () => DriveInfo.GetDrives();
    }

    // Pure selection: no probe, no cache, no GetFullPath.
    [Benchmark(Baseline = true, OperationsPerInvoke = 10_000)]
    public int SelectOnly()
    {
        var n = 0;
        for (var i = 0; i < 10_000; i++) { if (DriveGuard.SelectReady(_path, _mounts)) n++; }
        return n;
    }

    // The production path: GetFullPath + a warm mount-table cache.
    [Benchmark(OperationsPerInvoke = 10_000)]
    public int CachedIsReady()
    {
        var n = 0;
        for (var i = 0; i < 10_000; i++) { if (DriveGuard.IsReady(_path, _probe)) n++; }
        return n;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class PollDiffBenchmarks
{
    private const int Count = 10_000;

    private Dictionary<string, long> _prev = null!;
    private Dictionary<string, long> _cur = null!;

    [GlobalSetup]
    public void Setup()
    {
        _prev = new Dictionary<string, long>(Count);
        _cur = new Dictionary<string, long>(Count);
        for (var i = 0; i < Count; i++)
        {
            var key = "/data/file" + i.ToString("D5");
            _prev[key] = i;
            // 1% changed, like a quiet poll scan.
            _cur[key] = i % 100 == 0 ? i + 1 : i;
        }
    }

    [Benchmark(Baseline = true)]
    public int ContainsKeyThenIndexer()
    {
        var changed = 0;
        foreach (var kv in _cur)
        {
            if (!_prev.ContainsKey(kv.Key) || _prev[kv.Key] != kv.Value) changed++;
        }
        return changed;
    }

    [Benchmark]
    public int TryGetValue()
    {
        var changed = 0;
        foreach (var kv in _cur)
        {
            if (!_prev.TryGetValue(kv.Key, out var old) || old != kv.Value) changed++;
        }
        return changed;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class PendingDrainBenchmarks
{
    private ConcurrentDictionary<string, FileChangedEventArgs> _events = null!;

    [Params(1_000, 100_000)]
    public int Count;

    [GlobalSetup]
    public void Setup()
    {
        _events = new ConcurrentDictionary<string, FileChangedEventArgs>();
        for (var i = 0; i < Count; i++)
        {
            var key = "/data/file" + i.ToString("D6");
            _events[key] = new FileChangedEventArgs(ChangeType.Modified, key);
        }
    }

    // One extraction from a backlog where every entry is ready: the O(pending)
    // scan the drain does per event under a watcher-overflow backlog.
    [Benchmark]
    public FileChangedEventArgs DrainOne()
    {
        foreach (var item in _events)
        {
            if (_events.TryRemove(item.Key, out var e)) return e;
        }
        return null!;
    }
}
