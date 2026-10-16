using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using TicTack;

namespace Corpus
{
    // Pre-stream volume quiesce (disk-spec §6): lock every volume on the
    // target disk so mid-stream filesystem recognition can't mount and deny
    // raw-sector writes (proven on-box: the ESP's first bytes mounted FAT32
    // mid-stream, the next write died access-denied). Lock-retry, then
    // dismount-then-lock; unlock+close on Dispose. No persistent state:
    // locks evaporate with the handles (crash-safe by construction).
    // Null disk or off-Windows yields an empty set (file targets and
    // Linux CI acquire-and-release with zero syscalls).
    internal sealed class VolumeLockSet : IDisposable
    {
        internal const uint FsctlLockVolume = 0x00090018;
        internal const uint FsctlUnlockVolume = 0x0009001C;
        internal const uint FsctlDismountVolume = 0x00090020;

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint OpenExisting = 3;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CtlCode(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        private readonly List<SafeFileHandle> _held = new();
        private readonly ILogger _log;
        private bool _disposed;

        private VolumeLockSet(ILogger log) => _log = log;

        internal int Count => _held.Count;

        public static VolumeLockSet Acquire(int? targetDisk, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(log);
            var set = new VolumeLockSet(log);
            if (targetDisk == null || !OperatingSystem.IsWindows())
                return set;
            try
            {
                foreach (var volume in DiskCapture.VolumesOnDisk(targetDisk.Value))
                    set.LockOne(volume);
                log.Info($"locked {set._held.Count} volume(s) on disk {targetDisk.Value} for streaming");
                return set;
            }
            catch
            {
                set.Dispose();
                throw;
            }
        }

        private void LockOne(string volume)
        {
            var raw = CreateFile(volume, GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (raw == new IntPtr(-1))
                throw new IOException($"cannot open {volume} for locking (win32 {Marshal.GetLastWin32Error()})");
            var handle = new SafeFileHandle(raw, true);
            try
            {
                // Transient holders (Defender, indexer) release in seconds;
                // a stubborn holder eats one dismount. Fresh target volumes
                // hold no legitimate data yet — scanners only — so forcing
                // is safe; a live OS volume (pagefile) fails loud instead.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (CtlCode(handle.DangerousGetHandle(), FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    {
                        _held.Add(handle);
                        return;
                    }
                    Thread.Sleep(1000);
                }
                if (!CtlCode(handle.DangerousGetHandle(), FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new IOException($"cannot quiesce {volume}: lock timed out and dismount failed (win32 {Marshal.GetLastWin32Error()})");
                if (!CtlCode(handle.DangerousGetHandle(), FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new IOException($"cannot lock {volume} after dismount (win32 {Marshal.GetLastWin32Error()})");
                _held.Add(handle);
                _log.Info($"dismounted+locked {volume} (a scanner held it)");
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var h in _held)
            {
                try
                {
                    if (!h.IsInvalid && !h.IsClosed)
                        CtlCode(h.DangerousGetHandle(), FsctlUnlockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    _log.Debug("volume unlock failed (handle still closes): " + ex.Message);
                }
                try
                {
                    h.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Debug("volume handle close failed: " + ex.Message);
                }
            }
            _held.Clear();
        }
    }
}
