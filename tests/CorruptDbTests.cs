namespace TicTack;

// Filesystem-corruption screen (file-consistency: the FS can corrupt a db
// that SQLite then happily answers queries from). Each store must refuse
// to trust a corrupt file: quarantine it aside and rebuild or degrade,
// never silently proceed on bad data.
public class CorruptDbTests
{
    // Zero page 1 past the 100-byte header (sqlite_master root): header
    // stays valid so the open succeeds, but quick_check fails. Deterministic
    // for any store size, unlike flipping data pages that might be free.
    static void CorruptSchemaPage(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        fs.Seek(100, SeekOrigin.Begin);
        fs.Write(new byte[512], 0, 512);
        fs.Flush(true);
    }

    static string Quarantine(string dir) =>
        Assert.Single(Directory.GetFiles(dir, "*.corrupt-*"));

    [Fact]
    public void StateDb_CorruptSchemaPage_QuarantinedAndRebuilt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tictack-corrupt-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "state.db");
            using (var db = new StateDb(path))
            {
                db.Upsert("a.txt", 100, 1000);
                db.Upsert("b.txt", 200, 2000);
            }
            // Pools cleared BEFORE corrupting: a pooled close/checkpoint
            // after the write would patch the page header back (12 bytes).
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            CorruptSchemaPage(path);
            Assert.True(!File.Exists(path + "-wal"), "wal should be gone after clean close, else replay heals the corruption");
            Assert.All(File.ReadAllBytes(path).Skip(100).Take(512), b => Assert.Equal(0, b));

            var log = new RecordingLogger();
            using (var db = new StateDb(path, log))
            {
                db.Upsert("c.txt", 300, 3000);
                Assert.Equal("c.txt", Assert.Single(db.LoadAll()).Key);
            }
            Quarantine(dir);
            Assert.Contains(log.Messages, m => m.Contains("quarantined"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Deferred_CorruptSchemaPage_HoldsDroppedFilesKept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tictack-corrupt-deferred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var dd = new DeferredDeletion(dir, "tictack-deferred-t", 7, new RecordingLogger()))
                dd.RecordPending(new[] { Path.Combine(dir, "x.txt"), Path.Combine(dir, "y.txt") });
            var dbPath = Path.Combine(dir, "tictack-deferred-t.db");
            Assert.True(File.Exists(dbPath));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            CorruptSchemaPage(dbPath);

            // Corrupt ledger → Warn, holds dropped, destination files kept.
            // Nothing deletes on a corrupt hold store, ever.
            var log = new RecordingLogger();
            using (var dd = new DeferredDeletion(dir, "tictack-deferred-t", 7, log))
            {
                Assert.False(dd.HasPending);
                Assert.Empty(dd.TakeExpiredChunk().Files);
            }
            Quarantine(dir);
            Assert.Contains(log.Messages, m => m.Contains("could not be loaded"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void JobRunStore_CorruptSchemaPage_ReopensFresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tictack-corrupt-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "jobs.db");
            using (var store = new JobRunStore(path))
                store.SetLastRun("nightly", new DateTime(2026, 1, 1));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            CorruptSchemaPage(path);

            // The store itself throws (production Scheduler.CreateStore
            // catches this and reopens); the test mirrors that path.
            var thrown = Assert.Throws<CorruptDatabaseException>(() => new JobRunStore(path));
            Assert.Equal(path, thrown.DbPath);
            using (var store = new JobRunStore(path))
            {
                Assert.Null(store.GetLastRun("nightly"));
                store.SetLastRun("nightly", new DateTime(2026, 1, 2));
                Assert.Equal(new DateTime(2026, 1, 2), store.GetLastRun("nightly"));
            }
            Quarantine(dir);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
