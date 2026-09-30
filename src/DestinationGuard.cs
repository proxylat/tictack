using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TicTack
{
    // Destination self-defense: harden the backup root so only the service
    // identity can write. Ransomware running as the user then gets
    // access-denied instead of encrypting the backups. Opt-in behind
    // sync.destination_protect (default off): service mode only — a CLI run
    // as the user is the user, and will lock itself out too. Enforces the
    // inheritable root rule every start (cheap, one directory); existing
    // trees need a one-time recursive pass (see README). Toggle-off does
    // not revert ACLs. Never fails startup: every failure is a Warn.
    internal static class DestinationGuard
    {
        public static void Enforce(string destination, ILogger log)
        {
            try
            {
                if (!Directory.Exists(destination))
                    Directory.CreateDirectory(destination);
                if (OperatingSystem.IsWindows())
                    EnforceWindows(destination, log);
                else
#pragma warning disable CA1416 // Single-platform TFM (net10.0-windows): the analyzer never narrows else off Windows; runtime dispatch is correct — EnforceLinux carries [SupportedOSPlatform("linux")]
                    EnforceLinux(destination, log);
#pragma warning restore CA1416
            }
            catch (Exception ex) { log.Warn("Destination protection failed, continuing unprotected: " + ex.Message); }
        }

        [SupportedOSPlatform("windows")]
        private static void EnforceWindows(string destination, ILogger log)
        {
            try
            {
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                var rule = new FileSystemAccessRule(users,
                    FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Deny);
                var dir = new DirectoryInfo(destination);
                var security = dir.GetAccessControl();
                security.AddAccessRule(rule);
                dir.SetAccessControl(security);
                // Inheritable: files the service creates below pick it up.
                log.Info("Destination protected: Users write denied (service SYSTEM unaffected): " + destination);
            }
            catch (Exception ex) { log.Warn("Destination protection failed, continuing unprotected: " + ex.Message); }
        }

        [SupportedOSPlatform("linux")]
        private static void EnforceLinux(string destination, ILogger log)
        {
            try
            {
                var mode = File.GetUnixFileMode(destination);
                var stripped = mode & ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
                if (stripped != mode)
                {
                    File.SetUnixFileMode(destination, stripped);
                    log.Info("Destination protected: group/other write stripped (service root unaffected): " + destination);
                }
                else
                {
                    log.Info("Destination already group/other write-free: " + destination);
                }
            }
            catch (Exception ex) { log.Warn("Destination protection failed, continuing unprotected: " + ex.Message); }
        }
    }
}
