using System;
using System.Runtime.InteropServices;

namespace TicTack
{
    internal static class RemoteSandbox
    {
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const int JobObjectBasicUIRestrictions = 4;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
        private const uint JOB_OBJECT_UILIMIT_ALL = 0x000000FF;

        private const int PR_SET_NO_NEW_PRIVS = 38;
        private const int RLIMIT_NPROC = 6;
        private const int SYS_landlock_create_ruleset = 444;
        private const int SYS_landlock_add_rule = 445;
        private const int SYS_landlock_restrict_self = 446;
        private const uint LANDLOCK_RULE_PATH_BENEATH = 1;
        private const ulong LANDLOCK_ACCESS_FS_READ_FILE = 1UL << 2;
        private const ulong LANDLOCK_ACCESS_FS_READ_DIR = 1UL << 3;
        private const ulong LANDLOCK_ACCESS_FS_EXECUTE = 1UL << 0;
        private const ulong LANDLOCK_ACCESS_FS_WRITE_FILE = 1UL << 1;
        private const ulong LANDLOCK_ACCESS_FS_REMOVE_DIR = 1UL << 4;
        private const ulong LANDLOCK_ACCESS_FS_REMOVE_FILE = 1UL << 5;
        private const ulong LANDLOCK_ACCESS_FS_MAKE_CHAR = 1UL << 6;
        private const ulong LANDLOCK_ACCESS_FS_MAKE_DIR = 1UL << 7;
        private const ulong LANDLOCK_ACCESS_FS_MAKE_REG = 1UL << 8;
        private const ulong LANDLOCK_ACCESS_FS_MAKE_SOCK = 1UL << 9;
        private const ulong LANDLOCK_ACCESS_FS_MAKE_FIFO = 1UL << 10;
        private const ulong LANDLOCK_ACCESS_FS_MAKE_BLOCK = 1UL << 11;
        private const ulong LANDLOCK_ACCESS_FS_MAKE_SYM = 1UL << 12;
        private const int O_PATH = 0x200000;
        private const int O_CLOEXEC = 0x80000;

        [StructLayout(LayoutKind.Sequential)]
        private struct LandlockRulesetAttr
        {
            public ulong HandledAccessFs;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct LandlockPathBeneathAttr
        {
            public ulong AllowedAccess;
            public int ParentFd;
        }

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
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            public uint UIRestrictionsClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RLimit
        {
            public ulong RlimCur;
            public ulong RlimMax;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, IntPtr newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, ref JOBOBJECT_BASIC_UI_RESTRICTIONS lpJobObjectInfo, int cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("libc", SetLastError = true)]
        private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

        [DllImport("libc", SetLastError = true)]
        private static extern int setrlimit(int resource, ref RLimit rlim);

        [DllImport("libc", SetLastError = true)]
        private static extern long syscall(long number, IntPtr a1, nuint a2, uint a3);

        [DllImport("libc", SetLastError = true)]
        private static extern long syscall(long number, int fd, uint ruleType, IntPtr attr, uint flags);

        [DllImport("libc", SetLastError = true)]
        private static extern long syscall(long number, int fd, uint flags);

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int open(string pathname, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        public static void Apply(ILogger log, string? rootPath = null)
        {
            ProcessHardening.Apply(log, blockChildProcesses: true);
            if (OperatingSystem.IsWindows())
            {
                ApplyWindows(log);
                return;
            }
            if (OperatingSystem.IsLinux())
                ApplyLinux(log, rootPath);
        }

        private static void ApplyWindows(ILogger log)
        {
            try
            {
                if (OpenProcessToken(Environment.ProcessId == 0 ? IntPtr.Zero : System.Diagnostics.Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES, out var tok))
                {
                    AdjustTokenPrivileges(tok, true, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
                    CloseHandle(tok);
                }

                var job = CreateJobObjectW(IntPtr.Zero, null);
                if (job != IntPtr.Zero)
                {
                    var ex = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    ex.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
                    ex.BasicLimitInformation.ActiveProcessLimit = 1;
                    SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref ex, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());

                    var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = JOB_OBJECT_UILIMIT_ALL };
                    SetInformationJobObject(job, JobObjectBasicUIRestrictions, ref ui, Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>());

                    AssignProcessToJobObject(job, System.Diagnostics.Process.GetCurrentProcess().Handle);
                }
                log.Info("Remote agent Windows sandbox applied (privileges stripped, JobObject active-process=1).");
            }
            catch (Exception ex)
            {
                log.Warn("Remote agent Windows sandbox partial: " + ex.Message);
            }
        }

        private static void ApplyLinux(ILogger log, string? rootPath)
        {
            try
            {
                _ = prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0);
                var rlim = new RLimit { RlimCur = 0, RlimMax = 0 };
                _ = setrlimit(RLIMIT_NPROC, ref rlim);
                bool landlock = !string.IsNullOrEmpty(rootPath) && TryApplyLandlock(rootPath);
                log.Info("Remote agent Linux sandbox applied (NO_NEW_PRIVS, RLIMIT_NPROC=0" + (landlock ? ", Landlock=ro" : "") + ").");
            }
            catch (Exception ex)
            {
                log.Warn("Remote agent Linux sandbox partial: " + ex.Message);
            }
        }

        private static bool TryApplyLandlock(string rootPath)
        {
            ulong readOnly = LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_READ_DIR;
            ulong handled = readOnly
                | LANDLOCK_ACCESS_FS_EXECUTE | LANDLOCK_ACCESS_FS_WRITE_FILE
                | LANDLOCK_ACCESS_FS_REMOVE_DIR | LANDLOCK_ACCESS_FS_REMOVE_FILE
                | LANDLOCK_ACCESS_FS_MAKE_CHAR | LANDLOCK_ACCESS_FS_MAKE_DIR
                | LANDLOCK_ACCESS_FS_MAKE_REG | LANDLOCK_ACCESS_FS_MAKE_SOCK
                | LANDLOCK_ACCESS_FS_MAKE_FIFO | LANDLOCK_ACCESS_FS_MAKE_BLOCK
                | LANDLOCK_ACCESS_FS_MAKE_SYM;

            var rsAttr = new LandlockRulesetAttr { HandledAccessFs = handled };
            int rsFd;
            unsafe
            {
                rsFd = (int)syscall(SYS_landlock_create_ruleset, (IntPtr)(&rsAttr), (nuint)Marshal.SizeOf<LandlockRulesetAttr>(), 0);
            }
            if (rsFd < 0) return false;

            try
            {
                if (!AddLandlockReadPath(rsFd, rootPath, readOnly))
                    return false;
                // Allow reading system CA/SSL dirs if present so SslStream can initialize.
                AddLandlockReadPath(rsFd, "/etc/ssl", readOnly);
                AddLandlockReadPath(rsFd, "/usr/share/ca-certificates", readOnly);
                return syscall(SYS_landlock_restrict_self, rsFd, 0) == 0;
            }
            finally
            {
                _ = close(rsFd);
            }
        }

        private static bool AddLandlockReadPath(int rulesetFd, string path, ulong access)
        {
            int fd = open(path, O_PATH | O_CLOEXEC);
            if (fd < 0) return false;
            try
            {
                var pb = new LandlockPathBeneathAttr { AllowedAccess = access, ParentFd = fd };
                unsafe
                {
                    return syscall(SYS_landlock_add_rule, rulesetFd, LANDLOCK_RULE_PATH_BENEATH, (IntPtr)(&pb), 0) == 0;
                }
            }
            finally
            {
                _ = close(fd);
            }
        }
    }
}
