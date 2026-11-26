using System;
using System.IO;
using TicTack;
using Xunit;

namespace TicTack.Tests;

public class LinuxParityTests
{
    [Theory]
    [InlineData("sda1", "sda")]
    [InlineData("vda12", "vda")]
    [InlineData("nvme0n1p2", "nvme0n1")]
    [InlineData("mmcblk0p1", "mmcblk0")]
    [InlineData("loop0p1", "loop0")]
    [InlineData("nvme0n1", "nvme0n1")]
    public void DiskCacheGuard_StripPartitionSuffix_HandlesAllDeviceTypes(string input, string expected)
    {
        Assert.Equal(expected, DiskCacheGuard.StripPartitionSuffix(input));
    }

    [Fact]
    public void DiskCacheGuard_IsLinuxWriteBackEnabled_ReadsSysfsFormat()
    {
        var root = Path.Combine(Path.GetTempPath(), "TicTackTest_sysblock_" + Guid.NewGuid());
        var queueDir = Path.Combine(root, "sda", "queue");
        Directory.CreateDirectory(queueDir);
        try
        {
            File.WriteAllText(Path.Combine(queueDir, "write_cache"), "write back\n");
            Assert.True(DiskCacheGuard.IsLinuxWriteBackEnabled("sda", root));

            File.WriteAllText(Path.Combine(queueDir, "write_cache"), "write through\n");
            Assert.False(DiskCacheGuard.IsLinuxWriteBackEnabled("sda", root));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void LinuxSnapshotScope_ParseMountFs_PicksLongestPrefix()
    {
        var lines = new[]
        {
            "26 1 8:1 / / rw,relatime - ext4 /dev/sda1 rw",
            "52 26 0:30 / /mnt/data rw,relatime - btrfs /dev/nvme0n1p2 rw",
            "53 26 0:31 / /mnt/zfs rw,relatime - zfs tank/data rw"
        };
        var btrfs = LinuxSnapshotScope.ParseMountFs("/mnt/data/docs/file.txt", lines);
        Assert.NotNull(btrfs);
        Assert.Equal("btrfs", btrfs!.Value.FsType);
        Assert.Equal("/mnt/data", btrfs.Value.MountPoint);

        var zfs = LinuxSnapshotScope.ParseMountFs("/mnt/zfs/sub", lines);
        Assert.NotNull(zfs);
        Assert.Equal("zfs", zfs!.Value.FsType);
        Assert.Equal("tank/data", zfs.Value.Source);
    }

    [Fact]
    public void EventLogLogger_LinuxSyslog_HandlesFormatSpecifiersSafely()
    {
        var logger = new EventLogLogger("TicTackTest", LogLevel.Warn);
        logger.Warn("path %s %n %x safe");
        logger.Error("err %s %n", new InvalidOperationException("boom %s"));
    }

    [Fact]
    public void ServiceTuning_LinuxApply_DoesNotThrow()
    {
        var log = new RecordingLogger();
        var cfg = new TicTackConfig { CpuPriorityClass = 2 };
        ServiceTuning.Apply(cfg, log, "TicTackSv");
    }

    [Fact]
    public void SleepInhibit_Hold_DisabledReturnsCleanly()
    {
        var log = new RecordingLogger();
        using var hold = SleepInhibit.Hold(false, "test", 1, log);
    }
}
