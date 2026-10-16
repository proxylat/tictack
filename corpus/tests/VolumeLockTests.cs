namespace Corpus;

public sealed class VolumeLockTests
{
    [Fact]
    public void Acquire_NullDisk_YieldsEmptySet()
    {
        using var locks = VolumeLockSet.Acquire(null, new TestLogger());
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public void Acquire_OffWindows_YieldsEmptySet()
    {
        if (OperatingSystem.IsWindows()) return;
        using var locks = VolumeLockSet.Acquire(1, new TestLogger());
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var locks = VolumeLockSet.Acquire(null, new TestLogger());
        locks.Dispose();
        locks.Dispose();
    }

    [Fact]
    public void FsctlCodes_PinKernelValues()
    {
        // IOCTL typos are silent killers (wrong code, valid call): pin the
        // three volume codes against winioctl.h.
        Assert.Equal(0x00090018u, VolumeLockSet.FsctlLockVolume);
        Assert.Equal(0x0009001Cu, VolumeLockSet.FsctlUnlockVolume);
        Assert.Equal(0x00090020u, VolumeLockSet.FsctlDismountVolume);
    }

    [Fact]
    public void VolumesOnDisk_OffWindows_Empty()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Empty(DiskCapture.VolumesOnDisk(0));
    }
}
