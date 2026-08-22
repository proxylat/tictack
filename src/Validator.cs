using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace TicTack
{
    public static class ValidatorFactory
    {
        public static IValidator Create(VerificationLevel level, IFileAccessor accessor = null)
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
        public Task<bool> ValidateAsync(string sourcePath, string destPath)
        {
            try
            {
                var sInfo = new FileInfo(PathUtil.EnsureExtended(sourcePath));
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                return Task.FromResult(dInfo.Exists && sInfo.Length == dInfo.Length);
            }
            catch { return Task.FromResult(false); }
        }
    }

    public class HashValidator : IValidator
    {
        private readonly IFileAccessor _accessor;
        public HashValidator(IFileAccessor accessor = null) { _accessor = accessor; }

        public async Task<bool> ValidateAsync(string sourcePath, string destPath)
        {
            try
            {
                if (!File.Exists(PathUtil.EnsureExtended(sourcePath)) || !File.Exists(PathUtil.EnsureExtended(destPath))) return false;
                var sInfo = new FileInfo(PathUtil.EnsureExtended(sourcePath));
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                if (sInfo.Length != dInfo.Length) return false;
                var srcHash = await ComputeHashAsync(sourcePath);
                var dstHash = await ComputeHashAsync(destPath);
                return srcHash == dstHash;
            }
            catch { return false; }
        }

        private async Task<string> ComputeHashAsync(string path)
        {
            path = PathUtil.EnsureExtended(path);
            using (var sha256 = SHA256.Create())
            using (var stream = _accessor != null ? _accessor.OpenRead(path) : File.OpenRead(path))
            {
                var hash = await Task.Run(() => sha256.ComputeHash(stream));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
