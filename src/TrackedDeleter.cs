using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    // Deletion + StateDb bookkeeping shared by the event path, the delete
    // guard flush, the deferred flush, and the parity scanner. The label is
    // the log prefix the call sites used before they were merged.
    internal sealed class TrackedDeleter
    {
        private readonly IDeletionStrategy _deletion;
        private readonly StateDb? _stateDb;
        private readonly ILogger _log;

        public TrackedDeleter(IDeletionStrategy deletion, StateDb? stateDb, ILogger log)
        {
            _deletion = deletion;
            _stateDb = stateDb;
            _log = log;
        }

        public async Task<bool> DeleteAsync(string? sourcePath, string destPath, string rel, string label, CancellationToken ct)
        {
            var result = await _deletion.HandleDeletionAsync(sourcePath, destPath, ct);
            if (!result.Success)
            {
                _log.Error(label + ": " + destPath + ": " + result.ErrorMessage);
                return false;
            }
            // Barrier symmetry with the copy path (file fsync → rename →
            // dir sync): without this a crash can resurrect the unlinked
            // entry. Benign when it fails — next parity re-deletes — so a
            // failed sync never fails the delete, it just gets logged.
            if (!CopyAction.FlushDirectory(Path.GetDirectoryName(destPath)))
                _log.Debug(label + ": directory sync failed after delete: " + destPath);
            if (_stateDb != null)
            {
                try { await _stateDb.DeleteAsync(rel); }
                catch (Exception ex) { _log.Debug("StateDb delete failed: " + ex.Message); }
            }
            return true;
        }
    }
}
