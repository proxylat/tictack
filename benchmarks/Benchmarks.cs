using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
        const string root = "/home/user/Desktop/Projects/Lab/TicTack";
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
    private const string SourceBase = "/home/user/Desktop";
    private const string DestBase = "/mnt/pendrive/Sync/Desktop";
    private FileChangedEventArgs[] _events = null!;

    [GlobalSetup]
    public void Setup()
    {
        var events = new List<FileChangedEventArgs>(1000);
        for (var i = 0; i < 1000; i++)
        {
            events.Add(new FileChangedEventArgs(
                ChangeType.Created,
                $"/home/user/Desktop/Projects/Lab/TicTack/docs/note{i:D4}.md"));
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
            seed.Add(($"/home/user/Desktop/file{i:D6}.txt", i * 1024L, 637000000000000000L + i));
        }
        _db.UpsertBatch(seed);

        _batch = new List<(string path, long size, long mtime)>(1000);
        for (var i = 0; i < 1000; i++)
        {
            _batch.Add(($"/home/user/Desktop/file{i:D6}.txt", i * 2048L, 637000000000000001L + i));
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
    public long Count() => _db.Count();

    [Benchmark]
    public void UpsertBatch1000() => _db.UpsertBatch(_batch);
}
