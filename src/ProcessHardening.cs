using System;
using System.Runtime.InteropServices;

namespace TicTack
{
    // OS-level process mitigations shared by TicTackSv and Corpus:
    // blocks UNC/low-IL DLL hijacking & AppInit hooks on Windows, and
    // disables ptrace/core-dump memory inspection on Linux.
    internal static class ProcessHardening
    {
        private const int ProcessExtensionPointDisablePolicy = 6;
        private const int ProcessImageLoadPolicy = 10;
        private const int ProcessChildProcessPolicy = 13;

        private const uint ImageLoadNoRemoteImages = 0x1;
        private const uint ImageLoadNoLowMandatoryLabel = 0x2;
        private const uint ImageLoadPreferSystem32 = 0x4;

        private const int PR_SET_DUMPABLE = 4;
        private const int ProcessPowerThrottling = 4;
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        private const int PRIO_PROCESS = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessMitigationPolicy(int mitigationPolicy, ref uint lpBuffer, nuint dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass, ref PROCESS_POWER_THROTTLING_STATE processInformation, int processInformationSize);

        [DllImport("libc", SetLastError = true)]
        private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

        [DllImport("libc", SetLastError = true)]
        private static extern int setpriority(int which, uint who, int prio);

        public static void ApplyEcoQos(ILogger log)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var state = new PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                        ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                        StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    };
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    _ = SetProcessInformation(proc.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                    proc.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
                    log.Info("EcoQoS applied (Windows efficiency mode + BelowNormal priority).");
                }
                catch (Exception ex)
                {
                    log.Warn("EcoQoS apply skipped: " + ex.Message);
                }
                return;
            }
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    if (setpriority(PRIO_PROCESS, 0, 10) == 0)
                        log.Info("EcoQoS applied (Linux nice +10).");
                }
                catch (Exception ex)
                {
                    log.Warn("EcoQoS apply skipped: " + ex.Message);
                }
            }
        }

        public static void Apply(ILogger log, bool blockChildProcesses = false)
        {
            if (OperatingSystem.IsWindows())
            {
                ApplyWindows(log, blockChildProcesses);
                return;
            }
            if (OperatingSystem.IsLinux())
                ApplyLinux(log);
        }

        private static void ApplyWindows(ILogger log, bool blockChildProcesses)
        {
            try
            {
                uint imageFlags = ImageLoadNoRemoteImages | ImageLoadNoLowMandatoryLabel | ImageLoadPreferSystem32;
                _ = SetProcessMitigationPolicy(ProcessImageLoadPolicy, ref imageFlags, sizeof(uint));

                uint extFlags = 1;
                _ = SetProcessMitigationPolicy(ProcessExtensionPointDisablePolicy, ref extFlags, sizeof(uint));

                if (blockChildProcesses)
                {
                    uint childFlags = 1;
                    _ = SetProcessMitigationPolicy(ProcessChildProcessPolicy, ref childFlags, sizeof(uint));
                }
                log.Debug("Process mitigations applied (ImageLoad, ExtensionPoint" + (blockChildProcesses ? ", NoChildProcess" : "") + ").");
            }
            catch (Exception ex)
            {
                log.Warn("Process mitigations partial: " + ex.Message);
            }
        }

        private static void ApplyLinux(ILogger log)
        {
            try
            {
                if (prctl(PR_SET_DUMPABLE, 0, 0, 0, 0) == 0)
                    log.Debug("Process dumpable disabled (PR_SET_DUMPABLE=0).");
            }
            catch (Exception ex)
            {
                log.Warn("Process dumpable disable failed: " + ex.Message);
            }
        }
    }
}
