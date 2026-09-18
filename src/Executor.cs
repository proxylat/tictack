using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

    public class CopyAction : IFileAction
    {
        private readonly IFileAccessor _accessor;
        private readonly bool _fullDurability;
        private readonly Action<CopyCheckpoint>? _checkpoint;
        private readonly ILogger? _log;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        static extern int OpenDirectory(string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        static extern int Fsync(int fd);

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

        public CopyAction(IFileAccessor accessor, bool fullDurability = true, ILogger? log = null)
            : this(accessor, fullDurability, null, log)
        {
        }

        internal CopyAction(IFileAccessor accessor, bool fullDurability, Action<CopyCheckpoint>? checkpoint, ILogger? log = null)
        {
            _accessor = accessor;
            _fullDurability = fullDurability;
            _checkpoint = checkpoint;
            _log = log;
        }

        public async Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
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

                var isSparse = false;
                try { isSparse = (File.GetAttributes(src) & FileAttributes.SparseFile) == FileAttributes.SparseFile; }
                catch (Exception ex) { _log?.Debug("Sparse attribute probe failed for " + src + ": " + ex.Message); }

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
                    await srcStream.CopyToAsync(dstStream, 81920, ct);
                    // Prefer the bytes actually written: a source appended
                    // mid-copy makes Length a lie.
                    bytesCopied = dstStream.Position;
                    Stopwatch? fsw = timed ? Stopwatch.StartNew() : null;
                    if (_fullDurability)
                        dstStream.Flush(true);
                    else
                        dstStream.Flush();
                    if (fsw != null) TicTackEventSource.Log.FsyncCompleted(fsw.Elapsed.TotalMilliseconds);
                    _checkpoint?.Invoke(CopyCheckpoint.DataFlushed);
                    if (sw != null) copyMs = sw.Elapsed.TotalMilliseconds;
                }

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

                _checkpoint?.Invoke(CopyCheckpoint.BeforeCommit);
                if (File.Exists(dst))
                {
                    File.SetAttributes(dst, FileAttributes.Normal);
                    File.Replace(tmp, dst, null);
                }
                else
                {
                    File.Move(tmp, dst);
                }
                _checkpoint?.Invoke(CopyCheckpoint.AfterCommit);

                if (!FlushDirectory(Path.GetDirectoryName(dst)))
                    return ActionResult.Fail("Could not fsync destination directory");

                // Count only fully committed copies.
                if (copyMs >= 0) TicTackEventSource.Log.CopyCompleted(copyMs);
                TicTackEventSource.Log.FileCopied(bytesCopied);
                return ActionResult.Ok();
            }
            catch (UnauthorizedAccessException ex) { return ActionResult.Fail(ex.Message); }
            catch (DirectoryNotFoundException ex) { return ActionResult.Fail(ex.Message); }
            catch (PathTooLongException ex) { return ActionResult.Fail(ex.Message); }
            catch (NotSupportedException ex) { return ActionResult.Fail(ex.Message); }
            // Retryable: sharing violations and transient IO. Pipeline unwraps
            // these into ExponentialBackoffRetry.
            catch (IOException ex) { return ActionResult.Fail(ex.Message, retryable: true); }
            catch (Win32Exception ex) { return ActionResult.Fail(ex.Message, retryable: true); }
        }

        static bool FlushDirectory(string? path)
        {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path)) return true;
            var fd = OpenDirectory(path, O_RDONLY | O_DIRECTORY);
            if (fd < 0) return false;
            try { return Fsync(fd) == 0; }
            finally { Close(fd); }
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
                        Directory.Move(oldDest, dest);
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
                            Directory.Move(oldDest, dest);
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
        }
    }

}
