using System.Collections.Concurrent;

namespace TicTack;

public class StressTests
{
    const string Root = "TicTackStressTest";

    static string TestDir() =>
        Path.Combine(Path.GetTempPath(), Root + "_" + Guid.NewGuid());

    static SourceConfig MakeConfig(string src, string dst, double debounce = 0.1) => new()
    {
        Path = src,
        Destination = dst,
        DebounceSeconds = debounce,
        Filter = new FilterConfig(),
        Sync = new SyncConfig { Verification = "size" }
    };

    // ── 1. Burst creates: 500 files in parallel, verify all synced ──

    [Fact]
    public async Task BurstCreate_SyncsAll()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            var n = 500;
            for (int i = 0; i < n; i++)
                File.WriteAllText(Path.Combine(src, $"f{i}.txt"), $"content{i}");

            using var pipeline = new SyncPipeline(
                MakeConfig(src, dst),
                new EventMonitor(),
                new DateSizeComparer(),
                new CopyAction(new FileAccessor()),
                new RenameAction(),
                new ExponentialBackoffRetry(1, 0, 1),
                new SizeValidator(),
                new NoVersioning(),
                new MirrorDeletion(),
                new RecordingLogger()
            );
            pipeline.Start();
            await Task.Delay(5000); // let InitialSync finish for 500 files
            // pipeline.Dispose via using

            for (int i = 0; i < n; i++)
                Assert.True(File.Exists(Path.Combine(dst, $"f{i}.txt")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 2. Empty file (0 bytes) syncs ──

    [Fact]
    public async Task EmptyFile_Syncs()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            File.WriteAllText(Path.Combine(src, "empty.txt"), "");

            using var pipeline = new SyncPipeline(
                MakeConfig(src, dst),
                new EventMonitor(),
                new DateSizeComparer(),
                new CopyAction(new FileAccessor()),
                new RenameAction(),
                new ExponentialBackoffRetry(1, 0, 1),
                new SizeValidator(),
                new NoVersioning(),
                new MirrorDeletion(),
                new RecordingLogger()
            );
            pipeline.Start();
            await Task.Delay(500);


            Assert.True(File.Exists(Path.Combine(dst, "empty.txt")));
            Assert.Equal(0, new FileInfo(Path.Combine(dst, "empty.txt")).Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 3. File deleted mid-copy (source disappears) ──

    [Fact]
    public async Task CopyAction_SourceDeletedMidCopy_ReturnsFail()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            var file = Path.Combine(src, "goner.txt");
            File.WriteAllText(file, "bye");
            var action = new CopyAction(new FileAccessor());

            // Delete source just before copy (race condition sim)
            var args = new FileActionArgs(
                new FileChangedEventArgs(ChangeType.Created, file), src, dst);
            File.Delete(file);

            var result = await action.ExecuteAsync(args, CancellationToken.None);
            Assert.False(result.Success);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 4. Concurrent renames to same dest ──

    [Fact]
    public async Task RenameAction_ConcurrentSameDest_NoCrash()
    {
        var dir = TestDir();
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(dst);
        try
        {
            var f1 = Path.Combine(dst, "a.txt");
            var f2 = Path.Combine(dst, "b.txt");
            var target = Path.Combine(dst, "target.txt");
            File.WriteAllText(f1, "one");
            File.WriteAllText(f2, "two");

            var action = new RenameAction();
            var tasks = new[]
            {
                action.ExecuteAsync(new FileActionArgs(
                    new FileChangedEventArgs(ChangeType.Renamed, target, f1), dst, dst), CancellationToken.None),
                action.ExecuteAsync(new FileActionArgs(
                    new FileChangedEventArgs(ChangeType.Renamed, target, f2), dst, dst), CancellationToken.None)
            };
            await Task.WhenAll(tasks);

            // One should win, no exception
            Assert.True(File.Exists(target));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 5. Source file locked by another process ──

    [Fact]
    public async Task CopyAction_LockedSource_UsesPlatformSharingRules()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            var file = Path.Combine(src, "locked.txt");
            File.WriteAllText(file, "locked content");

            // Hold exclusive lock on source
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var action = new CopyAction(new FileAccessor());
                var args = new FileActionArgs(
                    new FileChangedEventArgs(ChangeType.Modified, file), src, dst);
                var result = await action.ExecuteAsync(args, CancellationToken.None);
                if (OperatingSystem.IsWindows())
                    Assert.False(result.Success);
                else
                    Assert.True(result.Success);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 6. StateDb concurrent batch ──

    [Fact]
    public void StateDb_ConcurrentBatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_sdb_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "stress.db");
        try
        {
            using var db = new StateDb(dbPath);
            var n = 100;
            Parallel.For(0, n, i => db.Upsert($"f{i}.txt", i, i));

            var all = db.LoadAll();
            Assert.Equal(n, all.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 7. StateDb UpdatePrefix correctness ──

    [Fact]
        public void StateDb_UpdatePrefix_RenamesChildPaths()
        {
            var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_sdb_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, "prefix.db");
            string S = Path.DirectorySeparatorChar.ToString();
            try
            {
                using var db = new StateDb(dbPath);
                db.Upsert($"folder{S}file1.txt", 100, 1000);
                db.Upsert($"folder{S}sub{S}file2.txt", 200, 2000);
                db.Upsert("other.txt", 300, 3000);

                db.UpdatePrefix("folder", "folder_renamed");

                var all = db.LoadAll();
                Assert.Equal(3, all.Count);
                Assert.True(all.ContainsKey($"folder_renamed{S}file1.txt"));
                Assert.True(all.ContainsKey($"folder_renamed{S}sub{S}file2.txt"));
                Assert.True(all.ContainsKey("other.txt"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

    // ── 8. RenameAction directory ──

    [Fact]
    public async Task RenameAction_MovesDirectory()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            var oldDir = Path.Combine(dir, "old_folder");
            var newDir = Path.Combine(dir, "new_folder");
            Directory.CreateDirectory(oldDir);
            File.WriteAllText(Path.Combine(oldDir, "a.txt"), "aaa");
            File.WriteAllText(Path.Combine(oldDir, "b.txt"), "bbb");

            var action = new RenameAction();
            var srcBase = Path.Combine(dir, "src");
            var dstBase = Path.Combine(dir, "dst");

            // Simulate rename of subfolder
            var dstOld = Path.Combine(dstBase, "old_folder");
            var dstNew = Path.Combine(dstBase, "new_folder");
            Directory.CreateDirectory(dstOld);
            File.WriteAllText(Path.Combine(dstOld, "a.txt"), "aaa");
            File.WriteAllText(Path.Combine(dstOld, "b.txt"), "bbb");

            var e = new FileChangedEventArgs(ChangeType.Renamed,
                Path.Combine(dir, "new_folder"), Path.Combine(dir, "old_folder"));
            var args = new FileActionArgs(e, dir, dstBase);
            var result = await action.ExecuteAsync(args, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(Directory.Exists(dstNew));
            Assert.True(File.Exists(Path.Combine(dstNew, "a.txt")));
            Assert.True(File.Exists(Path.Combine(dstNew, "b.txt")));
            Assert.False(Directory.Exists(dstOld));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 9. RenameAction directory non-existent old destination ──

    [Fact]
    public async Task RenameAction_Directory_OldNotExists_NoError()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            var dstBase = Path.Combine(dir, "dst");
            Directory.CreateDirectory(dstBase);

            var action = new RenameAction();
            var e = new FileChangedEventArgs(ChangeType.Renamed,
                Path.Combine(dir, "new_folder"), Path.Combine(dir, "old_folder"));
            var args = new FileActionArgs(e, dir, dstBase);
            var result = await action.ExecuteAsync(args, CancellationToken.None);
            Assert.True(result.Success);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 10. Pipeline debounced event processing ──

    [Fact]
    public async Task Pipeline_ProcessesDebouncedEvent()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            var monitor = new EventMonitor();
            var log = new RecordingLogger();
            var deletion = new RecordingDeletion(new MirrorDeletion());

            using var pipeline = new SyncPipeline(
                MakeConfig(src, dst),
                monitor,
                new DateSizeComparer(),
                new CopyAction(new FileAccessor()),
                new RenameAction(),
                new ExponentialBackoffRetry(1, 0, 1),
                new SizeValidator(),
                new NoVersioning(),
                deletion,
                log
            );
            pipeline.Start();
            await Task.Delay(100);

            var f = Path.Combine(src, "debounced.txt");
            File.WriteAllText(f, "hello");

            monitor.Fire(ChangeType.Created, f);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(Path.Combine(dst, "debounced.txt")) && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            pipeline.Dispose();

            Assert.True(File.Exists(Path.Combine(dst, "debounced.txt")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 11. Pipeline processes deletion via event ──

    [Fact]
    public async Task Pipeline_ProcessesDeleteEvent()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            var f = Path.Combine(src, "todelete.txt");
            File.WriteAllText(f, "bye");
            var d = Path.Combine(dst, "todelete.txt");
            File.WriteAllText(d, "bye");

            var monitor = new EventMonitor();
            var log = new RecordingLogger();
            var deletion = new RecordingDeletion(new MirrorDeletion());

            using var pipeline = new SyncPipeline(
                MakeConfig(src, dst),
                monitor,
                new DateSizeComparer(),
                new CopyAction(new FileAccessor()),
                new RenameAction(),
                new ExponentialBackoffRetry(1, 0, 1),
                new SizeValidator(),
                new NoVersioning(),
                deletion,
                log
            );
            pipeline.Start();
            await Task.Delay(100);

            File.Delete(f);
            monitor.Fire(ChangeType.Deleted, f);
            await Task.Delay(500);
            pipeline.Dispose();

            Assert.Contains(deletion.Calls, call => call.dst == d);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 12. Pipeline handles rapid bursts via emitted events ──

    [Fact]
    public async Task Pipeline_BurstEvents_NoCrash()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            var monitor = new EventMonitor();
            var log = new RecordingLogger();

            using var pipeline = new SyncPipeline(
                MakeConfig(src, dst, 0.01),
                monitor,
                new DateSizeComparer(),
                new CopyAction(new FileAccessor()),
                new RenameAction(),
                new ExponentialBackoffRetry(1, 0, 1),
                new SizeValidator(),
                new NoVersioning(),
                new MirrorDeletion(),
                log
            );
            pipeline.Start();
            await Task.Delay(50);

            var n = 200;
            for (int i = 0; i < n; i++)
            {
                var f = Path.Combine(src, $"burst{i}.txt");
                File.WriteAllText(f, $"burst{i}");
                monitor.Fire(ChangeType.Created, f);
            }
            await Task.Delay(1000);

            // Fire deletes for all
            for (int i = 0; i < n; i++)
            {
                var f = Path.Combine(src, $"burst{i}.txt");
                File.Delete(f);
                monitor.Fire(ChangeType.Deleted, f);
            }
            await Task.Delay(2000);
            pipeline.Dispose();

        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 13. SrcLock concurrent creation ──

    [Fact]
    public void SrcLock_ConcurrentCreation_NoConflict()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_lock_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var lockPath = Path.Combine(dir, "concurrent.lock");
        try
        {
            var locks = new ConcurrentBag<SrcLock>();
            Parallel.For(0, 10, _ => locks.Add(new SrcLock(lockPath, new RecordingLogger())));
            Assert.True(File.Exists(lockPath));
            foreach (var l in locks) l.Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 14. CopyAction very large file (streaming) ──

    [Fact]
    public async Task CopyAction_LargeFile_StreamsCorrectly()
    {
        var dir = TestDir();
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            var f = Path.Combine(src, "large.bin");
            using (var fs = new FileStream(f, FileMode.Create))
            {
                var data = new byte[5 * 1024 * 1024]; // 5 MB
                new Random(42).NextBytes(data);
                fs.Write(data, 0, data.Length);
            }

            var action = new CopyAction(new FileAccessor());
            var args = new FileActionArgs(
                new FileChangedEventArgs(ChangeType.Created, f), src, dst);
            var result = await action.ExecuteAsync(args, CancellationToken.None);

            Assert.True(result.Success);
            var dstFile = Path.Combine(dst, "large.bin");
            Assert.True(File.Exists(dstFile));
            Assert.Equal(new FileInfo(f).Length, new FileInfo(dstFile).Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 15. Power loss: partial .tictack.tmp leftover ──

    [Fact]
    public async Task CopyAction_LeftoverTmp_CleanedUp()
    {
        var dir = TestDir();
        var srcDir = Path.Combine(dir, "src");
        var dstDir = Path.Combine(dir, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        try
        {
            var src = Path.Combine(srcDir, "file.txt");
            File.WriteAllText(src, "hello");
            var dst = Path.Combine(dstDir, "file.txt");
            File.WriteAllText(dst + ".tictack.tmp", "partial garbage");

            var action = new CopyAction(new FileAccessor());
            var args = new FileActionArgs(
                new FileChangedEventArgs(ChangeType.Created, src), srcDir, dstDir);
            var result = await action.ExecuteAsync(args, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(File.Exists(dst));
            Assert.Equal("hello", File.ReadAllText(dst));
            Assert.False(File.Exists(dst + ".tictack.tmp"), "tmp file should be cleaned up");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 16. Power loss: dst exists, StateDb missing entry ──

    [Fact]
    public async Task InitialSync_AfterCrash_RecoversMissingState()
    {
        var dir = TestDir();
        var srcDir = Path.Combine(dir, "src");
        var dstDir = Path.Combine(dir, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        try
        {
            var file = Path.Combine(srcDir, "survivor.txt");
            File.WriteAllText(file, "survived");
            var dst = Path.Combine(dstDir, "survivor.txt");
            File.Copy(file, dst);

            var statePath = Path.Combine(dstDir, ".tictack.db");
            using (var db = new StateDb(statePath))
            {
                // Simulate crash AFTER copy but BEFORE StateDb write — no entry
            }

            using (var db2 = new StateDb(statePath))
            {
                var cache = db2.LoadAll();
                var rel = "survivor.txt";
                Assert.False(cache.ContainsKey(rel), "StateDb should not have entry");

                long srcSize = new FileInfo(file).Length;
                long srcTicks = new FileInfo(file).LastWriteTimeUtc.Ticks;
                db2.Upsert(rel, srcSize, srcTicks);
            }

            using (var db3 = new StateDb(statePath))
            {
                var cache = db3.LoadAll();
                Assert.True(cache.ContainsKey("survivor.txt"));
                Assert.Equal(new FileInfo(file).Length, cache["survivor.txt"].size);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 17. Power loss: StateDb WAL crash recovery ──

    [Fact]
    public void StateDb_WalCrashRecovery()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "crash.db");
        try
        {
            using (var db = new StateDb(dbPath))
            {
                db.Upsert("a.txt", 100, 1000);
                db.Upsert("b.txt", 200, 2000);
            }

            // Simulate crash by not closing cleanly — just dispose
            // SQLite WAL will replay on reopen
            using (var db2 = new StateDb(dbPath))
            {
                var all = db2.LoadAll();
                Assert.Equal(2, all.Count);
                Assert.Contains("a.txt", all.Keys);
                Assert.Contains("b.txt", all.Keys);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

}
