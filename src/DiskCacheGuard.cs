using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TicTack
{
    internal static class DiskCacheGuard
    {
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
        private const int StorageDeviceWriteCacheProperty = 4;
        private const int PropertyStandardQuery = 0;
        private const uint FILE_SHARE_READ_WRITE = 0x3;
        private const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROPERTY_QUERY
        {
            public int PropertyId;
            public int QueryType;
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_WRITE_CACHE_PROPERTY
        {
            public uint Version;
            public uint Size;
            public int WriteCacheType;
            public byte WriteCacheEnabled;
            public byte WriteCacheChangeable;
            public byte WriteThroughSupported;
            public byte FlushCacheSupported;
            public byte UserDefinedPowerProtection;
            public byte NVCacheEnabled;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            ref STORAGE_PROPERTY_QUERY lpInBuffer, int nInBufferSize,
            out STORAGE_WRITE_CACHE_PROPERTY lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static void WarnIfWriteCacheEnabled(TicTackConfig cfg, ILogger log)
        {
            if (cfg.Sources == null) return;
            if (OperatingSystem.IsLinux())
            {
                WarnIfLinuxWriteCacheEnabled(cfg, log);
                return;
            }
            if (!OperatingSystem.IsWindows()) return;
            var checkedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in cfg.Sources)
            {
                if (string.IsNullOrWhiteSpace(src.Destination)) continue;
                var root = Path.GetPathRoot(src.Destination);
                if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) continue;
                var drive = root.TrimEnd('\\');
                if (drive.Length != 2 || drive[1] != ':' || !checkedRoots.Add(drive)) continue;

                if (TryIsVolatileWriteCacheEnabled(drive, out bool enabled) && enabled)
                {
                    log.Warn($"Destination drive {drive} has volatile write-caching enabled without battery/NV cache. " +
                             "Use a UPS, disable write-caching in Device Manager, or set sync.durability: write-through.");
                }
            }
        }

        private static bool TryIsVolatileWriteCacheEnabled(string drive, out bool enabled)
        {
            enabled = false;
            var h = CreateFileW(@"\\.\" + drive, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
            try
            {
                var query = new STORAGE_PROPERTY_QUERY
                {
                    PropertyId = StorageDeviceWriteCacheProperty,
                    QueryType = PropertyStandardQuery
                };
                if (!DeviceIoControl(
                        h, IOCTL_STORAGE_QUERY_PROPERTY,
                        ref query, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                        out var prop, Marshal.SizeOf<STORAGE_WRITE_CACHE_PROPERTY>(),
                        out _, IntPtr.Zero))
                    return false;

                enabled = prop.WriteCacheEnabled != 0
                          && prop.NVCacheEnabled == 0
                          && prop.UserDefinedPowerProtection == 0;
                return true;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private static void WarnIfLinuxWriteCacheEnabled(TicTackConfig cfg, ILogger log)
        {
            string[] mountLines;
            try { mountLines = File.ReadAllLines("/proc/self/mountinfo"); }
            catch { return; }

            var checkedDevs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var src in cfg.Sources!)
            {
                if (string.IsNullOrWhiteSpace(src.Destination)) continue;
                var dev = ResolveLinuxBlockDevice(src.Destination, mountLines);
                if (string.IsNullOrEmpty(dev) || !checkedDevs.Add(dev)) continue;
                if (IsLinuxWriteBackEnabled(dev))
                {
                    log.Warn($"Destination device /dev/{dev} has volatile write-back caching enabled. " +
                             "Use a UPS, switch to write-through, or set sync.durability: write-through.");
                }
            }
        }

        internal static string? ResolveLinuxBlockDevice(string destPath, string[] mountInfoLines)
        {
            var devPath = FindMountSource(destPath, mountInfoLines);
            if (string.IsNullOrEmpty(devPath) || !devPath.StartsWith("/dev/", StringComparison.Ordinal)) return null;
            try
            {
                var target = File.ResolveLinkTarget(devPath, returnFinalTarget: true);
                if (target != null) devPath = target.FullName;
            }
            catch { }
            var name = Path.GetFileName(devPath);
            return string.IsNullOrEmpty(name) ? null : StripPartitionSuffix(name);
        }

        internal static string? FindMountSource(string path, string[] mountInfoLines)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return null; }

            string? bestMount = null;
            string? bestSource = null;
            foreach (var line in mountInfoLines)
            {
                var sep = line.IndexOf(" - ", StringComparison.Ordinal);
                if (sep < 0) continue;
                var left = line.Substring(0, sep).Split(' ');
                var right = line.Substring(sep + 3).Split(' ');
                if (left.Length < 5 || right.Length < 2) continue;
                var mountPoint = left[4].Replace("\\040", " ", StringComparison.Ordinal);
                var matches = mountPoint == "/"
                    || full.Equals(mountPoint, StringComparison.Ordinal)
                    || full.StartsWith(mountPoint + "/", StringComparison.Ordinal);
                if (matches && (bestMount == null || mountPoint.Length > bestMount.Length))
                {
                    bestMount = mountPoint;
                    bestSource = right[1];
                }
            }
            return bestSource;
        }

        internal static string StripPartitionSuffix(string devName)
        {
            if (devName.StartsWith("nvme", StringComparison.Ordinal)
                || devName.StartsWith("mmcblk", StringComparison.Ordinal)
                || devName.StartsWith("loop", StringComparison.Ordinal))
            {
                var pIdx = devName.LastIndexOf('p');
                if (pIdx > 0 && pIdx < devName.Length - 1 && AllDigits(devName, pIdx + 1))
                    return devName.Substring(0, pIdx);
                return devName;
            }
            int end = devName.Length;
            while (end > 0 && char.IsDigit(devName[end - 1])) end--;
            return end > 0 ? devName.Substring(0, end) : devName;
        }

        private static bool AllDigits(string s, int start)
        {
            for (int i = start; i < s.Length; i++)
                if (!char.IsDigit(s[i])) return false;
            return true;
        }

        internal static bool IsLinuxWriteBackEnabled(string blockDev, string sysBlockRoot = "/sys/block")
        {
            try
            {
                var path = Path.Combine(sysBlockRoot, blockDev, "queue", "write_cache");
                if (!File.Exists(path)) return false;
                return string.Equals(File.ReadAllText(path).Trim(), "write back", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
