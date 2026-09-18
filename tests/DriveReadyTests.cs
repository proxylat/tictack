namespace TicTack;

[Trait("Category", "Unit")]
public sealed class DriveReadyTests
{
    [Fact]
    public void IsDriveReady_ExistingTempDir_ReturnsTrue()
    {
        Func<DriveInfo[]> probe = () => DriveInfo.GetDrives();
        Assert.True(SyncPipeline.IsDriveReady(Path.GetTempPath(), probe));
    }

    [Fact]
    public void IsDriveReady_NoDrives_ReturnsFalse()
    {
        Func<DriveInfo[]> probe = static () => [];
        Assert.False(SyncPipeline.IsDriveReady(Path.GetTempPath(), probe));
    }

    [Fact]
    public void IsDriveReady_SecondCallWithinTtl_DoesNotReprobe()
    {
        var probes = 0;
        Func<DriveInfo[]> probe = () => { probes++; return DriveInfo.GetDrives(); };
        Assert.True(SyncPipeline.IsDriveReady(Path.GetTempPath(), probe));
        Assert.True(SyncPipeline.IsDriveReady(Path.GetTempPath(), probe));
        Assert.Equal(1, probes);
    }

    [Fact]
    public void IsDriveReady_DifferentProbe_RefreshesCache()
    {
        var probesA = 0;
        var probesB = 0;
        Func<DriveInfo[]> probeA = () => { probesA++; return DriveInfo.GetDrives(); };
        Func<DriveInfo[]> probeB = () => { probesB++; return DriveInfo.GetDrives(); };
        Assert.True(SyncPipeline.IsDriveReady(Path.GetTempPath(), probeA));
        Assert.True(SyncPipeline.IsDriveReady(Path.GetTempPath(), probeB));
        Assert.Equal(1, probesA);
        Assert.Equal(1, probesB);
    }
}
