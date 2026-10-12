namespace TicTack;

[Trait("Category", "Unit")]
public sealed class DriveReadyTests
{
    [Fact]
    public void IsDriveReady_ExistingTempDir_ReturnsTrue()
    {
        Func<DriveInfo[]> probe = () => DriveInfo.GetDrives();
        Assert.True(DriveGuard.IsReady(Path.GetTempPath(), probe));
    }

    [Fact]
    public void IsDriveReady_NoDrives_ReturnsFalse()
    {
        Func<DriveInfo[]> probe = static () => [];
        Assert.False(DriveGuard.IsReady(Path.GetTempPath(), probe));
    }

    [Fact]
    public void IsDriveReady_SecondCallWithinTtl_DoesNotReprobe()
    {
        var probes = 0;
        Func<DriveInfo[]> probe = () => { probes++; return DriveInfo.GetDrives(); };
        Assert.True(DriveGuard.IsReady(Path.GetTempPath(), probe));
        Assert.True(DriveGuard.IsReady(Path.GetTempPath(), probe));
        Assert.Equal(1, probes);
    }

    [Fact]
    public void SelectReady_LongestReadyRoot_Wins()
    {
        var drives = new List<(string Root, bool Ready)> { ("C:\\", true), ("C:\\data", true), ("D:\\", false) };
        Assert.True(DriveGuard.SelectReady("C:\\data\\f.txt", drives));
        Assert.False(DriveGuard.SelectReady("D:\\f.txt", drives));
        Assert.False(DriveGuard.SelectReady("E:\\f.txt", drives));
    }

    [Theory]
    [InlineData("/srv", false)]
    [InlineData("/srv/x", false)]
    [InlineData("/mnt", false)]
    [InlineData("/mnt/x", false)]
    [InlineData("/media/x", false)]
    [InlineData("/run/media/u/x", false)]
    [InlineData("/home/u/x", true)]
    [InlineData("/srvx/y", true)]
    [InlineData("/", true)]
    public void SelectReady_LinuxMountGuard_Matrix(string path, bool expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        var drives = new List<(string Root, bool Ready)> { ("/", true) };
        Assert.Equal(expected, DriveGuard.SelectReady(path, drives));
    }
}
