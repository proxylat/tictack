using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Corpus.Tests;

public sealed class ExtractSparseTests : IDisposable
{
    private readonly string _dir;

    public ExtractSparseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "corpus-exsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void MftRunStream_DetectsAndMeasuresSparseHoles()
    {
        var dbPath = Path.Combine(_dir, "chunks.db");
        using (new ChunkIndex(dbPath)) { }
        var manifest = new SnapshotManifest
        {
            Generation = 1,
            Name = "sp",
            CreatedUtc = "2026-01-01T00:00:00Z",
            VolumeLength = 1024 * 1024,
            ChunkMib = 4,
            Chunks = new List<ManifestEntry> { ManifestEntry.Hole(1024 * 1024) }
        };
        using var vol = new SnapshotVolumeReader(_dir, dbPath, manifest);

        // 4 KiB clusters:
        // VCN 0..2 (8 KiB) allocated at LCN 10
        // VCN 2..10 (32 KiB) sparse hole (Lcn = -1)
        // VCN 10..12 (8 KiB) allocated at LCN 20
        var runs = new List<MftRun>
        {
            new MftRun(0, 10, 2),
            new MftRun(2, -1, 8),
            new MftRun(10, 20, 2)
        };

        var stream = new MftRunStream(runs, vol, 4096, 1, "vm.vhdx");
        Assert.True(stream.HasSparseRuns);

        // At offset 0 (allocated): no sparse hole; next sparse offset is 8192
        Assert.Equal(0, stream.SparseHoleBytesAt(0, 48 * 1024));
        Assert.Equal(8192, stream.NextSparseOffsetIn(0, 16 * 1024));

        // At offset 8192 (start of sparse run): 32 KiB hole
        Assert.Equal(32 * 1024, stream.SparseHoleBytesAt(8192, 40 * 1024));
        Assert.Equal(16 * 1024, stream.SparseHoleBytesAt(8192, 16 * 1024));

        // At offset 40960 (allocated again): no sparse hole
        Assert.Equal(0, stream.SparseHoleBytesAt(40960, 8192));
    }
}
