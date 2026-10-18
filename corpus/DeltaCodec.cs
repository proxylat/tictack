using System;
using System.IO;
using System.Security.Cryptography;

namespace Corpus
{
    // §12.7 sub-chunk delta encoding: depth-1 deltas via patch-from semantics.
    //
    // FORMAT (codec-agnostic framing; patch bytes are opaque to the store):
    // - Pack blob: payload = baseDigest (32B) ‖ patch, codec byte = IDeltaCodec.CodecId.
    //   Overhead vs whole-compressed = 32B. Old readers hit Compression.Decode
    //   with an unknown codec byte and throw InvalidDataException — loud, no shims.
    // - Manifest entry: DigestHex + Size describe the RECONSTRUCTED bytes (content
    //   identity never changes), BaseDigestHex names the whole base chunk,
    //   DeltaCodec records which codec wrote the patch. Whole entries omit both
    //   (WhenWritingDefault), so old manifests load unchanged and old JSON stays
    //   byte-identical. An old binary ignores the unknown JSON fields, treats the
    //   entry as whole, and still refuses loud at the codec byte.
    //
    // SEAM: the §12.7-phase-1 spike picks the real codec AFTER this lands, so
    // capture + all four read paths (restore/export, verify, extract, skip-compare)
    // program to IDeltaCodec and tests use a fake. The follow-up wires the winner
    // as one ctor arg each on Store and SnapshotVolumeReader — no format change.
    // Codec ids: 0 = raw, 1 = zstd (reserved); delta codecs take >= 2.
    // Public so out-of-tree harnesses (and the post-spike codec) can implement it.
    public interface IDeltaCodec
    {
        byte CodecId { get; }

        // Best-effort compact patch. False = no compact patch (caller stores
        // whole); patch bytes are valid only when true is returned.
        bool TryCreatePatch(byte[] baseWhole, byte[] current, out byte[] patch);

        // Reconstruct current from base + patch. Throws InvalidDataException
        // on corrupt patch (the caller names the chunk).
        byte[] ApplyPatch(byte[] baseWhole, byte[] patch);
    }

    internal static class DeltaFormat
    {
        // Base digest prefix inside a delta payload; the rest is patch bytes.
        public const int BaseBytes = 32;
    }

    // Shared delta resolution: base must be a WHOLE chunk (depth-1 structural —
    // a delta base throws loud), reconstruction must hash to the entry digest
    // (self-checking — corrupt patch throws loud naming the chunk). Used by
    // Store (export/verify/skip-compare) and Extract (SnapshotVolumeReader).
    internal sealed class DeltaChunkReader
    {
        private readonly ChunkIndex _index;
        private readonly string _packsDir;
        private readonly string _landingDir;
        private readonly byte[] _key;
        private readonly IDeltaCodec _codec;

        public DeltaChunkReader(ChunkIndex index, string packsDir, string landingDir, byte[] key, IDeltaCodec codec)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentNullException.ThrowIfNull(packsDir);
            ArgumentNullException.ThrowIfNull(landingDir);
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(codec);
            _index = index;
            _packsDir = packsDir;
            _landingDir = landingDir;
            _key = key;
            _codec = codec;
        }

        public byte[] Resolve(ManifestEntry entry, byte[] deltaPayload, int generation, long chunkIndex)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(deltaPayload);
            if (string.IsNullOrEmpty(entry.BaseDigestHex))
                throw new InvalidDataException($"snapshot {generation} chunk {chunkIndex}: delta entry without base digest");
            if (deltaPayload.Length < DeltaFormat.BaseBytes)
                throw new InvalidDataException($"snapshot {generation} chunk {chunkIndex}: delta payload truncated");
            var payloadBase = ChunkId.ToHex(deltaPayload[..DeltaFormat.BaseBytes]);
            if (!string.Equals(payloadBase, entry.BaseDigestHex, StringComparison.Ordinal))
                throw new InvalidDataException($"snapshot {generation} chunk {chunkIndex}: delta base mismatch (entry vs blob)");
            var patch = deltaPayload[DeltaFormat.BaseBytes..];
            var baseBytes = ReadWholeBase(entry.BaseDigestHex, generation, chunkIndex);
            byte[] plain;
            try
            {
                plain = _codec.ApplyPatch(baseBytes, patch);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"snapshot {generation} chunk {chunkIndex}: corrupt delta patch ({ex.Message})", ex);
            }
            if (plain.Length != entry.Size
                || !CryptographicOperations.FixedTimeEquals(ChunkId.ComputeId(plain, _key), Convert.FromHexString(entry.DigestHex)))
                throw new InvalidDataException($"snapshot {generation} chunk {chunkIndex}: content mismatch");
            return plain;
        }

        // Base lookup resolves to a whole chunk only: an index hit on a
        // delta-kind blob is the forbidden depth-2 (fail loud, never chain),
        // a miss falls back to landing read-through (landing files are always
        // whole plaintext), anything else is a missing base (fail loud).
        private byte[] ReadWholeBase(string baseHex, int generation, long chunkIndex)
        {
            byte[] baseDigest;
            try
            {
                baseDigest = Convert.FromHexString(baseHex);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"snapshot {generation} chunk {chunkIndex}: delta base digest corrupt", ex);
            }
            if (_index.TryGet(baseDigest, out var row))
            {
                PackBlob blob;
                try
                {
                    blob = PackReader.ReadBlob(Path.Combine(_packsDir, row.Pack), row.PackOffset, (int)row.StoredBytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    throw new InvalidDataException($"snapshot {generation} chunk {chunkIndex}: delta base {baseHex} unreadable ({ex.Message})", ex);
                }
                if (blob.Codec == _codec.CodecId)
                    throw new InvalidDataException($"snapshot {generation} chunk {chunkIndex}: delta base {baseHex} is itself a delta (depth-2 forbidden)");
                try
                {
                    return Compression.Decode(blob.Payload, blob.Codec);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"snapshot {generation} chunk {chunkIndex}: delta base {baseHex} unreadable ({ex.Message})", ex);
                }
            }
            var path = Path.Combine(_landingDir, baseHex + ".chunk");
            if (File.Exists(path))
                return File.ReadAllBytes(path);
            throw new InvalidDataException($"snapshot {generation} chunk {chunkIndex}: delta base {baseHex} missing from index");
        }
    }
}
