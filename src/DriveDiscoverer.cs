using System;
using System.Collections.Generic;
using System.IO;

namespace TicTack
{
    public static class DriveDiscoverer
    {
        public static List<string> GetEligibleDrives(ResticDrivesConfig cfg, HashSet<string> excludeDrives, ILogger log)
        {
            var results = new List<string>();
            var sysDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "").TrimEnd('\\');
            var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sysDrive };
            if (cfg.ExcludeDrives != null)
                foreach (var d in cfg.ExcludeDrives)
                    exclude.Add(d.TrimEnd('\\'));
            if (excludeDrives != null)
                foreach (var d in excludeDrives)
                    exclude.Add(d.TrimEnd('\\'));

            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType == DriveType.NoRootDirectory ||
                    di.DriveType == DriveType.Ram ||
                    di.DriveType == DriveType.CDRom ||
                    di.DriveType == DriveType.Network)
                    continue;

                if (!di.IsReady) continue;

                var root = di.RootDirectory.FullName.TrimEnd('\\');
                if (exclude.Contains(root)) continue;

                if (cfg.RequireMarkerFile)
                {
                    var marker = Path.Combine(root, cfg.MarkerFileName ?? ".restic-target");
                    if (!File.Exists(marker))
                    {
                        log.Debug("Skipping " + root + ": no marker file (" + Path.GetFileName(marker) + ")");
                        continue;
                    }
                }

                results.Add(root + "\\");
                log.Info("Discovered eligible drive: " + root);
            }

            return results;
        }
    }
}
