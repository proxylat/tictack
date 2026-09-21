using System.Diagnostics;

namespace TicTack;

public class SrcLockTests : IDisposable
{
    private readonly string _dir;
    private readonly string _lockPath;
    private readonly RecordingLogger _log = new();

    public SrcLockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TicTackTest_lock_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _lockPath = Path.Combine(_dir, ".tictack.lock");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // A foreign holder's identity in the production format (host:pid),
    // guaranteed not to be this process.
    static string ForeignIdentity() =>
        Environment.MachineName + ":" + (Environment.ProcessId + 1);

    [Fact]
    public void AcquiresLock()
    {
        using var lockObj = new SrcLock(_lockPath, _log);
        Assert.True(lockObj.IsHeld);
        Assert.True(File.Exists(_lockPath));
    }

    [Fact]
    public void ReleasesLockOnDispose()
    {
        var lockObj = new SrcLock(_lockPath, _log);
        Assert.True(lockObj.IsHeld);
        lockObj.Dispose();
        Assert.False(File.Exists(_lockPath));
    }

    [Fact]
    public void DetectsStaleLock()
    {
        File.WriteAllText(_lockPath, ForeignIdentity());
        File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow.AddMinutes(-10));
        using var lockObj = new SrcLock(_lockPath, _log);
        Assert.True(lockObj.IsHeld);
    }

    [Fact]
    public void FreshLock_ReturnsImmediately()
    {
        File.WriteAllText(_lockPath, ForeignIdentity());
        File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow);

        var sw = Stopwatch.StartNew();
        var lockObj = new SrcLock(_lockPath, _log);
        sw.Stop();

        Assert.False(lockObj.IsHeld);
        // advisory lock, doesn't wait — returns < 500ms
        Assert.True(sw.ElapsedMilliseconds < 500);
        lockObj.Dispose();
        // A lock that was never held must not delete the foreign lock file.
        Assert.True(File.Exists(_lockPath));
        Assert.Contains(_log.Messages, m => m.Contains("lock acquisition failed"));
    }

    [Fact]
    public void StaleLock_JustUnderFiveMinutes_IsNotTakenOver()
    {
        File.WriteAllText(_lockPath, ForeignIdentity());
        File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow.AddMinutes(-5).AddSeconds(1));

        using var lockObj = new SrcLock(_lockPath, _log);

        Assert.False(lockObj.IsHeld);
        Assert.True(File.Exists(_lockPath));
    }

    [Fact]
    public void StaleLock_JustOverFiveMinutes_IsTakenOver()
    {
        File.WriteAllText(_lockPath, ForeignIdentity());
        File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow.AddMinutes(-5).AddSeconds(-1));

        using var lockObj = new SrcLock(_lockPath, _log);

        Assert.True(lockObj.IsHeld);
    }

    [Fact]
    public void ContendedLock_WithRetryTimeout_GivesUpAndWarns()
    {
        File.WriteAllText(_lockPath, ForeignIdentity());
        File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow);

        var sw = Stopwatch.StartNew();
        var lockObj = new SrcLock(_lockPath, _log, TimeSpan.FromMilliseconds(300));
        sw.Stop();

        Assert.False(lockObj.IsHeld);
        // One 5 s retry sleep must elapse: a zero sleep would return at ~300 ms.
        Assert.True(sw.ElapsedMilliseconds >= 4000);
        // The wait must be visible at info level, not buried in debug.
        Assert.Contains(_log.Messages, m => m.StartsWith("INF:") && m.Contains("Waiting for lock held by"));
        Assert.Contains(_log.Messages, m => m.Contains("lock acquisition failed"));
        lockObj.Dispose();
        Assert.True(File.Exists(_lockPath));
    }

    [Fact]
    public void LockFile_IdentityWritten()
    {
        using var lockObj = new SrcLock(_lockPath, _log);
        var content = SrcLock.ReadIdentity(_lockPath);
        Assert.Contains(Environment.MachineName, content);
        Assert.Contains(Environment.ProcessId.ToString(), content);
    }

    [Fact]
    public void RefreshTimer_TouchesLockFile()
    {
        using var lockObj = new SrcLock(_lockPath, _log, refreshInterval: TimeSpan.FromMilliseconds(100));
        Assert.True(lockObj.IsHeld);
        var t0 = File.GetLastWriteTimeUtc(_lockPath);

        Thread.Sleep(700);

        Assert.True(lockObj.IsHeld);
        Assert.True(File.GetLastWriteTimeUtc(_lockPath) > t0);
    }
}
