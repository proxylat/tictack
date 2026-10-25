using System;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using Microsoft.Win32;
using TicTack;

namespace Corpus
{
    // On-failure VSS diagnostics + BitLocker environment signals. All public
    // entry points are best-effort and never throw: this code runs on the
    // failure path and must not mask the original error.
    // Requirements for imaging (Windows): the VSS service, the COM+ Event
    // System service (EventSystem), and the volsnap driver, all running.
    internal static class VssHealth
    {
        public const string VssService = "VSS";
        public const string EventService = "EventSystem";
        public const string VolSnapDriver = "volsnap";
        public const string BdeService = "BDESVC";
        public const string FveDriver = "fvevol";
        public const string Swprv = "swprv";

        public const string StateRunning = "Running";
        public const string StateStopped = "Stopped";
        public const string StateMissing = "Missing";
        public const string StateUnknown = "Unknown";

        // Pure: known HRESULT -> one-line remediation. Null = no specific
        // advice; the caller still gets services + writer diagnostics.
        public static string? AdviceForHr(int hr) => hr switch
        {
            unchecked((int)0x80042302) => "VSS bad state (0x80042302): the Volume Shadow Copy service or COM+ Event System is stopped, disabled, or has no registered writers. Start both services and retry; if they already run, restart them (stop VSS first, then EventSystem; start EventSystem, then VSS) and check 'vssadmin list writers'.",
            unchecked((int)0x80070005) => "Access denied (0x80070005): VSS snapshots require elevation. Re-run from an elevated prompt.",
            _ => null,
        };

        // Best-effort service/driver state via sc.exe (inbox, no elevation
        // needed for query). Never throws.
        public static string QueryService(string name)
        {
            try
            {
                var r = ProcessRunner.RunAsync("sc query " + name, AppDomain.CurrentDomain.BaseDirectory, redirect: true, timeoutMs: 15000).GetAwaiter().GetResult();
                return ParseScState(r.Stdout ?? "", r.ExitCode, r.TimedOut);
            }
            catch { return StateUnknown; }
        }

        // Pure: sc.exe output -> state. Exit 1060 = not installed.
        internal static string ParseScState(string stdout, int exitCode, bool timedOut)
        {
            if (timedOut)
                return StateUnknown;
            if (exitCode == 1060 || stdout.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return StateMissing;
            if (exitCode != 0)
                return StateUnknown;
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (line.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                    return StateRunning;
                if (line.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                    return StateStopped;
                return StateUnknown;
            }
            return StateUnknown;
        }

        // swprv is a requirement with a different check: only a DISABLED
        // start type matters (stopped-but-demand-start is normal). True =
        // disabled, false = not disabled, null = could not determine.
        public static bool? IsServiceDisabled(string name)
        {
            try
            {
                var r = ProcessRunner.RunAsync("sc qc " + name, AppDomain.CurrentDomain.BaseDirectory, redirect: true, timeoutMs: 15000).GetAwaiter().GetResult();
                return ParseScDisabled(r.Stdout ?? "", r.ExitCode, r.TimedOut);
            }
            catch { return null; }
        }

        internal static bool? ParseScDisabled(string stdout, int exitCode, bool timedOut)
        {
            if (timedOut || exitCode != 0)
                return null;
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("START_TYPE", StringComparison.OrdinalIgnoreCase))
                    continue;
                return line.Contains("DISABLED", StringComparison.OrdinalIgnoreCase);
            }
            return null;
        }

        public static bool IsElevated()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static int TryGetHr(Exception? ex)
        {
            try
            {
                if (ex?.Data != null && ex.Data["VssHr"] is int hr)
                    return hr;
            }
            catch { }
            return 0;
        }

        // Pure: which failure class. AOT first: without dynamic code there is
        // no COM interop, so no service restart can ever help.
        internal static string FailureKind(int hr, bool comSupported) =>
            !comSupported ? "aot" : hr != 0 ? "vss" : "managed";

        // Called from the VSS snapshot failure path (all lanes). Logs service
        // states, writer registration, and remediation. Never throws.
        public static void ReportSnapshotFailure(ILogger log, Exception ex)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    log.Warn("VSS snapshot failed on a non-Windows host; VSS imaging requires Windows.");
                    return;
                }
                var hr = TryGetHr(ex);
                var kind = FailureKind(hr, RuntimeFeature.IsDynamicCodeSupported);
                if (kind == "aot")
                {
                    log.Warn("This Corpus binary was published with NativeAOT, which has no COM interop: VSS imaging cannot work here. Use the JIT build (build-corpus.bat --jit, or the release Corpus.exe once it flips to the JIT lane) and retry.");
                    return;
                }
                if (kind == "managed")
                    log.Warn("Snapshot setup threw before VSS was reached: " + ex.GetType().Name + ": " + ex.Message);
                var vss = QueryService(VssService);
                var es = QueryService(EventService);
                var vol = QueryService(VolSnapDriver);
                log.Warn("VSS requirements: service " + VssService + "=" + vss + ", service " + EventService + "=" + es + ", driver " + VolSnapDriver + "=" + vol + " (all required for imaging).");
                if (vss != StateRunning)
                    log.Warn("Service " + VssService + " is " + vss + ": start it from an elevated prompt ('net start " + VssService + "') and retry.");
                if (es != StateRunning)
                    log.Warn("Service " + EventService + " is " + es + ": start it from an elevated prompt ('net start " + EventService + "') and retry.");
                if (vol != StateRunning)
                    log.Warn("Driver " + VolSnapDriver + " is " + vol + ": volsnap is a boot-start driver, a reinstall/reboot is needed ('sc query " + VolSnapDriver + "' to re-check).");
                if (IsServiceDisabled(Swprv) == true)
                    log.Warn("Service " + Swprv + " is disabled: VSS snapshots need it enabled ('sc config " + Swprv + " start= demand' elevated), then retry.");
                if (vss == StateRunning && es == StateRunning && kind == "vss")
                    log.Warn("Services report Running but the snapshot still failed: restart them from an elevated prompt in this order (VSS depends on EventSystem, stop VSS first) - 'net stop " + VssService + "', 'net stop " + EventService + "', 'net start " + EventService + "', 'net start " + VssService + "' - then retry. If it persists, reboot.");
                var advice = AdviceForHr(hr);
                if (advice != null)
                    log.Warn(advice);
                if (kind == "vss")
                    ReportWriters(log);
            }
            catch { } // Diagnostics must never mask the original failure.
        }

        private static void ReportWriters(ILogger log)
        {
            int exit = -1;
            bool timedOut = false, ok = false;
            string stdout = "";
            try
            {
                var r = ProcessRunner.RunAsync("vssadmin list writers", AppDomain.CurrentDomain.BaseDirectory, redirect: true, timeoutMs: 30000).GetAwaiter().GetResult();
                exit = r.ExitCode;
                timedOut = r.TimedOut;
                stdout = r.Stdout ?? "";
                ok = true;
            }
            catch { }
            if (!ok || timedOut)
            {
                log.Warn("'vssadmin list writers' could not be queried (needs elevation); writer registration is unchecked.");
                return;
            }
            if (exit != 0)
            {
                log.Warn("'vssadmin list writers' exited with code " + exit + " (needs elevation); writer registration is unchecked.");
                return;
            }
            if (!stdout.Contains("Writer name", StringComparison.OrdinalIgnoreCase))
                log.Warn("'vssadmin list writers' shows no \"Writer name\" entries: no VSS writers are registered on this box. Re-register the VSS components and reboot, then re-check before imaging.");
        }

        // validate-verb view of the same requirements. Informational: states
        // are Info, only deviations Warn.
        public static void ReportServiceStates(ILogger log)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    log.Info("VSS requirements: not a Windows host; VSS checks skipped.");
                    return;
                }
                var vss = QueryService(VssService);
                var es = QueryService(EventService);
                var vol = QueryService(VolSnapDriver);
                log.Info("VSS requirements: service " + VssService + "=" + vss + ", service " + EventService + "=" + es + ", driver " + VolSnapDriver + "=" + vol + " (all required for imaging).");
                if (vss != StateRunning)
                    log.Warn("Service " + VssService + " is " + vss + ": imaging needs it running ('net start " + VssService + "' elevated).");
                if (es != StateRunning)
                    log.Warn("Service " + EventService + " is " + es + ": imaging needs it running ('net start " + EventService + "' elevated).");
                if (vol != StateRunning)
                    log.Warn("Driver " + VolSnapDriver + " is " + vol + ": volsnap is a boot-start driver, a reinstall/reboot may be needed.");
                if (IsServiceDisabled(Swprv) == true)
                    log.Warn("Service " + Swprv + " is disabled: VSS snapshots need it enabled ('sc config " + Swprv + " start= demand' elevated).");
                log.Info("BitLocker signals: " + Summarize(QueryBitLockerEnv()) + ".");
            }
            catch { } // validate must stay best-effort.
        }

        public sealed record BitLockerEnv(string BdeSvc, string FveVol, bool PolicyPreventsEncryption);

        public static BitLockerEnv QueryBitLockerEnv()
        {
            var bde = OperatingSystem.IsWindows() ? QueryService(BdeService) : StateUnknown;
            var fve = OperatingSystem.IsWindows() ? QueryService(FveDriver) : StateUnknown;
            return new BitLockerEnv(bde, fve, PolicyPreventsDeviceEncryption());
        }

        private static bool PolicyPreventsDeviceEncryption()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\BitLocker");
                return key?.GetValue("PreventDeviceEncryption") is int v && v == 1;
            }
            catch { return false; }
        }

        // Conservative: only true when BitLocker provably cannot be active.
        // A stopped BDESVC with the driver present still Warns: the volume
        // itself may be encrypted while the service is down.
        public static bool IsAbsentOrDisabled(BitLockerEnv env) =>
            env.FveVol == StateMissing
            || env.BdeSvc == StateMissing
            || (env.PolicyPreventsEncryption && env.BdeSvc != StateRunning);

        public static string Summarize(BitLockerEnv env) =>
            BdeService + "=" + env.BdeSvc + ", " + FveDriver + "=" + env.FveVol
            + ", PreventDeviceEncryption=" + (env.PolicyPreventsEncryption ? "1" : "0");
    }
}
