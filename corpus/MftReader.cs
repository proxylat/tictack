using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TicTack;

namespace Corpus
{
    // One MFT row from FSCTL_ENUM_USN_DATA: the FRN + parent FRN + name triple
    // the item-4 path→LCN resolver walks. V2 layout (UsnChangeSource enforces
    // the same major — V3 misreads offsets, so it falls back to full).
    public sealed record MftEnumEntry(ulong FileRef, ulong ParentRef, string Name, uint Attributes);

    // MFT-read seam: digest incrementals + the exclusion resolver share it.
    // Production = MftVolumeReader (Windows FSCTLs); every Linux test uses a
    // scripted fake. GetRecordBytes throws InvalidDataException on IOCTL
    // faults (caller falls back to full, never silently skips a live change);
    // dead slots return their raw bytes and get filtered by the parser.
    public interface IMftVolume : IDisposable
    {
        int BytesPerRecord { get; }
        int BytesPerCluster { get; }
        long RecordCount { get; }
        IReadOnlyList<(long StartLcn, long ClusterCount)> MftRuns { get; }
        IReadOnlyList<MftEnumEntry> Enumerate();
        byte[] GetRecordBytes(ulong fileRef);
    }

    // Pure FSCTL_ENUM_USN_DATA reply parser (Linux-testable): 8-byte next
    // start FRN followed by USN_RECORD_V2 rows. 0 next = enumeration done.
    internal static class MftEnumParser
    {
        internal static (ulong NextStart, List<MftEnumEntry> Entries) ParseEnumBuffer(byte[] buf, int ret)
        {
            ArgumentNullException.ThrowIfNull(buf);
            if (ret < 8)
                throw new InvalidDataException("FSCTL_ENUM_USN_DATA short reply");
            var next = BitConverter.ToUInt64(buf, 0);
            var entries = new List<MftEnumEntry>();
            var off = 8;
            while (off < ret)
            {
                if (off + 4 > ret)
                    throw new InvalidDataException("FSCTL_ENUM_USN_DATA truncated record header");
                var recLen = BitConverter.ToUInt32(buf, off);
                if (recLen < 60 || off + recLen > ret)
                    throw new InvalidDataException($"FSCTL_ENUM_USN_DATA corrupt record length {recLen}");
                var major = BitConverter.ToUInt16(buf, off + 4);
                if (major != 2)
                    throw new InvalidDataException($"USN record v{major} unsupported");
                var nameLen = BitConverter.ToUInt16(buf, off + 56);
                var nameOff = BitConverter.ToUInt16(buf, off + 58);
                if (nameOff < 60 || nameOff + nameLen > recLen)
                    throw new InvalidDataException("FSCTL_ENUM_USN_DATA corrupt name span");
                entries.Add(new MftEnumEntry(
                    BitConverter.ToUInt64(buf, off + 8),
                    BitConverter.ToUInt64(buf, off + 16),
                    Encoding.Unicode.GetString(buf, off + nameOff, nameLen),
                    BitConverter.ToUInt32(buf, off + 52)));
                off += (int)recLen;
            }
            return (next, entries);
        }
    }

    // Live MFT reader over a volume/snapshot device (Windows-only IO; the
    // parser above is pure). Ctor throws InvalidDataException for expected
    // unavailability (non-NTFS, no elevation), IOException for real faults.
    public sealed class MftVolumeReader : IMftVolume
    {
        private const uint FsctlEnumUsnData = 0x900B3;
        private const int EnumReadBytes = 64 * 1024;
        private const int ErrHandleEof = 38;   // defensive clean-end (normal end is next == 0)
        private const int ErrNoMoreFiles = 18;
        private const int MaxRecordBytes = 1024 * 1024;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[]? lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        private readonly FileStream _stream;
        private readonly string _device;
        private bool _disposed;

        public int BytesPerRecord { get; }
        public int BytesPerCluster { get; }
        public long RecordCount { get; }
        public IReadOnlyList<(long StartLcn, long ClusterCount)> MftRuns { get; }

        public MftVolumeReader(string device)
        {
            ArgumentException.ThrowIfNullOrEmpty(device);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("MFT reader needs Windows");
            _device = device;
            _stream = new FileStream(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                var geo = UsnChangeSource.QueryVolumeData(_stream.SafeFileHandle, device);
                if (geo == null)
                    throw new InvalidDataException("NTFS volume data unavailable");
                if (geo.Value.BytesPerCluster <= 0 || geo.Value.BytesPerRecord <= 0
                    || geo.Value.BytesPerRecord > MaxRecordBytes || geo.Value.MftValidDataLength < 0)
                    throw new InvalidDataException("NTFS volume data invalid");
                BytesPerCluster = geo.Value.BytesPerCluster;
                BytesPerRecord = geo.Value.BytesPerRecord;
                RecordCount = geo.Value.MftValidDataLength / geo.Value.BytesPerRecord;
                MftRuns = MftExtentReader.GetMftRuns(_stream.SafeFileHandle, BytesPerRecord);
            }
            catch
            {
                _stream.Dispose();
                throw;
            }
        }

        public IReadOnlyList<MftEnumEntry> Enumerate()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var all = new List<MftEnumEntry>();
            ulong start = 0;
            var buf = new byte[EnumReadBytes];
            while (true)
            {
                // MFT_ENUM_DATA_V1: start FRN + full USN span + V2-only pin.
                var input = new byte[28];
                BitConverter.GetBytes(start).CopyTo(input, 0);
                BitConverter.GetBytes(long.MaxValue).CopyTo(input, 16);
                BitConverter.GetBytes((ushort)2).CopyTo(input, 24);
                BitConverter.GetBytes((ushort)2).CopyTo(input, 26);
                if (!DeviceIoControl(_stream.SafeFileHandle, FsctlEnumUsnData, input, input.Length, buf, buf.Length, out var ret, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err is ErrHandleEof or ErrNoMoreFiles)
                        break;
                    if (UsnChangeSource.IsExpectedVolumeError(err))
                        throw new InvalidDataException($"FSCTL_ENUM_USN_DATA({_device}) unavailable: {err}");
                    throw new IOException($"FSCTL_ENUM_USN_DATA({_device}) failed: {err}");
                }
                var (next, entries) = MftEnumParser.ParseEnumBuffer(buf, ret);
                all.AddRange(entries);
                if (next == 0 || next == start)
                    break; // done, or no forward progress: stop rather than spin
                start = next;
            }
            return all;
        }

        public byte[] GetRecordBytes(ulong fileRef)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return MftExtentReader.ReadFileRecord(_stream.SafeFileHandle, fileRef, BytesPerRecord);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
        }
    }

    // Item-4 name matching over a cached enumeration. Pure for Linux tests.
    // Rules: "a\b" = that rooted path only (+ subtree when it is a dir);
    // "$x" = system name anywhere (user files never carry the $ prefix);
    // bare "Temp" = dirs named Temp anywhere + root-level files only (a
    // same-named user file in Documents must NOT be zeroed). Case- and
    // separator-insensitive; ":stream" suffixes strip to the host file.
    public static class MftPathResolver
    {
        public const uint FileAttributeDirectory = 0x10;
        public const ulong RootFileRef = 5;

        public static string SplitStream(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            var cut = name.IndexOf(':');
            return cut < 0 ? name : name.Substring(0, cut);
        }

        public static HashSet<ulong> Resolve(IReadOnlyList<string> names, IReadOnlyList<MftEnumEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(names);
            ArgumentNullException.ThrowIfNull(entries);
            var byRef = new Dictionary<ulong, MftEnumEntry>(entries.Count);
            var kids = new Dictionary<ulong, List<ulong>>();
            foreach (var e in entries)
            {
                byRef[e.FileRef] = e;
                if (!kids.TryGetValue(e.ParentRef, out var list))
                {
                    list = new List<ulong>();
                    kids[e.ParentRef] = list;
                }
                list.Add(e.FileRef);
            }
            var paths = new Dictionary<ulong, string>(entries.Count);
            var depths = new Dictionary<ulong, int>(entries.Count);
            foreach (var e in entries)
                FullPath(e.FileRef, byRef, paths, depths);

            var refs = new HashSet<ulong>();
            foreach (var raw in names)
            {
                var norm = SplitStream(raw).Replace('/', '\\').Trim('\\');
                if (norm.Length == 0)
                    continue;
                var pathed = norm.Contains('\\', StringComparison.Ordinal);
                var system = norm.StartsWith("$", StringComparison.Ordinal);
                foreach (var e in entries)
                {
                    if (e.FileRef == RootFileRef)
                        continue;
                    var isDir = (e.Attributes & FileAttributeDirectory) != 0;
                    bool hit;
                    if (pathed)
                        hit = string.Equals(paths[e.FileRef], norm, StringComparison.OrdinalIgnoreCase);
                    else if (system)
                        hit = string.Equals(e.Name, norm, StringComparison.OrdinalIgnoreCase);
                    else
                        hit = isDir
                            ? string.Equals(e.Name, norm, StringComparison.OrdinalIgnoreCase)
                            : depths[e.FileRef] == 1 && string.Equals(e.Name, norm, StringComparison.OrdinalIgnoreCase);
                    if (!hit)
                        continue;
                    refs.Add(e.FileRef);
                    if (isDir)
                        AddSubtree(e.FileRef, kids, refs);
                }
            }
            return refs;
        }

        private static void AddSubtree(ulong root, Dictionary<ulong, List<ulong>> kids, HashSet<ulong> refs)
        {
            var queue = new Queue<ulong>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!kids.TryGetValue(cur, out var list))
                    continue;
                foreach (var kid in list)
                {
                    if (refs.Add(kid))
                        queue.Enqueue(kid);
                }
            }
        }

        // Root-relative "a\b\c" + depth (root children = 1). Missing parents
        // (shouldn't happen — ENUM lists all rows) degrade to root children;
        // the visited set breaks reference cycles instead of hanging.
        private static void FullPath(ulong fileRef, Dictionary<ulong, MftEnumEntry> byRef,
            Dictionary<ulong, string> paths, Dictionary<ulong, int> depths)
        {
            if (paths.ContainsKey(fileRef))
                return;
            var chain = new List<string>();
            var seen = new HashSet<ulong>();
            var cur = fileRef;
            while (cur != RootFileRef && seen.Add(cur) && byRef.TryGetValue(cur, out var e) && e.ParentRef != cur)
            {
                chain.Add(e.Name);
                cur = e.ParentRef;
            }
            chain.Reverse();
            paths[fileRef] = string.Join('\\', chain);
            depths[fileRef] = chain.Count;
        }
    }

    // Item-4 production resolver: exclusion names → LCN runs via the shared
    // MFT reader. DATA runs only — never the MFT record cluster (one cluster
    // packs several records; zeroing it would wipe neighbors on restore).
    // Resolution never aborts an image: any failure returns null (capture
    // everything, loud) and the exclusion-set guard self-heals next round.
    public sealed class MftExclusionResolver : IExclusionResolver
    {
        public IReadOnlyList<string> ExclusionNames { get; }
        public long BytesPerCluster { get; }
        private readonly IReadOnlyList<(long StartLcn, long ClusterCount)> _runs;

        private MftExclusionResolver(IReadOnlyList<string> names, long bytesPerCluster,
            IReadOnlyList<(long StartLcn, long ClusterCount)> runs)
        {
            ExclusionNames = names;
            BytesPerCluster = bytesPerCluster;
            _runs = runs;
        }

        public IReadOnlyList<(long StartLcn, long ClusterCount)> ExcludedRuns() => _runs;

        // Pure build over cached IO (test seam); $LogFile strips here so no
        // caller can slip it through.
        internal static MftExclusionResolver Build(IReadOnlyList<string>? configured,
            IReadOnlyList<MftEnumEntry> entries, Func<ulong, byte[]> readRecord,
            int bytesPerCluster, int bytesPerRecord, IReadOnlyList<(long StartLcn, long ClusterCount)> mftRuns)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(readRecord);
            ArgumentNullException.ThrowIfNull(mftRuns);
            var names = CapturePolicy.EffectiveSkip(configured);
            var runs = new List<(long StartLcn, long ClusterCount)>();
            foreach (var r in MftPathResolver.Resolve(names, entries))
            {
                var ext = MftExtentReader.ParseRecordExtents(readRecord(r), r,
                    bytesPerCluster, bytesPerRecord, mftRuns, includeRecordCluster: false);
                if (ext != null)
                    runs.AddRange(ext);
            }
            return new MftExclusionResolver(names, bytesPerCluster, runs);
        }

        public static MftExclusionResolver? TryCreate(string device, IReadOnlyList<string>? configured, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(log);
            var names = CapturePolicy.EffectiveSkip(configured);
            if (names.Count == 0 || !OperatingSystem.IsWindows())
                return null;
            try
            {
                using var vol = new MftVolumeReader(device);
                return Build(configured, vol.Enumerate(), vol.GetRecordBytes,
                    vol.BytesPerCluster, vol.BytesPerRecord, vol.MftRuns);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Warn("exclusions disabled, capturing all (" + ex.Message + ")");
                return null;
            }
        }
    }
}
