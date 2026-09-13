using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public class NoVersioning : IVersioningStrategy
    {
        public Task<ActionResult> ArchivePreviousVersionAsync(string destPath, CancellationToken ct)
        {
            return Task.FromResult(ActionResult.Ok());
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

        public Task<ActionResult> ArchivePreviousVersionAsync(string destPath, CancellationToken ct)
        {
            if (!File.Exists(PathUtil.EnsureExtended(destPath))) return Task.FromResult(ActionResult.Ok());

            try
            {
                ct.ThrowIfCancellationRequested();
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var name = Path.GetFileNameWithoutExtension(destPath);
                var ext = Path.GetExtension(destPath);
                var dir = Path.GetDirectoryName(destPath);
                var syncRoot = Path.GetDirectoryName(_versionBase.TrimEnd('\\', '/'));
                string relDir;
                if (dir != null && syncRoot != null && dir.StartsWith(syncRoot, StringComparison.OrdinalIgnoreCase))
                    relDir = dir.Substring(syncRoot.Length).TrimStart('\\', '/');
                else if (dir != null && dir.StartsWith(_destBase, StringComparison.OrdinalIgnoreCase))
                    relDir = dir.Substring(_destBase.Length).TrimStart('\\', '/');
                else
                    relDir = "";
                var verDir = string.IsNullOrEmpty(relDir) ? _versionBase : Path.Combine(_versionBase, relDir);
                var verFile = Path.Combine(verDir, name + "_" + ts + "_" + Guid.NewGuid().ToString("N") + ext);
                var temp = verFile + ".tictack.tmp";

                Directory.CreateDirectory(verDir);
                using (var input = File.OpenRead(PathUtil.EnsureExtended(destPath)))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                File.Move(temp, verFile);

                // delete oldest versions beyond the cap
                var files = Directory.GetFiles(verDir, name + "_*" + ext);
                if (files.Length > _maxVersions)
                {
                    Array.Sort(files);
                    for (int i = 0; i < files.Length - _maxVersions; i++)
                        File.Delete(files[i]);
                }
                return Task.FromResult(ActionResult.Ok());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Task.FromResult(ActionResult.Fail(ex.Message)); }
        }
    }

    public static class VersioningFactory
    {
        public static IVersioningStrategy Create(VersioningConfig? config, string destBase)
        {
            if (config == null || string.IsNullOrEmpty(config.Path))
                return new NoVersioning();
            return new TimestampVersioning(config.Path, destBase, config.MaxVersions);
        }
    }
}
