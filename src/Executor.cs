using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public class CopyAction : IFileAction
    {
        private readonly IFileAccessor _accessor;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        const uint FSCTL_SET_SPARSE = 0x000900C4;

        public CopyAction(IFileAccessor accessor)
        {
            _accessor = accessor;
        }

        public async Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
        {
            try
            {
                var src = args.ChangeEvent.FullPath;
                var dst = PathUtil.EnsureExtended(args.DestPath);
                var tmp = dst + ".tictack.tmp";

                var dir = PathUtil.EnsureExtended(Path.GetDirectoryName(dst));
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
                    dstStream.Flush(true);
                }

                try { File.SetLastWriteTimeUtc(tmp, new FileInfo(src).LastWriteTimeUtc); } catch { }

                try
                {
                    if (File.Exists(dst))
                    {
                        File.SetAttributes(dst, FileAttributes.Normal);
                        File.Delete(dst);
                    }
                    File.Move(tmp, dst);
                }
                catch (IOException)
                {
                    if (string.IsNullOrEmpty(dst)) throw;
                    File.Copy(tmp, dst, overwrite: true);
                    try { File.Delete(tmp); } catch { }
                }

                return ActionResult.Ok();
            }
            catch (UnauthorizedAccessException ex) { return ActionResult.Fail(ex.Message); }
            catch (DirectoryNotFoundException ex) { return ActionResult.Fail(ex.Message); }
            catch (PathTooLongException ex) { return ActionResult.Fail(ex.Message); }
            catch (NotSupportedException ex) { return ActionResult.Fail(ex.Message); }
            catch (IOException ex) { return ActionResult.Fail(ex.Message); }
        }

    }

    public class DeleteAction : IFileAction
    {
        public Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
        {
            try
            {
                if (File.Exists(args.DestPath) || Directory.Exists(args.DestPath))
                {
                    File.SetAttributes(args.DestPath, FileAttributes.Normal);
                    File.Delete(args.DestPath);
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ActionResult.Fail(ex.Message));
            }
            return Task.FromResult(ActionResult.Ok());
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

    public class CommandAction : IFileAction
    {
        private readonly string _command;
        private readonly string _workingDir;

        public CommandAction(string command, string workingDir = null)
        {
            _command = command;
            _workingDir = workingDir;
        }

        public async Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
        {
            var cmd = _command
                .Replace("{source}", args.ChangeEvent.FullPath)
                .Replace("{dest}", args.DestPath)
                .Replace("{file}", Path.GetFileName(args.ChangeEvent.FullPath))
                .Replace("{source_base}", args.SourceBase);

            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c " + cmd;
                psi.WorkingDirectory = _workingDir ?? AppDomain.CurrentDomain.BaseDirectory;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                using (var p = Process.Start(psi))
                {
                    using (ct.Register(() => { try { p.Kill(); } catch { } }))
                    {
                        await Task.Run(() => p.WaitForExit(), ct);
                    }
                    if (p.ExitCode != 0)
                    {
                        var err = p.StandardError.ReadToEnd();
                        return ActionResult.Fail("exit=" + p.ExitCode + ": " + err);
                    }
                }
                return ActionResult.Ok();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ActionResult.Fail(ex.Message);
            }
        }
    }
}
