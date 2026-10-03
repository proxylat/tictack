using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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

    public class CopyAction : IFileAction
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

                using (var probe = _accessor.OpenRead(src))
                {
                    probe.ReadByte();
                }

                long bytesCopied = 0;
                double copyMs = -1;
                string? sourceHash = null;

                // Snapshot-first: the caller usually statted the source already,
                // so this costs zero syscalls; the re-read fallback replaces
                // the old standalone GetAttributes call 1:1 on the cold path.
                var srcAttributes = args.SourceSnapshot?.Attributes;
                if (!srcAttributes.HasValue && FileSnapshot.TryRead(src, out var currentSnap))
                    srcAttributes = currentSnap.Attributes;
                var isSparse = (srcAttributes.GetValueOrDefault() & FileAttributes.SparseFile) == FileAttributes.SparseFile;

                using (var srcStream = _accessor.OpenRead(src))
                using (var dstStream = File.Create(tmp))
                {
                    _checkpoint?.Invoke(CopyCheckpoint.TempCreated);
                    // Timed only while a counters listener is attached — otherwise
                    // this path stays allocation-free apart from the copy itself.
                    var timed = TicTackEventSource.Log.IsEnabled();
                    var sw = timed ? Stopwatch.StartNew() : null;
                    if (isSparse)
                    {
                        uint dummy;
                        DeviceIoControl(dstStream.SafeFileHandle.DangerousGetHandle(),
                            FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero);
                    }
                    else
                    {
                        dstStream.SetLength(srcStream.Length);
                    }
                    if (ComputeSourceHash)
                    {
                        // Hash the bytes as streamed: one pass serves both
                        // the copy and a hash validator. Same buffer size as
                        // CopyToAsync's default so throughput is unchanged.
                        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await srcStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                        {
                            hasher.AppendData(buffer, 0, read);
                            await dstStream.WriteAsync(buffer, 0, read, ct);
                        }
                        sourceHash = Convert.ToHexStringLower(hasher.GetHashAndReset());
                    }
                    else
                    {
                        await srcStream.CopyToAsync(dstStream, 81920, ct);
                    }
                    // Prefer the bytes actually written: a source appended
                    // mid-copy makes Length a lie.
                    bytesCopied = dstStream.Position;
                    if (sw != null) copyMs = sw.Elapsed.TotalMilliseconds;
                }
                // No flush here: durability is the complete phase's job.

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
            // Retryable: sharing violations and transient IO. Pipeline unwraps
            // these into ExponentialBackoffRetry.
            catch (IOException ex) { return TempCopyResult.Fail(ex.Message, retryable: true); }
            catch (Win32Exception ex) { return TempCopyResult.Fail(ex.Message, retryable: true); }
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

                if (!SyncDirectory(dst))
                    return ActionResult.Fail("Could not fsync destination directory" + DirSyncDetail(), retryable: true);

                // Count only fully committed copies.
                if (tmp.CopyMs >= 0) TicTackEventSource.Log.CopyCompleted(tmp.CopyMs);
                TicTackEventSource.Log.FileCopied(tmp.BytesCopied);
                // The hash covers the bytes the copy loop wrote; rename and
                // dir sync don't touch content, so it stays valid here.
                return new ActionResult { Success = true, SourceHash = tmp.SourceHash };
            }
            catch (UnauthorizedAccessException ex) { return ActionResult.Fail(ex.Message); }
            catch (DirectoryNotFoundException ex) { return ActionResult.Fail(ex.Message); }
            catch (PathTooLongException ex) { return ActionResult.Fail(ex.Message); }
            catch (NotSupportedException ex) { return ActionResult.Fail(ex.Message); }
            catch (IOException ex) { return ActionResult.Fail(ex.Message, retryable: true); }
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
                        var deleted = await _deletion.HandleDeletionAsync(null, args.OldDestPath, ct);
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
                else if (File.Exists(oldDest))
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

            // A directory fsync flushes every pending entry change in that
            // dir, so one sync per affected parent covers the whole
            // delete-then-move sequence above. Never fails the rename:
            // a lost entry resurrects and the next scan replays it.
            void SyncParents(string oldPath, string newPath)
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

}
