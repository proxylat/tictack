using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using TicTack;

namespace Corpus
{
    // Live-disk capture backend (the DiskImager.CapturePlan hook): turns a
    // live Windows disk into a DiskCapturePlan. PartitionTable.ReadDisk
    // enumerates (the same path --check-repair uses), one VSS snapshot set
    // freezes every data volume together (crash-consistent across the disk —
    // never one snapshot per partition), FAT/ESP partitions live-read through
    // EspLiveSource (VSS answers VSS_E_VOLUME_NOT_SUPPORTED on FAT32), and
    // MSR slots stay in the layout skipped-with-range, never snapshotted.
    // Every captured volume arms a BitLockerCaptureScope (suspend-then-freeze,
    // resume-after-teardown); locked volumes refuse via the scope's existing
    // semantics — RefuseIfLocked is NOT duplicated (Store calls it).
    // Windows-only: the public entry throws loud off-Windows; the internal
    // core takes factories so Linux tests drive the same planning with fakes.
    // NOTE (hook owner): Store.ImageDisk never disposes per-partition
    // sources — until it does, shadows/scopes live till process exit. Dispose
    // every plan Source (or add plan-level teardown) to complete VSS honestly.
    public static class DiskCapture
    {
        private const string EspTypeGuid = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";
        private const string RecoveryTypeGuid = "de94bba4-06d1-4d40-a16a-bfd50179d6ac";

        public static DiskCapturePlan BuildPlan(int diskNumber, ILogger log)
            => BuildPlan(diskNumber, log, useUnbuffered: true);

        // §12.12 seam: --buffered-io passes false (default ON).
        public static DiskCapturePlan BuildPlan(int diskNumber, ILogger log, bool useUnbuffered)
        {
            ArgumentNullException.ThrowIfNull(log);
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException($"disk {diskNumber}: live disk capture needs Windows (elevated GPT+VSS path; Linux disk verbs exit 1)");
            var layout = PartitionTable.ReadDisk(diskNumber, log);
            return BuildPlanCore(
                diskNumber,
                layout,
                TryFindVolume,
                ManageBdeBitLocker.Instance,
                (volumes, statics, l) => VssSnapshotSet.Create(volumes, l, statics),
                (lifetime, volume, device, _, scope) => new DiskSnapshotSource(lifetime, volume, device, scope, useUnbuffered),
                (lifetime, record, scope) => new LiveCaptureSource(lifetime, new EspLiveSource(diskNumber, record.OffsetBytes, record.LengthBytes, useUnbuffered), scope),
                log);
        }

        // Testable core: identical planning, injected seams. Resolution runs
        // BEFORE any arming (a letterless data partition fails before a single
        // protector suspends); teardown is idempotent (scopes + set dispose
        // twice-safe) so arm/create/source failures all resume + release.
        internal static DiskCapturePlan BuildPlanCore(
            int diskNumber,
            DiskLayout layout,
            Func<int, long, long, string?> findVolume,
            IBitLockerController bitlocker,
            Func<IReadOnlyList<string>, IReadOnlySet<string>, ILogger, IDiskSnapshotSet> createSet,
            Func<DiskCaptureLifetime, string, string, PartitionRecord, BitLockerCaptureScope, IBlockSource> openData,
            Func<DiskCaptureLifetime, PartitionRecord, BitLockerCaptureScope?, IBlockSource> openLive,
            ILogger log)
        {
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(findVolume);
            ArgumentNullException.ThrowIfNull(bitlocker);
            ArgumentNullException.ThrowIfNull(createSet);
            ArgumentNullException.ThrowIfNull(openData);
            ArgumentNullException.ThrowIfNull(openLive);
            ArgumentNullException.ThrowIfNull(log);
            if (layout.Partitions.Count == 0)
                throw new InvalidDataException($"disk {diskNumber}: no partitions to capture");
            var dataVol = new Dictionary<int, string>();
            var staticVol = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var liveVol = new Dictionary<int, string?>();
            var skipped = 0;
            foreach (var rec in layout.Partitions)
            {
                if (rec.Skipped)
                {
                    skipped++;
                    continue;
                }
                var volume = findVolume(diskNumber, rec.OffsetBytes, rec.LengthBytes);
                if (IsLiveRead(rec))
                {
                    liveVol[rec.PartitionNumber] = volume;
                }
                else
                {
                    if (volume == null)
                        throw new InvalidDataException($"disk {diskNumber} partition {rec.PartitionNumber} ({rec.Name}) has no drive letter, mount point, or volume path — assign one (diskpart/mountvol) so VSS can snapshot it, then retry");
                    dataVol[rec.PartitionNumber] = volume;
                    // Recovery partitions are known-static (nothing writes
                    // outside Update/reagentc): they clear the diff preflight
                    // on the small tier instead of the size tier, so a
                    // space-tight Recovery stops vetoing whole-disk capture.
                    if (IsRecovery(rec))
                        staticVol.Add(volume);
                }
            }
            // Two partitions resolving to one volume means misattribution
            // (same bytes imaged under two keys) — fail loud, never double-image.
            var allVolumes = new List<string>(dataVol.Values);
            foreach (var v in liveVol.Values)
            {
                if (v != null)
                    allVolumes.Add(v);
            }
            if (new HashSet<string>(allVolumes, StringComparer.OrdinalIgnoreCase).Count != allVolumes.Count)
                throw new InvalidDataException($"disk {diskNumber}: two partitions resolve to the same volume — refusing to double-image");
            log.Info($"disk {diskNumber}: snapshotting {dataVol.Count} volumes in one set ({liveVol.Count} live-read, {skipped} skipped)");
            var scopes = new List<BitLockerCaptureScope>();
            var byVolume = new Dictionary<string, BitLockerCaptureScope>(StringComparer.OrdinalIgnoreCase);
            IDiskSnapshotSet? set = null;
            try
            {
                foreach (var v in allVolumes)
                {
                    var scope = BitLockerCaptureScope.Arm(v, bitlocker, log);
                    scopes.Add(scope);
                    byVolume[v] = scope;
                }
                set = dataVol.Count > 0 ? createSet(new List<string>(dataVol.Values), staticVol, log) : null;
                var devices = new Dictionary<int, string>();
                if (set != null)
                {
                    foreach (var kv in dataVol)
                        devices[kv.Key] = set.DeviceFor(kv.Value);
                }
                var lifetime = new DiskCaptureLifetime(set, scopes, new List<string>(dataVol.Values));
                var created = new List<IBlockSource>();
                try
                {
                    var inputs = new List<PartitionImageInput>(layout.Partitions.Count);
                    foreach (var rec in layout.Partitions)
                    {
                        if (rec.Skipped)
                        {
                            inputs.Add(new PartitionImageInput(
                                rec.PartitionNumber, rec.Name, rec.TypeGuid, rec.UniqueGuid,
                                rec.FirstLba, rec.LastLba, rec.OffsetBytes, rec.LengthBytes,
                                rec.Filesystem, true, rec.SkipReason ?? "skipped", null, null));
                            continue;
                        }
                        if (liveVol.TryGetValue(rec.PartitionNumber, out var live))
                        {
                            var scope = live == null ? null : byVolume[live];
                            var src = openLive(lifetime, rec, scope);
                            created.Add(src);
                            inputs.Add(new PartitionImageInput(
                                rec.PartitionNumber, rec.Name, rec.TypeGuid, rec.UniqueGuid,
                                rec.FirstLba, rec.LastLba, rec.OffsetBytes, rec.LengthBytes,
                                rec.Filesystem, false, null, src, null));
                        }
                        else
                        {
                            var volume = dataVol[rec.PartitionNumber];
                            var src = openData(lifetime, volume, devices[rec.PartitionNumber], rec, byVolume[volume]);
                            created.Add(src);
                            inputs.Add(new PartitionImageInput(
                                rec.PartitionNumber, rec.Name, rec.TypeGuid, rec.UniqueGuid,
                                rec.FirstLba, rec.LastLba, rec.OffsetBytes, rec.LengthBytes,
                                rec.Filesystem, false, null, src,
                                IsNtfs(rec) ? new MftDigestChangeSource() : null));
                        }
                    }
                    return new DiskCapturePlan(layout.DiskGuid, layout.DiskSizeBytes, layout.SectorSizeBytes, inputs);
                }
                catch
                {
                    foreach (var c in created)
                        c.Dispose();
                    lifetime.Dispose();
                    throw;
                }
            }
            catch
            {
                set?.Dispose();
                foreach (var s in scopes)
                    s.Dispose();
                throw;
            }
        }

        // VSS rejects FAT outright (VSS_E_VOLUME_NOT_SUPPORTED), so the ESP
        // and any FAT data partition live-read; the EspLiveSource mechanism
        // is extent-based, not ESP-specific. Everything else (NTFS, ReFS,
        // exFAT, unknown-sniff) enters the VSS set — VSS itself refuses
        // unsupported filesystems loud, and a live-read fallback would
        // silently downgrade crash-consistency.
        private static bool IsLiveRead(PartitionRecord rec) =>
            string.Equals(rec.TypeGuid, EspTypeGuid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(rec.Filesystem, "fat32", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rec.Filesystem, "fat12", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rec.Filesystem, "fat16", StringComparison.OrdinalIgnoreCase);

        private static bool IsNtfs(PartitionRecord rec) =>
            string.Equals(rec.Filesystem, "ntfs", StringComparison.OrdinalIgnoreCase);

        private static bool IsRecovery(PartitionRecord rec) =>
            string.Equals(rec.TypeGuid, RecoveryTypeGuid, StringComparison.OrdinalIgnoreCase);

        // Volume resolution for one partition extent: the first single-extent
        // volume whose disk number, start offset, and length all match
        // (spanned/striped volumes never match — same rule as EspLiveSource).
        // Prefers a drive letter, then a mount point, then the volume GUID
        // path (VSS + GetDiskFreeSpaceExW + manage-bde accept all three;
        // DriveInfo would not, which is why preflight uses ExW). Null when
        // unresolvable — data partitions fail loud on null, live-read
        // partitions capture scopeless (their EspLiveSource extent match
        // fails loud instead if the volume is truly gone).
        internal static string? TryFindVolume(int diskNumber, long offsetBytes, long lengthBytes)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            var name = new char[512];
            var find = FindFirstVolume(name, (uint)name.Length);
            if (find == new IntPtr(-1))
                return null;
            string? guidFallback = null;
            try
            {
                do
                {
                    var volume = new string(name).TrimEnd('\0').TrimEnd('\\');
                    if (TryMatchExtent(volume, diskNumber, offsetBytes, lengthBytes))
                    {
                        ClassifyPaths(volume, out var letter, out var mount);
                        if (letter != null)
                            return letter;
                        if (mount != null)
                            return mount;
                        guidFallback ??= volume;
                    }
                    Array.Clear(name, 0, name.Length);
                }
                while (FindNextVolume(find, name, (uint)name.Length));
                return guidFallback;
            }
            finally
            {
                FindVolumeClose(find);
            }
        }

        // Every single-extent volume on one disk (GUID paths, no trailing
        // backslash): the restore locker quiesces these before streaming so
        // mid-stream filesystem recognition can't mount and deny raw
        // writes. Empty off Windows (callers need no guard).
        internal static List<string> VolumesOnDisk(int diskNumber)
        {
            var found = new List<string>();
            if (!OperatingSystem.IsWindows())
                return found;
            var name = new char[512];
            var find = FindFirstVolume(name, (uint)name.Length);
            if (find == new IntPtr(-1))
                return found;
            try
            {
                do
                {
                    var volume = new string(name).TrimEnd('\0').TrimEnd('\\');
                    if (VolumeOnDisk(volume, diskNumber))
                        found.Add(volume);
                    Array.Clear(name, 0, name.Length);
                }
                while (FindNextVolume(find, name, (uint)name.Length));
                return found;
            }
            finally
            {
                FindVolumeClose(find);
            }
        }

        private static bool VolumeOnDisk(string volume, int diskNumber)
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
                return BitConverter.ToInt32(outBuf, 8) == diskNumber;
            }
            catch (IOException)
            {
                return false;
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

        private static void ClassifyPaths(string volume, out string? letter, out string? mount)
        {
            letter = null;
            mount = null;
            var paths = new char[512];
            if (!GetVolumePathNamesForVolumeName(volume + "\\", paths, (uint)paths.Length, out _))
                return;
            foreach (var p in new string(paths).Split('\0'))
            {
                var t = p.TrimEnd('\\');
                if (t.Length == 0)
                    continue;
                if (t.Length == 2 && t[1] == ':' && char.IsAsciiLetter(t[0]))
                    letter ??= t;
                else
                    mount ??= t;
            }
        }

        private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstVolume([Out] char[] lpszVolumeName, uint cchBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextVolume(IntPtr hFindVolume, [Out] char[] lpszVolumeName, uint cchBufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindVolumeClose(IntPtr hFindVolume);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumePathNamesForVolumeName(string lpszVolumeName, [Out] char[] lpszVolumePathNames, uint cchBufferLength, out uint lpcchReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, [Out] byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
    }

    // One crash-consistent VSS snapshot set over a disk's data volumes:
    // Create freezes every volume in a single set (never one set per
    // partition), DeviceFor maps each volume to its frozen device, Complete
    // earns BackupComplete on Dispose (else Abort). Production =
    // VssSnapshotSet (Windows COM); tests fake it and count the sets.
    internal interface IDiskSnapshotSet : IDisposable
    {
        string DeviceFor(string volume);
        void Complete();
    }

    // Shared ownership for one disk capture: the single VSS set plus every
    // armed BitLocker scope. Each per-partition source AddRefs on construction
    // and Releases on Dispose; the last Release tears down (set first, then
    // scopes resume — suspension covers teardown too, mirroring
    // VssBlockSource.Dispose). Sources report full-length reads so the set
    // earns BackupComplete only when every data volume read to the end.
    internal sealed class DiskCaptureLifetime : IDisposable
    {
        private readonly object _gate = new();
        private readonly IDiskSnapshotSet? _set;
        private readonly List<BitLockerCaptureScope> _scopes;
        private readonly HashSet<string> _pending;
        private int _refs;
        private bool _tornDown;

        internal DiskCaptureLifetime(IDiskSnapshotSet? set, List<BitLockerCaptureScope> scopes, IReadOnlyList<string> dataVolumes)
        {
            ArgumentNullException.ThrowIfNull(scopes);
            ArgumentNullException.ThrowIfNull(dataVolumes);
            _set = set;
            _scopes = scopes;
            _pending = new HashSet<string>(dataVolumes, StringComparer.OrdinalIgnoreCase);
        }

        internal void AddRef()
        {
            lock (_gate)
            {
                _refs++;
            }
        }

        internal void Release()
        {
            bool last;
            lock (_gate)
            {
                _refs--;
                last = _refs == 0;
            }
            if (last)
                Dispose();
        }

        internal void ReportFullRead(string volume)
        {
            ArgumentException.ThrowIfNullOrEmpty(volume);
            IDiskSnapshotSet? complete = null;
            lock (_gate)
            {
                _pending.Remove(volume);
                if (_pending.Count == 0)
                    complete = _set;
            }
            complete?.Complete();
        }

        public void Dispose()
        {
            IDiskSnapshotSet? set;
            List<BitLockerCaptureScope> scopes;
            lock (_gate)
            {
                if (_tornDown)
                    return;
                _tornDown = true;
                set = _set;
                scopes = _scopes;
            }
            set?.Dispose();
            foreach (var s in scopes)
                s.Dispose();
        }
    }

    // Multi-volume VSS requester: the VssSnapshot.Create sequence (COM
    // security, component-mode writer fan-out, prepare-after-set, per-snapshot
    // Created + writers-involved checks, BackupComplete-only-on-success)
    // generalized to N volumes in ONE set. Elevation is gated upstream by
    // PartitionTable.ReadDisk, so only the platform is re-checked here.
    internal sealed class VssSnapshotSet : IDiskSnapshotSet
    {
        private readonly IVssBackupComponents _com;
        private readonly Dictionary<string, string> _devices;
        private readonly List<(Guid Instance, Guid Writer, int Type, string? Path, string Name)> _selected;
        private readonly ILogger _log;
        private bool _completed;
        private bool _disposed;

        private VssSnapshotSet(IVssBackupComponents com, Dictionary<string, string> devices, List<(Guid, Guid, int, string?, string)> selected, ILogger log)
        {
            _com = com;
            _devices = devices;
            _selected = selected;
            _log = log;
        }

        // AOT: same VSS COM entry point as VssSnapshot.Create (see Vss.cs).
        [UnconditionalSuppressMessage("Trimming", "IL2050", Justification = "Built-in COM interop on NativeAOT Windows; VSS API surface rooted, proven by smoke test.")]
        public static VssSnapshotSet Create(IReadOnlyList<string> volumes, ILogger log, IReadOnlySet<string>? knownStaticVolumes = null)
        {
            ArgumentNullException.ThrowIfNull(volumes);
            ArgumentNullException.ThrowIfNull(log);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("VSS disk capture needs Windows");
            if (volumes.Count == 0)
                throw new ArgumentException("need at least one volume to snapshot", nameof(volumes));
            var roots = new List<string>(volumes.Count);
            foreach (var v in volumes)
            {
                if (string.IsNullOrEmpty(v))
                    throw new ArgumentException("volume paths must be non-empty", nameof(volumes));
                roots.Add(v.TrimEnd('\\') + "\\");
            }
            // Normalize the static set into root form (trailing backslash,
            // case-insensitive) so membership matches the preflight roots.
            HashSet<string>? statics = null;
            if (knownStaticVolumes != null)
            {
                statics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in knownStaticVolumes)
                {
                    if (!string.IsNullOrEmpty(v))
                        statics.Add(v.TrimEnd('\\') + "\\");
                }
            }
            // Diff-area preflight per volume (same floor VssSnapshot.Create
            // enforces; GetDiskFreeSpaceExW reports the same bytes DriveInfo
            // would and also accepts volume GUID paths).
            foreach (var root in roots)
            {
                if (!GetDiskFreeSpaceExW(root, out var free, out var total, out _))
                    throw new IOException($"GetDiskFreeSpaceExW({root}) failed: {Marshal.GetLastWin32Error()}");
                var isStatic = statics != null && statics.Contains(root);
                var headroom = DiffFloor.RequiredHeadroomBytes((long)total, isStatic);
                if (isStatic)
                    log.Info($"volume {root} is known-static: diff preflight on the small tier ({headroom} bytes)");
                if ((long)free < headroom)
                    throw new InvalidOperationException($"volume {root} has {free} bytes free — VSS needs {headroom} bytes headroom for the diff area.");
            }

            // COM security FIRST, before any COM activity (same rationale as
            // VssSnapshot.Create — arriving late surfaces as VSS_E_BAD_STATE).
            var secHr = VssApi.CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, 6u, 3u, IntPtr.Zero, 0u, IntPtr.Zero);
            if (secHr < 0)
                throw new IOException($"CoInitializeSecurity failed (0x{secHr:X8}) — VSS writer calls cannot authenticate", Marshal.GetExceptionForHR(secHr));

            Hr(VssApi.CreateVssBackupComponents(out var com), "CreateVssBackupComponents");
            try
            {
                Hr(com.InitializeForBackup(null), "InitializeForBackup");
                // No SetContext: default IS VSS_CTX_BACKUP (same as VssSnapshot).
                Hr(com.SetBackupState(true, true, VssBackupType.Full, false), "SetBackupState");
                Hr(com.GatherWriterMetadata(out var gm), "GatherWriterMetadata");
                RunAsync(gm, "GatherWriterMetadata");
                var selected = new List<(Guid Instance, Guid Writer, int Type, string? Path, string Name)>();
                Hr(com.GetWriterMetadataCount(out var writers), "GetWriterMetadataCount");
                log.Info($"VSS: {writers} writers");
                for (uint i = 0; i < writers; i++)
                {
                    Hr(com.GetWriterMetadata(i, out var instance, out var meta), "GetWriterMetadata");
                    try
                    {
                        Hr(meta.GetIdentity(out _, out var writerId, out var name, out _, out _), "GetIdentity");
                        Hr(meta.SaveAsXML(out var xml), "SaveAsXML");
                        var added = 0;
                        foreach (var (type, path, cname) in ParseComponents(xml))
                        {
                            var hr = com.AddComponent(instance, writerId, type, path, cname);
                            log.Info($"VSS add {name}/{cname} type={type} path={path ?? "<root>"} -> 0x{hr:X8}");
                            Hr(hr, "AddComponent");
                            selected.Add((instance, writerId, type, path, cname));
                            added++;
                        }
                        log.Info($"VSS writer: {name} ({added} components)");
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(meta);
                    }
                }
                Hr(com.StartSnapshotSet(out var setId), "StartSnapshotSet");
                var snapIds = new List<(string Root, Guid SnapId)>();
                foreach (var root in roots)
                {
                    Hr(com.AddToSnapshotSet(root, Guid.Empty, out var snapId), "AddToSnapshotSet");
                    snapIds.Add((root, snapId));
                }
                log.Info($"VSS set={setId} ({snapIds.Count} volumes)");
                // Prepare AFTER the set exists with its volumes (same ordering
                // constraint as VssSnapshot — Prepare validates set existence).
                Hr(com.PrepareForBackup(out var pf), "PrepareForBackup");
                RunAsync(pf, "PrepareForBackup");
                Hr(com.DoSnapshotSet(out var ds), "DoSnapshotSet");
                RunAsync(ds, "DoSnapshotSet");
                var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < snapIds.Count; i++)
                {
                    var (root, snapId) = snapIds[i];
                    Hr(com.GetSnapshotProperties(snapId, out var prop), "GetSnapshotProperties");
                    string device;
                    try
                    {
                        device = Marshal.PtrToStringUni(prop.SnapshotDeviceObject)
                            ?? throw new IOException("snapshot device object came back null");
                    }
                    finally
                    {
                        VssApi.VssFreeSnapshotProperties(ref prop);
                    }
                    if (prop.Status != (int)VssSnapshotState.Created)
                        throw new IOException($"snapshot {snapId} landed in state {prop.Status}, want Created(12)");
                    // NO_WRITERS bit (0x10) must be CLEAR — same loud check.
                    if ((prop.SnapshotAttributes & 0x10) != 0)
                        throw new IOException($"snapshot {snapId} came back writerless (attributes 0x{prop.SnapshotAttributes:X8}) — writers did not participate");
                    devices[volumes[i]] = device;
                    log.Info($"VSS snapshot of {root} at {device} (attributes 0x{prop.SnapshotAttributes:X8}, writers involved)");
                }
                return new VssSnapshotSet(com, devices, selected, log);
            }
            catch (Exception ex)
            {
                VssHealth.ReportSnapshotFailure(log, ex);
                try { com.AbortBackup(); } catch (Exception abex) { log.Warn("VSS AbortBackup after failure: " + abex.Message); }
                Marshal.ReleaseComObject(com);
                throw;
            }
        }

        public string DeviceFor(string volume)
        {
            ArgumentException.ThrowIfNullOrEmpty(volume);
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _devices[volume];
        }

        public void Complete()
        {
            _completed = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_completed && _selected.Count > 0)
                {
                    foreach (var (instance, writer, type, path, name) in _selected)
                    {
                        try { _com.SetBackupSucceeded(instance, writer, type, path, name, true); }
                        catch (Exception ex) { _log.Warn($"VSS SetBackupSucceeded({name}): " + ex.Message); }
                    }
                    var hr = _com.BackupComplete(out var bc);
                    if (hr >= 0) RunAsync(bc, "BackupComplete");
                    else _log.Warn("VSS BackupComplete start failed: 0x" + hr.ToString("X8"));
                }
                else
                {
                    try { _com.AbortBackup(); } catch (Exception ex) { _log.Warn("VSS AbortBackup: " + ex.Message); }
                }
            }
            finally
            {
                try { _com.FreeWriterMetadata(); } catch (Exception ex) { _log.Warn("VSS FreeWriterMetadata: " + ex.Message); }
                // No explicit DeleteSnapshots: BACKUP-context shadows are
                // volatile + auto-release by definition (same finding as
                // VssSnapshot — an explicit delete only answers NOT_FOUND).
                Marshal.ReleaseComObject(_com);
            }
        }

        // Same contracts as VssSnapshot's privates (unsplittable without
        // touching Vss.cs): fail-loud HRESULTs, unbounded async waits (VSS
        // internal timeouts bound them), DATABASE/FILE_GROUP components.
        private static void RunAsync(IVssAsync async, string op)
        {
            try
            {
                Hr(async.Wait(0xFFFFFFFF), op + ".Wait");
                Hr(async.QueryStatus(out var hr, out _), op + ".QueryStatus");
                Hr(hr, op);
            }
            finally
            {
                Marshal.ReleaseComObject(async);
            }
        }

        private static void Hr(int hr, string op)
        {
            if (hr < 0)
            {
                var ex = new IOException($"{op} failed (0x{hr:X8})", Marshal.GetExceptionForHR(hr));
                ex.Data["VssHr"] = hr; // VssHealth.TryGetHr reads this on the failure path.
                throw ex;
            }
        }

        private static List<(int Type, string? Path, string Name)> ParseComponents(string xml)
        {
            var list = new List<(int, string?, string)>();
            foreach (var el in XDocument.Parse(xml).Descendants())
            {
                var local = el.Name.LocalName;
                int type;
                if (local.Equals("DATABASE", StringComparison.OrdinalIgnoreCase)) type = 1;
                else if (local.Equals("FILE_GROUP", StringComparison.OrdinalIgnoreCase)) type = 2;
                else continue;
                var name = (string?)el.Attribute("componentName");
                if (string.IsNullOrEmpty(name)) continue;
                var path = (string?)el.Attribute("logicalPath");
                list.Add((type, string.IsNullOrEmpty(path) ? null : path, name));
            }
            return list;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
    }

    // IBlockSource over one volume of a shared disk snapshot set: the same
    // read shape as VssBlockSource (segmented VssReadStream, FS-view length,
    // bitmap runs, lazy MFT, USN planning from the frozen device) but the VSS
    // set + BitLocker scope live in the shared DiskCaptureLifetime, not per
    // source — one set freezes the whole disk.
    internal sealed class DiskSnapshotSource : ISparseBlockSource, IVolumeHandleProvider, IMftVolume, IBitLockerVolume, ILiveMftVolumeProvider
    {
        private readonly DiskCaptureLifetime _lifetime;
        private readonly string _volume;
        private readonly string _device;
        private readonly BitLockerCaptureScope _bitlocker;
        private readonly Lazy<IMftVolume> _mft;
        private readonly bool _useUnbuffered;
        private bool _disposed;

        internal DiskSnapshotSource(DiskCaptureLifetime lifetime, string volume, string device, BitLockerCaptureScope bitlocker, bool useUnbuffered = true)
        {
            ArgumentNullException.ThrowIfNull(lifetime);
            ArgumentException.ThrowIfNullOrEmpty(volume);
            ArgumentException.ThrowIfNullOrEmpty(device);
            ArgumentNullException.ThrowIfNull(bitlocker);
            _lifetime = lifetime;
            _volume = volume;
            _device = device.TrimEnd('\\');
            _bitlocker = bitlocker;
            _useUnbuffered = useUnbuffered;
            Length = VssBlockSource.DeviceLength(volume, _device);
            BytesPerCluster = VolumeBitmap.BytesPerCluster(volume.TrimEnd('\\') + "\\");
            _mft = new Lazy<IMftVolume>(() => new MftVolumeReader(_device));
            lifetime.AddRef();
        }

        public long Length { get; }

        public long BytesPerCluster { get; }

        public IReadOnlyList<(long Start, long Count)> GetAllocatedRuns()
            => VolumeBitmap.ReadRuns(_device, Length / BytesPerCluster);

        string IVolumeHandleProvider.SnapshotDevicePath => _device;

        private IMftVolume Mft
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    return _mft.Value;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new InvalidDataException($"MFT unavailable on {_device} ({ex.Message})", ex);
                }
            }
        }

        public int BytesPerRecord => Mft.BytesPerRecord;

        int IMftVolume.BytesPerCluster => Mft.BytesPerCluster;

        public long RecordCount => Mft.RecordCount;

        public IReadOnlyList<(long StartLcn, long ClusterCount)> MftRuns => Mft.MftRuns;

        public IReadOnlyList<MftEnumEntry> Enumerate() => Mft.Enumerate();

        public byte[] GetRecordBytes(ulong fileRef) => Mft.GetRecordBytes(fileRef);

        byte[]? IMftVolume.GetMftAllocationBitmap() => Mft.GetMftAllocationBitmap();

        // §9 design B, disk path: same live-view RECORD reader as
        // VssBlockSource, over this partition's live volume (letter,
        // mount, or GUID path — the letter shape opens via \\.\, the rest
        // are already volume handles, mirroring ResolveImageExclusions).
        // Status via the single `mft-live:` line (ok / null-reason).
        IMftVolume? ILiveMftVolumeProvider.TryOpenLiveMft(ILogger log)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(log);
            if (!OperatingSystem.IsWindows())
            {
                log.Info("mft-live: null (non-Windows)");
                return null;
            }
            var device = _volume.Length == 2 && _volume[1] == ':' && char.IsAsciiLetter(_volume[0])
                ? @"\\.\" + _volume
                : _volume;
            try
            {
                var live = new MftVolumeReader(device);
                log.Info("mft-live: ok");
                return live;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException)
            {
                log.Info($"mft-live: null ({ex.GetType().Name})");
                return null;
            }
        }

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var raw = ExtentStreams.OpenExtentRead(_device, _useUnbuffered);
            return new VssReadStream(raw, Length, () => _lifetime.ReportFullRead(_volume));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_mft.IsValueCreated)
                _mft.Value.Dispose();
            _lifetime.Release();
        }

        string? IBitLockerVolume.BitLockerState => _bitlocker.CaptureState;
        List<string>? IBitLockerVolume.BitLockerKeyIds => _bitlocker.CaptureKeyIds;
    }

    // Live-read partition (ESP / FAT data: VSS cannot snapshot FAT) with the
    // same lifetime + BitLocker bookkeeping as snapshot sources: the scope
    // suspends during the read and resumes on teardown, and the manifest
    // records the capture-time state via IBitLockerVolume.
    internal sealed class LiveCaptureSource : IBlockSource, IBitLockerVolume
    {
        private readonly DiskCaptureLifetime _lifetime;
        private readonly IBlockSource _inner;
        private readonly BitLockerCaptureScope? _bitlocker;
        private bool _disposed;

        internal LiveCaptureSource(DiskCaptureLifetime lifetime, IBlockSource inner, BitLockerCaptureScope? bitlocker)
        {
            ArgumentNullException.ThrowIfNull(lifetime);
            ArgumentNullException.ThrowIfNull(inner);
            _lifetime = lifetime;
            _inner = inner;
            _bitlocker = bitlocker;
            lifetime.AddRef();
        }

        public long Length => _inner.Length;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _inner.OpenRead();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inner.Dispose();
            _lifetime.Release();
        }

        string? IBitLockerVolume.BitLockerState => _bitlocker?.CaptureState;
        List<string>? IBitLockerVolume.BitLockerKeyIds => _bitlocker?.CaptureKeyIds;
    }
}
