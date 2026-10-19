using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TicTack
{
    // Mount-table snapshot cache: GetDrives()+IsReady costs ~218us/26KB per
    // call, and the initial sync probes once per file. Fail-closed TTL:
    // a vanished drive reports ready for at most 30s (copy then errors),
    // a fresh drive waits at most 30s (start-retry loop covers it).
    internal static class DriveGuard
    {
        private static readonly Func<DriveInfo[]> DefaultProbe = static () => DriveInfo.GetDrives();
        private static readonly object CacheLock = new();
        private static readonly Dictionary<Func<DriveInfo[]>, (List<(string Root, bool Ready)> Drives, DateTime At)> _caches = new();

        private static List<(string Root, bool Ready)> GetCachedDrives(Func<DriveInfo[]>? getDrives)
        {
            getDrives ??= DefaultProbe;
            lock (CacheLock)
            {
                // Per-probe entries: concurrent callers with different probes
                // (tests, future call sites) must not evict each other, or a
                // second call within TTL reprobes and the count drifts.
                if (_caches.TryGetValue(getDrives, out var hit)
                    && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(30))
                    return hit.Drives;
                var fresh = getDrives()
                    .Select(d =>
                    {
                        bool ready;
                        try { ready = d.IsReady; } catch { ready = false; }
                        return (d.RootDirectory.FullName, ready);
                    })
                    .ToList();
                _caches[getDrives] = (fresh, DateTime.UtcNow);
                return fresh;
            }
        }

        internal static bool IsReady(string path, Func<DriveInfo[]>? getDrives = null)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                return SelectReady(fullPath, GetCachedDrives(getDrives));
            }
            catch { return false; }
        }

        // Pure reachability selection over a mount list, split out so the
        // benchmark measures the selection without the probe or cache.
        internal static bool SelectReady(string fullPath, IReadOnlyList<(string Root, bool Ready)> drives)
        {
            string? best = null;
            foreach (var d in drives)
            {
                if (!d.Ready) continue;
                if (!fullPath.StartsWith(d.Root, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || d.Root.Length > best.Length) best = d.Root;
            }
            if (best == null) return false;
            if (OperatingSystem.IsLinux() && best == "/"
                && (fullPath == "/srv" || fullPath.StartsWith("/srv/", StringComparison.Ordinal)
                    || fullPath == "/mnt" || fullPath.StartsWith("/mnt/", StringComparison.Ordinal)
                    || fullPath == "/media" || fullPath.StartsWith("/media/", StringComparison.Ordinal)
                    || fullPath.StartsWith("/run/media/", StringComparison.Ordinal)))
                return false;
            return true;
        }
    }
}
