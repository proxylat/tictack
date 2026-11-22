using System;
using System.IO;
using System.Security.Cryptography;

namespace Corpus
{
    // Content identity: SHA-256(plaintext ‖ idKey). The store-wide key
    // keeps ids opaque to anyone holding only the store files. Per-pack
    // encryption is an external shell hook (see PackHook) applied after
    // sealing — it never enters identity, so GC/verify/dedup compare digest
    // strings and need no key threading.
    public static class ChunkId
    {
        public static byte[] ComputeId(byte[] plaintext, byte[] idKey)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            h.AppendData(plaintext);
            h.AppendData(idKey);
            return h.GetHashAndReset();
        }

        public static string ToHex(byte[] digest)
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        private static string? _overrideKeyPath;
        private static string? _overrideKeyEnv;

        public static void ConfigureOverride(string? keyPath, string? keyEnv)
        {
            _overrideKeyPath = string.IsNullOrWhiteSpace(keyPath) ? null : keyPath.Trim();
            _overrideKeyEnv = string.IsNullOrWhiteSpace(keyEnv) ? null : keyEnv.Trim();
        }

        public static byte[] EnsureKey(string storeRoot, string? keyPath = null, string? keyEnv = null)
        {
            var effectiveEnv = keyEnv ?? _overrideKeyEnv;
            if (!string.IsNullOrWhiteSpace(effectiveEnv))
            {
                var raw = Environment.GetEnvironmentVariable(effectiveEnv);
                if (!string.IsNullOrWhiteSpace(raw))
                    return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
            }

            var effectivePath = keyPath ?? _overrideKeyPath;
            var path = !string.IsNullOrWhiteSpace(effectivePath)
                ? effectivePath
                : Path.Combine(storeRoot, "id.key");

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (!File.Exists(path))
            {
                var key = RandomNumberGenerator.GetBytes(32);
                AtomicFile.WriteAllBytes(path, key, overwrite: false, ownerOnlyOnLinux: true);
                return key;
            }
            return File.ReadAllBytes(path);
        }

        public static string KeyId(byte[] idKey)
        {
            return ToHex(SHA256.HashData(idKey)).Substring(0, 8);
        }
    }
}
