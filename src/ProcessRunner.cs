using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

        // argv-safe quote for a path embedded in a command string: the child
        // parses with CommandLineToArgvW rules, where \" is an escaped quote
        // — so a backslash-terminated path (a drive root like "S:\") must
        // double it or the closing quote is eaten (dism then reports
        // "missing a required servicing command" — VM-proven). Use for every
        // quoted path arg; harmless when no trailing slash is present.
        public static string QuoteArg(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return "\"" + value + (value.EndsWith('\\') ? "\\" : "") + "\"";
        }

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
            string command, string workingDir, bool redirect, CancellationToken ct = default, int timeoutMs = TimeoutMs, bool lowPriority = false, bool sandbox = false)
        {
            using var p = Process.Start(CreateStartInfo(command, workingDir, redirect));
            if (p == null) return (-1, string.Empty, string.Empty, false);

            var childJob = sandbox ? ChildJobSandbox.Assign(p) : IntPtr.Zero;
            try
            {
                if (lowPriority)
                {
                    try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { }
                }

                Task<string>? stdout = null, stderr = null;
                if (redirect)
                {
                    stdout = p.StandardOutput.ReadToEndAsync();
                    stderr = p.StandardError.ReadToEndAsync();
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutMs);
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
            finally
            {
                ChildJobSandbox.Close(childJob);
            }
        }

        private static async Task<string> DrainAsync(Task<string> read)
        {
            try { return await read.ConfigureAwait(false); }
            catch { return string.Empty; }
        }

        private static class ChildJobSandbox
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public nuint MinimumWorkingSetSize;
                public nuint MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public nuint Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IO_COUNTERS
            {
                public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
                public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_BASIC_UI_RESTRICTIONS { public uint UIRestrictionsClass; }

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int cb);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_BASIC_UI_RESTRICTIONS info, int cb);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr hObject);

            public static IntPtr Assign(Process proc)
            {
                if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
                try
                {
                    var job = CreateJobObjectW(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) return IntPtr.Zero;
                    var ex = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    ex.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                    _ = SetInformationJobObject(job, 9, ref ex, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
                    var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = 0xFF };
                    _ = SetInformationJobObject(job, 4, ref ui, Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>());
                    _ = AssignProcessToJobObject(job, proc.Handle);
                    return job;
                }
                catch { return IntPtr.Zero; }
            }

            public static void Close(IntPtr job)
            {
                if (job != IntPtr.Zero && OperatingSystem.IsWindows())
                    _ = CloseHandle(job);
            }
        }
    }
}
