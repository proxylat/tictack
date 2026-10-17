using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DiscUtils.Vhdx;
using Microsoft.Win32.SafeHandles;
using TicTack;

namespace Corpus
{
    // §12.5 clone seam: production BlockCloner issues the real ReFS IOCTL,
    // tests inject a recording fake (Linux has no block-clone API).
    public interface IFileCloner
    {
        bool TryClone(string sourcePath, string destPath, out string reason);
    }

    // Whole-file block clone via FSCTL_DUPLICATE_EXTENTS_TO_FILE (ReFS-only,
    // same-volume). False-never-throws: any FS/layout/IOCTL failure returns
    // false with a WRN-ready reason and the caller falls back to plain copy
    // (a garbage path then fails loud in the fallback copy, not here).
    public sealed class BlockCloner : IFileCloner
    {
        // CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 176, METHOD_BUFFERED, FILE_WRITE_DATA).
        private const uint FsctlDuplicateExtentsToFile = 0x982C0;
        // Spec: <4 GB pieces; 1 GiB is a multiple of every cluster size ≤1 MiB.
        private const long MaxBytesPerCall = 1L << 30;

        [StructLayout(LayoutKind.Sequential)]
        private struct DuplicateExtentsData
        {
            public IntPtr SourceFileHandle;
            public long SourceFileOffset;
            public long TargetFileOffset;
            public long ByteCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, ref DuplicateExtentsData lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetVolumePathNameW(string lpszFileName, StringBuilder lpszVolumePathName, int cchBufferLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetVolumeInformationW(string lpRootPathName, StringBuilder? lpVolumeNameBuffer, int nVolumeNameSize, out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags, StringBuilder lpFileSystemNameBuffer, int nFileSystemNameSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceW(string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector, out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);

        public bool TryClone(string sourcePath, string destPath, out string reason)
        {
            ArgumentNullException.ThrowIfNull(sourcePath);
            ArgumentNullException.ThrowIfNull(destPath);
            try
            {
                return TryCloneCore(sourcePath, destPath, out reason);
            }
            catch (Exception ex)
            {
                reason = "unexpected clone error (" + ex.GetType().Name + ": " + ex.Message + ")";
                return false;
            }
        }

        private static bool TryCloneCore(string sourcePath, string destPath, out string reason)
        {
            if (!OperatingSystem.IsWindows())
            {
                reason = "host OS is not Windows (block clone is a Windows ReFS API)";
                return false;
            }
            var srcFull = Path.GetFullPath(sourcePath);
            var dstFull = Path.GetFullPath(destPath);
            if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
            {
                reason = "source and destination are the same file";
                return false;
            }
            var srcVol = VolumeOf(srcFull);
            var dstVol = VolumeOf(dstFull);
            if (!string.Equals(srcVol, dstVol, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"cross-volume clone ({srcVol} -> {dstVol}; block clone needs one volume)";
                return false;
            }
            var fs = FilesystemOf(srcVol);
            if (!string.Equals(fs, "ReFS", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"filesystem is {fs}, not ReFS (block clone is ReFS-only)";
                return false;
            }
            var cluster = ClusterBytes(srcVol);
            var length = new FileInfo(srcFull).Length;
            using var src = new FileStream(srcFull, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = new FileStream(dstFull, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            dst.SetLength(length); // API contract: extend BEFORE cloning.
            var aligned = length - (length % cluster);
            // The IOCTL takes a raw HANDLE: pin it across the loop (a bare
            // DangerousGetHandle could race finalization between calls).
            var srcHandle = src.SafeFileHandle;
            var addedRef = false;
            try
            {
                srcHandle.DangerousAddRef(ref addedRef);
                var srcPtr = srcHandle.DangerousGetHandle();
                for (var off = 0L; off < aligned;)
                {
                    var piece = Math.Min(MaxBytesPerCall, aligned - off);
                    piece -= piece % cluster;
                    if (piece <= 0)
                        throw new IOException($"cluster size {cluster} exceeds clone piece size (volume {srcVol})");
                    var data = new DuplicateExtentsData
                    {
                        SourceFileHandle = srcPtr,
                        SourceFileOffset = off,
                        TargetFileOffset = off,
                        ByteCount = piece,
                    };
                    if (!DeviceIoControl(dst.SafeFileHandle, FsctlDuplicateExtentsToFile, ref data, Marshal.SizeOf<DuplicateExtentsData>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
                    {
                        reason = $"FSCTL_DUPLICATE_EXTENTS_TO_FILE failed at offset {off} (win32 {Marshal.GetLastWin32Error()})";
                        return false;
                    }
                    off += piece;
                }
            }
            finally
            {
                if (addedRef)
                    srcHandle.DangerousRelease();
            }
            if (aligned < length)
            {
                // Sub-cluster tail: plain copy (at most cluster-1 bytes).
                src.Seek(aligned, SeekOrigin.Begin);
                dst.Seek(aligned, SeekOrigin.Begin);
                var tail = new byte[length - aligned];
                var filled = 0;
                int n;
                while (filled < tail.Length && (n = src.Read(tail, filled, tail.Length - filled)) > 0)
                    filled += n;
                if (filled != tail.Length)
                    throw new IOException($"short tail read cloning '{srcFull}' ({filled} of {tail.Length})");
                dst.Write(tail, 0, tail.Length);
            }
            dst.Flush();
            reason = "";
            return true;
        }

        private static string VolumeOf(string fullPath)
        {
            var sb = new StringBuilder(512);
            if (!GetVolumePathNameW(fullPath, sb, sb.Capacity))
                throw new IOException($"GetVolumePathNameW({fullPath}) failed: {Marshal.GetLastWin32Error()}");
            return sb.ToString();
        }

        private static string FilesystemOf(string volumeRoot)
        {
            var fs = new StringBuilder(32);
            if (!GetVolumeInformationW(volumeRoot, null, 0, out _, out _, out _, fs, fs.Capacity))
                throw new IOException($"GetVolumeInformationW({volumeRoot}) failed: {Marshal.GetLastWin32Error()}");
            return fs.ToString();
        }

        private static long ClusterBytes(string volumeRoot)
        {
            if (!GetDiskFreeSpaceW(volumeRoot, out var spc, out var bps, out _, out _))
                throw new IOException($"GetDiskFreeSpaceW({volumeRoot}) failed: {Marshal.GetLastWin32Error()}");
            if (spc == 0 || bps == 0)
                throw new IOException($"GetDiskFreeSpaceW({volumeRoot}) returned zero cluster geometry");
            return (long)spc * bps;
        }
    }

    // §12.5 export integration: `--export-vhdx --snapshot N --out new.vhdx
    // --clone-from old.vhdx` — clone the previous export (metadata op),
    // overwrite dirty extents only, verify every data byte. Base gen is N-1
    // per spec (diff manifests N-1 vs N); a wrong-base file fails the
    // verify loud instead of corrupting silently. Called INSTEAD OF
    // Store.ExportVhdx when --clone-from is present.
    public static class ReflinkExport
    {
        public static void ExportVhdxFromClone(Store store, int generation, string outPath, string cloneFromPath, ILogger log, IFileCloner? cloner = null)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(outPath);
            ArgumentNullException.ThrowIfNull(cloneFromPath);
            ArgumentNullException.ThrowIfNull(log);
            if (!File.Exists(cloneFromPath))
                throw new FileNotFoundException("clone-from VHDX not found (explicit path, no guessing): " + cloneFromPath, cloneFromPath);
            var target = store.GetManifest(generation);
            if (generation <= 1)
                throw new InvalidDataException($"--clone-from needs a base gen: snapshot {generation} has no gen {generation - 1} (export it without --clone-from)");
            SnapshotManifest baseManifest;
            try
            {
                baseManifest = store.GetManifest(generation - 1);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"--clone-from needs base gen {generation - 1} for snapshot {generation}: {ex.Message}", ex);
            }
            if (baseManifest.VolumeLength != target.VolumeLength || baseManifest.ChunkMib != target.ChunkMib || baseManifest.Chunks.Count != target.Chunks.Count)
                throw new InvalidDataException($"--clone-from base gen {baseManifest.Generation} unusable: volume/chunk grid differs from gen {generation} (length {baseManifest.VolumeLength} vs {target.VolumeLength}, chunks {baseManifest.Chunks.Count} vs {target.Chunks.Count}) — export without --clone-from");
            var capacity = VhdxExporter.AlignedCapacity(target.VolumeLength);
            try
            {
                using var probe = new Disk(cloneFromPath, FileAccess.Read);
                if (probe.Capacity != capacity)
                    throw new InvalidDataException($"--clone-from file '{cloneFromPath}' capacity {probe.Capacity} != snapshot {generation} VHDX capacity {capacity} (wrong base export?)");
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"--clone-from file '{cloneFromPath}' is not a readable VHDX: {ex.Message}", ex);
            }
            if ((cloner ?? new BlockCloner()).TryClone(cloneFromPath, outPath, out var cloneReason))
            {
                log.Info($"cloned '{cloneFromPath}' -> '{outPath}' (block clone, zero bytes copied)");
            }
            else
            {
                log.Warn($"block clone unavailable ({cloneReason}); plain copy fallback for '{cloneFromPath}'");
                File.Copy(cloneFromPath, outPath, true);
            }
            var dirty = DiffDirtyChunks(baseManifest, target);
            string? verifyError;
            using (var disk = new Disk(outPath, FileAccess.ReadWrite))
            {
                using (var filter = new DirtyRangeWriteFilter(disk.Content, target, dirty))
                {
                    store.ExportToStream(generation, filter);
                    log.Info($"reflink export: {filter.WrittenBytes} dirty bytes written, {filter.SkippedBytes} clean bytes left COW-shared ({dirty.Count} dirty chunks)");
                }
                using (var verifier = new ContentVerifyStream(disk.Content))
                {
                    store.ExportToStream(generation, verifier);
                    if (verifier.GapBytes > 0)
                        log.Info($"{verifier.GapBytes} gap bytes left as cloned (don't-care free space)");
                    verifyError = verifier.FailureMessage(outPath, generation);
                }
            }
            if (verifyError != null)
            {
                // Never leave a corrupt VHDX masquerading as gen N (dest is
                // ours — ExportVhdx overwrites it too).
                try
                {
                    File.Delete(outPath);
                }
                catch (Exception ex)
                {
                    log.Warn($"could not delete failed reflink export '{outPath}': {ex.Message}");
                }
                throw new InvalidDataException(verifyError);
            }
            log.Info($"exported snapshot {generation} ({target.Chunks.Count} chunks) to VHDX {outPath} via clone-from gen {baseManifest.Generation}");
        }

        internal static List<int> DiffDirtyChunks(SnapshotManifest baseManifest, SnapshotManifest target)
        {
            ArgumentNullException.ThrowIfNull(baseManifest);
            ArgumentNullException.ThrowIfNull(target);
            if (baseManifest.Chunks.Count != target.Chunks.Count)
                throw new InvalidDataException($"cannot diff gen {baseManifest.Generation} vs {target.Generation}: chunk counts differ ({baseManifest.Chunks.Count} vs {target.Chunks.Count})");
            var dirty = new List<int>();
            for (var i = 0; i < target.Chunks.Count; i++)
            {
                var b = baseManifest.Chunks[i];
                var t = target.Chunks[i];
                if (b.IsHole != t.IsHole || b.Size != t.Size || !string.Equals(b.DigestHex, t.DigestHex, StringComparison.Ordinal))
                    dirty.Add(i);
            }
            return dirty;
        }
    }

    // Write filter over VHDX content: dirty ranges pass through, clean
    // writes are dropped (position still advances) so cloned extents stay
    // COW-shared. Ranges sorted; sequential ExportToStream writes make the
    // cursor O(writes + ranges), never O(writes x ranges). Never disposes
    // the inner stream (owned by the Disk).
    internal sealed class DirtyRangeWriteFilter : Stream
    {
        private readonly Stream _inner;
        private readonly List<(long Start, long End)> _ranges;
        private int _cursor;
        private long _pos;

        public long WrittenBytes { get; private set; }
        public long SkippedBytes { get; private set; }

        public DirtyRangeWriteFilter(Stream inner, SnapshotManifest manifest, IReadOnlyList<int> dirtyChunks)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(dirtyChunks);
            _inner = inner;
            var sorted = new List<int>(dirtyChunks);
            sorted.Sort();
            if (sorted.Count > 0 && (sorted[0] < 0 || sorted[sorted.Count - 1] >= manifest.Chunks.Count))
                throw new ArgumentOutOfRangeException(nameof(dirtyChunks), "dirty chunk index outside the manifest grid");
            _ranges = new List<(long, long)>(sorted.Count);
            var off = 0L;
            var d = 0;
            for (var i = 0; i < manifest.Chunks.Count && d < sorted.Count; i++)
            {
                var size = manifest.Chunks[i].Size;
                if (sorted[d] == i)
                {
                    _ranges.Add((off, off + size));
                    d++;
                }
                off += size;
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _pos;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetLength(long value) => _inner.SetLength(value);

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0)
                throw new IOException("seek before begin");
            if (target < _pos)
                _cursor = 0; // backward seek invalidates the forward cursor
            _pos = target;
            _inner.Seek(_pos, SeekOrigin.Begin);
            return _pos;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, buffer.Length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length - offset);
            if (count == 0)
                return;
            var end = _pos + count;
            while (_cursor < _ranges.Count && _ranges[_cursor].End <= _pos)
                _cursor++;
            var written = 0L;
            for (var i = _cursor; i < _ranges.Count && _ranges[i].Start < end; i++)
            {
                var s = Math.Max(_ranges[i].Start, _pos);
                var e = Math.Min(_ranges[i].End, end);
                if (s >= e)
                    continue;
                _inner.Seek(s, SeekOrigin.Begin);
                _inner.Write(buffer, (int)(offset + s - _pos), (int)(e - s));
                written += e - s;
            }
            WrittenBytes += written;
            SkippedBytes += count - written;
            _pos = end;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Flush();
            base.Dispose(disposing);
        }
    }

    // Compare stream for the reflink verify pass: every written byte is a
    // data byte (ExportToStream seeks gaps) — read the VHDX bytes at the
    // same offset and fail loud on any difference. This checks clean
    // (cloned) ranges as well as dirty ones, so a wrong base file or a
    // corrupt clone fails here, never silently. Gaps are unchecked by
    // design (cloned bytes are don't-care there) and only counted.
    internal sealed class ContentVerifyStream : Stream
    {
        private readonly Stream _inner;
        private readonly List<long> _badOffsets = new();
        private long _pos;
        private long _badBytes;
        private long _checkedBytes;

        public long GapBytes { get; private set; }

        public ContentVerifyStream(Stream inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _pos;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException("verify stream is write-compare only");

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0)
                throw new IOException("seek before begin");
            if (target > _pos)
                GapBytes += target - _pos;
            _pos = target;
            return _pos;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, buffer.Length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length - offset);
            if (count == 0)
                return;
            _inner.Seek(_pos, SeekOrigin.Begin);
            var rented = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                var filled = 0;
                int n;
                while (filled < count && (n = _inner.Read(rented, filled, count - filled)) > 0)
                    filled += n;
                var badHere = count - filled; // short read: missing bytes mismatch too
                for (var i = 0; i < filled; i++)
                {
                    if (rented[i] != buffer[offset + i])
                        badHere++;
                }
                if (badHere > 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        if (i >= filled || rented[i] != buffer[offset + i])
                        {
                            if (_badOffsets.Count < 8)
                                _badOffsets.Add(_pos + i);
                            break;
                        }
                    }
                    _badBytes += badHere;
                }
                _checkedBytes += count;
                _pos += count;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public string? FailureMessage(string vhdxPath, int generation) =>
            _badBytes == 0 ? null : $"snapshot {generation} reflink-verify FAILED: {_badBytes} of {_checkedBytes} data bytes differ in {vhdxPath} (first bad offset {_badOffsets[0]}; base VHDX or clone diverged from gen {generation})";
    }
}
