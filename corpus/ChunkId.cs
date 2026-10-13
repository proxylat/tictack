using System;
using System.IO;
using System.Security.Cryptography;

namespace Corpus
{
    // Content identity: SHA-256(plaintext ‖ idKey ‖ encKey?). The
    // store-wide key keeps ids opaque to anyone holding only the store
    // files; mixing the encryption key into the digest makes cross-key
    // dedup impossible BY CONSTRUCTION (same plaintext + different key =
    // different digest = different index row). This function is the ONLY
    // place key scope enters identity — GC/verify/dedup compare digest
    // strings and need no key threading.
    public static class ChunkId
    {
        // encKey null → byte-identical to the unkeyed digest.
        public static byte[] ComputeId(byte[] plaintext, byte[] idKey, byte[]? encKey = null)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            h.AppendData(plaintext);
            h.AppendData(idKey);
            if (encKey != null)
                h.AppendData(encKey);
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
