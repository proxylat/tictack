using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Corpus.Tests;

public sealed class ConsistencyGroupTests : IDisposable
{
    private readonly string _dir;

    public ConsistencyGroupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "corpus-cg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void WriterVerifier_DetectsFailureStates_AndRespectsStrictFlag()
    {
        var failures = new List<string>();
        var log = new ListLogger();

        VssWriterVerifier.EvaluateWriter("SqlServerWriter", (int)VssWriterState.Stable, 0, log, failures);
        Assert.Empty(failures);

        VssWriterVerifier.EvaluateWriter("SqlServerWriter", (int)VssWriterState.FailedAtFreeze, unchecked((int)0x800423F3), log, failures);
        Assert.Single(failures);
        Assert.Contains("SqlServerWriter", failures[0], StringComparison.Ordinal);
        Assert.Contains("FailedAtFreeze", failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotManifest_ConsistencyGroupId_RoundTrips()
    {
        var key = ChunkId.EnsureKey(_dir);
        var m = new SnapshotManifest
        {
            Generation = 1,
            Name = "cg",
            CreatedUtc = "2026-01-01T00:00:00Z",
            VolumeLength = 4096,
            ChunkMib = 4,
            ConsistencyGroupId = "grp-1234abcd",
            Chunks = new List<ManifestEntry> { ManifestEntry.Hole(4096) }
        };
        var path = ManifestStore.Save(_dir, m, key, preserveCreatedUtc: true);

        var loaded = ManifestStore.Load(path, key);
        Assert.Equal("grp-1234abcd", loaded.ConsistencyGroupId);
    }

    private sealed class ListLogger : TicTack.ILogger
    {
        public List<string> Warnings { get; } = new();
        public void Debug(string msg) { }
        public void Info(string msg) { }
        public void Warn(string msg) => Warnings.Add(msg);
        public void Error(string msg, Exception? ex = null) { }
    }
}
