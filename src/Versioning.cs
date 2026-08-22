using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public class NoVersioning : IVersioningStrategy
    {
        public Task ArchivePreviousVersionAsync(string destPath, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }

    public class TimestampVersioning : IVersioningStrategy
    {
        private readonly string _versionBase;
        private readonly string _destBase;
        private readonly int _maxVersions;

        public TimestampVersioning(string versionBase, string destBase, int maxVersions = 10)
        {
            _versionBase = versionBase;
            _destBase = destBase;
            _maxVersions = Math.Max(1, maxVersions);
        }

        public Task ArchivePreviousVersionAsync(string destPath, CancellationToken ct)
        {
            if (!File.Exists(PathUtil.EnsureExtended(destPath))) return Task.CompletedTask;

            try
            {
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var name = Path.GetFileNameWithoutExtension(destPath);
                var ext = Path.GetExtension(destPath);
                var relDir = Path.GetRelativePath(_destBase, Path.GetDirectoryName(destPath));
                var verDir = relDir == "." ? _versionBase : Path.Combine(_versionBase, relDir);
                var verFile = Path.Combine(verDir, name + "_" + ts + ext);

                Directory.CreateDirectory(verDir);
                File.Copy(PathUtil.EnsureExtended(destPath), verFile, overwrite: false);

                // ponytail: limit versions by deleting oldest
                var files = Directory.GetFiles(verDir, name + "_*" + ext);
                if (files.Length > _maxVersions)
                {
                    Array.Sort(files);
                    for (int i = 0; i < files.Length - _maxVersions; i++)
                        File.Delete(files[i]);
                }
            }
            catch { }
            return Task.CompletedTask;
        }
    }

    public static class VersioningFactory
    {
        public static IVersioningStrategy Create(VersioningConfig config, string destBase)
        {
            if (config == null || string.IsNullOrEmpty(config.Path))
                return new NoVersioning();
            return new TimestampVersioning(config.Path, destBase, config.MaxVersions);
        }
    }
}
