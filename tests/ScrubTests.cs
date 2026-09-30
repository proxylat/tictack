using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack;

public class ScrubTests
{
    static string TestDir() => Path.Combine(Path.GetTempPath(), "TicTackTest_scrub_" + Guid.NewGuid().ToString("N"));

    static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    // Deterministic shard placement: StableHash is stable, so scan names
    // until one lands in the wanted shard. No randomness, no flakiness.
    static string NameForShard(int want)
    {
        for (var i = 0; ; i++)
        {
            var n = "shard" + i + ".txt";
            if (Scrubber.ShardOf(n) == want) return n;
        }
    }

    sealed class RecordingDelete
    {
        public readonly List<(string? src, string dst, string rel)> Calls = new List<(string?, string, string)>();
        // Emulates TrackedDeleter bookkeeping: the real deleter drops the
        // state row alongside the file, so the next scan recopies fresh.
        public StateDb? Db;
        public async Task<bool> Handle(string? src, string dst, string rel, string label, CancellationToken ct)
        {
            Calls.Add((src, dst, rel));
            if (Db != null) await Db.DeleteAsync(rel);
            return true;
        }
    }

    static Scrubber BuildScrubber(SourceConfig cfg, StateDb db, RecordingDelete del, JobRunStore store, RecordingLogger log) =>
        new Scrubber(cfg, null, db, del.Handle, store, log, p => Directory.Exists(p), (p, o) => Directory.EnumerateFiles(p, "*", o));

    static SourceConfig Cfg(string src, string dst) => new SourceConfig { Path = src, Destination = dst };

    [Fact]
    public async Task Match_StoresHashAndLeavesDestAlone()
    {
        var root = TestDir();
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var name = NameForShard(0);
        try
        {
            WriteFile(Path.Combine(src, name), "scrub-me");
            WriteFile(Path.Combine(dst, name), "scrub-me");
            var log = new RecordingLogger();
            using var db = new StateDb(Path.Combine(root, "state.db"), log);
            var rel = name;
            var fi = new FileInfo(Path.Combine(src, name));
            db.Upsert(rel, fi.Length, fi.LastWriteTimeUtc.Ticks);
            using var store = new JobRunStore(Path.Combine(root, "verify.db"));
            var del = new RecordingDelete();
            var scrubber = BuildScrubber(Cfg(src, dst), db, del, store, log);

            await scrubber.RunAsync(CancellationToken.None);

            Assert.Empty(del.Calls);
            Assert.True(File.Exists(Path.Combine(dst, name)));
            var row = await db.GetFullStateAsync(rel);
            Assert.True(row.HasValue);
            Assert.False(string.IsNullOrEmpty(row!.Value.hash));
            Assert.Equal(1, store.GetCounter("scrub-shard"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Mismatch_DeletesAndDropsRow()
    {
        var root = TestDir();
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var name = NameForShard(0);
        try
        {
            WriteFile(Path.Combine(src, name), "original-content");
            WriteFile(Path.Combine(dst, name), "CORRUPTED-CONTENT");
            var log = new RecordingLogger();
            using var db = new StateDb(Path.Combine(root, "state.db"), log);
            var rel = name;
            var fi = new FileInfo(Path.Combine(src, name));
            db.Upsert(rel, fi.Length, fi.LastWriteTimeUtc.Ticks);
            using var store = new JobRunStore(Path.Combine(root, "verify.db"));
            var del = new RecordingDelete();
            del.Db = db;
            var scrubber = BuildScrubber(Cfg(src, dst), db, del, store, log);

            await scrubber.RunAsync(CancellationToken.None);

            var call = Assert.Single(del.Calls);
            Assert.Equal(Path.Combine(dst, name), call.dst);
            Assert.Equal(rel, call.rel);
            Assert.False((await db.GetFullStateAsync(rel)).HasValue);
            Assert.Contains(log.Messages, m => m.Contains("mismatch"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Shard_RotatesAcrossRuns()
    {
        var root = TestDir();
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var name = NameForShard(2);
        try
        {
            WriteFile(Path.Combine(src, name), "rotating");
            WriteFile(Path.Combine(dst, name), "rotating");
            var log = new RecordingLogger();
            using var db = new StateDb(Path.Combine(root, "state.db"), log);
            var rel = name;
            var fi = new FileInfo(Path.Combine(src, name));
            db.Upsert(rel, fi.Length, fi.LastWriteTimeUtc.Ticks);
            using var store = new JobRunStore(Path.Combine(root, "verify.db"));
            var del = new RecordingDelete();
            var scrubber = BuildScrubber(Cfg(src, dst), db, del, store, log);

            await scrubber.RunAsync(CancellationToken.None);
            Assert.False((await db.GetFullStateAsync(rel))!.Value.hash != null);
            Assert.Equal(1, store.GetCounter("scrub-shard"));

            await scrubber.RunAsync(CancellationToken.None);
            Assert.False((await db.GetFullStateAsync(rel))!.Value.hash != null);
            Assert.Equal(2, store.GetCounter("scrub-shard"));

            await scrubber.RunAsync(CancellationToken.None);
            Assert.False(string.IsNullOrEmpty((await db.GetFullStateAsync(rel))!.Value.hash));
            Assert.Equal(3, store.GetCounter("scrub-shard"));
            Assert.Empty(del.Calls);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task MissingSource_SkippedWithoutDelete()
    {
        var root = TestDir();
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var name = NameForShard(0);
        try
        {
            // Row exists and dest exists, but the source is gone: a
            // parity/deletion concern, never a scrub mismatch.
            WriteFile(Path.Combine(dst, name), "orphan-dest");
            var log = new RecordingLogger();
            using var db = new StateDb(Path.Combine(root, "state.db"), log);
            db.Upsert(name, 11, DateTime.UtcNow.Ticks);
            using var store = new JobRunStore(Path.Combine(root, "verify.db"));
            var del = new RecordingDelete();
            var scrubber = BuildScrubber(Cfg(src, dst), db, del, store, log);

            await scrubber.RunAsync(CancellationToken.None);

            Assert.Empty(del.Calls);
            Assert.True((await db.GetFullStateAsync(name)).HasValue);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
