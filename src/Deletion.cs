using System;
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
}
