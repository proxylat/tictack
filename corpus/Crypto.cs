using System;
using System.Security.Cryptography;

namespace Corpus
{
    // Per-chunk encryption: AES-256-GCM over the compressed payload
    // (compress-then-encrypt — ciphertext is uncompressible, so the reverse
    // order would pay zstd CPU for zero gain). BCL only, no new deps.
    public static class ChunkCrypto
    {
        public const int NonceBytes = 12;
        public const int KeyBytes = 32;
        public const int TagBytes = 16;

        // Output is ciphertext ‖ 16B GCM tag (storedLen covers both).
        public static byte[] Encrypt(byte[] compressed, byte[] key32, byte[] nonce12)
        {
            ArgumentNullException.ThrowIfNull(compressed);
            ArgumentNullException.ThrowIfNull(key32);
            ArgumentNullException.ThrowIfNull(nonce12);
            if (key32.Length != KeyBytes) throw new ArgumentException($"key must be {KeyBytes} bytes", nameof(key32));
            if (nonce12.Length != NonceBytes) throw new ArgumentException($"nonce must be {NonceBytes} bytes", nameof(nonce12));
            var cipher = new byte[compressed.Length];
            var tag = new byte[TagBytes];
            using (var gcm = new AesGcm(key32, TagBytes))
                gcm.Encrypt(nonce12, compressed, cipher, tag);
            var output = new byte[cipher.Length + TagBytes];
            Buffer.BlockCopy(cipher, 0, output, 0, cipher.Length);
            Buffer.BlockCopy(tag, 0, output, cipher.Length, TagBytes);
            return output;
        }

        // Throws CryptographicException on auth failure — DO NOT wrap.
        public static byte[] Decrypt(byte[] cipherAndTag, byte[] key32, byte[] nonce12)
        {
            ArgumentNullException.ThrowIfNull(cipherAndTag);
            ArgumentNullException.ThrowIfNull(key32);
            ArgumentNullException.ThrowIfNull(nonce12);
            if (key32.Length != KeyBytes) throw new ArgumentException($"key must be {KeyBytes} bytes", nameof(key32));
            if (nonce12.Length != NonceBytes) throw new ArgumentException($"nonce must be {NonceBytes} bytes", nameof(nonce12));
            if (cipherAndTag.Length < TagBytes) throw new ArgumentException("ciphertext shorter than tag", nameof(cipherAndTag));
            var plain = new byte[cipherAndTag.Length - TagBytes];
            using (var gcm = new AesGcm(key32, TagBytes))
                gcm.Decrypt(nonce12, cipherAndTag.AsSpan(0, plain.Length), cipherAndTag.AsSpan(plain.Length), plain);
            return plain;
        }

        // First 8 lowercase hex of SHA-256(key), same shape as ChunkId.KeyId.
        public static string KeyId(byte[] key32)
        {
            ArgumentNullException.ThrowIfNull(key32);
            if (key32.Length != KeyBytes) throw new ArgumentException($"key must be {KeyBytes} bytes", nameof(key32));
            return Convert.ToHexString(SHA256.HashData(key32)).ToLowerInvariant().Substring(0, 8);
        }
    }
}
