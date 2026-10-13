using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TicTack;

namespace Corpus
{
    // Incremental imaging options (§A.4; lives here, not Store.cs, so the
    // capture contract reads in one place).
    public sealed record ImageOptions(
        int ParentGeneration = 0,   // 0 = auto (latest) when Incremental, else full
        bool Incremental = false,
        bool ForceFull = false,
        IChangeSource? ChangeSource = null);

    public sealed record IncrementalPlan(HashSet<long> DirtyChunks, string NewCursor);

    public interface IChangeSource
    {
        // Null = incremental impossible → caller falls back to full re-read.
        // Never throws for expected unavailability (non-NTFS, no elevation,
        // journal wrap); throws only for IO faults mid-read.
        IncrementalPlan? Plan(SnapshotManifest parent, IBlockSource source, int chunkBytes, ILogger log);
    }

    // Scripted change source: every Linux test uses this (real USN needs
    // elevated NTFS; on-box validation is manual, not CI).
    public sealed class FakeChangeSource : IChangeSource
    {
        private readonly Func<SnapshotManifest, IBlockSource, int, IncrementalPlan?> _plan;

        public FakeChangeSource(Func<SnapshotManifest, IBlockSource, int, IncrementalPlan?> plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            _plan = plan;
        }

        public IncrementalPlan? Plan(SnapshotManifest parent, IBlockSource source, int chunkBytes, ILogger log) =>
            _plan(parent, source, chunkBytes);
    }

    // Live-volume handle seam for UsnChangeSource: implementors expose the
    // frozen snapshot device planning reads from. Sources without one
    // (files, fakes) make Plan return null → full re-read, never wrong data.
    internal interface IVolumeHandleProvider
    {
        string SnapshotDevicePath { get; }
    }

    // Primary change detector: reads the NTFS USN journal between the
    // parent cursor and now, mapping changed file ids to volume clusters
    // via MFT data runs. Windows-only; null-on-Linux internally so callers
    // stay branch-free. Null = incremental impossible → full re-read.
    public sealed class UsnChangeSource : IChangeSource
    {
        private const uint FsctlQueryUsnJournal = 0x900F4;
        private const uint FsctlReadUsnJournal = 0x900BB;
        private const uint FsctlGetNtfsVolumeData = 0x90064;
        private const int UsnReadBytes = 64 * 1024;

        // DATA_OVERWRITE|DATA_EXTEND|DATA_TRUNCATION|FILE_CREATE|FILE_DELETE|
        // RENAME_NEW_NAME|CLOSE — any data or namespace change.
        private const uint ReasonMask = 0x80002307;

        private const int ErrInvalidFunction = 1;      // ioctl unsupported (non-NTFS)
        private const int ErrAccessDenied = 5;         // no elevation
        private const int ErrInvalidParameter = 87;    // stale cursor/journal id
        private const int ErrJournalDeleteInProgress = 1178;
        private const int ErrJournalNotActive = 1179;  // journal disabled
        private const int ErrJournalEntryDeleted = 1181; // wrap / cursor evicted

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[]? lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        // Cursor v1: "usn:<journalIdHex16>:<nextUsnHex16>" (two's-complement
        // hex; ids with the high bit set round-trip exactly).
        internal static string FormatCursor(long journalId, long nextUsn) =>
            $"usn:{journalId:x16}:{nextUsn:x16}";

        // Unknown prefixes (future v2 "usn2:..."), garbage, empty → null
        // (= full re-read). Never throws.
        internal static (long JournalId, long NextUsn)? ParseCursor(string? cursor)
        {
            if (string.IsNullOrEmpty(cursor))
                return null;
            var parts = cursor.Split(':');
            if (parts.Length != 3 || parts[0] != "usn")
                return null;
            if (!ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var journal))
                return null;
            if (!ulong.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var usn))
                return null;
            return ((long)journal, (long)usn);
        }

        public IncrementalPlan? Plan(SnapshotManifest parent, IBlockSource source, int chunkBytes, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(log);
            if (!OperatingSystem.IsWindows())
                return null;
            var cursor = ParseCursor(parent.ChangeCursor);
            if (cursor == null)
                return null;
            if (source is FileBlockSource)
                return null;
            if (source is not IVolumeHandleProvider vol)
            {
                log.Info("incremental needs a live volume handle, full image (unsupported block source)");
                return null;
            }
            var device = vol.SnapshotDevicePath;
            FileStream volStream;
            try
            {
                volStream = new FileStream(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Info($"USN journal unavailable on {device}, full image (open failed: {ex.Message})");
                return null;
            }
            using (volStream)
            {
                var handle = volStream.SafeFileHandle;
                var journal = QueryJournal(handle, device);
                if (journal == null)
                {
                    log.Info($"USN journal unavailable on {device}, full image (query failed)");
                    return null;
                }
                var (journalId, firstUsn, tipNow) = journal.Value;
                if (journalId != cursor.Value.JournalId)
                {
                    log.Info($"USN journal id changed on {device}, full image (reformat?)");
                    return null;
                }
                if (cursor.Value.NextUsn < firstUsn)
                {
                    log.Info($"USN cursor evicted on {device}, full image (journal wrapped)");
                    return null;
                }
                if (cursor.Value.NextUsn > tipNow)
                {
                    log.Info($"USN cursor past journal tip on {device}, full image (volume rolled back?)");
                    return null;
                }
                var geo = QueryVolumeData(handle, device);
                if (geo == null || geo.Value.BytesPerCluster <= 0 || geo.Value.BytesPerRecord <= 0 || geo.Value.BytesPerRecord > 1024 * 1024)
                {
                    log.Info($"USN volume data unreadable on {device}, full image");
                    return null;
                }
                var read = ReadChangedFileIds(handle, journalId, firstUsn, cursor.Value.NextUsn, tipNow, device, log);
                if (read == null)
                    return null;
                var (fileIds, tipUsn) = read.Value;
                List<(long StartLcn, long ClusterCount)> mftRuns;
                try
                {
                    mftRuns = MftExtentReader.GetMftRuns(handle, geo.Value.BytesPerRecord);
                }
                catch (InvalidDataException ex)
                {
                    log.Info($"full image ($MFT runs unreadable on {device}: {ex.Message})");
                    return null;
                }
                var dirty = new HashSet<long>();
                var bpc = (long)geo.Value.BytesPerCluster;
                foreach (var id in fileIds)
                {
                    List<(long StartLcn, long ClusterCount)>? runs;
                    try
                    {
                        runs = MftExtentReader.GetDataRuns(handle, id, geo.Value.BytesPerCluster, geo.Value.BytesPerRecord, mftRuns);
                    }
                    catch (InvalidDataException ex)
                    {
                        log.Info($"full image (MFT record for changed file {id:X16} unreadable: {ex.Message})");
                        return null;
                    }
                    if (runs == null)
                        continue; // deleted/reused id → skip: its clusters' rewrites carry their own USN records
                    AddRunChunks(dirty, runs, bpc, chunkBytes);
                }
                // NTFS self-metadata ($MFT/$LogFile/$Bitmap/...) has no
                // reliable USN coverage of its own writes, yet every file
                // write mutates it: always re-capture FRNs 0..15 (MFT zone +
                // log + bitmap + boot + secure + upcase + $Extend dir).
                // Synthetic FRNs carry seq 0 (live records never do), so the
                // sequence check is bypassed — magic + in-use still filter
                // unused reserved slots. $UsnJrnl itself (under $Extend,
                // FRN varies) stays one interval stale by design — advisory
                // file, and a post-restore cursor past its EOF falls back
                // to full re-read via the tip check above.
                for (var sys = 0UL; sys < 16; sys++)
                {
                    List<(long StartLcn, long ClusterCount)>? sysRuns;
                    try
                    {
                        sysRuns = MftExtentReader.GetDataRuns(handle, sys, geo.Value.BytesPerCluster, geo.Value.BytesPerRecord, mftRuns, skipSequenceCheck: true);
                    }
                    catch (InvalidDataException ex)
                    {
                        log.Info($"full image (MFT record for system file {sys} unreadable: {ex.Message})");
                        return null;
                    }
                    if (sysRuns == null)
                        continue; // reserved/unused slot → skip
                    AddRunChunks(dirty, sysRuns, bpc, chunkBytes);
                }
                return new IncrementalPlan(dirty, FormatCursor(journalId, tipUsn));
            }
        }

        // Expected unavailability → null (full re-read), never throw;
        // anything else is an IO fault → throw (fail loud).
        private static bool IsExpectedUsnError(int err) =>
            err is ErrInvalidFunction or ErrAccessDenied or ErrInvalidParameter
                or ErrJournalDeleteInProgress or ErrJournalNotActive or ErrJournalEntryDeleted;

        // Bootstrap cursor for full images: WITHOUT it, incrementals never
        // engage (a cursor-less parent yields a null plan → full, forever).
        // Queries the FROZEN shadow (same device the bytes came from), so the
        // cursor matches the captured state exactly — a live tip would skip
        // changes made during imaging. Best-effort: any failure → null
        // (incrementals keep falling back, never wrong data).
        internal static string? TryCurrentCursor(IBlockSource source, ILogger log)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            if (source is not IVolumeHandleProvider vol)
                return null;
            try
            {
                using var stream = new FileStream(vol.SnapshotDevicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var journal = QueryJournal(stream.SafeFileHandle, vol.SnapshotDevicePath);
                if (journal == null)
                    return null;
                return FormatCursor(journal.Value.JournalId, journal.Value.NextUsn);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Info("full image recorded without cursor (" + ex.Message + ")");
                return null;
            }
        }

        private static (long JournalId, long FirstUsn, long NextUsn)? QueryJournal(SafeFileHandle handle, string device)
        {
            var output = new byte[64];
            if (!DeviceIoControl(handle, FsctlQueryUsnJournal, null, 0, output, output.Length, out var ret, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                if (!IsExpectedUsnError(err))
                    throw new IOException($"FSCTL_QUERY_USN_JOURNAL({device}) failed: {err}");
                return null;
            }
            if (ret < 24)
                throw new IOException($"FSCTL_QUERY_USN_JOURNAL({device}) short reply: {ret} bytes");
            return (BitConverter.ToInt64(output, 0), BitConverter.ToInt64(output, 8), BitConverter.ToInt64(output, 16));
        }

        private static (int BytesPerCluster, int BytesPerRecord)? QueryVolumeData(SafeFileHandle handle, string device)
        {
            var output = new byte[96];
            if (!DeviceIoControl(handle, FsctlGetNtfsVolumeData, null, 0, output, output.Length, out var ret, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                if (!IsExpectedUsnError(err))
                    throw new IOException($"FSCTL_GET_NTFS_VOLUME_DATA({device}) failed: {err}");
                return null;
            }
            if (ret < 56)
                throw new IOException($"FSCTL_GET_NTFS_VOLUME_DATA({device}) short reply: {ret} bytes");
            return (BitConverter.ToInt32(output, 44), BitConverter.ToInt32(output, 48));
        }

        // LCN range → chunk grid range via byte offsets (chunk i covers
        // bytes [i*chunkBytes,...), not raw LCNs).
        private static void AddRunChunks(HashSet<long> dirty, List<(long StartLcn, long ClusterCount)> runs, long bpc, int chunkBytes)
        {
            foreach (var (startLcn, count) in runs)
            {
                var first = startLcn * bpc / chunkBytes;
                var last = ((startLcn + count) * bpc - 1) / chunkBytes;
                for (var c = first; c <= last; c++)
                    dirty.Add(c);
            }
        }

        // Bounded 64KB reads from FirstUsn to the journal tip, keeping only
        // records at/after the cursor. Reads ALWAYS start at FirstUsn (never
        // at the cursor): StartUsn must be an exact record boundary and
        // mid values fail 87 (proven on-box: boundary OK, +1 fails) — and a
        // stored tip is not reliably a boundary (query tips carry slack).
        // Client-side >= filtering is exact regardless: USNs are assigned
        // monotonically at write, so post-freeze ⟺ USN ≥ tip.
        // The loop is TIP-BOUNDED (never issues StartUsn ≥ tip): on BACKUP
        // shadows an at-tip read fails 19 instead of returning empty
        // (proven on-box, twice; live + ClientAccessible return empty).
        // The shadow is frozen, so the query-time tip is immutable here.
        // Null = wrapped/evicted/unavailable → full re-read.
        private static (HashSet<ulong> Ids, long NextUsn)? ReadChangedFileIds(SafeFileHandle handle, long journalId, long firstUsn, long cursorUsn, long tipUsn, string device, ILogger log)
        {
            var ids = new HashSet<ulong>();
            var usn = firstUsn;
            var buf = new byte[UsnReadBytes];
            while (usn < tipUsn)
            {
                var input = new byte[40];
                BitConverter.GetBytes(usn).CopyTo(input, 0);
                BitConverter.GetBytes(ReasonMask).CopyTo(input, 8);
                BitConverter.GetBytes(journalId).CopyTo(input, 32);
                if (!DeviceIoControl(handle, FsctlReadUsnJournal, input, input.Length, buf, buf.Length, out var ret, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (!IsExpectedUsnError(err))
                        throw new IOException($"FSCTL_READ_USN_JOURNAL({device}) failed: {err}");
                    log.Info($"USN journal read stopped on {device}, full image (win32 {err})");
                    return null;
                }
                if (ret < 8)
                    throw new IOException($"FSCTL_READ_USN_JOURNAL({device}) short reply: {ret} bytes");
                var nextUsn = BitConverter.ToInt64(buf, 0);
                var off = 8;
                while (off < ret)
                {
                    if (off + 4 > ret)
                        throw new IOException($"FSCTL_READ_USN_JOURNAL({device}) truncated record header");
                    var recLen = BitConverter.ToUInt32(buf, off);
                    if (recLen < 60 || off + recLen > ret)
                        throw new IOException($"FSCTL_READ_USN_JOURNAL({device}) corrupt record length {recLen}");
                    var major = BitConverter.ToUInt16(buf, off + 4);
                    // V3 carries 16-byte FRNs (Reason @56, not @40) — parsing
                    // it with V2 offsets misreads the mask filter (silent
                    // under-capture), so anything but V2 falls back to full.
                    // NTFS emits V3 only when range tracking is on.
                    if (major != 2)
                    {
                        log.Info($"USN record v{major} unsupported on {device}, full image");
                        return null;
                    }
                    // V2 layout: Usn @24 (major enforced above, so offsets hold).
                    var recUsn = BitConverter.ToInt64(buf, off + 24);
                    if (recUsn >= cursorUsn)
                    {
                        var reason = BitConverter.ToUInt32(buf, off + 40);
                        if ((reason & ReasonMask) != 0)
                            ids.Add(BitConverter.ToUInt64(buf, off + 8));
                    }
                    off += (int)recLen;
                }
                if (nextUsn <= usn)
                    break; // no forward progress: stop rather than spin
                usn = nextUsn;
            }
            return (ids, tipUsn);
        }
    }

    // MFT extent localization (Windows-only IO; the parsers below it are
    // pure and Linux-testable). Maps a 64-bit file reference number to the
    // volume clusters holding its data: non-resident $DATA + $INDEX_ALLOC
    // runs (big-directory indexes live in 0xA0, not 0x80), plus the file's
    // own MFT record cluster (timestamps/sizes/run-lists change on every
    // write — without it, extends image with stale sizes).
    internal static class MftExtentReader
    {
        private const uint FsctlGetNtfsFileRecord = 0x90068;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[]? lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        // Null = unlocalizable (deleted/reused id) → caller skips. Throws
        // InvalidDataException when a LIVE record won't parse → caller
        // falls back to full (never silently skip a live change).
        // skipSequenceCheck is for synthetic FRNs (system 0..15, seq 0):
        // live records always carry seq ≥ 1, so the check would reject
        // them all; magic + in-use still filter unused slots.
        internal static List<(long StartLcn, long ClusterCount)>? GetDataRuns(SafeFileHandle volume, ulong fileRef, int bytesPerCluster, int bytesPerRecord, List<(long StartLcn, long ClusterCount)> mftRuns, bool skipSequenceCheck = false)
        {
            var record = ReadFileRecord(volume, fileRef, bytesPerRecord);
            return ParseRecordExtents(record, fileRef, bytesPerCluster, bytesPerRecord, mftRuns, skipSequenceCheck);
        }

        internal static List<(long StartLcn, long ClusterCount)> GetMftRuns(SafeFileHandle volume, int bytesPerRecord)
        {
            var record = ReadFileRecord(volume, 0, bytesPerRecord);
            return ParseMftRuns(record);
        }

        // Ioctl failure ≈ real fault, not an unknown id: stale/deleted FRNs
        // still RETURN their (reused) record bytes and get filtered by the
        // seq/in-use checks below — so a failed call throws → caller falls
        // back to full (never silently skip a live change).
        private static byte[] ReadFileRecord(SafeFileHandle volume, ulong fileRef, int bytesPerRecord)
        {
            var input = BitConverter.GetBytes(fileRef);
            var output = new byte[12 + bytesPerRecord];
            if (!DeviceIoControl(volume, FsctlGetNtfsFileRecord, input, input.Length, output, output.Length, out var ret, IntPtr.Zero))
                throw new InvalidDataException($"FSCTL_GET_NTFS_FILE_RECORD({fileRef:X16}) failed: {Marshal.GetLastWin32Error()}");
            if (ret < 12)
                throw new IOException($"FSCTL_GET_NTFS_FILE_RECORD short reply: {ret} bytes");
            var len = BitConverter.ToUInt32(output, 8);
            if (len > ret - 12)
                throw new IOException($"FSCTL_GET_NTFS_FILE_RECORD corrupt length {len}");
            var record = new byte[len];
            Buffer.BlockCopy(output, 12, record, 0, (int)len);
            return record;
        }

        internal static List<(long StartLcn, long ClusterCount)>? ParseRecordExtents(byte[] record, ulong fileRef, int bytesPerCluster, int bytesPerRecord, List<(long StartLcn, long ClusterCount)> mftRuns, bool skipSequenceCheck = false)
        {
            if (record.Length < 48)
                throw new InvalidDataException("MFT record truncated");
            if (record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E')
                return null; // slot reused/zeroed → skip: its clusters' rewrites carry their own USN records
            if (!skipSequenceCheck && BitConverter.ToUInt16(record, 0x10) != (ushort)(fileRef >> 48))
                return null; // sequence mismatch = slot reused → same skip reasoning
            if ((BitConverter.ToUInt16(record, 0x16) & 1) == 0)
                return null; // deleted, not yet reused → same skip reasoning
            var extents = new List<(long StartLcn, long ClusterCount)>();
            foreach (var (off, len) in AttributeSpans(record))
            {
                var type = BitConverter.ToUInt32(record, off);
                if (type != 0x80 && type != 0xA0)
                    continue;
                if (len < 9)
                    throw new InvalidDataException("MFT data attribute truncated");
                if (record[off + 8] == 0)
                    continue; // resident → bytes live in this record → covered by the record cluster below
                if (len < 34)
                    throw new InvalidDataException("MFT non-resident data attribute truncated");
                var runOff = BitConverter.ToUInt16(record, off + 32);
                if (runOff >= len)
                    throw new InvalidDataException("MFT run offset outside attribute");
                extents.AddRange(DecodeDataRuns(record.AsSpan(off + runOff, (int)len - runOff)));
            }
            // The file's own record cluster (see class comment).
            var recordNumber = (long)(fileRef & 0xFFFF_FFFF_FFFF);
            var lcn = MapMftOffsetToLcn(recordNumber * (long)bytesPerRecord, bytesPerCluster, mftRuns);
            if (lcn == null)
                throw new InvalidDataException("MFT record offset unmappable");
            extents.Add((lcn.Value, 1));
            return extents;
        }

        internal static List<(long StartLcn, long ClusterCount)> ParseMftRuns(byte[] record)
        {
            if (record.Length < 48 || record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E')
                throw new InvalidDataException("MFT record 0 corrupt");
            foreach (var (off, len) in AttributeSpans(record))
            {
                if (BitConverter.ToUInt32(record, off) != 0x80)
                    continue;
                if (len < 34 || record[off + 8] == 0)
                    continue; // resident $DATA (impossible for $MFT, skip defensively)
                var runOff = BitConverter.ToUInt16(record, off + 32);
                if (runOff >= len)
                    throw new InvalidDataException("MFT run offset outside attribute");
                return DecodeDataRuns(record.AsSpan(off + runOff, (int)len - runOff));
            }
            throw new InvalidDataException("MFT has no non-resident $DATA");
        }

        private static List<(int Offset, uint Length)> AttributeSpans(byte[] record)
        {
            var firstAttr = BitConverter.ToUInt16(record, 0x14);
            var bytesInUse = BitConverter.ToUInt32(record, 0x18);
            var end = Math.Min((int)bytesInUse, record.Length);
            if (firstAttr < 42 || firstAttr >= end)
                throw new InvalidDataException("MFT first-attribute offset invalid");
            var spans = new List<(int Offset, uint Length)>();
            var off = (int)firstAttr;
            while (true)
            {
                if (off + 8 > end)
                    throw new InvalidDataException("MFT attribute header truncated");
                var type = BitConverter.ToUInt32(record, off);
                if (type == 0xFFFFFFFF)
                    break;
                var len = BitConverter.ToUInt32(record, off + 4);
                if (len < 8 || off + len > end)
                    throw new InvalidDataException("MFT attribute truncated");
                spans.Add((off, len));
                off += (int)len;
            }
            return spans;
        }

        // NTFS data-run varint decode: [lenBytes:offBytes][length][signed
        // LCN delta]... 0x00-terminated. Pure for Linux tests.
        internal static List<(long StartLcn, long ClusterCount)> DecodeDataRuns(ReadOnlySpan<byte> runs)
        {
            var extents = new List<(long StartLcn, long ClusterCount)>();
            var lcn = 0L;
            var i = 0;
            while (i < runs.Length)
            {
                var header = runs[i++];
                if (header == 0)
                    break;
                var lenBytes = header & 0x0F;
                var offBytes = (header >> 4) & 0x0F;
                if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || i + lenBytes + offBytes > runs.Length)
                    throw new InvalidDataException("MFT data run truncated");
                var count = 0L;
                for (var b = 0; b < lenBytes; b++)
                    count |= (long)runs[i + b] << (8 * b);
                i += lenBytes;
                if (offBytes == 0)
                    continue; // sparse run: no clusters, nothing to dirty
                var delta = 0L;
                for (var b = 0; b < offBytes; b++)
                    delta |= (long)runs[i + b] << (8 * b);
                var sign = runs[i + offBytes - 1];
                i += offBytes;
                if (offBytes < 8 && (sign & 0x80) != 0)
                    delta |= -1L << (8 * offBytes);
                lcn += delta;
                if (count <= 0 || lcn < 0)
                    throw new InvalidDataException("MFT data run invalid");
                extents.Add((lcn, count));
            }
            return extents;
        }

        // $MFT byte offset → volume LCN through the MFT's own runs (the MFT
        // itself fragments; MftStartLcn alone would mislocalize). Pure.
        internal static long? MapMftOffsetToLcn(long mftOffset, int bytesPerCluster, List<(long StartLcn, long ClusterCount)> mftRuns)
        {
            var vcn = mftOffset / bytesPerCluster;
            foreach (var (start, count) in mftRuns)
            {
                if (vcn < count)
                    return start + vcn;
                vcn -= count;
            }
            return null;
        }
    }
}
