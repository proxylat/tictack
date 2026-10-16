using System;
using System.Collections.Generic;
using System.IO;

namespace Corpus
{
    // Physical-disk inventory for --list-disks (and the restore.cmd prompt):
    // probe \\.\PhysicalDrive0..31, keep what opens. Windows-only; Linux
    // returns empty (no block-device probing from managed code in v1).
    // Model stays "" — no STORAGE_PROPERTY_QUERY in v1; number+size is all
    // the restore token needs.
    public sealed record DiskInfo(int Number, string DevicePath, long Size, bool IsBoot, string Model);

    // Volume inventory for --list-volumes: ready drives via DriveInfo
    // (cross-platform, no probing ioctls). Not-ready drives (empty DVD,
    // sleeping card readers) are skipped — TotalSize throws on them.
    public sealed record VolumeInfo(string Name, string FileSystem, long Size);

    public static class DiskInventory
    {
        public static List<VolumeInfo> ListVolumes()
        {
            var vols = new List<VolumeInfo>();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady)
                    continue;
                vols.Add(new VolumeInfo(d.Name, d.DriveFormat, d.TotalSize));
            }
            return vols;
        }

        public static List<DiskInfo> ListDisks()
        {
            var disks = new List<DiskInfo>();
            if (!OperatingSystem.IsWindows())
                return disks;
            var boot = BootDiskGuard.SystemDiskDevice();
            for (var n = 0; n < 32; n++)
            {
                var path = $@"\\.\PhysicalDrive{n}";
                long size;
                try
                {
                    size = NativeDisk.GetLengthBytes(path);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                disks.Add(new DiskInfo(n, path, size, string.Equals(path, boot, StringComparison.OrdinalIgnoreCase), ""));
            }
            return disks;
        }
    }
}
