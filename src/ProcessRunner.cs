using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    // One owner for the cmd.exe /c vs /bin/sh -c launch, the 10-minute kill,
    // the whole-tree kill, and concurrent pipe draining. Redirected pipes must
    // be drained while waiting or a verbose child deadlocks on a full buffer.
    internal static class ProcessRunner
    {
        public const int TimeoutMs = 600000;

        public static ProcessStartInfo CreateStartInfo(string command, string workingDir, bool redirect)
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = redirect,
                RedirectStandardOutput = redirect,
                RedirectStandardError = redirect
            };
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c " + command;
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
            }
            return psi;
        }

        public static async Task<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> RunAsync(
            string command, string workingDir, bool redirect, CancellationToken ct = default)
        {
            using var p = Process.Start(CreateStartInfo(command, workingDir, redirect));
            if (p == null) return (-1, string.Empty, string.Empty, false);

            Task<string>? stdout = null, stderr = null;
            if (redirect)
            {
                stdout = p.StandardOutput.ReadToEndAsync();
                stderr = p.StandardError.ReadToEndAsync();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeoutMs);
            try
            {
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                if (stdout != null) await DrainAsync(stdout).ConfigureAwait(false);
                if (stderr != null) await DrainAsync(stderr).ConfigureAwait(false);
                return (-1, string.Empty, string.Empty, true);
            }

            var outText = stdout != null ? await DrainAsync(stdout).ConfigureAwait(false) : string.Empty;
            var errText = stderr != null ? await DrainAsync(stderr).ConfigureAwait(false) : string.Empty;
            return (p.ExitCode, outText, errText, false);
        }

        private static async Task<string> DrainAsync(Task<string> read)
        {
            try { return await read.ConfigureAwait(false); }
            catch { return string.Empty; }
        }
    }
}
