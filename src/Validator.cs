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
                case VerificationLevel.Hash:
                case VerificationLevel.Full:
                    return new HashValidator(accessor);
                default: return new SizeValidator();
            }
        }
    }

    public class SizeValidator : IValidator
    {
        public Task<bool> ValidateAsync(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null)
        {
            try
            {
                var source = sourceSnapshot;
                if (!source.HasValue)
                {
                    if (!FileSnapshot.TryRead(sourcePath, out var current)) return Task.FromResult(false);
                    source = current;
                }
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                return Task.FromResult(dInfo.Exists && source.Value.Length == dInfo.Length);
            }
            catch { return Task.FromResult(false); }
        }
    }

    public class HashValidator : IValidator
    {
        private readonly IFileAccessor? _accessor;
        public HashValidator(IFileAccessor? accessor = null) { _accessor = accessor; }

        public Task<bool> ValidateAsync(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null)
        {
            try
            {
                var source = sourceSnapshot;
                if (!source.HasValue)
                {
                    if (!FileSnapshot.TryRead(sourcePath, out var current)) return Task.FromResult(false);
                    source = current;
                }
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                if (!dInfo.Exists || source.Value.Length != dInfo.Length) return Task.FromResult(false);
                var srcHash = FileHasher.ComputeHex(sourcePath, _accessor);
                var dstHash = FileHasher.ComputeHex(destPath, _accessor);
                return Task.FromResult(srcHash == dstHash);
            }
            catch { return Task.FromResult(false); }
        }
    }
}
