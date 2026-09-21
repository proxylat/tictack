using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TicTack
{
    public class ExponentialBackoffRetry
    {
        private readonly int _maxAttempts;
        private readonly int _initialDelayMs;
        private readonly double _backoff;
        private readonly ILogger? _log;

        public ExponentialBackoffRetry(int maxAttempts = 5, int initialDelayMs = 1000, double backoff = 2.0, ILogger? log = null)
        {
            _maxAttempts = Math.Max(1, maxAttempts);
            _initialDelayMs = Math.Max(0, initialDelayMs);
            _backoff = Math.Max(1.0, backoff);
            _log = log;
        }

        public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                attempt++;
                try { return await action(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (attempt >= _maxAttempts) throw;
                    _log?.Debug("Retry " + attempt + "/" + _maxAttempts + " after failure: " + ex.Message);
                }
                // Cap the delay so a large user-configured backoff cannot overflow
                // the int narrowing and hand Task.Delay a negative value.
                var delay = (int)Math.Min(_initialDelayMs * Math.Pow(_backoff, attempt - 1), 60000);
                await Task.Delay(delay, ct);
            }
        }
    }

    public class FileAccessor : IFileAccessor
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        static extern int OpenFile(string path, int flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        const int O_RDONLY = 0;
        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint FILE_SHARE_DELETE = 0x00000004;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x00000008;

        public Stream OpenRead(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                // Bypass managed Unix sharing locks so active files remain readable.
                var fd = OpenFile(path, O_RDONLY);
                if (fd < 0)
                    throw new IOException("Failed to open source file: " + path + " (errno " + Marshal.GetLastWin32Error() + ")");
                return new FileStream(new SafeFileHandle((IntPtr)fd, true), FileAccess.Read);
            }

            path = PathUtil.EnsureExtended(path);

            var rawHandle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
            if (rawHandle == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new FileStream(new SafeFileHandle(rawHandle, true), FileAccess.Read);
        }
    }

    // VSS snapshot accessor (sync.file_access=vss, Windows-only): direct-open
    // fast path, and only when the open fails with a sharing/lock violation
    // does it serve the file from a per-volume shadow copy. Snapshots use
    // VSS_CTX_FILE_SHARE_BACKUP: auto-release, nonpersistent, no writer
    // involvement — releasing the IVssBackupComponents handle deletes the
    // snapshot, so expiry/dispose cannot orphan shadow copies (process death
    // releases them too). A cached snapshot is revalidated against the live
    // file's stat on every serve: the pipeline's post-copy validation reads
    // through this same accessor, so without the freshness check a stale
    // cache would validate against itself and converge on old bytes.
    // COM goes through [GeneratedComInterface] (AOT-safe); hand-written
    // ComImport RCWs are not. Method order below is the vss.h vtable order —
    // one wrong slot calls the wrong COM method (verified against
    // ReactOS sdk/include/psdk/vsbackup.idl).
    public sealed partial class VssFileAccessor : IFileAccessor, IDisposable
    {
        internal static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(10);

        const int VSS_CTX_FILE_SHARE_BACKUP = 1;
        const int VSS_OBJECT_SNAPSHOT_SET = 2;
        const int ERROR_SHARING_VIOLATION = 32;
        const int ERROR_LOCK_VIOLATION = 33;

        internal enum VssBackupType { Undefined = 0, Full = 1, Incremental = 2, Differential = 3, Log = 4, Copy = 5, Other = 6 }

        [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
        [Guid("507c37b4-cf5b-48e0-b0e6-ef342a56e633")]
        internal partial interface IVssAsync
        {
            void Cancel();
            void Wait();
            void QueryStatus(out int hrResult, out uint percentComplete);
        }

        [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
        [Guid("665c1d5f-c218-414d-a05d-7fef5f9d5c86")]
        internal partial interface IVssBackupComponents
        {
            void GetWriterComponentsCount(out uint components);
            void GetWriterComponents(uint index, out nint writer);
            void InitializeForBackup(nint xml);
            void SetBackupState([MarshalAs(UnmanagedType.Bool)] bool selectComponents, [MarshalAs(UnmanagedType.Bool)] bool bootableSystemState, VssBackupType backupType, [MarshalAs(UnmanagedType.Bool)] bool partialFileSupport);
            void InitializeForRestore(nint xml);
            void SetRestoreState(int restoreType);
            void GatherWriterMetadata(out nint async);
            void GetWriterMetadataCount(out uint count);
            void GetWriterMetadata(uint index, out Guid instance, out nint metadata);
            void FreeWriterMetadata();
            void AddComponent(Guid instance, Guid id, int type, nint logicalPath, nint name);
            void PrepareForBackup(out nint async);
            void AbortBackup();
            void GatherWriterStatus(out nint async);
            void GetWriterStatusCount(out uint count);
            void FreeWriterStatus();
            void GetWriterStatus(uint index, out Guid instance, out Guid id, out nint writer, out int status, out int failure);
            void SetBackupSucceeded(Guid instance, Guid id, int type, nint path, nint name, [MarshalAs(UnmanagedType.Bool)] bool succeeded);
            void SetBackupOptions(Guid id, int type, nint path, nint name, nint options);
            void SetSelectedForRestore(Guid id, int type, nint path, nint name, [MarshalAs(UnmanagedType.Bool)] bool selected);
            void SetRestoreOptions(Guid id, int type, nint path, nint name, nint options);
            void SetAdditionalRestores(Guid id, int type, nint path, nint name, [MarshalAs(UnmanagedType.Bool)] bool additional);
            void SetPreviousBackupStamp(Guid id, int type, nint path, nint name, nint stamp);
            void SaveAsXML(nint xml);
            void BackupComplete(out nint async);
            void AddAlternativeLocationMapping(Guid id, int type, nint logical, nint name, nint path, nint filespec, [MarshalAs(UnmanagedType.Bool)] bool recursive, nint destination);
            void AddRestoreSubcomponent(Guid id, int type, nint logical, nint name, nint path, nint subName, [MarshalAs(UnmanagedType.Bool)] bool repair);
            void SetFileRestoreStatus(Guid id, int type, nint path, nint name, int status);
            void AddNewTarget(Guid id, int type, nint logical, nint component, nint path, nint filename, [MarshalAs(UnmanagedType.Bool)] bool recursive, nint alternate);
            void SetRangesFilePath(Guid id, int type, nint logical, nint component, uint partial, nint ranges);
            void PreRestore(out nint async);
            void PostRestore(out nint async);
            void SetContext(int context);
            void StartSnapshotSet(out Guid snapshotSetId);
            void AddToSnapshotSet(string volumeName, Guid providerId, out Guid snapshotId);
            void DoSnapshotSet(out IVssAsync async);
            void DeleteSnapshots(Guid sourceObjectId, int objectType, [MarshalAs(UnmanagedType.Bool)] bool forceDelete, out int deletedSnapshots, out Guid nondeletedSnapshotId);
            void ImportSnapshots(out nint async);
            void BreakSnapshotSet(Guid snapshotSetId);
            void GetSnapshotProperties(Guid snapshotId, out nint prop);
            void Query(Guid queriedObjectId, int queriedObjectType, int returnedObjectType, out nint enums);
            void IsVolumeSupported(Guid providerId, string volumeName, [MarshalAs(UnmanagedType.Bool)] out bool supported);
            void DisableWriterClasses(nint writerIds, uint classId);
            void EnableWriterClasses(nint classIds, uint id);
            void DisableWriterInstances(nint instances, uint id);
            void ExposeSnapshot(Guid snapshotId, nint pathFromRoot, int attributes, nint expose, out nint exposed);
            void RevertToSnapshot(Guid snapshotId, [MarshalAs(UnmanagedType.Bool)] bool force);
            void QueryRevertStatus(nint volume, out nint async);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VssSnapshotProp
        {
            public Guid SnapshotId;
            public Guid SnapshotSetId;
            public int SnapshotsCount;
            [MarshalAs(UnmanagedType.LPWStr)] public string SnapshotDeviceObject;
            [MarshalAs(UnmanagedType.LPWStr)] public string OriginalVolumeName;
            [MarshalAs(UnmanagedType.LPWStr)] public string OriginatingMachine;
            [MarshalAs(UnmanagedType.LPWStr)] public string ServiceMachine;
            [MarshalAs(UnmanagedType.LPWStr)] public string ExposedName;
            [MarshalAs(UnmanagedType.LPWStr)] public string ExposedPath;
            public Guid ProviderId;
            public int SnapshotAttributes;
            public long CreationTimestamp;
            public int Status;
        }

        [DllImport("vssapi.dll", PreserveSig = true)]
        static extern int CreateVssBackupComponentsInternal(out IVssBackupComponents backup);

        [DllImport("vssapi.dll")]
        static extern void VssFreeSnapshotProperties(nint prop);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, uint bufferLength);

        sealed class SnapshotEntry
        {
            public Guid SetId;
            public string DeviceObject = string.Empty;
            public DateTime CreatedUtc;
            public IVssBackupComponents? Components;
        }

        private readonly FileAccessor _direct = new FileAccessor();
        private readonly ILogger? _log;
        private readonly Dictionary<string, SnapshotEntry> _cache = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();
        private bool _disposed;

        public VssFileAccessor(ILogger? log = null) { _log = log; }

#pragma warning disable MA0055 // Finalizer is the orphan-snapshot backstop: an undisposed accessor still releases its shadow copies at GC instead of leaking them to process end.
        ~VssFileAccessor() { Dispose(false); }
#pragma warning restore MA0055

        // Cheap usability check for factory fallback: create + init only,
        // no snapshot side effects. Privilege failures surface here.
        public void Probe()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("VSS snapshots require Windows.");
            var components = CreateComponents();
            // VSS protocol order: the interface must be initialized before any
            // other call. SetContext first AVs on uninitialized native state
            // instead of returning an HRESULT (observed 0xC0000005 on Win x64).
            components.InitializeForBackup(nint.Zero);
            components.SetContext(VSS_CTX_FILE_SHARE_BACKUP);
        }

        public Stream OpenRead(string path)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("VSS snapshots require Windows.");
            try
            {
                return _direct.OpenRead(path);
            }
            catch (Exception ex) when (IsLockViolation(ex))
            {
                Stream? snap = null;
                try { snap = OpenFromSnapshot(path); }
                catch (Exception snapEx) { _log?.Warn("VSS snapshot read failed, keeping direct error: " + snapEx.Message); }
                if (snap != null) return snap;
                throw;
            }
        }

        // #pragma cannot suppress this trim-analyzer diagnostic (reported at the
        // call site regardless); attribute suppression is the reliable channel.
        // Justification: TicTackSv is fully preserved via TrimmerRootAssembly
        // in the Aot publish profile, so the trimmer cannot remove the
        // interface methods this P/Invoke marshals.
        [UnconditionalSuppressMessage("TrimAnalysis", "IL2050", Justification = "VSS COM interface rooted via TrimmerRootAssembly in Aot.pubxml.")]
        static IVssBackupComponents CreateComponents()
        {
            int hr = CreateVssBackupComponentsInternal(out var components);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            return components!;
        }

        Stream OpenFromSnapshot(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                throw new IOException("VSS: cannot determine volume for: " + path);
            var canonical = CanonicalVolumeName(root);
            var entry = GetFreshEntry(canonical, fullPath, root);
            var mapped = MapToSnapshot(entry.DeviceObject, root, fullPath);
            return _direct.OpenRead(mapped);
        }

        SnapshotEntry GetFreshEntry(string canonicalVolume, string fullPath, string volumeRoot)
        {
            SnapshotEntry? entry;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(VssFileAccessor));
                _cache.TryGetValue(canonicalVolume, out entry);
                if (entry != null && DateTime.UtcNow - entry.CreatedUtc < SnapshotTtl
                    && SnapshotMatchesLive(entry, fullPath, volumeRoot))
                    return entry;
            }
            // Snapshot creation takes seconds: build outside the lock, then
            // publish under it (a duplicate build only wastes one snapshot).
            var fresh = CreateSnapshot(canonicalVolume);
            lock (_gate)
            {
                if (_cache.TryGetValue(canonicalVolume, out var current))
                {
                    EvictLocked(current);
                    _cache.Remove(canonicalVolume);
                }
                _cache[canonicalVolume] = fresh;
                return fresh;
            }
        }

        // True when the cached snapshot still reflects the live file, so the
        // serve cannot validate stale bytes against themselves.
        static bool SnapshotMatchesLive(SnapshotEntry entry, string fullPath, string volumeRoot)
        {
            try
            {
                if (!FileSnapshot.TryRead(fullPath, out var live)) return false;
                if (!FileSnapshot.TryRead(MapToSnapshot(entry.DeviceObject, volumeRoot, fullPath), out var snap)) return false;
                return live.Equals(snap);
            }
            catch { return false; }
        }

        SnapshotEntry CreateSnapshot(string canonicalVolume)
        {
            var components = CreateComponents();
            components.InitializeForBackup(nint.Zero);
            components.SetContext(VSS_CTX_FILE_SHARE_BACKUP);
            components.SetBackupState(false, false, VssBackupType.Copy, false);
            components.StartSnapshotSet(out var setId);
            components.AddToSnapshotSet(canonicalVolume, Guid.Empty, out var snapshotId);
            components.DoSnapshotSet(out var async);
            async.Wait();
            async.QueryStatus(out int hr, out _);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            components.GetSnapshotProperties(snapshotId, out nint propPtr);
            string device;
            try
            {
                var prop = Marshal.PtrToStructure<VssSnapshotProp>(propPtr);
                device = prop.SnapshotDeviceObject;
            }
            finally { VssFreeSnapshotProperties(propPtr); }
            if (string.IsNullOrEmpty(device))
                throw new IOException("VSS: snapshot has no device object.");
            return new SnapshotEntry { SetId = setId, DeviceObject = device, CreatedUtc = DateTime.UtcNow, Components = components };
        }

        void EvictLocked(SnapshotEntry entry)
        {
            // Best effort: the FILE_SHARE_BACKUP snapshot is nonpersistent and
            // dies with the components handle anyway; explicit delete keeps
            // shadow storage tidy without depending on GC timing.
            if (entry.Components != null)
            {
                try { entry.Components.DeleteSnapshots(entry.SetId, VSS_OBJECT_SNAPSHOT_SET, true, out _, out _); }
                catch { }
                entry.Components = null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        void Dispose(bool disposing)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var entry in _cache.Values) EvictLocked(entry);
                _cache.Clear();
            }
        }

        internal static string CanonicalVolumeName(string volumeRoot)
        {
            var sb = new StringBuilder(50);
            if (GetVolumeNameForVolumeMountPoint(volumeRoot, sb, (uint)sb.Capacity))
                return sb.ToString();
            throw new IOException("VSS: cannot resolve volume name for: " + volumeRoot + " (win32 " + Marshal.GetLastWin32Error() + ")");
        }

        internal static string MapToSnapshot(string deviceObject, string volumeRoot, string fullPath)
        {
            var rel = fullPath.StartsWith(volumeRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(volumeRoot.Length).TrimStart('\\', '/')
                : throw new IOException("VSS: path escapes its volume: " + fullPath);
            return deviceObject.EndsWith("\\", StringComparison.Ordinal)
                ? deviceObject + rel
                : deviceObject + "\\" + rel;
        }

        internal static bool IsLockViolation(Exception ex)
        {
            return (ex.HResult & 0xFFFF) == ERROR_SHARING_VIOLATION
                || (ex.HResult & 0xFFFF) == ERROR_LOCK_VIOLATION;
        }
    }
}
