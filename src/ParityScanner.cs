using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    // Mirror parity: destination files/dirs with no source counterpart are
    // deleted through the configured strategy. Extracted from SyncPipeline so
    // the orchestrator does not also own destination reconciliation.
    internal sealed class ParityScanner
    {
        private readonly SourceConfig _config;
        private readonly IFileFilter? _filter;
        private readonly ILogger _log;
        private readonly Func<string, bool> _directoryExists;
        private readonly Func<string, SearchOption, IEnumerable<string>> _enumerateFiles;
        private readonly Func<string, SearchOption, IEnumerable<string>> _enumerateDirectories;
        private readonly TrackedDeleter _deleter;

        public ParityScanner(
            SourceConfig config,
            IFileFilter? filter,
            Func<string, bool> directoryExists,
            Func<string, SearchOption, IEnumerable<string>> enumerateFiles,
            Func<string, SearchOption, IEnumerable<string>> enumerateDirectories,
            TrackedDeleter deleter,
            ILogger log)
        {
            _config = config;
            _filter = filter;
            _directoryExists = directoryExists;
            _enumerateFiles = enumerateFiles;
            _enumerateDirectories = enumerateDirectories;
            _deleter = deleter;
            _log = log;
        }

        public async Task EnforceAsync(CancellationToken ct)
        {
            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!_directoryExists(_config.Path))
            {
                _log.Warn("Source folder missing, parity cleanup blocked: " + _config.Path);
                return;
            }
            try
            {
                foreach (var f in _enumerateFiles(_config.Path, SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) return;
                    if (_filter != null && !_filter.ShouldProcess(f)) continue;
                    sourcePaths.Add(PathUtil.Relative(f, _config.Path));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is DirectoryNotFoundException)
            {
                _log.Warn("Source scan incomplete, parity cleanup blocked: " + ex.Message);
                return;
            }
            await EnforceAsync(sourcePaths, ct);
        }

        internal static bool UnderDir(string path, string prefix)
        {
            return path.StartsWith(prefix + '/', StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + '\\', StringComparison.OrdinalIgnoreCase);
        }

        public async Task EnforceAsync(HashSet<string> sourcePaths, CancellationToken ct)
        {
            if (!_directoryExists(_config.Destination)) return;

            var staleFiles = new List<(string path, string rel)>();
            try
            {
                foreach (var f in _enumerateFiles(_config.Destination, SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) return;
                    var name = Path.GetFileName(f);
                    if (name == ".tictack.lock" || name == ".tictack-deferred.json") continue;
                    var rel = PathUtil.Relative(f, _config.Destination);
                    if (UnderDir(rel, ".archive") || UnderDir(rel, ".versions")) continue;
                    if (sourcePaths.Contains(rel)) continue;
                    staleFiles.Add((f, rel));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is DirectoryNotFoundException)
            {
                _log.Warn("Destination scan incomplete, parity cleanup blocked: " + ex.Message);
                return;
            }

            if (staleFiles.Count > 0)
            {
                foreach (var sf in staleFiles)
                {
                    await _deleter.DeleteAsync(null, sf.path, sf.rel, "Parity deletion failed", ct);
                }
                _log.Info("Cleanup: archived " + staleFiles.Count + " stale files");
            }

            var sourceDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in sourcePaths)
            {
                var parent = Path.GetDirectoryName(file);
                while (!string.IsNullOrEmpty(parent) && parent != ".")
                {
                    sourceDirectories.Add(parent);
                    parent = Path.GetDirectoryName(parent);
                }
            }

            IEnumerable<string> destinationDirectories;
            try
            {
                destinationDirectories = _enumerateDirectories(_config.Destination, SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is DirectoryNotFoundException)
            {
                _log.Warn("Destination directory scan incomplete, parity cleanup blocked: " + ex.Message);
                return;
            }
            foreach (var dir in destinationDirectories)
            {
                if (ct.IsCancellationRequested) return;
                var rel = PathUtil.Relative(dir, _config.Destination);
                if (rel == ".archive" || UnderDir(rel, ".archive")) continue;
                if (rel == ".versions" || UnderDir(rel, ".versions")) continue;
                if (rel == ".tictack.lock") continue;
                if (sourceDirectories.Contains(rel)) continue;
                _log.Debug("Cleanup: removing stale dir " + dir);
                await _deleter.DeleteAsync(null, dir, rel, "Parity deletion failed", ct);
            }
        }
    }
}
