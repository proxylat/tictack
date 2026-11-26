using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TicTack
{
    // SCM/IFEO tuning from config.yaml. Most keys are nullable: absent
    // means don't touch — except RequiredPrivileges, where absent applies
    // the least-privilege 4 and only [] opts out (full token). Each write
    // is independent (Warn-and-continue, never fails startup). Success
    // lines log at Debug (steady-state noise); failures Warn.
    // Windows-only; Linux is a no-op.
    public static class ServiceTuning
    {
        public static void Apply(TicTackConfig cfg, ILogger log, string serviceName)
        {
            if (OperatingSystem.IsLinux())
            {
                ApplyLinux(cfg, log);
                return;
            }
            if (!OperatingSystem.IsWindows()) return;
            ApplyPreshutdown(cfg, log, serviceName);
            ApplyPipeTimeout(cfg, log, serviceName);
            ApplyRequiredPrivileges(cfg, log, serviceName);
            ApplyIfeo(cfg, log);
        }

        private static void ApplyLinux(TicTackConfig cfg, ILogger log)
        {
            if (cfg.CpuPriorityClass.HasValue)
            {
                int nice = cfg.CpuPriorityClass.Value switch
                {
                    1 => 19,
                    5 => 10,
                    _ => 0
                };
                try
                {
                    if (LinuxNative.SetPriority(0, 0, nice) == 0)
                        log.Debug("Linux nice set to " + nice);
                    else
                        log.Warn("Linux setpriority(" + nice + ") failed (errno " + Marshal.GetLastWin32Error() + ")");
                }
                catch (Exception ex) { log.Warn("Linux setpriority failed: " + ex.Message); }
            }
            if (cfg.IoPriority.HasValue)
            {
                int ioprio = cfg.IoPriority.Value switch
                {
                    0 => 3 << 13,
                    1 => (2 << 13) | 7,
                    _ => (2 << 13) | 4
                };
                long sysNum = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => 251,
                    Architecture.Arm64 => 30,
                    _ => -1
                };
                if (sysNum > 0)
                {
                    try
                    {
                        if (LinuxNative.Syscall3(sysNum, 1, 0, ioprio) == 0)
                            log.Debug("Linux ioprio set to 0x" + ioprio.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
                        else
                            log.Warn("Linux ioprio_set failed (errno " + Marshal.GetLastWin32Error() + ")");
                    }
                    catch (Exception ex) { log.Warn("Linux ioprio_set failed: " + ex.Message); }
                }
            }
        }

        private static class LinuxNative
        {
            [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
            public static extern int SetPriority(int which, uint who, int prio);

            [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
            public static extern long Syscall3(long number, int arg1, int arg2, int arg3);
        }

        private static void ApplyPreshutdown(TicTackConfig cfg, ILogger log, string serviceName)
        {
            if (!cfg.PreshutdownTimeoutMs.HasValue) return;
            try
            {
                var scm = Native.OpenSCManager(null, null, 0x0001);
                if (scm == IntPtr.Zero) { log.Warn("PreshutdownTimeoutMs skipped: OpenSCManager failed"); return; }
                try
                {
                    var svc = Native.OpenService(scm, serviceName, 0x0002);
                    if (svc == IntPtr.Zero) { log.Warn("PreshutdownTimeoutMs skipped: OpenService failed"); return; }
                    try
                    {
                        var info = new Native.PreshutdownInfo { Timeout = (uint)cfg.PreshutdownTimeoutMs.Value };
                        if (!Native.ChangeServiceConfig2(svc, 7, ref info))
                            log.Warn("PreshutdownTimeoutMs apply failed (win32 " + Marshal.GetLastWin32Error() + ")");
                        else log.Debug("PreshutdownTimeoutMs set to " + cfg.PreshutdownTimeoutMs.Value);
                    }
                    finally { Native.CloseServiceHandle(svc); }
                }
                finally { Native.CloseServiceHandle(scm); }
            }
            catch (Exception ex) { log.Warn("PreshutdownTimeoutMs apply failed: " + ex.Message); }
        }

        private static void ApplyPipeTimeout(TicTackConfig cfg, ILogger log, string serviceName)
        {
            if (!cfg.ServicesPipeTimeoutMs.HasValue) return;
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + serviceName, true);
                if (key == null) { log.Warn("ServicesPipeTimeoutMs skipped: cannot open service key"); return; }
                key.SetValue("ServicesPipeTimeout", cfg.ServicesPipeTimeoutMs.Value, RegistryValueKind.DWord);
                log.Debug("ServicesPipeTimeoutMs set to " + cfg.ServicesPipeTimeoutMs.Value);
            }
            catch (Exception ex) { log.Warn("ServicesPipeTimeoutMs apply failed: " + ex.Message); }
        }

        private static void ApplyIfeo(TicTackConfig cfg, ILogger log)
        {
            if (!cfg.CpuPriorityClass.HasValue && !cfg.IoPriority.HasValue
                && !cfg.PagePriority.HasValue) return;
            string exe;
            try { exe = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? ""); }
            catch { exe = ""; }
            if (string.IsNullOrEmpty(exe)) exe = "TicTackSv.exe";
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + exe + @"\PerfOptions", true);
                if (key == null) { log.Warn("IFEO PerfOptions skipped: cannot open subkey"); return; }
                SetDword(key, log, "CpuPriorityClass", cfg.CpuPriorityClass);
                SetDword(key, log, "IoPriority", cfg.IoPriority);
                SetDword(key, log, "PagePriority", cfg.PagePriority);
            }
            catch (Exception ex) { log.Warn("IFEO apply failed: " + ex.Message); }
        }

        private static void SetDword(RegistryKey key, ILogger log, string name, int? value)
        {
            if (!value.HasValue) return;
            try
            {
                key.SetValue(name, value.Value, RegistryValueKind.DWord);
                log.Debug(name + " set to " + value.Value);
            }
            catch (Exception ex) { log.Warn(name + " apply failed: " + ex.Message); }
        }

        private static void ApplyRequiredPrivileges(TicTackConfig cfg, ILogger log, string serviceName)
        {
            // Explicit opt-out: empty list = leave the full token alone.
            if (cfg.RequiredPrivileges != null && cfg.RequiredPrivileges.Length == 0) return;
            // Default (key absent): least-privilege 4. Custom list: verbatim.
            var isDefault = cfg.RequiredPrivileges == null;
            var privs = (cfg.RequiredPrivileges ?? new[]
            {
                "SeChangeNotifyPrivilege", "SeBackupPrivilege",
                "SeRestorePrivilege", "SeManageVolumePrivilege",
            }).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (privs.Length == 0) { log.Warn("RequiredPrivileges skipped: list has no usable entries"); return; }
            try
            {
                var scm = Native.OpenSCManager(null, null, 0x0001);
                if (scm == IntPtr.Zero) { log.Warn("RequiredPrivileges skipped: OpenSCManager failed"); return; }
                try
                {
                    var svc = Native.OpenService(scm, serviceName, 0x0002);
                    if (svc == IntPtr.Zero) { log.Warn("RequiredPrivileges skipped: OpenService failed"); return; }
                    try
                    {
                        // MULTI_SZ: entries joined with NUL + double-NUL terminator.
                        var multiSz = string.Join('\0', privs) + "\0\0";
                        var info = new Native.RequiredPrivs { Privileges = multiSz };
                        if (!Native.ChangeServiceConfigPrivs(svc, 6, ref info))
                            log.Warn("RequiredPrivileges apply failed (win32 " + Marshal.GetLastWin32Error() + ")");
                        else log.Debug("RequiredPrivileges set to " + string.Join(" ", privs)
                            + (isDefault ? " (default)" : ""));
                    }
                    finally { Native.CloseServiceHandle(svc); }
                }
                finally { Native.CloseServiceHandle(scm); }
            }
            catch (Exception ex) { log.Warn("RequiredPrivileges apply failed: " + ex.Message); }
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct RequiredPrivs { public string Privileges; }

            [StructLayout(LayoutKind.Sequential)]
            internal struct PreshutdownInfo { public uint Timeout; }

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr OpenSCManager(string? machine, string? database, int access);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr OpenService(IntPtr scm, string name, int access);

            [DllImport("advapi32.dll", SetLastError = true)]
            internal static extern bool ChangeServiceConfig2(IntPtr service, int infoLevel, ref PreshutdownInfo info);

            [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2", CharSet = CharSet.Unicode)]
            internal static extern bool ChangeServiceConfigPrivs(IntPtr service, int infoLevel, ref RequiredPrivs info);

            [DllImport("advapi32.dll", SetLastError = true)]
            internal static extern bool CloseServiceHandle(IntPtr handle);
        }
    }
}
