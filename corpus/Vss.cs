using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using TicTack;

namespace Corpus
{
    // Minimal writer-involved VSS requester (system-image-future.md §5 rung 3):
    // in-box vssapi.dll, no AlphaVSS, no native shims, single-file publish
    // unaffected. V-table order + enum values verified against mingw-w64
    // vsbackup.h/vss.h (WIDL from the real IDL) — notably IVssAsync is
    // Cancel/Wait/QueryStatus and VSS_OBJECT_SNAPSHOT is 3.
    //
    // ComImport convention: the 13 used methods carry exact signatures; the
    // rest are positional padding (slot order is the contract) guarded by
    // [Obsolete(error)] so an accidental call fails the build, not VSS.
    internal enum VssObjectType
    {
        Unknown = 0,
        None = 1,
        SnapshotSet = 2,
        Snapshot = 3,
        Provider = 4
    }

    internal enum VssBackupType
    {
        Undefined = 0,
        Full = 1,
        Incremental = 2,
        Differential = 3,
        Log = 4,
        Copy = 5,
        Other = 6
    }

    internal enum VssSnapshotState
    {
        Unknown = 0,
        Preparing = 1,
        ProcessingPrepare = 2,
        Prepared = 3,
        ProcessingPrecommit = 4,
        Precommitted = 5,
        ProcessingCommit = 6,
        Committed = 7,
        ProcessingPostcommit = 8,
        ProcessingPrefinalcommit = 9,
        Prefinalcommitted = 10,
        ProcessingPostfinalcommit = 11,
        Created = 12,
        Aborted = 13,
        Deleted = 14,
        Postcommitted = 15
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct VssSnapshotProp
    {
        public Guid SnapshotId;
        public Guid SnapshotSetId;
        public int SnapshotsCount;
        public IntPtr SnapshotDeviceObject;
        public IntPtr OriginalVolumeName;
        public IntPtr OriginatingMachine;
        public IntPtr ServiceMachine;
        public IntPtr ExposedName;
        public IntPtr ExposedPath;
        public Guid ProviderId;
        public int SnapshotAttributes;
        public long CreationTimestamp;
        public int Status;
    }

    [ComImport, Guid("507c37b4-cf5b-4e95-b0af-14eb9767467e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVssAsync
    {
        [PreserveSig] int Cancel();
        [PreserveSig] int Wait(uint milliseconds);
        [PreserveSig] int QueryStatus(out int hrResult, out int reserved);
    }

    // Truncated after slot 9: identity + metadata-as-XML are all the
    // requester reads. (IVssWMComponent has a zero IID in the IDL, so the
    // XML carries component discovery instead of v-table calls.)
    [ComImport, Guid("902fcf7f-b7fd-42f8-81f1-b2e400b1e5bd"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVssExamineWriterMetadata
    {
        [PreserveSig] int GetIdentity(out Guid instance, out Guid writer, [MarshalAs(UnmanagedType.BStr)] out string name, out int usage, out int source);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetFileCounts(out uint inc, out uint exc, out uint comp);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetIncludeFile(uint i, out IntPtr desc);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetExcludeFile(uint i, out IntPtr desc);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetComponent(uint i, out IntPtr comp);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetRestoreMethod(out IntPtr method, out IntPtr service, out IntPtr proc, out int restore, out byte reboot, out uint maps);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetAlternateLocationMapping(uint i, out IntPtr desc);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetBackupSchema(out uint mask);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetDocument(out IntPtr doc);
        [PreserveSig] int SaveAsXML([MarshalAs(UnmanagedType.BStr)] out string xml);
    }

    [ComImport, Guid("665c1d5f-c218-414d-a05d-7fef5f9d5c86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVssBackupComponents
    {
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetWriterComponentsCount(out uint count);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetWriterComponents(uint i, out IntPtr writer);
        [PreserveSig] int InitializeForBackup([MarshalAs(UnmanagedType.BStr)] string? xml);
        [PreserveSig] int SetBackupState([MarshalAs(UnmanagedType.U1)] bool selectComponents, [MarshalAs(UnmanagedType.U1)] bool bootableSystemState, VssBackupType type, [MarshalAs(UnmanagedType.U1)] bool partialFileSupport);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int InitializeForRestore(IntPtr xml);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int SetRestoreState(int type);
        [PreserveSig] int GatherWriterMetadata(out IVssAsync async);
        [PreserveSig] int GetWriterMetadataCount(out uint count);
        [PreserveSig] int GetWriterMetadata(uint i, out Guid instance, out IVssExamineWriterMetadata metadata);
        [PreserveSig] int FreeWriterMetadata();
        [PreserveSig] int AddComponent(Guid instance, Guid writer, int type, [MarshalAs(UnmanagedType.LPWStr)] string? path, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PrepareForBackup(out IVssAsync async);
        [PreserveSig] int AbortBackup();
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GatherWriterStatus(out IVssAsync async);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetWriterStatusCount(out uint count);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int FreeWriterStatus();
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int GetWriterStatus(uint i, out Guid instance, out Guid writer, out IntPtr name, out int state, out int failure);
        [PreserveSig] int SetBackupSucceeded(Guid instance, Guid writer, int type, [MarshalAs(UnmanagedType.LPWStr)] string? path, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.U1)] bool succeeded);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int SetBackupOptions(Guid writer, int type, IntPtr path, IntPtr name, IntPtr options);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int SetSelectedForRestore(Guid writer, int type, IntPtr path, IntPtr name, byte selected);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int SetRestoreOptions(Guid writer, int type, IntPtr path, IntPtr name, IntPtr options);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int SetAdditionalRestores(Guid writer, int type, IntPtr path, IntPtr name, byte additional);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int SetPreviousBackupStamp(Guid writer, int type, IntPtr path, IntPtr name, IntPtr stamp);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int SaveAsXML(out IntPtr xml);
        [PreserveSig] int BackupComplete(out IVssAsync async);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int AddAlternativeLocationMapping(Guid writer, int type, IntPtr path, IntPtr name, IntPtr p, IntPtr spec, byte rec, IntPtr dest);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int AddRestoreSubcomponent(Guid writer, int type, IntPtr path, IntPtr name, IntPtr subPath, IntPtr subName, byte repair);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int SetFileRestoreStatus(Guid writer, int type, IntPtr path, IntPtr name, int status);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int AddNewTarget(Guid writer, int type, IntPtr path, IntPtr name, IntPtr p, IntPtr file, byte rec, IntPtr alt);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int SetRangesFilePath(Guid writer, int type, IntPtr path, IntPtr name, uint i, IntPtr ranges);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int PreRestore(out IVssAsync async);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int PostRestore(out IVssAsync async);
        [PreserveSig] int SetContext(int context);
        [PreserveSig] int StartSnapshotSet(out Guid setId);
        [PreserveSig] int AddToSnapshotSet([MarshalAs(UnmanagedType.LPWStr)] string volume, Guid provider, out Guid snapshotId);
        [PreserveSig] int DoSnapshotSet(out IVssAsync async);
        [Obsolete("VSS v-table padding — never call (BACKUP context auto-releases; explicit delete always NOT_FOUND)", true)] [PreserveSig] int DeleteSnapshots(Guid sourceId, VssObjectType type, [MarshalAs(UnmanagedType.Bool)] bool force, out int deleted, out Guid nonDeleted);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int ImportSnapshots(out IVssAsync async);
        [Obsolete("VSS v-table padding — never call", true)] [PreserveSig] int BreakSnapshotSet(Guid setId);
        [PreserveSig] int GetSnapshotProperties(Guid snapshotId, out VssSnapshotProp prop);
    }

    internal static class VssApi
    {
        // The factory export is C++-MANGLED (verified via export-table dump
        // on Win11 26100: the undecorated name does not exist, GetProcAddress
        // → NULL). x64 ignores the calling convention, Cdecl documents it.
        [DllImport("vssapi.dll", EntryPoint = "?CreateVssBackupComponents@@YAJPEAPEAVIVssBackupComponents@@@Z", CallingConvention = CallingConvention.Cdecl, PreserveSig = true)]
        internal static extern int CreateVssBackupComponents(out IVssBackupComponents components);

        [DllImport("vssapi.dll")]
        internal static extern void VssFreeSnapshotProperties(ref VssSnapshotProp prop);

        // DCOM privacy for the VSS service RPC: .NET's default process
        // blanket (CONNECT) is below what VSS demands (PKT_PRIVACY), which
        // surfaces as VSS_E_BAD_STATE from Prepare/Do, not access-denied.
        [DllImport("ole32.dll", PreserveSig = true)]
        internal static extern int CoInitializeSecurity(IntPtr secDesc, int authnSvcCount, IntPtr authnSvc, IntPtr reserved1, uint authnLevel, uint impLevel, IntPtr authList, uint capabilities, IntPtr reserved3);
    }

    // One shadow copy, owned end to end: Create freezes the volume, Dispose
    // deletes exactly our snapshot set (never /all — that would eat System
    // Restore points). Complete() is the honesty bit: only a full capture
    // earns BackupComplete — probes that read nothing must Abort, because
    // BackupComplete can trigger writer log truncation for data we never
    // took. VSS internal timeouts bound every wait (abort timers later).
    public sealed class VssSnapshot : IDisposable
    {
        private const int VssCtxBackup = 0;

        private readonly IVssBackupComponents _com;
        private readonly Guid _setId;
        private readonly List<(Guid Instance, Guid Writer, int Type, string? Path, string Name)> _selected;
        private readonly ILogger _log;
        private bool _completed;
        private bool _disposed;

        public string DeviceObject { get; }
        public Guid SnapshotId { get; }

        private VssSnapshot(IVssBackupComponents com, Guid setId, Guid snapId, string device, List<(Guid, Guid, int, string?, string)> selected, ILogger log)
        {
            _com = com;
            _setId = setId;
            SnapshotId = snapId;
            DeviceObject = device;
            _selected = selected;
            _log = log;
        }

        public static VssSnapshot Create(string drive, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(drive);
            ArgumentNullException.ThrowIfNull(log);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("VSS capture needs Windows (volume: " + drive + ")");
            if (drive.Length != 2 || drive[1] != ':' || !char.IsAsciiLetter(drive[0]))
                throw new ArgumentException("want a drive letter like \"T:\" (got: " + drive + ")", nameof(drive));
            if (!IsElevated())
                throw new InvalidOperationException("VSS capture needs elevation — run Corpus.exe elevated.");
            // Diff-area preflight, cheap rung: volume-proportional floor with
            // documented-minimum clamps (see DiffFloor — a fixed floor bans
            // small volumes outright, and half-the-volume bans full ones).
            var driveInfo = new DriveInfo(drive);
            var headroom = DiffFloor.RequiredHeadroomBytes(driveInfo.TotalSize);
            if (driveInfo.AvailableFreeSpace < headroom)
                throw new InvalidOperationException($"volume {drive} has {driveInfo.AvailableFreeSpace} bytes free — VSS needs {headroom} bytes headroom for the diff area.");

            // COM security FIRST, before any COM activity: the VSS writer
            // fan-out (Prepare/Do) demands PKT_PRIVACY, and .NET's default
            // process blanket is weaker — arriving too late surfaces as
            // VSS_E_BAD_STATE, not access-denied. CoInitializeSecurity wins
            // exactly once per process; anything but S_OK here is fatal.
            var secHr = VssApi.CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, 6u, 3u, IntPtr.Zero, 0u, IntPtr.Zero);
            if (secHr < 0)
                throw new IOException($"CoInitializeSecurity failed (0x{secHr:X8}) — VSS writer calls cannot authenticate", Marshal.GetExceptionForHR(secHr));

            Hr(VssApi.CreateVssBackupComponents(out var com), "CreateVssBackupComponents");
            try
            {
                Hr(com.InitializeForBackup(null), "InitializeForBackup");
                // No SetContext: default IS VSS_CTX_BACKUP (AlphaShadow skips
                // it too for the Backup context — explicit call avoided).
                // Component mode, bootable, full: matches the proven
                // AlphaShadow/vshadow requester shape select-for-select.
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
                Hr(com.AddToSnapshotSet(drive + "\\", Guid.Empty, out var snapId), "AddToSnapshotSet");
                log.Info($"VSS set={setId} snap={snapId}");
                // Prepare AFTER the set exists with its volumes: AlphaShadow
                // order (Prepare-before-Start answers VSS_E_BAD_STATE —
                // Prepare validates set existence).
                Hr(com.PrepareForBackup(out var pf), "PrepareForBackup");
                RunAsync(pf, "PrepareForBackup");
                Hr(com.DoSnapshotSet(out var ds), "DoSnapshotSet");
                RunAsync(ds, "DoSnapshotSet");
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
                // NO_WRITERS bit (0x10) must be CLEAR — a set bit means the
                // snapshot silently degraded to crash-consistent. Fail loud.
                if ((prop.SnapshotAttributes & 0x10) != 0)
                    throw new IOException($"snapshot {snapId} came back writerless (attributes 0x{prop.SnapshotAttributes:X8}) — writers did not participate");
                log.Info($"VSS snapshot of {drive} at {device} (attributes 0x{prop.SnapshotAttributes:X8}, writers involved)");
                return new VssSnapshot(com, setId, snapId, device, selected, log);
            }
            catch
            {
                try { com.AbortBackup(); } catch (Exception ex) { log.Warn("VSS AbortBackup after failure: " + ex.Message); }
                Marshal.ReleaseComObject(com);
                throw;
            }
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
                    // Component mode demands per-component success marks
                    // before BackupComplete (AlphaShadow does the same).
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
                // volatile + auto-release by definition — BackupComplete (or
                // Abort) already discards them, and an explicit delete only
                // answers VSS_E_OBJECT_NOT_FOUND (0x80042308, verified
                // on-box in both paths). Releasing the object triggers any
                // remaining auto-release; the loop script's stranded-shadow
                // checks are the tripwire. Never persist this context.
                Marshal.ReleaseComObject(_com);
            }
        }

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
                throw new IOException($"{op} failed (0x{hr:X8})", Marshal.GetExceptionForHR(hr));
        }

        // Writer-metadata XML → selectable components. Schema (v1.2,
        // observed on-box): <FILE_GROUP>/<DATABASE> under <BACKUP_LOCATIONS>
        // with componentName + optional logicalPath; NO componentType attr —
        // the element name carries the type (database=1, file group=2).
        // Matched case-insensitively against schema drift.
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

        private static bool IsElevated()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
