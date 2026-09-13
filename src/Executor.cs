using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public class CopyAction : IFileAction
    {
        private readonly IFileAccessor _accessor;
        private readonly bool _fullDurability;

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

        public CopyAction(IFileAccessor accessor, bool fullDurability = true)
        {
            _accessor = accessor;
            _fullDurability = fullDurability;
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

                var isSparse = false;
                try { isSparse = (File.GetAttributes(src) & FileAttributes.SparseFile) != 0; } catch { }

                using (var srcStream = _accessor.OpenRead(src))
                using (var dstStream = File.Create(tmp))
                {
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
                    if (_fullDurability)
                        dstStream.Flush(true);
                    else
                        dstStream.Flush();
                }

                try
                {
                    var sourceSnapshot = args.SourceSnapshot;
                    if (!sourceSnapshot.HasValue && FileSnapshot.TryRead(src, out var current))
                        sourceSnapshot = current;
                    if (sourceSnapshot.HasValue)
                        File.SetLastWriteTimeUtc(tmp, sourceSnapshot.Value.LastWriteTimeUtc);
                }
                catch { }

                try
                {
                    if (File.Exists(dst))
                    {
                        File.SetAttributes(dst, FileAttributes.Normal);
                        File.Replace(tmp, dst, null);
                    }
                    else
                    {
                        File.Move(tmp, dst);
                    }
                }
                catch (IOException)
                {
                    if (string.IsNullOrEmpty(dst)) throw;
                    File.Copy(tmp, dst, overwrite: true);
                    try { File.Delete(tmp); } catch { }
                }

                if (!_fullDurability)
                    FlushDirectory(Path.GetDirectoryName(dst));

                return ActionResult.Ok();
            }
            catch (UnauthorizedAccessException ex) { return ActionResult.Fail(ex.Message); }
            catch (DirectoryNotFoundException ex) { return ActionResult.Fail(ex.Message); }
            catch (PathTooLongException ex) { return ActionResult.Fail(ex.Message); }
            catch (NotSupportedException ex) { return ActionResult.Fail(ex.Message); }
            catch (IOException ex) { return ActionResult.Fail(ex.Message); }
        }

        static void FlushDirectory(string? path)
        {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path)) return;
            var fd = OpenDirectory(path, O_RDONLY | O_DIRECTORY);
            if (fd < 0) return;
            try { Fsync(fd); }
            finally { Close(fd); }
        }

    }

    public class RenameAction : IFileAction
    {
        public Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
        {
            if (args.OldDestPath == null)
                return Task.FromResult(ActionResult.Ok());

            try
            {
                if (Directory.Exists(args.OldDestPath))
                {
                    if (!Directory.Exists(args.DestPath))
                        Directory.Move(args.OldDestPath, args.DestPath);
                    else
                        Directory.Delete(args.OldDestPath, true);
                }
                else if (File.Exists(args.OldDestPath))
                {
                    var dir = Path.GetDirectoryName(args.DestPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    if (File.Exists(args.DestPath)) File.Delete(args.DestPath);
                    File.Move(args.OldDestPath, args.DestPath);
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ActionResult.Fail(ex.Message));
            }
            return Task.FromResult(ActionResult.Ok());
        }
    }

}
