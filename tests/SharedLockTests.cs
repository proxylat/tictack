namespace TicTack;

public class SharedLockTests : IDisposable
{
    private readonly string _dir;
    private readonly string _lockPath;
    private readonly RecordingLogger _log = new();

    public SharedLockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TicTackTest_sharedlock_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _lockPath = Path.Combine(_dir, ".tictack.lock");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void SharedPlusShared_Coexists()
    {
        using var a = new SrcLock(_lockPath, _log, mode: LockMode.Shared);
        using var b = new SrcLock(_lockPath, _log, mode: LockMode.Shared);
        Assert.True(a.IsHeld);
        Assert.True(b.IsHeld);
    }

    [Fact]
    public void Exclusive_BlockedByLiveShared()
    {
        using var shared = new SrcLock(_lockPath, _log, mode: LockMode.Shared);
        Assert.True(shared.IsHeld);
        using var excl = new SrcLock(_lockPath, _log);
        Assert.False(excl.IsHeld);
    }

    [Fact]
    public void Shared_BlockedByLiveExclusive()
    {
        using var excl = new SrcLock(_lockPath, _log);
        Assert.True(excl.IsHeld);
        using var shared = new SrcLock(_lockPath, _log, mode: LockMode.Shared);
        Assert.False(shared.IsHeld);
    }

    [Fact]
    public void Exclusive_SweepsStaleRoster()
    {
        using var shared = new SrcLock(_lockPath, _log, mode: LockMode.Shared);
        Assert.True(shared.IsHeld);
        var roster = Directory.GetFiles(_dir, ".tictack.lock.shared.*");
        Assert.Single(roster);
        // Backdate past the 5-minute stale floor; the 30s refresh cannot
        // fire before the immediate exclusive attempt below.
        File.SetLastWriteTimeUtc(roster[0], DateTime.UtcNow.AddMinutes(-10));
        using var excl = new SrcLock(_lockPath, _log);
        Assert.True(excl.IsHeld);
    }
}
