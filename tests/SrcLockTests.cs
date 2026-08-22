using System.Diagnostics;

namespace TicTack;

public class SrcLockTests : IDisposable
{
    private readonly string _dir;
    private readonly string _lockPath;
    private readonly MockLogger _log = new();

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
        File.WriteAllText(_lockPath, "other:12345");
        File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow.AddMinutes(-10));
        using var lockObj = new SrcLock(_lockPath, _log);
        Assert.True(lockObj.IsHeld);
    }

    [Fact]
    public void FreshLock_ReturnsImmediately()
    {
        File.WriteAllText(_lockPath, "other:12345");
        File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow);

        var sw = Stopwatch.StartNew();
        using var lockObj = new SrcLock(_lockPath, _log);
        sw.Stop();

        Assert.False(lockObj.IsHeld);
        // ponytail: advisory lock, doesn't wait — returns < 500ms
        Assert.True(sw.ElapsedMilliseconds < 500);
    }

    [Fact]
    public void LockFile_IdentityWritten()
    {
        using var lockObj = new SrcLock(_lockPath, _log);
        var content = File.ReadAllText(_lockPath);
        Assert.Contains(Environment.MachineName, content);
        Assert.Contains(Environment.ProcessId.ToString(), content);
    }
}
