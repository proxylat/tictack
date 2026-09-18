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
    }
}
