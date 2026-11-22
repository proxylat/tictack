using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TicTack
{
    // Audits DACLs / POSIX modes on config.yaml (LPE vector via jobs: commands)
    // and secret files (psk_file, Corpus id.key). Warn-only by default;
    // strict_config_perms: true turns insecure permissions into a hard stop.
    internal static class ConfigGuard
    {
        public static bool CheckConfigFile(string? path, ILogger log, bool strict = false) =>
            CheckPath(path, checkRead: false, "Config file", log, strict);

        public static bool CheckSecretFile(string? path, ILogger log, bool strict = false) =>
            CheckPath(path, checkRead: true, "Secret key file", log, strict);

        private static bool CheckPath(string? path, bool checkRead, string label, ILogger log, bool strict)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return true;

            try
            {
                string? issue = OperatingSystem.IsWindows()
                    ? CheckWindows(path, checkRead)
                    : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
#pragma warning disable CA1416
                        ? CheckUnix(path, checkRead)
#pragma warning restore CA1416
                        : null;

                if (issue == null) return true;
                var msg = $"{label} '{path}' has overly permissive permissions ({issue}).";
                if (strict)
                {
                    log.Error(msg + " Refusing to start under strict_config_perms.");
                    return false;
                }
                log.Warn(msg);
                return true;
            }
            catch (Exception ex)
            {
                log.Debug($"{label} permission check skipped for '{path}': {ex.Message}");
                return true;
            }
        }

        [SupportedOSPlatform("windows")]
        private static string? CheckWindows(string path, bool checkRead)
        {
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var acl = new FileInfo(path).GetAccessControl();
            var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

            var badRights = FileSystemRights.WriteData | FileSystemRights.AppendData
                | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes
                | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            if (checkRead)
                badRights |= FileSystemRights.ReadData;

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (!rule.IdentityReference.Equals(users) && !rule.IdentityReference.Equals(everyone)) continue;
                if ((rule.FileSystemRights & badRights) != default)
                    return checkRead ? "Users/Everyone have read or write access" : "Users/Everyone have write access";
            }
            return null;
        }

        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static string? CheckUnix(string path, bool checkRead)
        {
            var mode = File.GetUnixFileMode(path);
            var bad = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            if (checkRead)
                bad |= UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            if ((mode & bad) == UnixFileMode.None)
                return null;
            return checkRead ? "group/other read or write bits set (recommend chmod 600)" : "group/other write bits set (recommend chmod 600/644)";
        }
    }
}
