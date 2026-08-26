using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public class MirrorDeletion : IDeletionStrategy
    {
        public Task HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct)
        {
            try
            {
                if (File.Exists(destPath))
                {
                    File.SetAttributes(destPath, FileAttributes.Normal);
                    File.Delete(destPath);
                }
                else if (Directory.Exists(destPath))
                {
                    Directory.Delete(destPath, true);
                }
            }
            catch { }
            return Task.CompletedTask;
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

            if (File.Exists(archivePath))
            {
                File.SetAttributes(archivePath, FileAttributes.Normal);
                File.Delete(archivePath);
            }
            try { File.Move(src, archivePath); }
            catch
            {
                File.Copy(src, archivePath, overwrite: true);
                File.Delete(src);
            }
        }

        public Task HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct)
        {
            try
            {
                var syncRoot = Path.GetDirectoryName(_archiveBase.TrimEnd('\\', '/'));
                string RelFromSyncRoot(string p)
                {
                    if (syncRoot != null && p.StartsWith(syncRoot, StringComparison.OrdinalIgnoreCase))
                        return p.Substring(syncRoot.Length).TrimStart('\\', '/');
                    if (p.StartsWith(_destBase, StringComparison.OrdinalIgnoreCase))
                        return p.Substring(_destBase.Length).TrimStart('\\', '/');
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
                        var rel = Path.Combine(relDir, f.Substring(destPath.Length).TrimStart('\\', '/'));
                        ArchiveFile(f, Path.Combine(_archiveBase, rel));
                    }
                    Directory.Delete(destPath, true);
                }
            }
            catch { }
            return Task.CompletedTask;
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
