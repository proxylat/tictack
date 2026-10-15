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

        public static byte[] EnsureKey(string storeRoot)
        {
            Directory.CreateDirectory(storeRoot);
            var path = Path.Combine(storeRoot, "id.key");
            if (!File.Exists(path))
            {
                var key = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(path, key);
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
