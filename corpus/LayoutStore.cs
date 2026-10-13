using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Corpus
{
    // Whole-disk layout sidecar (spec §2): one DiskLayout per ImageDisk call
    // listing the sibling per-partition manifests. Lives in
    // <storeRoot>/layouts/ — NEVER manifests/ (ManifestStore.List and Gc.Plan
    // load every manifests/*.json as a snapshot; a layout there breaks both).
    // Sealed with HMAC-SHA256 under id.key via the same
    // canonical-JSON-with-empty-Hmac scheme as ManifestStore.
    // (Home of the DiskLayout/PartitionRecord types: the capture lane's
    // PartitionTable.ReadDisk returns DiskLayout — do NOT redefine these in
    // PartitionTable.cs, use these.)
    public sealed class PartitionRecord
    {
        public int PartitionNumber { get; set; }
        public string Name { get; set; } = "";
        public string TypeGuid { get; set; } = "";
        public string UniqueGuid { get; set; } = "";
        public long FirstLba { get; set; }
        public long LastLba { get; set; }
        public long OffsetBytes { get; set; }
        public long LengthBytes { get; set; }
        public string Filesystem { get; set; } = "unknown";
        public bool Skipped { get; set; }
        public string? SkipReason { get; set; }
        public int SnapshotGeneration { get; set; }   // 0 = skipped/never
        public string PartitionKey { get; set; } = "";
    }

    public sealed class DiskLayout
    {
        public int SchemaVersion { get; set; } = 1;
        public int Generation { get; set; }           // max sibling manifest gen
        public int DiskNumber { get; set; }
        public string DiskGuid { get; set; } = "";
        public long DiskSizeBytes { get; set; }
        public int SectorSizeBytes { get; set; } = 512;
        public string Style { get; set; } = "gpt";
        public List<PartitionRecord> Partitions { get; set; } = new();
        public string Hmac { get; set; } = "";
    }

    public static class LayoutStore
    {
        public static string Save(string layoutsDir, DiskLayout layout, byte[] key)
        {
            Directory.CreateDirectory(layoutsDir);
            layout.Hmac = "";
            layout.Hmac = ComputeHmac(JsonSerializer.Serialize(layout), key);
            var path = Path.Combine(layoutsDir, $"{layout.Generation:D6}-disk{layout.DiskNumber}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(layout));
            return path;
        }

        public static DiskLayout Load(string path, byte[] key)
        {
            DiskLayout? layout;
            try
            {
                layout = JsonSerializer.Deserialize<DiskLayout>(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Disk layout {path} is not valid JSON.", ex);
            }
            if (layout is null) throw new InvalidDataException($"Disk layout {path} is empty.");
            var stored = layout.Hmac ?? "";
            layout.Hmac = "";
            var expected = ComputeHmac(JsonSerializer.Serialize(layout), key);
            if (!string.Equals(stored, expected, StringComparison.Ordinal))
                throw new InvalidDataException($"Disk layout {path} failed HMAC verification.");
            layout.Hmac = stored;
            // Never silent-migrate: a migrated layout could misplace extents.
            if (layout.SchemaVersion > 1)
                throw new InvalidDataException($"Disk layout {path} has unsupported SchemaVersion {layout.SchemaVersion} (this build reads v1).");
            return layout;
        }

        private static string ComputeHmac(string json, byte[] key)
        {
            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        }
    }
}
