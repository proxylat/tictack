using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    // Sampled hash scrub: every run re-hashes 1/ShardCount of the
    // DESTINATION and compares against the stored hash. The shard index
    // rotates in the verify store, so a full pass completes every
    // ShardCount runs. Size+mtime-equal bit-rot is invisible to the 6h
    // parity scan; this is what catches it. A mismatch goes through the
    // normal deletion path (archive + drop the state row), so the next
    // scan recopies the file fresh.
    internal sealed class Scrubber
    {
        internal const int ShardCount = 20;
        internal const string ShardKey = "scrub-shard";

        private readonly SourceConfig _config;
        private readonly IFileFilter? _filter;
        private readonly StateDb? _stateDb;
        private readonly Func<string?, string, string, string, CancellationToken, Task<bool>> _deleteAsync;
        private readonly JobRunStore? _verifyStore;
        private readonly ILogger _log;
        private readonly Func<string, bool> _directoryExists;
        private readonly Func<string, SearchOption, IEnumerable<string>> _enumerateFiles;

        public Scrubber(
            SourceConfig config,
            IFileFilter? filter,
            StateDb? stateDb,
            Func<string?, string, string, string, CancellationToken, Task<bool>> deleteAsync,
            JobRunStore? verifyStore,
            ILogger log,
            Func<string, bool> directoryExists,
            Func<string, SearchOption, IEnumerable<string>> enumerateFiles)
        {
            _config = config;
            _filter = filter;
            _stateDb = stateDb;
            _deleteAsync = deleteAsync;
            _verifyStore = verifyStore;
            _log = log;
            _directoryExists = directoryExists;
            _enumerateFiles = enumerateFiles;
        }

        // Deterministic shard for a destination-relative path, so tests can
        // place files exactly. StableHash is 8 lowercase hex chars.
        internal static int ShardOf(string rel)
        {
            var v = Convert.ToUInt32(DeferredDeletion.StableHash(rel), 16);
            return (int)(v % (uint)ShardCount);
        }

        public async Task RunAsync(CancellationToken ct)
        {
            if (_stateDb == null || _verifyStore == null) return;
            if (!_directoryExists(_config.Path)) return;
            try
            {
                var shard = _verifyStore.GetCounter(ShardKey) % ShardCount;
                if (shard < 0) shard += ShardCount;
                var verified = 0;
                var mismatched = 0;
                var skipped = 0;
                foreach (var f in _enumerateFiles(_config.Destination, SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    string rel;
                    try { rel = PathUtil.Relative(f, _config.Destination); }
                    catch { skipped++; continue; }
                    if (_filter != null && !_filter.ShouldProcess(f)) continue;
                    if (ShardOf(rel) != shard) continue;
                    var r = await ScrubOneAsync(f, rel, ct);
                    if (r < 0) skipped++;
                    else { verified++; mismatched += r; }
                }
                _verifyStore.SetCounter(ShardKey, shard + 1);
                if (verified > 0)
                    _log.Info("Scrub: shard " + shard + "/" + ShardCount + ": " + verified + " verified, " + mismatched + " mismatched, " + skipped + " skipped");
                else
                    _log.Debug("Scrub: shard " + shard + "/" + ShardCount + ": nothing to verify");
            }
            catch (Exception ex) { _log.Warn("Scrub failed: " + ex.Message); }
        }

        // -1 skipped (missing source, unknown file, unreadable), 0 match,
        // 1 mismatch with recovery already queued through the deleter.
        private async Task<int> ScrubOneAsync(string destPath, string rel, CancellationToken ct)
        {
            try
            {
                var srcFull = PathUtil.EnsureExtended(Path.Combine(_config.Path, rel));
                var dstFull = PathUtil.EnsureExtended(destPath);
                var srcInfo = new FileInfo(srcFull);
                var dstInfo = new FileInfo(dstFull);
                if (!srcInfo.Exists || !dstInfo.Exists) return -1;
                var row = await _stateDb!.GetFullStateAsync(rel);
                if (row == null) return -1;
                string srcHash;
                if (row.Value.hash != null
                    && row.Value.size == srcInfo.Length
                    && row.Value.mtime == srcInfo.LastWriteTimeUtc.Ticks)
                {
                    srcHash = row.Value.hash;
                }
                else
                {
                    srcHash = FileHasher.ComputeHex(srcFull, null);
                    try { await _stateDb.UpdateHashAsync(rel, srcHash); }
                    catch (Exception ex) { _log.Debug("Scrub hash store failed: " + ex.Message); }
                }
                var dstHash = FileHasher.ComputeHex(dstFull, null);
                if (string.Equals(srcHash, dstHash, StringComparison.Ordinal)) return 0;
                _log.Warn("Scrub mismatch, re-queueing from source: " + rel);
                await _deleteAsync(null, destPath, rel, "Scrub mismatch", ct);
                return 1;
            }
            catch (Exception ex)
            {
                // Locked or vanished mid-read is a skip, never a mismatch:
                // deleting on a read failure would destroy good data.
                _log.Debug("Scrub skipped: " + rel + " (" + ex.Message + ")");
                return -1;
            }
        }
    }
}
