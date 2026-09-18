using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TicTack
{
    public static class VolumeResolver
    {
        // MA0009/MA0023 false positives, kept explicit: the pattern is a
        // negated character class (linear time, no ReDoS shape) and group 1
        // is load-bearing (m.Groups[1].Value below), which ExplicitCapture
        // would stop capturing.
#pragma warning disable MA0009, MA0023
        private static readonly Regex VolumePattern = new Regex(@"\[([^\]]+)\]", RegexOptions.Compiled);
#pragma warning restore MA0009, MA0023

        public static string Resolve(string path)
        {
            return Resolve(path, BuildVolumeMap(GetVolumes(null)));
        }

        internal static string Resolve(string path, IEnumerable<(string label, string root)> volumes)
        {
            return Resolve(path, BuildVolumeMap(volumes));
        }

        private static string Resolve(string path, Dictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (map.Count == 0) return path;
            return VolumePattern.Replace(path, m =>
            {
                var label = m.Groups[1].Value;
                return map.TryGetValue(label, out var root) ? root : m.Value;
            });
        }

        public static void ResolveConfig(TicTackConfig cfg, ILogger? log = null)
        {
            ResolveConfig(cfg, GetVolumes(log));
        }

        internal static void ResolveConfig(TicTackConfig cfg, IEnumerable<(string label, string root)> volumes)
        {
            if (cfg == null || cfg.Sources == null) return;

            // Build the map once per config instead of once per path.
            var map = BuildVolumeMap(volumes);

            foreach (var src in cfg.Sources)
            {
                src.Path = Resolve(src.Path, map);
                for (var i = 0; i < src.Paths.Count; i++)
                    src.Paths[i] = Resolve(src.Paths[i], map);
                src.Destination = Resolve(src.Destination, map);
                src.StateDbPath = Resolve(src.StateDbPath, map);
                if (src.Sync != null && src.Sync.Versioning != null && src.Sync.Versioning.Path != null)
                    src.Sync.Versioning.Path = Resolve(src.Sync.Versioning.Path, map);
                if (src.Sync != null && src.Sync.Deletion != null && src.Sync.Deletion.Path != null)
                    src.Sync.Deletion.Path = Resolve(src.Sync.Deletion.Path, map);
            }

            if (cfg.Logging != null && cfg.Logging.Path != null)
                cfg.Logging.Path = Resolve(cfg.Logging.Path, map);
            if (cfg.Logging != null && cfg.Logging.AlertPath != null)
                cfg.Logging.AlertPath = Resolve(cfg.Logging.AlertPath, map);

            foreach (var job in cfg.Jobs ?? Enumerable.Empty<JobConfig>())
            {
                if (job.WorkingDir != null)
                    job.WorkingDir = Resolve(job.WorkingDir, map);
            }

            if (cfg.ExternalDrives != null && cfg.ExternalDrives.WorkingDir != null)
                cfg.ExternalDrives.WorkingDir = Resolve(cfg.ExternalDrives.WorkingDir, map);
        }

        private static IEnumerable<(string label, string root)> GetVolumes(ILogger? log)
        {
            try
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady && !string.IsNullOrEmpty(d.VolumeLabel))
                    .Select(d => (d.VolumeLabel, d.RootDirectory.FullName.TrimEnd('\\')))
                    .ToList();
            }
            catch (Exception ex)
            {
                log?.Warn("Volume table unavailable, [Label] paths left unresolved: " + ex.Message);
                return Array.Empty<(string label, string root)>();
            }
        }

        private static Dictionary<string, string> BuildVolumeMap(IEnumerable<(string label, string root)> volumes)
        {
            try
            {
                return volumes
                    .Where(v => !string.IsNullOrEmpty(v.label) && !string.IsNullOrEmpty(v.root))
                    .GroupBy(v => v.label, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToDictionary(v => v.label,
                        v => v.root,
                        StringComparer.OrdinalIgnoreCase);
            }
            catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }
    }
}
