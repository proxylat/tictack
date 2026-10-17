using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Corpus
{
    // Live-read source for the ESP (disk-spec §1.2): VSS AddToSnapshotSet
    // answers VSS_E_VOLUME_NOT_SUPPORTED on FAT32, so there is no shadow to
    // read — this streams the live volume device instead, during the NTFS
    // snapshot freeze window. Volume-device resolution matches
    // IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS (disk number + offset + length)
    // over enumerated volumes — no WMI. Windows-only; ESP has no USN
    // journal so its manifests always re-read full (cheap at ~100MB).
    public sealed class EspLiveSource : IBlockSource
    {
        private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

        private readonly string _devicePath;
        private readonly bool _useUnbuffered;
        private bool _disposed;

        public EspLiveSource(int diskNumber, long offsetBytes, long lengthBytes, bool useUnbuffered = true)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("ESP live read needs Windows block-device paths.");
            if (offsetBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(offsetBytes));
            if (lengthBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(lengthBytes));
            _devicePath = Resolve(diskNumber, offsetBytes, lengthBytes)
                ?? throw new InvalidDataException($"no live volume matches disk {diskNumber} offset {offsetBytes} length {lengthBytes} (single-extent volumes only; spanned/striped never match)");
            _useUnbuffered = useUnbuffered;
            OffsetBytes = offsetBytes;
            Length = lengthBytes;
        }

        public string DevicePath => _devicePath;

        public long OffsetBytes { get; }

        public long Length { get; }

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ExtentStreams.OpenExtentRead(_devicePath, _useUnbuffered);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        // First single-extent volume whose disk number, start offset, and
        // length all match the partition extent. Spanned/striped volumes
        // (extent count != 1) never match — fail loud, never a half read.
        private static string? Resolve(int diskNumber, long offsetBytes, long lengthBytes)
        {
            var name = new char[512];
            var find = FindFirstVolume(name, (uint)name.Length);
            if (find == new IntPtr(-1))
                return null;
            try
            {
                do
                {
                    var volume = new string(name).TrimEnd('\0').TrimEnd('\\');
                    if (TryMatchExtent(volume, diskNumber, offsetBytes, lengthBytes))
                        return volume;
                    Array.Clear(name, 0, name.Length);
                }
                while (FindNextVolume(find, name, (uint)name.Length));
                return null;
            }
            finally
            {
                FindVolumeClose(find);
            }
        }

        private static bool TryMatchExtent(string volume, int diskNumber, long offsetBytes, long lengthBytes)
        {
            try
            {
                using var handle = NativeDisk.OpenHandle(volume, read: true);
                var outBuf = new byte[4096];
                if (!DeviceIoControl(handle.DangerousGetHandle(), IoctlVolumeGetVolumeDiskExtents,
                        IntPtr.Zero, 0, outBuf, (uint)outBuf.Length, out _, IntPtr.Zero))
                    return false;
                if (BitConverter.ToInt32(outBuf, 0) != 1)
                    return false;
                return BitConverter.ToInt32(outBuf, 8) == diskNumber
                    && BitConverter.ToInt64(outBuf, 16) == offsetBytes
                    && BitConverter.ToInt64(outBuf, 24) == lengthBytes;
            }
            catch (IOException)
            {
                return false;
            }
        }

        // Capture-side BitLocker probe support: first drive letter for a
        // (disk, offset), for the pre-image locked-volume refuse. Null when
        // unresolvable (off-Windows, unmounted, mount-point-only) — callers
        // skip the probe, never fail on it.
        internal static string? TryFindDriveLetter(int diskNumber, long offsetBytes)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            var name = new char[512];
            var find = FindFirstVolume(name, (uint)name.Length);
            if (find == new IntPtr(-1))
                return null;
            try
            {
                do
                {
                    var volume = new string(name).TrimEnd('\0').TrimEnd('\\');
                    if (TryMatchOffset(volume, diskNumber, offsetBytes))
                    {
                        var paths = new char[512];
                        if (!GetVolumePathNamesForVolumeName(volume + "\\", paths, (uint)paths.Length, out _))
                            return null;
                        foreach (var p in new string(paths).Split('\0'))
                        {
                            var t = p.TrimEnd('\\');
                            if (t.Length > 0)
                                return t; // drive letter (X:) or mount point — manage-bde takes both
                        }
                        return null;
                    }
                    Array.Clear(name, 0, name.Length);
                }
                while (FindNextVolume(find, name, (uint)name.Length));
                return null;
            }
            finally
            {
                FindVolumeClose(find);
            }
        }

        private static bool TryMatchOffset(string volume, int diskNumber, long offsetBytes)
        {
            try
            {
                using var handle = NativeDisk.OpenHandle(volume, read: true);
                var outBuf = new byte[4096];
                if (!DeviceIoControl(handle.DangerousGetHandle(), IoctlVolumeGetVolumeDiskExtents,
                        IntPtr.Zero, 0, outBuf, (uint)outBuf.Length, out _, IntPtr.Zero))
                    return false;
                if (BitConverter.ToInt32(outBuf, 0) != 1)
                    return false; // spanned/striped: never attribute to one partition
                return BitConverter.ToInt32(outBuf, 8) == diskNumber
                    && BitConverter.ToInt64(outBuf, 16) == offsetBytes;
            }
            catch (IOException)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumePathNamesForVolumeName(string lpszVolumeName, [Out] char[] lpszVolumePathNames, uint cchBufferLength, out uint lpcchReturnLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstVolume([Out] char[] lpszVolumeName, uint cchBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextVolume(IntPtr hFindVolume, [Out] char[] lpszVolumeName, uint cchBufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindVolumeClose(IntPtr hFindVolume);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, [Out] byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
    }
}
