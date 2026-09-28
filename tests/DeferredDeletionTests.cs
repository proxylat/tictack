namespace TicTack;

public class DeferredDeletionTests
{
    static string TestDir() => Path.Combine(Path.GetTempPath(), "TicTackTest_deferred_" + Guid.NewGuid());

    static string DbFile(string dir, string prefix) => Path.Combine(dir, prefix + ".db");

    static void WriteBatchFile(string path, DateTime blockedAt, IEnumerable<string> files, string? sourceRoot)
    {
        var list = string.Join(",", files.Select(f => "\"" + f.Replace("\\", "\\\\") + "\""));
        File.WriteAllText(path,
            "{\"BlockedAt\":\"" + blockedAt.ToString("O") + "\",\"LastWarningAt\":\"0001-01-01T00:00:00\"," +
            "\"PendingFiles\":[" + list + "],\"SourceRoot\":" + (sourceRoot == null ? "null" : "\"" + sourceRoot.Replace("\\", "\\\\") + "\"") + "}");
    }

    [Fact]
    public void RecordPending_TwoBatches_ProceedTogether()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        var c = Path.Combine(dir, "c.txt");
        try
        {
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 0, new RecordingLogger());
            dd.RecordPending(new List<string> { a, b }, dir);
            dd.RecordPending(new List<string> { b, c }, dir);

            // One indexed store (b deduped across batches), one result.
            Assert.True(File.Exists(DbFile(dir, "tictack-deferred-test")));
            Assert.Empty(Directory.GetFiles(dir, "tictack-deferred-test-*.json"));
            var action = dd.Check();

            Assert.Equal(DeferredActionType.Proceed, action.Type);
            Assert.NotNull(action.Files);
            Assert.Equal(3, action.Files!.Count);
            Assert.Contains(a, action.Files);
            Assert.Contains(b, action.Files);
            Assert.Contains(c, action.Files);
            // Delete-on-clear: store file gone, no file means no pending.
            Assert.False(File.Exists(DbFile(dir, "tictack-deferred-test")));
            Assert.False(dd.HasPending);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RecordPending_PureDupes_CreatesNoNewRows()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            var log = new RecordingLogger();
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 7, log);
            var a = Path.Combine(dir, "a.txt");
            dd.RecordPending(new List<string> { a }, dir);
            dd.RecordPending(new List<string> { a }, dir);

            Assert.Equal(1, dd.PendingCount);
            Assert.True(File.Exists(DbFile(dir, "tictack-deferred-test")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Batches_ExpireOnTheirOwnClocks()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            const string prefix = "tictack-deferred-test";
            var oldFile = Path.Combine(dir, "old.txt");
            var newFile = Path.Combine(dir, "new.txt");
            // Old batch arrives as a legacy JSON with an 8-day-old clock.
            WriteBatchFile(Path.Combine(dir, prefix + "-20000101-000000.json"),
                DateTime.UtcNow.AddDays(-8), new[] { oldFile }, dir);

            using var dd = new DeferredDeletion(dir, prefix, 7, new RecordingLogger());
            dd.RecordPending(new List<string> { newFile }, dir);

            var action = dd.Check();

            // Only the expired batch proceeds; the fresh one keeps waiting.
            Assert.Equal(DeferredActionType.Proceed, action.Type);
            Assert.Equal(oldFile, Assert.Single(action.Files!));
            Assert.True(dd.HasPending);
            Assert.Equal(1, dd.PendingCount);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Save_Failure_IsLogged()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        var blocker = Path.Combine(dir, "blocker");
        File.WriteAllText(blocker, "not a directory");
        try
        {
            var log = new RecordingLogger();
            using var dd = new DeferredDeletion(blocker, "tictack-deferred-test", 1, log);
            dd.RecordPending(new List<string> { Path.Combine(dir, "gone.txt") });

            Assert.Contains(log.Messages, m => m.Contains("could not be saved"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Check_ExpiredHold_ProceedsWithMissingFiles()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 0, new RecordingLogger());
            var a = Path.Combine(dir, "gone-a.txt");
            var b = Path.Combine(dir, "gone-b.txt");
            dd.RecordPending(new List<string> { a, b }, dir);

            var action = dd.Check();

            Assert.Equal(DeferredActionType.Proceed, action.Type);
            Assert.Equal(2, action.Files!.Count);
            Assert.False(dd.HasPending);
            Assert.False(File.Exists(DbFile(dir, "tictack-deferred-test")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void CancelIfPending_RestoreDropsHoldWithoutExpiryScan()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 7, new RecordingLogger());
            var back = Path.Combine(dir, "back.txt");
            dd.RecordPending(new List<string> { back }, dir);
            File.WriteAllText(back, "returned");

            Assert.True(dd.CancelIfPending(back));
            Assert.False(dd.HasPending);
            Assert.False(File.Exists(DbFile(dir, "tictack-deferred-test")));
            Assert.False(dd.CancelIfPending(back));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TakeExpiredChunk_ReappearedFiles_CancelledInBand()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 0, new RecordingLogger());
            var back = Path.Combine(dir, "back.txt");
            var gone = Path.Combine(dir, "gone.txt");
            dd.RecordPending(new List<string> { back, gone }, dir);
            File.WriteAllText(back, "returned");

            var chunk = dd.TakeExpiredChunk(500);

            Assert.Equal(gone, Assert.Single(chunk.Files));
            Assert.Equal(1, chunk.Cancelled);
            Assert.False(dd.HasPending);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TakeExpiredChunk_BoundedDrain()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 0, new RecordingLogger());
            var files = Enumerable.Range(0, 1200).Select(i => Path.Combine(dir, "f" + i + ".txt")).ToList();
            dd.RecordPending(files, dir);

            var first = dd.TakeExpiredChunk(500);
            Assert.Equal(500, first.Files.Count);
            Assert.True(dd.HasPending);
            var second = dd.TakeExpiredChunk(500);
            Assert.Equal(500, second.Files.Count);
            var third = dd.TakeExpiredChunk(500);
            Assert.Equal(200, third.Files.Count);
            Assert.False(dd.HasPending);
            Assert.False(File.Exists(DbFile(dir, "tictack-deferred-test")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Check_WithinHold_WaitsAndWarns()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            var log = new RecordingLogger();
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 7, log);
            dd.RecordPending(new List<string> { Path.Combine(dir, "held.txt") }, dir);

            var action = dd.Check();

            Assert.Equal(DeferredActionType.Waiting, action.Type);
            Assert.True(dd.HasPending);
            Assert.Contains(log.Messages, m => m.Contains("remaining"));
            // Warning touched metadata only: no JSON batch files, one .db.
            Assert.Empty(Directory.GetFiles(dir, "tictack-deferred-test-*.json"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void NextDueUtc_OneShotDeadlineThenDailyWarning()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 7, new RecordingLogger());
            Assert.Null(dd.NextDueUtc());
            var before = DateTime.UtcNow;
            dd.RecordPending(new List<string> { Path.Combine(dir, "held.txt") }, dir);

            // First warning is due immediately (never warned).
            var first = dd.NextDueUtc();
            Assert.NotNull(first);
            Assert.True(first.Value <= DateTime.UtcNow.AddMinutes(1));

            dd.CheckWarnings();
            // After warning: next due is the daily warning, not hourly.
            var second = dd.NextDueUtc();
            Assert.NotNull(second);
            Assert.True(second.Value > DateTime.UtcNow.AddHours(20));
            Assert.True(second.Value < before.AddDays(7).AddMinutes(1));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void CheckWarnings_SourceMissing_HoldsExpiredBatch()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            var log = new RecordingLogger();
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 0, log);
            dd.RecordPending(new List<string> { Path.Combine(dir, "held.txt") },
                Path.Combine(dir, "no-such-source"));

            Assert.Equal(DeferredActionType.Waiting, dd.CheckWarnings());
            Assert.Contains(log.Messages, m => m.Contains("source unavailable"));
            var chunk = dd.TakeExpiredChunk(500);
            Assert.Empty(chunk.Files);
            Assert.True(dd.HasPending);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ExternalDelete_WhileStopped_CancelsHolds()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            const string prefix = "tictack-deferred-test";
            using (var dd = new DeferredDeletion(dir, prefix, 7, new RecordingLogger()))
                dd.RecordPending(new List<string> { Path.Combine(dir, "held.txt") }, dir);
            Assert.True(File.Exists(DbFile(dir, prefix)));

            // "I don't care": deleting the hold file drops every hold.
            File.Delete(DbFile(dir, prefix));
            using (var dd = new DeferredDeletion(dir, prefix, 7, new RecordingLogger()))
            {
                Assert.False(dd.HasPending);
                Assert.Equal(0, dd.PendingCount);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RecordPending_PathsLoggedAtDebugOnly()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            var log = new RecordingLogger();
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 7, log);
            dd.RecordPending(new List<string> { Path.Combine(dir, "held.txt") }, dir);

            // Count line stays at Warn; the path sample drops to Debug.
            Assert.Contains(log.Messages, m => m.StartsWith("WRN:") && m.Contains("hold for 7 days"));
            Assert.Contains(log.Messages, m => m.StartsWith("DBG:") && m.Contains("held.txt"));
            Assert.DoesNotContain(log.Messages, m => (m.StartsWith("WRN:") || m.StartsWith("INF:")) && m.Contains("held.txt"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void CorruptBatch_PreservedWhileHealthyBatchProceeds()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            const string prefix = "tictack-deferred-test";
            var corrupt = Path.Combine(dir, prefix + "-20000101-000000.json");
            File.WriteAllText(corrupt, "{not json");
            var log = new RecordingLogger();
            using var dd = new DeferredDeletion(dir, prefix, 0, log);
            var gone = Path.Combine(dir, "gone.txt");
            dd.RecordPending(new List<string> { gone }, dir);

            var action = dd.Check();

            Assert.Equal(DeferredActionType.Proceed, action.Type);
            Assert.Equal(gone, Assert.Single(action.Files!));
            // Corrupt evidence preserved, and its load failure was logged.
            Assert.True(File.Exists(corrupt));
            Assert.Contains(log.Messages, m => m.Contains("could not be loaded") && m.Contains(corrupt));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void LegacyFile_MigratesWithOriginalClock()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            const string prefix = "tictack-deferred-test";
            const string legacy = "tictack-deferred-legacy.json";
            var blockedAt = DateTime.UtcNow.AddDays(-6);
            var held = Path.Combine(dir, "held.txt");
            WriteBatchFile(Path.Combine(dir, legacy), blockedAt, new[] { held }, dir);

            using var dd = new DeferredDeletion(dir, prefix, 7, new RecordingLogger(), legacy);

            // Adopted under its original clock (~1 day left), legacy gone.
            Assert.True(dd.HasPending);
            Assert.False(File.Exists(Path.Combine(dir, legacy)));
            Assert.True(File.Exists(DbFile(dir, prefix)));
            // Migrated rows carry LastWarningAt=MinValue, so the first
            // warning is due immediately: fire it, then the next due is
            // min(expiry, warn+1d) — about a day out for a 6-day-old
            // 7-day hold.
            dd.CheckWarnings();
            var due = dd.NextDueUtc();
            Assert.NotNull(due);
            Assert.True(due.Value > DateTime.UtcNow.AddHours(20));
            Assert.True(due.Value < DateTime.UtcNow.AddDays(1).AddMinutes(10));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void StableHash_IsDeterministicAndCaseInsensitive()
    {
        var a = DeferredDeletion.StableHash(@"C:\Users\User\Desktop");
        Assert.Equal(a, DeferredDeletion.StableHash(@"C:\Users\User\Desktop"));
        Assert.Equal(a, DeferredDeletion.StableHash(@"c:\users\user\desktop"));
        Assert.NotEqual(a, DeferredDeletion.StableHash(@"C:\Users\User\Documents"));
        Assert.Equal(8, a.Length);
    }

    [Fact]
    public void RecordPending_CaseDistinctNames_KeptOnLinux()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            using var dd = new DeferredDeletion(dir, "tictack-deferred-test", 0, new RecordingLogger());
            dd.RecordPending(new List<string>
            {
                Path.Combine(dir, "a.txt"),
                Path.Combine(dir, "A.txt")
            }, dir);

            var action = dd.Check();

            var expected = OperatingSystem.IsWindows() ? 1 : 2;
            Assert.Equal(DeferredActionType.Proceed, action.Type);
            Assert.Equal(expected, action.Files!.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
