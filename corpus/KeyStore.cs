using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;

namespace Corpus
{
    // Encryption keys: raw 32B files at storeRoot/keys/<keyid>.key (0600 on
    // Linux, age-importable by shape), active key named by keys/current.
    // Old keys are retained on rotation so old snapshots keep verifying;
    // reads resolve per-manifest EncKeyId.
    public sealed class KeyStore
    {
        private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
        private readonly string _currentKeyId;

        public KeyStore(string storeRoot)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            var keysDir = Path.Combine(storeRoot, "keys");
            if (Directory.Exists(keysDir))
            {
                foreach (var path in Directory.EnumerateFiles(keysDir, "*.key"))
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length != ChunkCrypto.KeyBytes)
                        throw new InvalidDataException($"Encryption key file {path} is {bytes.Length} bytes, want {ChunkCrypto.KeyBytes}.");
                    _keys[Path.GetFileNameWithoutExtension(path)] = bytes;
                }
                var currentPath = Path.Combine(keysDir, "current");
                if (File.Exists(currentPath))
                    _currentKeyId = File.ReadAllText(currentPath).Trim();
                else
                    _currentKeyId = "";
            }
            else
            {
                _currentKeyId = "";
            }
            // Never guess: a missing pointer with keys present means
            // unencrypted writes, never a silent pick (Store warns).
            if (_currentKeyId != "" && !_keys.ContainsKey(_currentKeyId))
                _currentKeyId = "";
        }

        public bool HasCurrent => _currentKeyId != "";

        public string CurrentKeyId => _currentKeyId;

        public byte[] CurrentKey =>
            _currentKeyId != "" ? _keys[_currentKeyId] : throw new InvalidOperationException("No current encryption key (keys/current missing).");

        public bool TryGet(string keyId, [NotNullWhen(true)] out byte[]? key)
        {
            ArgumentNullException.ThrowIfNull(keyId);
            return _keys.TryGetValue(keyId, out key);
        }

        // Rotation fallback + --key-list + Store's missing-pointer warning.
        internal IReadOnlyList<(string KeyId, byte[] Key)> AllKeys
        {
            get
            {
                var list = new List<(string KeyId, byte[] Key)>(_keys.Count);
                foreach (var kv in _keys)
                    list.Add((kv.Key, kv.Value));
                return list;
            }
        }

        // Generate + activate; re-run = rotate (old .key files retained,
        // current repointed). Never prints key bytes — callers print the id.
        public static (string KeyId, string Path) Generate(string storeRoot)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            var keysDir = Path.Combine(storeRoot, "keys");
            Directory.CreateDirectory(keysDir);
            var key = RandomNumberGenerator.GetBytes(ChunkCrypto.KeyBytes);
            var id = ChunkCrypto.KeyId(key);
            var path = Path.Combine(keysDir, id + ".key");
            File.WriteAllBytes(path, key);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var tmp = Path.Combine(keysDir, "current.tmp");
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs))
            {
                w.Write(id);
                w.Flush();
                fs.Flush(true);
            }
            File.Move(tmp, Path.Combine(keysDir, "current"), overwrite: true);
            return (id, path);
        }
    }
}
