using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public class MirrorDeletion : IDeletionStrategy
    {
        public Task<ActionResult> HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(destPath))
                {
                    File.SetAttributes(destPath, FileAttributes.Normal);
                    File.Delete(destPath);
                }
                else if (Directory.Exists(destPath))
                {
                    Directory.Delete(destPath, true);
                }
                return Task.FromResult(ActionResult.Ok());
            }
            catch (Exception ex) { return Task.FromResult(ActionResult.Fail(ex.Message)); }
        }
    }

    public class ArchiveDeletion : IDeletionStrategy
    {
        private readonly string _archiveBase;
        private readonly string _destBase;

        public ArchiveDeletion(string archiveBase, string destBase)
        {
            _archiveBase = archiveBase;
            _destBase = destBase;
        }

        void ArchiveFile(string src, string archivePath)
        {
            var dir = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = archivePath + ".tictack.tmp";
            try
            {
                using (var input = File.OpenRead(src))
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                if (!FileHasher.SameContent(src, temp))
                    throw new IOException("Archive validation failed: " + src);
                File.Move(temp, archivePath, true);
                File.Delete(src);
                // Ordering barrier (Pillai): the move and the unlink each
                // change a directory entry; without a dir sync a crash
                // resurrects the unlinked entry. Benign (next parity
                // re-deletes), so best-effort — never fails the archive.
                CopyAction.FlushDirectory(Path.GetDirectoryName(archivePath));
                CopyAction.FlushDirectory(Path.GetDirectoryName(src));
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        public Task<ActionResult> HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var syncRoot = Path.GetDirectoryName(_archiveBase.TrimEnd('\\', '/'));
                string RelFromSyncRoot(string p)
                {
                    if (syncRoot != null && p.StartsWith(syncRoot, StringComparison.OrdinalIgnoreCase))
                        return PathUtil.Relative(p, syncRoot);
                    if (p.StartsWith(_destBase, StringComparison.OrdinalIgnoreCase))
                        return PathUtil.Relative(p, _destBase);
                    return "";
                }

                if (File.Exists(destPath))
                {
                    ArchiveFile(destPath, Path.Combine(_archiveBase, RelFromSyncRoot(destPath)));
                }
                else if (Directory.Exists(destPath))
                {
                    var relDir = RelFromSyncRoot(destPath);
                    foreach (var f in Directory.EnumerateFiles(destPath, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.Combine(relDir, PathUtil.Relative(f, destPath));
                        ArchiveFile(f, Path.Combine(_archiveBase, rel));
                    }
                    Directory.Delete(destPath, true);
                    // Same barrier for the recursive delete above: without
                    // it the whole removed tree can resurrect on crash.
                    CopyAction.FlushDirectory(Path.GetDirectoryName(destPath));
                }
                return Task.FromResult(ActionResult.Ok());
            }
            catch (Exception ex) { return Task.FromResult(ActionResult.Fail(ex.Message)); }
        }
    }

    public static class DeletionStrategyFactory
    {
        public static IDeletionStrategy Create(DeletionConfig? config, string destBase)
        {
            if (config == null) return new ArchiveDeletion(Path.Combine(destBase, ".archive"), destBase);
            var mode = config.Mode != null ? config.Mode.ToLowerInvariant() : null;
            switch (mode)
            {
                case "mirror": return new MirrorDeletion();
                case "archive":
                    var path = config.Path ?? Path.Combine(destBase, ".archive");
                    return new ArchiveDeletion(path, destBase);
                default: return new ArchiveDeletion(Path.Combine(destBase, ".archive"), destBase);
            }
        }
    }

    // Separate Restic-style retention pruner for .archive (independent from .versions).
    public static class ArchivePruner
    {
        public static (int Kept, int Pruned, int Failed) Forget(
            string archiveDir,
            RetentionPolicy policy,
            bool dryRun,
            ILogger log,
            DateTime? now = null)
        {
            if (string.IsNullOrEmpty(archiveDir) || !Directory.Exists(archiveDir))
                return (0, 0, 0);
            if (policy.IsEmpty)
            {
                log.Info("Archive retention skipped (no policy configured): " + archiveDir);
                return (0, 0, 0);
            }

            var files = new List<VersionFile>();
            foreach (var p in Directory.EnumerateFiles(archiveDir, "*", SearchOption.AllDirectories))
            {
                DateTime ts;
                try { ts = File.GetLastWriteTime(p); }
                catch { continue; }
                files.Add(new VersionFile(p, ts, hasTimestamp: true, pinned: false));
            }

            var decisions = policy.Evaluate(files, now ?? DateTime.Now);
            int kept = 0, pruned = 0, failed = 0;
            foreach (var d in decisions)
            {
                if (d.Keep)
                {
                    kept++;
                    continue;
                }
                if (dryRun)
                {
                    log.Info("[DRY-RUN] prune archive: " + d.Path + " (" + d.Reason + ")");
                    pruned++;
                    continue;
                }
                try
                {
                    File.SetAttributes(d.Path, FileAttributes.Normal);
                    File.Delete(d.Path);
                    pruned++;
                }
                catch (Exception ex)
                {
                    log.Warn("Archive prune failed for " + d.Path + ": " + ex.Message);
                    failed++;
                }
            }

            if (!dryRun && pruned > 0)
                RemoveEmptyDirs(archiveDir);
            return (kept, pruned, failed);
        }

        private static void RemoveEmptyDirs(string root)
        {
            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                        Directory.Delete(dir);
                }
                catch { }
            }
        }
    }
}
