using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Corpus
{
    public sealed record ManifestEntry(string DigestHex, long Size,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsHole = false)
    {
        public static ManifestEntry Hole(long size) => new("", size, true);
    }

    public sealed class SnapshotManifest
    {
        public int Generation { get; set; }
        public string Name { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public long VolumeLength { get; set; }
        public int ChunkMib { get; set; }
        public int CompressionLevel { get; set; }
        public string KeyId { get; set; } = "";
        public string PrevDigest { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int ParentGeneration { get; set; }        // 0 = full image
        // Plaintext provenance (journal id + USN — not sensitive), readable
        // without encryption keys so --list/planning never need key access.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ChangeCursor { get; set; }        // null = none/full
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? EncKeyId { get; set; }            // null = unencrypted
        public List<ManifestEntry> Chunks { get; set; } = new();
        public string Hmac { get; set; } = "";
    }

    // Snapshot manifests: canonical JSON (default STJ serialization,
    // declaration order) authenticated with HMAC-SHA256 over the JSON with
    // Hmac="". PrevDigest chains generations so a deleted middle manifest
    // is detectable.
    public static class ManifestStore
    {
        public static SnapshotManifest Build(int generation, string name, long volumeLength, int chunkMib, int compressionLevel, string keyId, string prevDigest, List<ManifestEntry> chunks,
            int parentGeneration = 0, string? changeCursor = null, string? encKeyId = null)
        {
            return new SnapshotManifest
            {
                Generation = generation,
                Name = name,
                VolumeLength = volumeLength,
                ChunkMib = chunkMib,
                CompressionLevel = compressionLevel,
                KeyId = keyId,
                PrevDigest = prevDigest,
                ParentGeneration = parentGeneration,
                ChangeCursor = changeCursor,
                EncKeyId = encKeyId,
                Chunks = chunks,
            };
        }

        public static string Save(string manifestsDir, SnapshotManifest m, byte[] key)
        {
            Directory.CreateDirectory(manifestsDir);
            m.CreatedUtc = DateTime.UtcNow.ToString("o");
            m.Hmac = "";
            m.Hmac = ComputeHmac(JsonSerializer.Serialize(m), key);
            var path = Path.Combine(manifestsDir, $"{m.Generation:D6}-{Sanitize(m.Name)}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(m));
            return path;
        }

        public static SnapshotManifest Load(string path, byte[] key)
        {
            SnapshotManifest? m;
            try
            {
                m = JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Manifest {path} is not valid JSON.", ex);
            }
            if (m is null) throw new InvalidDataException($"Manifest {path} is empty.");
            var stored = m.Hmac ?? "";
            m.Hmac = "";
            var expected = ComputeHmac(JsonSerializer.Serialize(m), key);
            if (!string.Equals(stored, expected, StringComparison.Ordinal))
                throw new InvalidDataException($"Manifest {path} failed HMAC verification.");
            m.Hmac = stored;
            return m;
        }

        public static int NextGeneration(string manifestsDir)
        {
            if (!Directory.Exists(manifestsDir)) return 1;
            var max = 0;
            foreach (var path in Directory.EnumerateFiles(manifestsDir, "*.json"))
            {
                var name = Path.GetFileName(path);
                if (name.Length > 7 && name[6] == '-' && int.TryParse(name.AsSpan(0, 6), out var gen) && gen > max)
                    max = gen;
            }
            return max + 1;
        }

        public static List<(int Generation, string Name)> List(string manifestsDir)
        {
            var result = new List<(int Generation, string Name)>();
            if (!Directory.Exists(manifestsDir)) return result;
            foreach (var path in Directory.EnumerateFiles(manifestsDir, "*.json"))
            {
                var name = Path.GetFileName(path);
                if (name.Length > 7 && name[6] == '-' && int.TryParse(name.AsSpan(0, 6), out var gen))
                    result.Add((gen, name.Substring(7, name.Length - 7 - 5)));
            }
            result.Sort((a, b) => a.Generation.CompareTo(b.Generation));
            return result;
        }

        private static string ComputeHmac(string json, byte[] key)
        {
            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        }

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }
    }
}
