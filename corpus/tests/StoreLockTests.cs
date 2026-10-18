using System.Diagnostics;

namespace Corpus;

// §12.9 store lock: exclusive lock file in the store root, stale-steal via
// PID-liveness + heartbeat, --retry-lock backoff waits, loud failures.
public sealed class StoreLockTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly TestLogger _log = new();

    public StoreLockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_storelock_" + Guid.NewGuid().ToString("N"));
        _storeRoot = Path.Combine(_dir, "store");
        Directory.CreateDirectory(_storeRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string LockPath => Path.Combine(_storeRoot, StoreLock.LockFileName);

    private static int OwnPid => Process.GetCurrentProcess().Id;

    // Seed a lock file as another (or crashed) holder would leave it.
    private void SeedHolder(int pid, string host, DateTime mtimeUtc, string since = "2026-01-01T00:00:00.0000000Z")
    {
        File.WriteAllText(LockPath, $"pid={pid} host={host} since={since} id=seed");
        File.SetLastWriteTimeUtc(LockPath, mtimeUtc);
    }

    [Fact]
    public void Parse_Accepts()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), StoreLock.ParseRetryTimeout("90"));
        Assert.Equal(TimeSpan.FromSeconds(10), StoreLock.ParseRetryTimeout("10s"));
        Assert.Equal(TimeSpan.FromSeconds(10), StoreLock.ParseRetryTimeout("10S"));
        Assert.Equal(TimeSpan.FromMinutes(10), StoreLock.ParseRetryTimeout("10m"));
        Assert.Equal(TimeSpan.FromHours(2), StoreLock.ParseRetryTimeout("2h"));
        Assert.Equal(TimeSpan.Zero, StoreLock.ParseRetryTimeout("0"));
        Assert.Equal(TimeSpan.FromMinutes(5), StoreLock.ParseRetryTimeout("  5m  "));
    }

    [Fact]
    public void Parse_Refuses()
    {
        foreach (var bad in new[] { "", "  ", "abc", "10x", "-5", "m", "s", "h", "1.5h", "10 m", "--", "s10", "99999999999999999999h" })
            Assert.Throws<StoreLockException>(() => StoreLock.ParseRetryTimeout(bad));
    }


    // Windows: the held lock handle is Share.Read, so a plain ReadAllText
    // (Share.Read) self-conflicts with our own write access. ReadWrite share
    // tolerates the holder (mirrors the product reader in StoreLock).
    private static string ReadLockText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    [Fact]
    public void Acquire_Release_Roundtrip()
    {
        using (var lck = StoreLock.Acquire(_storeRoot, null, _log))
        {
            Assert.Equal(LockPath, lck.LockPath);
            Assert.True(File.Exists(LockPath));
            Assert.Contains("pid=" + OwnPid, ReadLockText(LockPath));
        }
        Assert.False(File.Exists(LockPath));
    }

    [Fact]
    public void Contention_Default_FailsLoudNamingHolder()
    {
        SeedHolder(OwnPid, Environment.MachineName, DateTime.UtcNow);
        var ex = Assert.Throws<StoreLockException>(() => StoreLock.Acquire(_storeRoot, null, _log));
        Assert.Contains("store locked by pid " + OwnPid, ex.Message);
        Assert.Contains("since", ex.Message);
    }

    [Fact]
    public void Retry_Timeout_SameLoudFailure()
    {
        SeedHolder(OwnPid, Environment.MachineName, DateTime.UtcNow);
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<StoreLockException>(
            () => StoreLock.Acquire(_storeRoot, TimeSpan.FromSeconds(4), _log));
        sw.Stop();
        Assert.Contains("store locked by pid " + OwnPid, ex.Message);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(3), "gave up early after " + sw.Elapsed);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "waited far past budget: " + sw.Elapsed);
    }

    [Fact]
    public void StalePid_Steals()
    {
        // Fresh heartbeat but a pid that can never be alive.
        SeedHolder(int.MaxValue, Environment.MachineName, DateTime.UtcNow);
        using var lck = StoreLock.Acquire(_storeRoot, null, _log);
        Assert.Contains("pid=" + OwnPid, ReadLockText(LockPath));
    }

    [Fact]
    public void StaleHeartbeat_StealsDespiteLivePid()
    {
        // Own (live) pid, but the heartbeat froze 10 min ago: crashed holder.
        SeedHolder(OwnPid, Environment.MachineName, DateTime.UtcNow.AddMinutes(-10));
        using var lck = StoreLock.Acquire(_storeRoot, null, _log);
        Assert.Contains("pid=" + OwnPid, ReadLockText(LockPath));
    }

    [Fact]
    public void ForeignHost_Fresh_Blocks()
    {
        // Another box's pid cannot be liveness-checked: conservative, wait it out.
        SeedHolder(1234, "definitely-not-this-box", DateTime.UtcNow);
        var ex = Assert.Throws<StoreLockException>(() => StoreLock.Acquire(_storeRoot, null, _log));
        Assert.Contains("store locked by pid 1234", ex.Message);
    }

    [Fact]
    public void GarbageFile_Fresh_BlocksNamingUnknown()
    {
        File.WriteAllText(LockPath, "corrupt!!!");
        File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow);
        var ex = Assert.Throws<StoreLockException>(() => StoreLock.Acquire(_storeRoot, null, _log));
        Assert.Contains("store locked by", ex.Message);
    }

    [Fact]
    public void MissingStoreRoot_FailsLoud()
    {
        var missing = Path.Combine(_dir, "no-store");
        Assert.Throws<StoreLockException>(() => StoreLock.Acquire(missing, null, _log));
    }

    [Fact]
    public async Task WaitThenProceed_Threads()
    {
        using var holder = StoreLock.Acquire(_storeRoot, null, _log);
        var waiter = Task.Run(() => StoreLock.Acquire(_storeRoot, TimeSpan.FromSeconds(20), _log));
        Thread.Sleep(1500); // let the waiter block on the held lock
        Assert.False(waiter.IsCompleted);
        holder.Dispose(); // release: the waiter must proceed, not time out
        using var acquired = await waiter;
        Assert.True(File.Exists(LockPath));
    }

    [Fact]
    public void Dispose_DeletesOnlyOwnFile()
    {
        var lck = StoreLock.Acquire(_storeRoot, null, _log);
        lck.Dispose(); // releases the handle and deletes our own file
        Assert.False(File.Exists(LockPath));
        // A stealer owns the path now: our Dispose must not remove theirs.
        // (Written after release: Windows denies writes while held.)
        File.WriteAllText(LockPath, "pid=1 host=x since=y id=stealer");
        lck.Dispose();
        Assert.True(File.Exists(LockPath));
        Assert.Contains("stealer", ReadLockText(LockPath));
        lck.Dispose(); // idempotent
    }

    private static string? FindSleep()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir.Trim(), "sleep");
            if (candidate.Length > 0 && File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static Process StartSleep(string sleep)
    {
        var psi = new ProcessStartInfo
        {
            FileName = sleep,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("60");
        return Process.Start(psi)!;
    }

    [Fact]
    public void Subprocess_Holder_TimesOutLoud()
    {
        var sleep = FindSleep();
        if (sleep == null)
            return; // no sleep(1) on this box; nothing to hold the pid with
        using var holder = StartSleep(sleep);
        try
        {
            SeedHolder(holder.Id, Environment.MachineName, DateTime.UtcNow);
            var ex = Assert.Throws<StoreLockException>(
                () => StoreLock.Acquire(_storeRoot, TimeSpan.FromSeconds(3), _log));
            Assert.Contains("store locked by pid " + holder.Id, ex.Message);
        }
        finally
        {
            try { holder.Kill(); } catch { }
            holder.WaitForExit(10_000);
        }
    }

    [Fact]
    public void Subprocess_DeadHolder_Steals()
    {
        var sleep = FindSleep();
        if (sleep == null)
            return;
        using var holder = StartSleep(sleep);
        SeedHolder(holder.Id, Environment.MachineName, DateTime.UtcNow);
        try { holder.Kill(); } catch { }
        Assert.True(holder.WaitForExit(10_000), "sleep refused to die");
        using var lck = StoreLock.Acquire(_storeRoot, null, _log);
        Assert.Contains("pid=" + OwnPid, ReadLockText(LockPath));
    }

    [Fact]
    public async Task Subprocess_KilledHolder_WaiterProceeds()
    {
        var sleep = FindSleep();
        if (sleep == null)
            return;
        using var holder = StartSleep(sleep);
        try
        {
            SeedHolder(holder.Id, Environment.MachineName, DateTime.UtcNow);
            var waiter = Task.Run(() => StoreLock.Acquire(_storeRoot, TimeSpan.FromSeconds(30), _log));
            Thread.Sleep(500); // let the waiter observe the live holder
            try { holder.Kill(); } catch { }
            Assert.True(holder.WaitForExit(10_000), "sleep refused to die");
            using var acquired = await waiter; // steals the dead pid, proceeds
            Assert.Contains("pid=" + OwnPid, ReadLockText(LockPath));
        }
        finally
        {
            try { holder.Kill(); } catch { }
            holder.WaitForExit(10_000);
        }
    }
}
