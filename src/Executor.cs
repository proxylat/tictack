using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    internal enum CopyCheckpoint
    {
        TempCreated,
        DataFlushed,
        BeforeCommit,
        AfterCommit
    }

    public enum FileDurability
    {
        Full,
        FDataSync,
        RenameOnly
    }

    // Amortizes destination-directory fsyncs over a batch (dir_sync=per-batch):
    // per-file copies record their parent dir, one FlushAll per checkpoint
    // fsyncs each distinct dir once. State upserts must follow FlushAll,
    // never precede it — a crash then re-copies (safe) instead of diverging.
    // Ordinal (never IgnoreCase): two dirs differing only by case on Linux
    // are different dirs; merging them would skip a real fsync.
    public sealed class DirSyncBatcher
    {
        private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public void Record(string? dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            lock (_gate) _dirs.Add(dir);
        }

        public bool FlushAll(ILogger? log)
        {
            string[] snapshot;
            lock (_gate)
            {
                if (_dirs.Count == 0) return true;
                snapshot = new string[_dirs.Count];
                _dirs.CopyTo(snapshot);
                _dirs.Clear();
            }
            foreach (var d in snapshot)
            {
                if (CopyAction.FlushDirectory(d)) continue;
                log?.Warn("Directory fsync failed: " + d + CopyAction.DirSyncDetail());
                lock (_gate)
                {
                    foreach (var rest in snapshot) _dirs.Add(rest);
                }
                return false;
            }
            return true;
        }
    }

    // Copy-phase output for the pipelined completer (complete_mode=pipelined):
    // everything the complete phase needs after the streams are closed.
    // Internal: same-assembly only (SyncPipeline + FileCompleter).
    internal sealed class TempCopyResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public bool Retryable { get; set; }
        public string Dst { get; set; } = "";
        public string Tmp { get; set; } = "";
        public string? SourceHash { get; set; }
        public long BytesCopied { get; set; }
        public double CopyMs { get; set; } = -1;

        public static TempCopyResult Fail(string message, bool retryable = false) =>
            new TempCopyResult { Success = false, ErrorMessage = message, Retryable = retryable };
    }

    public sealed class CopyAction : IFileAction
    {
        private readonly IFileAccessor _accessor;
        private readonly FileDurability _durability;
        private readonly Action<CopyCheckpoint>? _checkpoint;
        private readonly ILogger? _log;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        static extern int OpenDirectory(string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        static extern int Fsync(int fd);

        [DllImport("libc", EntryPoint = "fdatasync", SetLastError = true)]
        static extern int Fdatasync(int fd);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        static extern int Close(int fd);

        const int O_RDONLY = 0;
        const int O_DIRECTORY = 0x10000;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        const uint FSCTL_SET_SPARSE = 0x000900C4;
        const uint FSCTL_QUERY_ALLOCATED_RANGES = 0x000940CF;
        const int ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        struct FILE_ALLOCATED_RANGE_BUFFER
        {
            public long FileOffset;
            public long Length;
        }

        // Directory fsync on Windows: open the directory with backup
        // semantics and flush it, so the rename entry itself is durable.
        // (Rename is only atomic w.r.t. normal operation, not w.r.t. crash
        // — without this, a power loss can lose the rename on NTFS too.)
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateDirectoryHandle(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FlushFileBuffers(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        const uint WIN_GENERIC_READ = 0x80000000;
        const uint WIN_GENERIC_WRITE = 0x40000000;
        const uint WIN_SHARE_ALL = 0x7;
        const uint WIN_OPEN_EXISTING = 3;
        const uint WIN_FLAG_BACKUP_SEMANTICS = 0x02000000;

        public CopyAction(IFileAccessor accessor, bool fullDurability = true, ILogger? log = null)
            : this(accessor, fullDurability ? FileDurability.Full : FileDurability.RenameOnly, null, log)
        {
        }

        public CopyAction(IFileAccessor accessor, FileDurability durability, ILogger? log = null)
            : this(accessor, durability, null, log)
        {
        }

        internal CopyAction(IFileAccessor accessor, bool fullDurability, Action<CopyCheckpoint>? checkpoint, ILogger? log = null)
            : this(accessor, fullDurability ? FileDurability.Full : FileDurability.RenameOnly, checkpoint, log)
        {
        }

        internal CopyAction(IFileAccessor accessor, FileDurability durability, Action<CopyCheckpoint>? checkpoint, ILogger? log = null)
        {
            _accessor = accessor;
            if (durability == FileDurability.FDataSync && !OperatingSystem.IsLinux())
            {
                log?.Debug("fdatasync is Linux-only, using full durability on " + Environment.OSVersion.Platform);
                durability = FileDurability.Full;
            }
            _durability = durability;
            _checkpoint = checkpoint;
            _log = log;
        }

        // When set (dir_sync=per-batch), per-file directory fsyncs are
        // skipped; the owner must call FlushAll before upserting state.
        // Null (default) keeps the per-file fsync. Wired by SyncPipeline
        // for InitialSync only — the trickle event path stays per-file.
        public DirSyncBatcher? DirBatch { get; set; }

        // Off by default. When on, the copy loop feeds an IncrementalHash
        // and the returned ActionResult carries the source digest, so a hash
        // validator can skip re-reading the source. SyncPipeline enables it
        // only when the validator is a HashValidator.
        public bool ComputeSourceHash { get; set; }
        public long MinFreeBytes { get; set; }
        public long ResumeThresholdBytes { get; set; } = 16L * 1024 * 1024;
        internal Func<string, long>? FreeSpaceOverride { get; set; }

        public async Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
        {
            // Inline mode: copy then complete on this thread — same order,
            // checkpoints, and errors as the split phases below.
            var tmp = await CopyToTempAsync(args, ct);
            if (!tmp.Success)
                return ActionResult.Fail(tmp.ErrorMessage ?? "Copy failed", tmp.Retryable);
            return CompleteTemp(tmp, ct);
        }

        // Copy phase: probe, stream bytes to .tictack.tmp, stamp the source
        // mtime. Claims no durability: no flush, rename, or dir sync here.
        // Fires TempCreated only; the complete phase fires the rest in order.
        internal async Task<TempCopyResult> CopyToTempAsync(FileActionArgs args, CancellationToken ct)
        {
            try
            {
                var src = args.ChangeEvent.FullPath;
                var dst = PathUtil.EnsureExtended(args.DestPath);
                var tmp = dst + ".tictack.tmp";

                var dir = PathUtil.EnsureExtended(Path.GetDirectoryName(dst) ?? "");
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (MinFreeBytes > 0
                    && !DiskSpaceGuard.HasHeadroom(args.DestPath, args.SourceSnapshot?.Length ?? 0, MinFreeBytes, out var avail, FreeSpaceOverride))
                {
                    return TempCopyResult.Fail(
                        $"Destination low on free space ({avail / (1024 * 1024)} MiB available, need headroom for min_free_gb).",
                        retryable: false);
                }

                using (var probe = _accessor.OpenRead(src))
                {
                    probe.ReadByte();
                }


                // Snapshot-first: the caller usually statted the source already,
                // so this costs zero syscalls; the re-read fallback replaces
                // the old standalone GetAttributes call 1:1 on the cold path.
                var srcAttributes = args.SourceSnapshot?.Attributes;
                if (!srcAttributes.HasValue && FileSnapshot.TryRead(src, out var currentSnap))
                    srcAttributes = currentSnap.Attributes;
                var isSparse = (srcAttributes.GetValueOrDefault() & FileAttributes.SparseFile) == FileAttributes.SparseFile;

                var (bytesCopied, copyMs, sourceHash) = await StreamToTempAsync(src, tmp, isSparse, args.SourceSnapshot, ct);
                StampTempMtime(tmp, src, args, dst);
                return new TempCopyResult
                {
                    Success = true,
                    Dst = dst,
                    Tmp = tmp,
                    SourceHash = sourceHash,
                    BytesCopied = bytesCopied,
                    CopyMs = copyMs
                };
            }
            catch (UnauthorizedAccessException ex) { return TempCopyResult.Fail(ex.Message); }
            catch (DirectoryNotFoundException ex) { return TempCopyResult.Fail(ex.Message); }
            catch (PathTooLongException ex) { return TempCopyResult.Fail(ex.Message); }
            catch (NotSupportedException ex) { return TempCopyResult.Fail(ex.Message); }
            // Retryable: sharing violations and transient IO. Disk-full (ENOSPC /
            // ERROR_DISK_FULL) is never retryable so a full drive fails fast.
            catch (IOException ex)
            {
                if (DiskSpaceGuard.IsDiskFull(ex))
                {
                    try { var t = PathUtil.EnsureExtended(args.DestPath) + ".tictack.tmp"; if (File.Exists(t)) File.Delete(t); } catch { }
                    return TempCopyResult.Fail("Destination disk full: " + ex.Message, retryable: false);
                }
                return TempCopyResult.Fail(ex.Message, retryable: true);
            }
            catch (Win32Exception ex) { return TempCopyResult.Fail(ex.Message, retryable: true); }
        }
        // Byte pump: sparse flag or preallocate, then hashed or plain
        // copy. Returns what the temp actually holds (bytes, hash, ms).
        private async Task<(long BytesCopied, double CopyMs, string? SourceHash)> StreamToTempAsync(string src, string tmp, bool isSparse, FileSnapshot? snap, CancellationToken ct)
        {
            long bytesCopied = 0;
            double copyMs = -1;
            string? sourceHash = null;
            bool resumed = TryPrepareResume(src, tmp, snap, out long resumeOffset);

            using (var srcStream = _accessor.OpenRead(src))
            using (var dstStream = resumed
                ? new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                : File.Create(tmp))
            {
                _checkpoint?.Invoke(CopyCheckpoint.TempCreated);
                var timed = TicTackEventSource.Log.IsEnabled();
                var sw = timed ? Stopwatch.StartNew() : null;
                if (resumed)
                {
                    srcStream.Seek(resumeOffset, SeekOrigin.Begin);
                    dstStream.Seek(resumeOffset, SeekOrigin.Begin);
                }
                else if (isSparse)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        DeviceIoControl(dstStream.SafeFileHandle.DangerousGetHandle(),
                            FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    }
                }
                else
                {
                    dstStream.SetLength(srcStream.Length);
                }

                try
                {
                    if (isSparse && !resumed)
                        sourceHash = await CopySparseAsync(srcStream, dstStream, ComputeSourceHash, ct).ConfigureAwait(false);
                    else if (ComputeSourceHash && !resumed)
                        sourceHash = await CopyHashedAsync(srcStream, dstStream, ct).ConfigureAwait(false);
                    else
                        await srcStream.CopyToAsync(dstStream, 81920, ct);
                }
                catch (Exception ex) when (snap.HasValue && snap.Value.Length >= ResumeThresholdBytes && dstStream.Position > 0 && !DiskSpaceGuard.IsDiskFull(ex))
                {
                    SaveResumeSidecar(dstStream, tmp, snap.Value);
                    throw;
                }
                bytesCopied = dstStream.Position;
                if (sw != null) copyMs = sw.Elapsed.TotalMilliseconds;
            }
            return (bytesCopied, copyMs, sourceHash);
        }

        private static async Task<string> CopyHashedAsync(Stream srcStream, FileStream dstStream, CancellationToken ct)
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(FileHasher.BufferSize);
            try
            {
                int read;
                while ((read = await srcStream.ReadAsync(buffer, 0, FileHasher.BufferSize, ct).ConfigureAwait(false)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await dstStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            return Convert.ToHexStringLower(hasher.GetHashAndReset());
        }

        private static async Task<string?> CopySparseAsync(Stream srcStream, FileStream dstStream, bool computeHash, CancellationToken ct)
        {
            if (OperatingSystem.IsWindows() && srcStream is FileStream srcFs
                && TryGetAllocatedRanges(srcFs.SafeFileHandle, srcStream.Length, out var ranges))
            {
                return await CopyAllocatedRangesAsync(srcStream, dstStream, ranges, computeHash, ct).ConfigureAwait(false);
            }
            using var hasher = computeHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            var buffer = ArrayPool<byte>.Shared.Rent(FileHasher.BufferSize);
            try
            {
                int read;
                while ((read = await srcStream.ReadAsync(buffer, 0, FileHasher.BufferSize, ct).ConfigureAwait(false)) > 0)
                {
                    hasher?.AppendData(buffer, 0, read);
                    if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) < 0)
                        dstStream.Seek(read, SeekOrigin.Current);
                    else
                        await dstStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                }
                dstStream.SetLength(srcStream.Length);
                dstStream.Seek(srcStream.Length, SeekOrigin.Begin);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            return hasher != null ? Convert.ToHexStringLower(hasher.GetHashAndReset()) : null;
        }

        private static async Task<string?> CopyAllocatedRangesAsync(
            Stream srcStream, FileStream dstStream, List<FILE_ALLOCATED_RANGE_BUFFER> ranges, bool computeHash, CancellationToken ct)
        {
            using var hasher = computeHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            var buffer = ArrayPool<byte>.Shared.Rent(FileHasher.BufferSize);
            try
            {
                long cursor = 0;
                foreach (var range in ranges)
                {
                    if (range.FileOffset > cursor && hasher != null)
                        HashZeros(hasher, buffer, range.FileOffset - cursor);
                    srcStream.Seek(range.FileOffset, SeekOrigin.Begin);
                    dstStream.Seek(range.FileOffset, SeekOrigin.Begin);
                    long rem = range.Length;
                    while (rem > 0)
                    {
                        var take = (int)Math.Min(FileHasher.BufferSize, rem);
                        var read = await srcStream.ReadAsync(buffer, 0, take, ct).ConfigureAwait(false);
                        if (read == 0) break;
                        hasher?.AppendData(buffer, 0, read);
                        await dstStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        rem -= read;
                    }
                    cursor = range.FileOffset + range.Length;
                }
                if (srcStream.Length > cursor && hasher != null)
                    HashZeros(hasher, buffer, srcStream.Length - cursor);
                dstStream.SetLength(srcStream.Length);
                dstStream.Seek(srcStream.Length, SeekOrigin.Begin);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            return hasher != null ? Convert.ToHexStringLower(hasher.GetHashAndReset()) : null;
        }

        private static void HashZeros(IncrementalHash hasher, byte[] buffer, long count)
        {
            Array.Clear(buffer, 0, Math.Min(buffer.Length, (int)Math.Min(count, buffer.Length)));
            while (count > 0)
            {
                var step = (int)Math.Min(buffer.Length, count);
                hasher.AppendData(buffer, 0, step);
                count -= step;
            }
        }

        private static unsafe bool TryGetAllocatedRanges(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, long fileLength, out List<FILE_ALLOCATED_RANGE_BUFFER> ranges)
        {
            ranges = new List<FILE_ALLOCATED_RANGE_BUFFER>();
            if (fileLength <= 0) return true;
            var outBuf = new FILE_ALLOCATED_RANGE_BUFFER[256];
            long offset = 0;
            while (offset < fileLength)
            {
                var query = new FILE_ALLOCATED_RANGE_BUFFER { FileOffset = offset, Length = fileLength - offset };
                fixed (FILE_ALLOCATED_RANGE_BUFFER* pOut = outBuf)
                {
                    bool ok = DeviceIoControl(
                        handle.DangerousGetHandle(),
                        FSCTL_QUERY_ALLOCATED_RANGES,
                        new IntPtr(&query),
                        (uint)sizeof(FILE_ALLOCATED_RANGE_BUFFER),
                        new IntPtr(pOut),
                        (uint)(outBuf.Length * sizeof(FILE_ALLOCATED_RANGE_BUFFER)),
                        out uint bytesReturned,
                        IntPtr.Zero);
                    int err = ok ? 0 : Marshal.GetLastWin32Error();
                    if (!ok && err != ERROR_MORE_DATA)
                        return false;
                    int count = (int)(bytesReturned / (uint)sizeof(FILE_ALLOCATED_RANGE_BUFFER));
                    for (int i = 0; i < count; i++)
                        ranges.Add(outBuf[i]);
                    if (ok || count == 0)
                        break;
                    var last = outBuf[count - 1];
                    offset = last.FileOffset + last.Length;
                }
            }
            return true;
        }

        private static void SaveResumeSidecar(FileStream dstStream, string tmp, FileSnapshot snap)
        {
            try
            {
                dstStream.Flush(true);
                File.WriteAllText(tmp + ".resume", $"{snap.Length}|{snap.LastWriteTimeUtcTicks}|{dstStream.Position}");
            }
            catch { }
        }

        private bool TryPrepareResume(string src, string tmp, FileSnapshot? snap, out long resumeOffset)
        {
            resumeOffset = 0;
            if (!snap.HasValue || snap.Value.Length < ResumeThresholdBytes) return false;
            var resumePath = tmp + ".resume";
            if (!File.Exists(tmp) || !File.Exists(resumePath)) return false;

            try
            {
                var parts = File.ReadAllText(resumePath).Trim().Split('|');
                if (parts.Length != 3) return false;
                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long len)
                    || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long mtime)
                    || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long written))
                    return false;
                if (len != snap.Value.Length || mtime != snap.Value.LastWriteTimeUtcTicks) return false;
                if (written <= 0 || written >= len || new FileInfo(tmp).Length < written) return false;

                var localPrefix = FileHasher.ComputeHexPrefix(tmp, written, null);
                string? remotePrefix = _accessor is IPrefixHashProvider php
                    ? (php.TryComputePrefixHash(src, written, out var h) ? h : null)
                    : FileHasher.ComputeHexPrefix(src, written, _accessor);
                if (localPrefix == null || !string.Equals(localPrefix, remotePrefix, StringComparison.Ordinal))
                    return false;

                resumeOffset = written;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Best-effort mtime stamp; never fails the copy when it cannot.
        private void StampTempMtime(string tmp, string src, FileActionArgs args, string dst)
        {
            try
            {
                var sourceSnapshot = args.SourceSnapshot;
                if (!sourceSnapshot.HasValue && FileSnapshot.TryRead(src, out var current))
                    sourceSnapshot = current;
                if (sourceSnapshot.HasValue)
                    File.SetLastWriteTimeUtc(tmp, sourceSnapshot.Value.LastWriteTimeUtc);
            }
            catch (Exception ex)
            {
                // Without the source mtime the destination re-copies on
                // every date/size comparison; never fail the copy for it.
                _log?.Warn("Could not preserve source timestamp on " + dst + ": " + ex.Message);
            }
        }

        // Complete phase: flush, atomic rename, directory sync. Fires
        // DataFlushed, BeforeCommit, AfterCommit in the same order the inline
        // path always has (ExecutorTests pins it). The tmp is reopened — one
        // extra open per file — so no handle crosses the thread boundary to
        // the completer. Synchronous: every step inside is sync I/O.
        internal ActionResult CompleteTemp(TempCopyResult tmp, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var dst = tmp.Dst;
                var timed = TicTackEventSource.Log.IsEnabled();
                using (var s = File.Open(tmp.Tmp, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    var fsw = timed ? Stopwatch.StartNew() : null;
                    FlushData(s);
                    if (fsw != null) TicTackEventSource.Log.FsyncCompleted(fsw.Elapsed.TotalMilliseconds);
                    _checkpoint?.Invoke(CopyCheckpoint.DataFlushed);
                }

                _checkpoint?.Invoke(CopyCheckpoint.BeforeCommit);
                if (File.Exists(dst))
                {
                    File.SetAttributes(dst, FileAttributes.Normal);
                    File.Replace(tmp.Tmp, dst, null);
                }
                else
                {
                    File.Move(tmp.Tmp, dst);
                }
                _checkpoint?.Invoke(CopyCheckpoint.AfterCommit);
                try { var rp = tmp.Tmp + ".resume"; if (File.Exists(rp)) File.Delete(rp); } catch { }

                if (!SyncDirectory(dst))
                    return ActionResult.Fail("Could not fsync destination directory" + DirSyncDetail(), retryable: true);

                // Count only fully committed copies.
                if (tmp.CopyMs >= 0) TicTackEventSource.Log.CopyCompleted(tmp.CopyMs);
                TicTackEventSource.Log.FileCopied(tmp.BytesCopied);
                // The hash covers the bytes the copy loop wrote; rename and
                // dir sync don't touch content, so it stays valid here.
                return new ActionResult { Success = true, SourceHash = tmp.SourceHash, BytesCopied = tmp.BytesCopied };
            }
            catch (UnauthorizedAccessException ex) { return ActionResult.Fail(ex.Message); }
            catch (DirectoryNotFoundException ex) { return ActionResult.Fail(ex.Message); }
            catch (PathTooLongException ex) { return ActionResult.Fail(ex.Message); }
            catch (NotSupportedException ex) { return ActionResult.Fail(ex.Message); }
            catch (IOException ex) { return ActionResult.Fail(ex.Message, retryable: !DiskSpaceGuard.IsDiskFull(ex)); }
            catch (Win32Exception ex) { return ActionResult.Fail(ex.Message, retryable: true); }
        }

        private void FlushData(FileStream s)
        {
            switch (_durability)
            {
                // File data + size durable, mtime may roll back on crash
                // (safe direction: the next comparison re-copies). Linux-only;
                // the ctor already normalized other OSes to Full.
                case FileDurability.FDataSync:
                    var fd = (int)s.SafeFileHandle.DangerousGetHandle();
                    if (Fdatasync(fd) != 0)
                        throw new IOException("fdatasync failed, errno " + Marshal.GetLastPInvokeError());
                    break;
                case FileDurability.RenameOnly:
                    s.Flush();
                    break;
                default:
                    s.Flush(true);
                    break;
            }
        }

        private bool SyncDirectory(string dst)
        {
            var parent = Path.GetDirectoryName(dst);
            var batch = DirBatch;
            if (batch != null)
            {
                batch.Record(parent);
                return true;
            }
            return FlushDirectory(parent);
        }

        // Last dir-sync failure on THIS thread (diagnostic only): raw OS
        // error + step ("open"/"flush"), so a failing machine says WHY.
        // ThreadStatic because copy workers run in parallel; each thread
        // reads its own failure straight after its own failed call.
        [ThreadStatic]
        internal static int DirSyncError;
        [ThreadStatic]
        internal static string? DirSyncStep;

        // Renders the thread-local failure above for error messages and
        // logs. Empty when the last call succeeded (code stays 0).
        internal static string DirSyncDetail() =>
            DirSyncError != 0 ? " (os error " + DirSyncError + " at " + (DirSyncStep ?? "?") + ")" : "";

        internal static bool FlushDirectory(string? path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (OperatingSystem.IsLinux())
            {
                var fd = OpenDirectory(path, O_RDONLY | O_DIRECTORY);
                if (fd < 0) { DirSyncError = Marshal.GetLastSystemError(); DirSyncStep = "open"; return false; }
                try
                {
                    if (Fsync(fd) != 0) { DirSyncError = Marshal.GetLastSystemError(); DirSyncStep = "flush"; return false; }
                    return true;
                }
                finally { Close(fd); }
            }
            if (OperatingSystem.IsWindows())
            {
                // FlushFileBuffers on a directory handle fails with
                // ERROR_ACCESS_DENIED when the handle is read-only on
                // several stacks: open write-capable first (a dest dir we
                // just renamed into is writable by construction), fall
                // back to read-only for locked-down dirs.
                if (FlushDirectoryHandle(path, WIN_GENERIC_READ | WIN_GENERIC_WRITE)) return true;
                return FlushDirectoryHandle(path, WIN_GENERIC_READ);
            }
            return true;
        }

        private static bool FlushDirectoryHandle(string? path, uint access)
        {
            if (string.IsNullOrEmpty(path)) return true;
            var handle = CreateDirectoryHandle(PathUtil.EnsureExtended(path),
                access, WIN_SHARE_ALL, IntPtr.Zero,
                WIN_OPEN_EXISTING, WIN_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) { DirSyncError = Marshal.GetLastWin32Error(); DirSyncStep = "open"; return false; }
            try
            {
                if (!FlushFileBuffers(handle)) { DirSyncError = Marshal.GetLastWin32Error(); DirSyncStep = "flush"; return false; }
                DirSyncError = 0;
                return true;
            }
            finally { CloseHandle(handle); }
        }

    }

    public class RenameAction : IFileAction
    {
        private readonly IDeletionStrategy? _deletion;
        private readonly ILogger? _log;

        public RenameAction(IDeletionStrategy? deletion = null, ILogger? log = null)
        {
            _deletion = deletion;
            _log = log;
        }

        public async Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
        {
            if (args.OldDestPath == null)
                return ActionResult.Ok();

            var oldDest = PathUtil.EnsureExtended(args.OldDestPath);
            var dest = PathUtil.EnsureExtended(args.DestPath);

            try
            {
                var r = await MoveDirectoryTreeAsync(oldDest, dest, args, ct);
                if (r == null && File.Exists(oldDest))
                    r = await MoveFileAsync(oldDest, dest, args, ct);
                if (r != null) return r;
            }
            catch (IOException ex)
            {
                _log?.Error("Rename failed: " + args.OldDestPath + " -> " + args.DestPath + ": " + ex.Message);
                return ActionResult.Fail(ex.Message, retryable: true);
            }
            catch (Win32Exception ex)
            {
                _log?.Error("Rename failed: " + args.OldDestPath + " -> " + args.DestPath + ": " + ex.Message);
                return ActionResult.Fail(ex.Message, retryable: true);
            }
            catch (Exception ex)
            {
                _log?.Error("Rename failed: " + args.OldDestPath + " -> " + args.DestPath + ": " + ex.Message);
                return ActionResult.Fail(ex.Message);
            }
            return ActionResult.Ok();
        }
        // Directory rename: move the tree, or clear the collision through
        // the deletion strategy first. Never raw-deletes either side.
        private async Task<ActionResult?> MoveDirectoryTreeAsync(string oldDest, string dest, FileActionArgs args, CancellationToken ct)
        {
            if (Directory.Exists(oldDest))
            {
                if (!Directory.Exists(dest))
                {
                    Directory.Move(oldDest, dest);
                    SyncParents(oldDest, dest);
                }
                else
                {
                    // Collision: the old-name tree holds destination-only content.
                    // Never raw-delete it; route through the configured deletion
                    // strategy (archive or mirror) or refuse when none is wired.
                    if (_deletion == null)
                    {
                        var msg = "Rename target exists, old tree preserved: " + args.OldDestPath;
                        _log?.Warn(msg);
                        return ActionResult.Fail(msg);
                    }
                    var deleted = await _deletion.HandleDeletionAsync(null, args.OldDestPath!, ct); // null returns Ok() at method top
                    if (!deleted.Success)
                    {
                        _log?.Error("Rename cleanup failed: " + args.OldDestPath + ": " + deleted.ErrorMessage);
                        return deleted;
                    }
                    // The strategy consumed the old tree (mirror deletes, archive
                    // moves it aside); move only if something is left to move.
                    if (Directory.Exists(oldDest))
                    {
                        Directory.Move(oldDest, dest);
                        SyncParents(oldDest, dest);
                    }
                }
            }
            return null;
        }

        // File rename with the same collision rule as directories.
        private async Task<ActionResult?> MoveFileAsync(string oldDest, string dest, FileActionArgs args, CancellationToken ct)
        {
            if (File.Exists(oldDest))
            {
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(dest))
                {
                    if (_deletion == null)
                    {
                        var msg = "Rename target exists, old file preserved: " + args.OldDestPath;
                        _log?.Warn(msg);
                        return ActionResult.Fail(msg);
                    }
                    var deleted = await _deletion.HandleDeletionAsync(null, args.DestPath, ct);
                    if (!deleted.Success)
                    {
                        _log?.Error("Rename cleanup failed: " + args.DestPath + ": " + deleted.ErrorMessage);
                        return deleted;
                    }
                }
                File.Move(oldDest, dest);
                SyncParents(oldDest, dest);
            }
            return null;
        }

        // A directory fsync flushes every pending entry change in that
        // dir, so one sync per affected parent covers the whole
        // delete-then-move sequence above. Never fails the rename:
        // a lost entry resurrects and the next scan replays it.
        
        private void SyncParents(string oldPath, string newPath)
        {

            string? synced = null;
            foreach (var p in new[] { newPath, oldPath })
            {
                string? parent = null;
                try { parent = Path.GetDirectoryName(p); } catch { }
                if (string.IsNullOrEmpty(parent)) continue;
                if (parent.Equals(synced, StringComparison.Ordinal)) continue;
                synced = parent;
                if (!CopyAction.FlushDirectory(parent))
                    _log?.Debug("Rename directory sync failed: " + parent);
            }
        
        }
    }
}
