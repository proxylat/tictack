using System;
using System.IO;
using System.Threading.Tasks;

namespace TicTack
{
    public static class ValidatorFactory
    {
        public static IValidator Create(VerificationLevel level, IFileAccessor? accessor = null)
        {
            switch (level)
            {
                case VerificationLevel.Size:
                case VerificationLevel.DateAndSize:
                    return new SizeValidator();
                case VerificationLevel.Hash:
                case VerificationLevel.Full:
                    return new HashValidator(accessor);
                default: throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown verification level");
            }
        }
    }

    // Validation mirrors comparison; the validators wrap the comparers instead
    // of duplicating their bodies (the two had drifted as copy-paste twins).
    public class SizeValidator : IValidator
    {
        private readonly SizeComparer _comparer = new SizeComparer();

        public Task<bool> ValidateAsync(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null) =>
            Task.FromResult(_comparer.AreEqual(sourcePath, destPath, sourceSnapshot));
    }

    public class HashValidator : IValidator
    {
        private readonly HashComparer _comparer;

        public HashValidator(IFileAccessor? accessor = null) { _comparer = new HashComparer(accessor); }

        public Task<bool> ValidateAsync(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null) =>
            Task.FromResult(_comparer.AreEqual(sourcePath, destPath, sourceSnapshot));

        // Copy-time fast path: the source hash was captured while writing, so only the
        // destination needs hashing. Caller must have verified the source is unchanged
        // since the hash was taken (fresh-vs-pre snapshot equality); otherwise the
        // trusted hash is stale and this must not run. Length pre-check avoids a
        // pointless full read on a truncated destination.
        public Task<bool> ValidateWithSourceHashAsync(string destPath, string sourceHash, FileSnapshot sourceSnapshot)
        {
            FileInfo dstInfo;
            try { dstInfo = new FileInfo(destPath); }
            catch { return Task.FromResult(false); }
            if (!dstInfo.Exists || dstInfo.Length != sourceSnapshot.Length)
                return Task.FromResult(false);
            string destHash;
            try { destHash = _comparer.HashFile(destPath); }
            catch { return Task.FromResult(false); }
            return Task.FromResult(string.Equals(destHash, sourceHash, StringComparison.Ordinal));
        }
    }
}
