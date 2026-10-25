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
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsHole = false,
        // §12.7 sub-chunk delta: base digest (always a WHOLE chunk) + the codec
        // id that wrote the patch. Null/0 = whole chunk. Omitted when default
        // so old manifests load unchanged and old JSON stays byte-identical;
        // DigestHex/Size always describe the RECONSTRUCTED bytes.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? BaseDigestHex = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] byte DeltaCodec = 0)
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
        // Chunker that wrote this snapshot: null = fixed grid (every
        // snapshot before CDC existed omits the field, so old JSON stays
        // byte-identical and loads as fixed). "buzhash" = content-defined
        // boundaries; restore walks cumulative entry sizes instead of the
        // fixed grid. Mixed repos work: each snapshot carries its own shape.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Chunker { get; set; }
        public int CompressionLevel { get; set; }
        public string KeyId { get; set; } = "";
        public string PrevDigest { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int ParentGeneration { get; set; }        // 0 = full image
        // Plaintext provenance (journal id + USN — not sensitive), readable
        // without encryption keys so --list-snapshots/planning never need key access.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ChangeCursor { get; set; }        // null = none/full
        // Whole-disk rung (spec §3): partition identity. Optional with
        // WhenWritingDefault so old manifests load with defaults and volume
        // verbs are byte-identical.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? PartitionKey { get; set; }        // null = volume snapshot
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int DiskNumber { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int PartitionNumber { get; set; }
        // Item 1-minimal: layout fingerprint (SHA-256 over $MFT runs). Null on
        // old/file-source manifests → guard abstains, never false-positives.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LayoutFingerprint { get; set; }
        // Item 4: exclusion names whose extents were zeroed at capture. Null =
        // none (omitted → old/dense JSON byte-identical).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public List<string>? SkippedExclusions { get; set; }
        // Item 13: MFT group digests (SHA-256 hex per image-chunk-sized band
        // of the $MFT stream) + the record size that shaped them. Null/0 on
        // old manifests → digest path abstains to the USN hint.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public List<string>? MftChunkDigests { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int MftRecordBytes { get; set; }
        // Item 13b (§9 skip): per-record MFT digests (SkipRecordDigest hex per
        // record number: LSN/USA-insensitive, unlike the raw group digests).
        // Lets the pre-stream skip decision diff at record granularity instead
        // of group expansion — group 0 flips on any VSS touch, which otherwise
        // unions every live file's extents and kills the gate on live NTFS.
        // Live records carry their hex; dead/empty slots carry a sentinel
        // ("dead"/"empty": never 64-hex) and are excluded from comparison —
        // they hold no restorable data and their shadow-view bytes are
        // unstable. Null on old manifests → the record gate abstains to the
        // byte backstop.
        // Pre-skip-digest gens compare unequal (raw vs masked) and refuse once
        // to the backstop — fail to skip, never wrong-skip — then self-heal.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public List<string>? MftRecordDigests { get; set; }
        // Record-digest liveness version (MftDigest.SkipRecordVersion): 0 =
        // pre-liveness gens (empty/dead slots hashed — unstable under VSS
        // shadow views) or no digests; 1 = live-only slots but LSN/USA-only
        // masking (B6K USA-tail + root-SI churn still vetoed every skip);
        // 2 = +tails/SI masking but byte-classified liveness (B6L shadow
        // smear on unallocated FRNs still vetoed every skip); 3 =
        // bitmap-classified liveness but shadow-view record bytes (B6K/v10
        // smear on ALLOCATED records still vetoed every skip); 4 =
        // bitmap liveness + LIVE-view record bytes (design B) but atime
        // churn still vetoed every skip (B6AB); 5 = current (+SI
        // last-access masking for every FRN).
        // The record gate requires current; older gens refuse once to the
        // byte backstop, then self-heal (the next gen is written by current
        // code). Omitted when 0, so old JSON loads 0 without a schema break.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int MftRecordDigestVersion { get; set; }
        // Item 12b: BitLocker state DURING capture (post-suspend — the
        // on-disk state the image carries) + protector key IDs (IDs only,
        // never key material). Null on old/file-source manifests.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? BitLockerState { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public List<string>? BitLockerKeyIds { get; set; }
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
            int parentGeneration = 0, string? changeCursor = null,
            string? partitionKey = null, int diskNumber = 0, int partitionNumber = 0,
            string? layoutFingerprint = null, List<string>? skippedExclusions = null,
            List<string>? mftChunkDigests = null, int mftRecordBytes = 0,
            string? bitLockerState = null, List<string>? bitLockerKeyIds = null,
            List<string>? mftRecordDigests = null,
            string? chunker = null)
        {
            return new SnapshotManifest
            {
                Generation = generation,
                Name = name,
                VolumeLength = volumeLength,
                ChunkMib = chunkMib,
                Chunker = chunker,
                CompressionLevel = compressionLevel,
                KeyId = keyId,
                PrevDigest = prevDigest,
                ParentGeneration = parentGeneration,
                ChangeCursor = changeCursor,
                PartitionKey = partitionKey,
                DiskNumber = diskNumber,
                PartitionNumber = partitionNumber,
                LayoutFingerprint = layoutFingerprint,
                SkippedExclusions = skippedExclusions,
                MftChunkDigests = mftChunkDigests,
                MftRecordBytes = mftRecordBytes,
                MftRecordDigests = mftRecordDigests,
                MftRecordDigestVersion = mftRecordDigests != null ? MftDigest.SkipRecordVersion : 0,
                BitLockerState = bitLockerState,
                BitLockerKeyIds = bitLockerKeyIds,
                Chunks = chunks,
            };
        }

        public static string Save(string manifestsDir, SnapshotManifest m, byte[] key)
        {
            Directory.CreateDirectory(manifestsDir);
            m.CreatedUtc = DateTime.UtcNow.ToString("o");
            m.Hmac = "";
            m.Hmac = ComputeHmac(JsonSerializer.Serialize(m, CorpusJsonContext.Default.SnapshotManifest), key);
            var path = Path.Combine(manifestsDir, $"{m.Generation:D6}-{Sanitize(m.Name)}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(m, CorpusJsonContext.Default.SnapshotManifest));
            return path;
        }

        public static SnapshotManifest Load(string path, byte[] key)
        {
            SnapshotManifest? m;
            try
            {
                m = JsonSerializer.Deserialize(File.ReadAllText(path), CorpusJsonContext.Default.SnapshotManifest);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Manifest {path} is not valid JSON.", ex);
            }
            if (m is null) throw new InvalidDataException($"Manifest {path} is empty.");
            var stored = m.Hmac ?? "";
            m.Hmac = "";
            var expected = ComputeHmac(JsonSerializer.Serialize(m, CorpusJsonContext.Default.SnapshotManifest), key);
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
