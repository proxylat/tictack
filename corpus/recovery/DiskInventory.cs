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

    public static class DiskInventory
    {
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
