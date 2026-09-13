using System;
using System.Collections.Generic;
using System.IO;

namespace TicTack
{
    public static class DriveDiscoverer
    {
        public static List<string> GetEligibleDrives(ExternalDrivesConfig cfg, HashSet<string> excludeDrives, ILogger log)
        {
            var drives = new List<DriveCandidate>();
            foreach (var di in DriveInfo.GetDrives())
                drives.Add(new DriveCandidate(di.RootDirectory.FullName, di.DriveType, di.IsReady));
            return GetEligibleDrives(cfg, excludeDrives, log, drives);
        }

        internal static List<string> GetEligibleDrives(ExternalDrivesConfig cfg, HashSet<string> excludeDrives,
            ILogger log, IEnumerable<DriveCandidate> drives)
        {
            var results = new List<string>();
            var sysDrive = NormalizeRoot(Path.GetPathRoot(Environment.SystemDirectory) ?? "");
            var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sysDrive };
            if (cfg.ExcludeDrives != null)
                foreach (var d in cfg.ExcludeDrives)
                    exclude.Add(NormalizeRoot(d));
            if (excludeDrives != null)
                foreach (var d in excludeDrives)
                    exclude.Add(NormalizeRoot(d));

            foreach (var di in drives)
            {
                if (di.Type == DriveType.NoRootDirectory ||
                    di.Type == DriveType.Ram ||
                    di.Type == DriveType.CDRom ||
                    di.Type == DriveType.Network)
                    continue;

                if (!di.IsReady) continue;

                var root = NormalizeRoot(di.Root);
                if (exclude.Contains(root)) continue;

                if (cfg.RequireMarkerFile)
                {
                    var marker = Path.Combine(root, cfg.MarkerFileName ?? ".tictack-target");
                    if (!File.Exists(marker))
                    {
                        log.Debug("Skipping " + root + ": no marker file (" + Path.GetFileName(marker) + ")");
                        continue;
                    }
                }

                results.Add(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar);
                log.Info("Discovered eligible drive: " + root);
            }

            return results;
        }

        private static string NormalizeRoot(string root)
        {
            var trimmed = root.TrimEnd('\\', '/');
            return trimmed.Length == 0 ? Path.DirectorySeparatorChar.ToString() : trimmed;
        }

        internal readonly record struct DriveCandidate(string Root, DriveType Type, bool IsReady);
    }
}
