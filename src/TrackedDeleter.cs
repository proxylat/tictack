using System;
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
            if (_stateDb != null)
            {
                try { await _stateDb.DeleteAsync(rel); }
                catch (Exception ex) { _log.Debug("StateDb delete failed: " + ex.Message); }
            }
            return true;
        }
    }
}
