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
        private static List<(string Root, bool Ready)>? _cache;
        private static DateTime _cacheAt;
        private static Func<DriveInfo[]>? _cacheProbe;

        private static List<(string Root, bool Ready)> GetCachedDrives(Func<DriveInfo[]>? getDrives)
        {
            getDrives ??= DefaultProbe;
            lock (CacheLock)
            {
                if (_cache != null && ReferenceEquals(_cacheProbe, getDrives)
                    && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(30))
                    return _cache;
                _cache = getDrives()
                    .Select(d =>
                    {
                        bool ready;
                        try { ready = d.IsReady; } catch { ready = false; }
                        return (d.RootDirectory.FullName, ready);
                    })
                    .ToList();
                _cacheAt = DateTime.UtcNow;
                _cacheProbe = getDrives;
                return _cache;
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
