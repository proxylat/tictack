using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using TicTack;

namespace Corpus
{
    // kopia-style background deep-verify: re-reads a deterministic random
    // sample of a snapshot's chunks and hash-compares against the manifest.
    // Verify-on-write covers fresh data; this catches at-rest bit rot.
    public sealed record DeepVerifySummary(int Checked, int Failed, int TotalChunks)
    {
        public bool Ok => Failed == 0;
    }

    public static class DeepVerifyJob
    {
        // Read-only: opens its own ChunkIndex handle, never writes packs,
        // index, landing, or manifests. Old snapshots verify unchanged —
        // only whole-chunk entries need no codec; delta entries need the
        // same codec the snapshot was written with (loud failure otherwise).
        public static DeepVerifySummary Run(string storeRoot, string dbPath, SnapshotManifest manifest,
            int percent, ILogger log, CancellationToken ct = default, IDeltaCodec? deltaCodec = null)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            ArgumentNullException.ThrowIfNull(dbPath);
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(log);
            if (percent < 0 || percent > 100)
                throw new ArgumentOutOfRangeException(nameof(percent), "deep verify percent must be 0..100");
            if (percent == 0)
                return new DeepVerifySummary(0, 0, manifest.Chunks.Count);

            var selected = SelectCandidates(manifest.Chunks, percent, manifest.Generation);
            if (selected.Count == 0)
            {
                log.Info($"deep-verify snapshot {manifest.Generation}: no stored chunks to check");
                return new DeepVerifySummary(0, 0, manifest.Chunks.Count);
            }

            var packsDir = Path.Combine(storeRoot, "packs");
            var landingDir = Path.Combine(storeRoot, "landing");
            var keyPath = Path.Combine(storeRoot, "id.key");
            if (!File.Exists(keyPath))
                throw new InvalidDataException($"store {storeRoot} has no id.key");
            var key = File.ReadAllBytes(keyPath);

            var failed = 0;
            using var index = new ChunkIndex(dbPath);
            DeltaChunkReader? deltaReader = deltaCodec != null ? new DeltaChunkReader(index, packsDir, landingDir, key, deltaCodec) : null;
            foreach (var ci in selected)
            {
                ct.ThrowIfCancellationRequested();
                if (!VerifyOne(index, packsDir, landingDir, key, deltaCodec, deltaReader, manifest, ci, log))
                    failed++;
            }

            if (failed > 0)
                log.Error($"deep-verify snapshot {manifest.Generation}: {failed}/{selected.Count} sampled chunks FAILED");
            else
                log.Info($"deep-verify snapshot {manifest.Generation}: {selected.Count}/{manifest.Chunks.Count} chunks OK");
            return new DeepVerifySummary(selected.Count, failed, manifest.Chunks.Count);
        }

        // Deterministic per generation (stable arithmetic seed, never
        // HashCode): reruns sample the same chunks; different snapshots
        // spread across the grid. Holes carry no bytes, so only data
        // entries are candidates. Ceiling so small snapshots still check
        // at least one chunk at any nonzero percent.
        internal static List<int> SelectCandidates(IReadOnlyList<ManifestEntry> chunks, int percent, int generation)
        {
            ArgumentNullException.ThrowIfNull(chunks);
            var candidates = new List<int>(chunks.Count);
            for (var i = 0; i < chunks.Count; i++)
            {
                if (!chunks[i].IsHole)
                    candidates.Add(i);
            }
            var take = (int)Math.Ceiling(candidates.Count * percent / 100.0);
            take = Math.Clamp(take, 0, candidates.Count);
            var rng = new Random(unchecked((int)(0xC0FFEEu ^ ((uint)generation * 2654435761u))));
            for (var i = candidates.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
            var selected = candidates.GetRange(0, take);
            selected.Sort();
            return selected;
        }

        // Same read funnel as Verify/Extract (index → pack → decode, landing
        // read-through, shared delta resolve). Every failure returns false
        // after ONE Error naming snapshot + chunk — never throws past the
        // caller, never silently skips. Cancellation still propagates.
        private static bool VerifyOne(ChunkIndex index, string packsDir, string landingDir, byte[] key,
            IDeltaCodec? deltaCodec, DeltaChunkReader? deltaReader, SnapshotManifest manifest, int ci, ILogger log)
        {
            var entry = manifest.Chunks[ci];
            if (entry.IsHole)
                return true;
            byte[] digest;
            try
            {
                digest = Convert.FromHexString(entry.DigestHex);
            }
            catch (FormatException ex)
            {
                log.Error($"deep-verify snapshot {manifest.Generation} chunk {ci}: corrupt digest", ex);
                return false;
            }
            if (digest.Length != 32)
            {
                log.Error($"deep-verify snapshot {manifest.Generation} chunk {ci}: corrupt digest");
                return false;
            }
            try
            {
                var plain = ReadPlain(index, packsDir, landingDir, deltaCodec, deltaReader, manifest, ci, entry, digest);
                if (plain.Length != entry.Size
                    || !CryptographicOperations.FixedTimeEquals(ChunkId.ComputeId(plain, key), digest))
                {
                    log.Error($"deep-verify snapshot {manifest.Generation} chunk {ci}: content mismatch");
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error($"deep-verify snapshot {manifest.Generation} chunk {ci}: unreadable ({ex.Message})");
                return false;
            }
        }

        private static byte[] ReadPlain(ChunkIndex index, string packsDir, string landingDir,
            IDeltaCodec? deltaCodec, DeltaChunkReader? deltaReader, SnapshotManifest manifest,
            int ci, ManifestEntry entry, byte[] digest)
        {
            if (index.TryGet(digest, out var row))
            {
                var blob = PackReader.ReadBlob(Path.Combine(packsDir, row.Pack), row.PackOffset, (int)row.StoredBytes);
                if (entry.BaseDigestHex != null)
                {
                    if (deltaCodec == null || deltaReader == null)
                        throw new InvalidDataException($"delta entry needs codec {entry.DeltaCodec} (old reader cannot read deltas)");
                    if (blob.Codec != deltaCodec.CodecId)
                        throw new InvalidDataException($"delta entry stored as codec {blob.Codec}, expected {deltaCodec.CodecId}");
                    return deltaReader.Resolve(entry, blob.Payload, manifest.Generation, ci);
                }
                return Compression.Decode(blob.Payload, blob.Codec);
            }
            var landed = Path.Combine(landingDir, entry.DigestHex + ".chunk");
            if (File.Exists(landed))
            {
                var bytes = File.ReadAllBytes(landed);
                if (bytes.Length != entry.Size)
                    throw new InvalidDataException($"landing size mismatch ({bytes.Length} != {entry.Size})");
                return bytes;
            }
            throw new InvalidDataException("missing from index");
        }
    }
}
