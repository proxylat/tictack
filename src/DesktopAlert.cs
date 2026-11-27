using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TicTack
{
    public static class DesktopAlert
    {
        private const uint WtsCurrentServerHandle = 0;
        private const uint MbOk = 0x00000000;
        private const uint MbYesNo = 0x00000004;
        private const uint MbIconError = 0x00000010;
        private const uint MbIconQuestion = 0x00000020;
        private const uint MbIconWarning = 0x00000030;
        private const int IdYes = 6;

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSSendMessageW(
            IntPtr hServer, uint sessionId,
            string pTitle, int titleLength,
            string pMessage, int messageLength,
            uint style, int timeout,
            out int pResponse, [MarshalAs(UnmanagedType.Bool)] bool bWait);

        private static readonly object Lock = new object();
        private static DateTime _lastAlert;

        private static string? _alertPath;
        private static bool _osNotifications = true;
        private static bool _cmdPrompts;
        private static bool _osPrompts = true;
        private static int _timeoutSeconds = 15;

        public static void Configure(string? path, bool osNotifications = true, bool cmdPrompts = false, bool osPrompts = true, int timeoutSeconds = 15)
        {
            _alertPath = path;
            _osNotifications = osNotifications;
            _cmdPrompts = cmdPrompts;
            _osPrompts = osPrompts;
            _timeoutSeconds = Math.Max(0, timeoutSeconds);
        }

        public static void Write(string level, string message, Exception? ex = null)
        {
            var now = DateTime.Now;
            lock (Lock)
            {
                if ((now - _lastAlert).TotalSeconds < 30) return;
                _lastAlert = now;
            }

            WriteAlertFile(level, message, ex, now);
            if (_osNotifications)
                SendOsNotification("TicTack " + level, message, level);
        }

        public static void Notify(string title, string message, string level = "WARN")
        {
            WriteAlertFile(level, title + ": " + message, null, DateTime.Now);
            if (_osNotifications)
                SendOsNotification(title, message, level);
        }

        public static bool PromptYesNo(string title, string message, bool defaultOnTimeout = false)
        {
            if (_cmdPrompts && Environment.UserInteractive && !Console.IsInputRedirected)
                return PromptConsole(title, message, defaultOnTimeout);
            if (_osPrompts)
                return PromptOsDialog(title, message, defaultOnTimeout);
            return defaultOnTimeout;
        }

        private static void WriteAlertFile(string level, string message, Exception? ex, DateTime now)
        {
            var dir = _alertPath;
            if (string.IsNullOrEmpty(dir)) return;

            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var ts = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var path = Path.Combine(dir, "TicTack-" + level + "-" + ts + ".txt");
                using var w = new StreamWriter(path);
                w.WriteLine("TicTack " + level + " — " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                w.WriteLine(new string('-', 50));
                w.WriteLine(message);
                if (ex != null)
                {
                    w.WriteLine();
                    w.WriteLine("Exception:");
                    w.WriteLine(ex.ToString());
                }
            }
            catch { }
        }

        private static void SendOsNotification(string title, string message, string level)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (Environment.UserInteractive && TrySendWindowsToast(title, message, _timeoutSeconds))
                        return;
                    var sessionId = WTSGetActiveConsoleSessionId();
                    if (sessionId == 0xFFFFFFFF) return;
                    var style = MbOk | (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase) ? MbIconError : MbIconWarning);
                    WTSSendMessageW((IntPtr)WtsCurrentServerHandle, sessionId,
                        title, title.Length * 2, message, message.Length * 2,
                        style, _timeoutSeconds, out _, bWait: false);
                }
                else if (OperatingSystem.IsLinux())
                {
                    SendLinuxNotification(title, message);
                }
            }
            catch { }
        }

        private static void SendLinuxNotification(string title, string message)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            {
                StartNotifySend(null, null, title, message);
                return;
            }
            // Root systemd services lack DBUS_SESSION_BUS_ADDRESS: deliver via each active desktop user's session bus.
            if (!Directory.Exists("/run/user")) return;
            foreach (var dir in Directory.EnumerateDirectories("/run/user"))
            {
                var uidStr = Path.GetFileName(dir);
                if (!int.TryParse(uidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid) || uid < 1000)
                    continue;
                var bus = Path.Combine(dir, "bus");
                if (!File.Exists(bus)) continue;
                var user = LookupLinuxUser(uid);
                if (user != null)
                    StartNotifySend(user, "unix:path=" + bus, title, message);
            }
        }

        private static void StartNotifySend(string? runAsUser, string? dbusAddr, string title, string message)
        {
            var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
            if (runAsUser != null && dbusAddr != null)
            {
                psi.FileName = "runuser";
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(runAsUser);
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add("env");
                psi.ArgumentList.Add("DBUS_SESSION_BUS_ADDRESS=" + dbusAddr);
                psi.ArgumentList.Add("notify-send");
            }
            else
            {
                psi.FileName = "notify-send";
            }
            psi.ArgumentList.Add("--app-name=TicTack");
            if (_timeoutSeconds == 0)
                psi.ArgumentList.Add("--urgency=critical");
            else
                psi.ArgumentList.Add("--expire-time=" + (_timeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(message);
            using var proc = Process.Start(psi);
        }

        private static string? LookupLinuxUser(int uid)
        {
            var uidText = uid.ToString(CultureInfo.InvariantCulture);
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var parts = line.Split(':');
                if (parts.Length >= 3 && string.Equals(parts[2], uidText, StringComparison.Ordinal))
                    return parts[0];
            }
            return null;
        }

        internal static string EscapeXml(string s) =>
            (s ?? string.Empty)
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("'", "&apos;", StringComparison.Ordinal);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatus(IntPtr hService, out ServiceStatus lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        private static bool IsWpnServiceRunning()
        {
            if (!OperatingSystem.IsWindows()) return false;
            var scm = OpenSCManagerW(null, null, 0x0001); // SC_MANAGER_CONNECT
            if (scm == IntPtr.Zero) return false;
            try
            {
                var svc = OpenServiceW(scm, "WpnService", 0x0004); // SERVICE_QUERY_STATUS
                if (svc == IntPtr.Zero) return false;
                try
                {
                    return QueryServiceStatus(svc, out var st) && st.dwCurrentState == 4; // SERVICE_RUNNING
                }
                finally { CloseServiceHandle(svc); }
            }
            catch { return false; }
            finally { CloseServiceHandle(scm); }
        }

        private static bool TrySendWindowsToast(string title, string message, int timeoutSeconds)
        {
            if (!IsWpnServiceRunning()) return false;
            try
            {
                var safeTitle = EscapeXml(title);
                var safeMsg = EscapeXml(message);
                var xml = $"<toast><visual><binding template='ToastText02'><text id='1'>{safeTitle}</text><text id='2'>{safeMsg}</text></binding></visual></toast>";
                var exp = timeoutSeconds > 0
                    ? $"$t.ExpirationTime = [DateTimeOffset]::Now.AddSeconds({timeoutSeconds});"
                    : string.Empty;
                var script =
                    "$reg = 'HKCU:\\Software\\Classes\\AppUserModelId\\TicTack';" +
                    "if (-not (Test-Path $reg)) { New-Item -Path $reg -Force | Out-Null; Set-ItemProperty -Path $reg -Name DisplayName -Value 'TicTack' };" +
                    "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null;" +
                    "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null;" +
                    "$x = New-Object Windows.Data.Xml.Dom.XmlDocument;" +
                    $"$x.LoadXml(\"{xml.Replace("\"", "`\"", StringComparison.Ordinal)}\");" +
                    "$t = New-Object Windows.UI.Notifications.ToastNotification $x;" +
                    exp +
                    "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('TicTack').Show($t);";
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-WindowStyle");
                psi.ArgumentList.Add("Hidden");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(script);
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                return proc.WaitForExit(5000) && proc.ExitCode == 0;
            }
            catch { return false; }
        }

        private static bool PromptConsole(string title, string message, bool defaultOnTimeout)
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine($"[{title}] {message}");
                Console.Write("Proceed? [y/n]: ");
                var readTask = Task.Run(() => Console.ReadLine());
                var waitMs = _timeoutSeconds > 0 ? _timeoutSeconds * 1000 : System.Threading.Timeout.Infinite;
                if (!readTask.Wait(waitMs)) return defaultOnTimeout;
                var ans = (readTask.Result ?? string.Empty).Trim().ToLowerInvariant();
                return ans == "y" || ans == "yes" || ans == "c" || ans == "continue";
            }
            catch { return defaultOnTimeout; }
        }

        private static bool PromptOsDialog(string title, string message, bool defaultOnTimeout)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var sessionId = WTSGetActiveConsoleSessionId();
                    if (sessionId == 0xFFFFFFFF) return defaultOnTimeout;
                    if (WTSSendMessageW((IntPtr)WtsCurrentServerHandle, sessionId,
                        title, title.Length * 2, message, message.Length * 2,
                        MbYesNo | MbIconQuestion, _timeoutSeconds, out var resp, bWait: true))
                        return resp == IdYes;
                    return defaultOnTimeout;
                }
                if (OperatingSystem.IsLinux())
                {
                    var psi = new ProcessStartInfo { FileName = "zenity", UseShellExecute = false, CreateNoWindow = true };
                    psi.ArgumentList.Add("--question");
                    psi.ArgumentList.Add("--title=" + title);
                    psi.ArgumentList.Add("--text=" + message);
                    if (_timeoutSeconds > 0)
                        psi.ArgumentList.Add("--timeout=" + _timeoutSeconds.ToString(CultureInfo.InvariantCulture));
                    using var proc = Process.Start(psi);
                    if (proc == null) return defaultOnTimeout;
                    proc.WaitForExit(_timeoutSeconds > 0 ? (_timeoutSeconds + 5) * 1000 : 300_000);
                    return proc.HasExited && proc.ExitCode == 0;
                }
            }
            catch { }
            return defaultOnTimeout;
        }
    }
}
