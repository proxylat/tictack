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
            if (!OperatingSystem.IsWindows() || cfg.Sources == null) return;
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
    }
}
