using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Corpus
{
    // Block-target seam: restore streams decoded chunk bytes through this,
    // mirroring IBlockSource on the imaging side. FileBlockTarget is the
    // Linux-provable stand-in (a .img file plays the disk); RawDiskTarget
    // opens a Windows block device for real restores.
    public interface IBlockTarget : IDisposable
    {
        string DisplayName { get; }
        long Capacity { get; }
        Stream OpenWrite();
    }

    public sealed class FileBlockTarget : IBlockTarget
    {
        private readonly string _path;
        private bool _disposed;

        public FileBlockTarget(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            _path = path;
        }

        public string Path => _path;

        public string DisplayName => _path;

        // Missing file = growable (restore creates it); an existing file
        // offers exactly its current length, so a short file fails the
        // capacity preflight before a single byte is written.
        public long Capacity => File.Exists(_path) ? new FileInfo(_path).Length : long.MaxValue;

        public Stream OpenWrite()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    public sealed class RawDiskTarget : IBlockTarget
    {
        private readonly string _devicePath;
        private bool _disposed;

        public RawDiskTarget(string devicePath)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("RawDiskTarget needs Windows block-device paths.");
            ArgumentNullException.ThrowIfNull(devicePath);
            _devicePath = devicePath;
        }

        public string DisplayName => _devicePath;

        public long Capacity => NativeDisk.GetLengthBytes(_devicePath);

        public Stream OpenWrite()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new FileStream(NativeDisk.OpenHandle(_devicePath, read: false), FileAccess.Write);
        }

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new FileStream(NativeDisk.OpenHandle(_devicePath, read: true), FileAccess.Read);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    // Shared kernel32 block-device plumbing: CreateFile open +
    // IOCTL_DISK_GET_LENGTH_INFO. Windows-only; callers guard first.
    internal static class NativeDisk
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint IoctlDiskGetLengthInfo = 0x0007405C;
        private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControlLength(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, out long lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControlExtents(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, [Out] byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        public static SafeFileHandle OpenHandle(string devicePath, bool read)
        {
            var access = read ? GenericRead : (GenericRead | GenericWrite);
            var raw = CreateFile(devicePath, access, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (raw == new IntPtr(-1))
                throw new IOException($"cannot open {devicePath} (win32 {Marshal.GetLastWin32Error()})");
            return new SafeFileHandle(raw, true);
        }

        // GET_LENGTH_INFO returns GET_LENGTH_INFORMATION { Length }. Device
        // reads past it fault; restore preflight refuses before that.
        public static long GetLengthBytes(string devicePath)
        {
            using var handle = OpenHandle(devicePath, read: true);
            if (!DeviceIoControlLength(handle.DangerousGetHandle(), IoctlDiskGetLengthInfo, IntPtr.Zero, 0, out var length, 8, out _, IntPtr.Zero))
                throw new IOException($"length query failed for {devicePath} (win32 {Marshal.GetLastWin32Error()})");
            return length;
        }

        // VOLUME_DISK_EXTENTS { count DWORD, DiskNumber DWORD at byte 8 }.
        // False when the volume spans disks (caller treats as unresolvable).
        public static bool TryVolumeDiskNumber(string volumePath, out int diskNumber)
        {
            diskNumber = 0;
            try
            {
                using var handle = OpenHandle(volumePath, read: true);
                var outBuf = new byte[32];
                if (!DeviceIoControlExtents(handle.DangerousGetHandle(), IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, outBuf, (uint)outBuf.Length, out _, IntPtr.Zero))
                    return false;
                if (BitConverter.ToInt32(outBuf, 0) != 1)
                    return false;
                diskNumber = BitConverter.ToInt32(outBuf, 8);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
