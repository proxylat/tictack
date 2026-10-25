using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using TicTack;

namespace Corpus
{
    // Servicing mount (research item 6): read/write mount of one WIM index
    // for offline edits, then unmount with commit or discard. DISM on
    // Windows, wimlib-imagex mountrw (FUSE) on Linux — same IWimRunner string
    // protocol as WimBackend, only the arg shapes differ. A marker file
    // inside the mount dir claims the mount: unmount refuses a directory
    // without (or with a foreign) marker, so we never unmount someone
    // else's mount. Over-large scope skipped: no index auto-pick (caller
    // passes the 1-based index), no --rebuild/--check knobs.

    // MountDir problems that are not about content (missing dir): names the
    // path, zero runner calls.
    public sealed class WimMountRefusedException : SingleFileBackupException
    {
        public string MountDir { get; }

        public WimMountRefusedException(string mountDir, string reason)
            : base($"refusing mount at {mountDir}: {reason}")
        {
            MountDir = mountDir;
        }
    }

    // Never mount over user files: the dir must exist and be empty.
    public sealed class WimMountNonEmptyException : SingleFileBackupException
    {
        public string MountDir { get; }

        public WimMountNonEmptyException(string mountDir)
            : base($"refusing mount over non-empty {mountDir} — never mount over user files (empty the directory first)")
        {
            MountDir = mountDir;
        }
    }

    // Unmount without our marker (absent, unreadable, or foreign): refuse
    // loud — never unmount someone else's mount.
    public sealed class WimMountMissingMarkerException : SingleFileBackupException
    {
        public string MountDir { get; }

        public WimMountMissingMarkerException(string mountDir, string detail)
            : base($"refusing unmount of {mountDir}: {detail}")
        {
            MountDir = mountDir;
        }
    }

    public static class WimMount
    {
        public const string MarkerFileName = ".corpus-mount.json";

        internal sealed record Marker(string ImagePath, int Index, long MountedUtcTicks);

        // Read/write mount (servicing needs write — deliberately NO
        // /ReadOnly; the read-only verify path in SingleFileBackup keeps
        // its own /ReadOnly mount). Writes the claim marker via temp+rename
        // after the backend reports success.
        public static void Mount(string imagePath, int index, string mountDir, IWimRunner runner, ILogger log)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(mountDir);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (index < 1) throw new ArgumentOutOfRangeException(nameof(index), "WIM image index is 1-based");
            if (!File.Exists(imagePath))
                throw new SingleFileSourceNotFoundException(imagePath);
            if (!Directory.Exists(mountDir))
                throw new WimMountRefusedException(mountDir, "mount directory does not exist — create the empty directory first");
            if (Directory.EnumerateFileSystemEntries(mountDir).Any())
                throw new WimMountNonEmptyException(mountDir);
            var wimlib = runner.Backend == WimBackendKind.Wimlib;
            // wimlib shape from `man wimmountrw` (probed 2026-10-17):
            // mountrw WIMFILE [IMAGE] DIRECTORY — IMAGE is the bare 1-based
            // positional. DISM shape mirrors the house /Mount-Image form
            // (SingleFileBackup verify) minus /ReadOnly — on-box DISM
            // servicing-mount probe still owed (no Windows in this container).
            var idx = index.ToString(CultureInfo.InvariantCulture);
            var args = wimlib
                ? $"mountrw {ProcessRunner.QuoteArg(imagePath)} {idx} {ProcessRunner.QuoteArg(mountDir)}"
                : $"/Mount-Image /ImageFile:{ProcessRunner.QuoteArg(imagePath)} /Index:{idx} /MountDir:{ProcessRunner.QuoteArg(mountDir)}";
            var r = runner.Run(args);
            if (r.ExitCode != 0)
                throw wimlib
                    ? new WimBackendException(imagePath, args, r)
                    : new SingleFileDismException(imagePath, args, r);
            WriteMarker(mountDir, new Marker(imagePath, index, DateTime.UtcNow.Ticks));
            log.Info($"mounted {imagePath} index {idx} at {mountDir} (read/write)");
        }

        // commit=true saves edits back into the WIM, false discards them.
        // wimlib discard shape: bare `unmount DIR` — there is NO --discard
        // flag (`wimlib-imagex unmount --help` + man wimunmount, probed
        // 2026-10-17; commit is the opt-in --commit). DISM commit carries
        // /CheckIntegrity (shape from knowledge — on-box DISM probe owed).
        public static void Unmount(string mountDir, bool commit, IWimRunner runner, ILogger log)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mountDir);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            var markerPath = Path.Combine(mountDir, MarkerFileName);
            var marker = ReadMarker(mountDir, markerPath);
            var wimlib = runner.Backend == WimBackendKind.Wimlib;
            var args = wimlib
                ? commit
                    ? $"unmount {ProcessRunner.QuoteArg(mountDir)} --commit"
                    : $"unmount {ProcessRunner.QuoteArg(mountDir)}"
                : commit
                    ? $"/Unmount-Image /MountDir:{ProcessRunner.QuoteArg(mountDir)} /Commit /CheckIntegrity"
                    : $"/Unmount-Image /MountDir:{ProcessRunner.QuoteArg(mountDir)} /Discard";
            // Commit path: the marker lives inside the mounted view, so
            // remove it BEFORE the backend runs — otherwise the commit bakes
            // our bookkeeping into the serviced image. On backend failure the
            // marker is rewritten so the still-mounted dir stays claimed.
            var preDeleted = false;
            if (commit && File.Exists(markerPath))
            {
                File.Delete(markerPath);
                preDeleted = true;
            }
            var r = runner.Run(args);
            if (r.ExitCode != 0)
            {
                if (preDeleted)
                {
                    try { WriteMarker(mountDir, marker); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        log.Warn($"unmount failed and marker reclaim failed for {markerPath}: {ex.Message}");
                    }
                }
                throw wimlib
                    ? new WimBackendException(marker.ImagePath, args, r)
                    : new SingleFileDismException(marker.ImagePath, args, r);
            }
            // Discard path (and commit leftovers under fakes, where no real
            // mount hides the file): best-effort delete if still present.
            if (File.Exists(markerPath))
                File.Delete(markerPath);
            log.Info($"unmounted {mountDir} ({(commit ? "committed" : "discarded")})");
        }

        private static Marker ReadMarker(string mountDir, string markerPath)
        {
            if (!File.Exists(markerPath))
                throw new WimMountMissingMarkerException(mountDir, $"no {MarkerFileName} marker — refusing to unmount a directory we did not mount (never unmount someone else's mount)");
            Marker? marker = null;
            string? failure = null;
            try
            {
                marker = JsonSerializer.Deserialize(File.ReadAllText(markerPath), CorpusJsonContext.Default.Marker);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                failure = ex.GetType().Name;
            }
            if (failure != null || marker is null || string.IsNullOrWhiteSpace(marker.ImagePath) || marker.Index < 1)
                throw new WimMountMissingMarkerException(mountDir, $"foreign marker at {markerPath} ({failure ?? "schema mismatch"}) — refusing to unmount a directory we did not mount");
            return marker;
        }

        private static void WriteMarker(string mountDir, Marker marker)
        {
            var path = Path.Combine(mountDir, MarkerFileName);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(marker, CorpusJsonContext.Default.Marker));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
