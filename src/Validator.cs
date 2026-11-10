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
    // Content-only append proof for the post-copy validation mismatch
    // path. Bytes are truth: no stat/mtime flux detection. Returns a
    // benign reason when the mismatch is a mid-copy source race (the
    // next cycle re-copies and converges), null for a true failure.
    // (a) current source shorter than the bytes written -> truncation /
    // rotation; (b) source grew past the bytes written -> pure append
    // (dest == first-N prefix) OR middle-edit / rotation / torn write
    // (prefix differs). Both converge by re-copying next cycle, so both
    // are benign, never Error. Same-size drift (no growth evidence) and
    // short dest (our own write incomplete) stay validation failures.
    internal static class ValidationAppendProof
    {
        internal static string? Classify(string sourcePath, string destPath, long bytesWritten, long currentLength)
        {
            if (currentLength < bytesWritten)
                return "source truncated during copy";
            string? destHash;
            try
            {
                var destInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                if (!destInfo.Exists || destInfo.Length != bytesWritten) return null;
                destHash = FileHasher.ComputeHex(destPath, null);
            }
            catch { return null; }
            if (currentLength == bytesWritten)
            {
                // The snapshot can predate a post-snapshot append (source
                // grew after the validation re-read): re-stat, grown ->
                // race, benign. Still same-size -> true drift, failure.
                try { currentLength = new FileInfo(PathUtil.EnsureExtended(sourcePath)).Length; }
                catch { return null; }
                if (currentLength == bytesWritten) return null;
            }
            var prefixHash = FileHasher.ComputeHexPrefix(sourcePath, bytesWritten, null);
            if (prefixHash == null) return null;
            return string.Equals(destHash, prefixHash, StringComparison.Ordinal)
                ? "source appended during copy"
                : "source changed during copy";
        }
    }

    public sealed class SizeValidator : IValidator
    {
        private readonly SizeComparer _comparer = new SizeComparer();

        public Task<bool> ValidateAsync(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null) =>
            Task.FromResult(_comparer.AreEqual(sourcePath, destPath, sourceSnapshot));
    }

    public sealed class HashValidator : IValidator
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
