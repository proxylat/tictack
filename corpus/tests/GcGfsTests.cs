using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Corpus.Tests;

public sealed class GcGfsTests : IDisposable
{
    private readonly string _dir;

    public GcGfsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "corpus-gfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "manifests"));
        Directory.CreateDirectory(Path.Combine(_dir, "packs"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void GarbageCollector_RetainsGfsBuckets_Independently()
    {
        var key = ChunkId.EnsureKey(_dir);
        var dbPath = Path.Combine(_dir, "chunks.db");
        using (new ChunkIndex(dbPath)) { }

        // Seed 6 manifests across 2 years, 2 months, 2 weeks, and same-day duplicates:
        // Gen 1: 2024-05-01 (2024 yearly)
        // Gen 2: 2025-01-10 (2025-01 monthly)
        // Gen 3: 2025-02-03 (same week/month as gen 4 -> pruned)
        // Gen 4: 2025-02-05 (2025-02 monthly, 2025-W06 weekly, 2025-02-05 daily)
        // Gen 5: 2025-02-10 09:00 (same day as gen 6 -> pruned)
        // Gen 6: 2025-02-10 18:00 (KeepLast=1 + 2025-02-10 daily + 2025-W07 weekly + 2025 yearly)
        var dates = new (int Gen, string Utc)[]
        {
            (1, "2024-05-01T12:00:00Z"),
            (2, "2025-01-10T12:00:00Z"),
            (3, "2025-02-03T12:00:00Z"),
            (4, "2025-02-05T12:00:00Z"),
            (5, "2025-02-10T09:00:00Z"),
            (6, "2025-02-10T18:00:00Z")
        };

        foreach (var (gen, utc) in dates)
        {
            var m = new SnapshotManifest
            {
                Generation = gen,
                Name = "snap",
                CreatedUtc = utc,
                VolumeLength = 4096,
                ChunkMib = 4,
                Chunks = new List<ManifestEntry> { ManifestEntry.Hole(4096) }
            };
            ManifestStore.Save(Path.Combine(_dir, "manifests"), m, key, preserveCreatedUtc: true);
        }

        var ret = new CorpusRetentionConfig
        {
            KeepLast = 1,
            KeepDaily = 2,
            KeepWeekly = 2,
            KeepMonthly = 2,
            KeepYearly = 2
        };

        var gc = new GarbageCollector(_dir, dbPath, ret, new NullLogger());
        var plan = gc.Plan();

        var droppedNames = plan.DropManifests.Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "000003-snap.json", "000005-snap.json" }, droppedNames);
    }

    private sealed class NullLogger : TicTack.ILogger
    {
        public void Debug(string msg) { }
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg, Exception? ex = null) { }
    }
}
