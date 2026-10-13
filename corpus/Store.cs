using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using TicTack;

namespace Corpus
{
    // Store orchestrator: chunk → id → dedup → pack → index → manifest.
    // One PackWriter per Image call; the ChunkIndex stays open for the
    // Store lifetime so repeated images share one connection.
    public sealed class Store : IDisposable
    {
        private readonly string _packsDir;
        private readonly string _manifestsDir;
        private readonly int _chunkMib;
        private readonly int _zstdLevel;
        private readonly ILogger _log;
        private readonly byte[] _key;
        private readonly ChunkIndex _index;
        private bool _disposed;

        public Store(string storeRoot, string dbPath, int chunkMib, int zstdLevel, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            ArgumentNullException.ThrowIfNull(dbPath);
            ArgumentNullException.ThrowIfNull(log);
            _packsDir = Path.Combine(storeRoot, "packs");
            _manifestsDir = Path.Combine(storeRoot, "manifests");
            Directory.CreateDirectory(_packsDir);
            Directory.CreateDirectory(_manifestsDir);
            _chunkMib = chunkMib;
            _zstdLevel = zstdLevel;
            _log = log;
            _key = ChunkId.EnsureKey(storeRoot);
            _index = new ChunkIndex(dbPath);
        }

        public SnapshotManifest Image(IBlockSource source, string name)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = ManifestStore.NextGeneration(_manifestsDir);
            var prevDigest = "";
            var existing = ManifestStore.List(_manifestsDir);
            if (existing.Count > 0)
            {
                var latest = existing[existing.Count - 1];
                var latestPath = Path.Combine(_manifestsDir, $"{latest.Generation:D6}-{latest.Name}.json");
                prevDigest = ManifestStore.Load(latestPath, _key).Hmac;
            }

            var entries = new List<ManifestEntry>();
            var pending = new List<ChunkRow>();
            // Digests packed but not yet flushed: TryGet can't see them, so
            // without this set intra-image duplicates pack twice and the
            // index ends up pointing at the last copy. Cleared on every
            // flush (≤500 entries — never grows with volume size).
            var packedThisImage = new HashSet<string>();
            var newChunks = 0;
            var totalBytes = 0L;
            using (var pack = new PackWriter(_packsDir))
            using (var stream = source.OpenRead())
            {
                foreach (var chunk in Chunker.Read(stream, _chunkMib * 1024 * 1024))
                {
                    var digest = ChunkId.ComputeId(chunk.Data, _key);
                    var hex = ChunkId.ToHex(digest);
                    entries.Add(new ManifestEntry(hex, chunk.Data.Length));
                    totalBytes += chunk.Data.Length;
                    if (packedThisImage.Contains(hex) || _index.TryGet(digest, out _))
                        continue;
                    var (payload, codec) = Compression.Encode(chunk.Data, _zstdLevel);
                    var (packName, offset) = pack.Add(digest, payload, chunk.Data.Length, codec);
                    pending.Add(new ChunkRow(digest, chunk.Data.Length, payload.Length, packName, offset));
                    packedThisImage.Add(hex);
                    newChunks++;
                    if (pending.Count >= 500)
                    {
                        _index.UpsertBatch(pending);
                        pending.Clear();
                        packedThisImage.Clear();
                    }
                }
                _index.UpsertBatch(pending);
                pending.Clear();
            }

            var manifest = ManifestStore.Build(
                generation, name, source.Length, _chunkMib, _zstdLevel,
                ChunkId.KeyId(_key), prevDigest, entries);
            ManifestStore.Save(_manifestsDir, manifest, _key);
            _log.Info($"imaged '{name}' gen {generation}: {entries.Count} chunks, {newChunks} new, {totalBytes} bytes");
            return manifest;
        }

        public void Export(int generation, string outPath)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var manifest = LoadGeneration(generation);
            using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            foreach (var entry in manifest.Chunks)
            {
                var digest = Convert.FromHexString(entry.DigestHex);
                if (!_index.TryGet(digest, out var row))
                    throw new InvalidDataException($"chunk {entry.DigestHex} of snapshot {generation} missing from index");
                var payload = PackReader.ReadPayload(Path.Combine(_packsDir, row.Pack), row.PackOffset, (int)row.StoredBytes);
                var plain = Compression.Decode(payload, ReadCodec(Path.Combine(_packsDir, row.Pack), row.PackOffset));
                if (IsAllZero(plain))
                    output.Seek(plain.Length, SeekOrigin.Current);
                else
                    output.Write(plain, 0, plain.Length);
            }
            output.SetLength(manifest.VolumeLength);
            _log.Info($"exported snapshot {generation} ({manifest.Chunks.Count} chunks) to {outPath}");
        }

        // Full re-read: index → pack → decode → id compare. Never throws;
        // every failure path logs and returns false for CLI exit codes.
        public bool Verify(int generation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                var manifest = LoadGeneration(generation);
                var failed = false;
                var logged = 0;
                for (var i = 0; i < manifest.Chunks.Count; i++)
                {
                    var entry = manifest.Chunks[i];
                    var digest = Convert.FromHexString(entry.DigestHex);
                    if (!_index.TryGet(digest, out var row))
                    {
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: missing from index");
                        continue;
                    }
                    byte[] plain;
                    try
                    {
                        var payload = PackReader.ReadPayload(Path.Combine(_packsDir, row.Pack), row.PackOffset, (int)row.StoredBytes);
                        plain = Compression.Decode(payload, ReadCodec(Path.Combine(_packsDir, row.Pack), row.PackOffset));
                    }
                    // Broad catch: zstd throws plain System.Exception on corrupt
                    // frames, IO/pack faults throw their own — all mean failure.
                    catch (Exception ex)
                    {
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: unreadable ({ex.Message})");
                        continue;
                    }
                    if (plain.Length != entry.Size || !CryptographicOperations.FixedTimeEquals(ChunkId.ComputeId(plain, _key), digest))
                    {
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: content mismatch");
                    }
                }
                if (!failed)
                    _log.Info($"snapshot {generation} verified: {manifest.Chunks.Count} chunks OK");
                return !failed;
            }
            catch (Exception ex)
            {
                _log.Error($"snapshot {generation} verify failed", ex);
                return false;
            }
        }

        public List<(int Generation, string Name)> ListSnapshots()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ManifestStore.List(_manifestsDir);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _index.Dispose();
        }

        private SnapshotManifest LoadGeneration(int generation)
        {
            foreach (var (gen, name) in ManifestStore.List(_manifestsDir))
            {
                if (gen == generation)
                    return ManifestStore.Load(Path.Combine(_manifestsDir, $"{gen:D6}-{name}.json"), _key);
            }
            throw new InvalidDataException($"snapshot generation {generation} not found in {_manifestsDir}");
        }

        // Codec lives at header byte 8 (see PackWriter layout); the index row
        // doesn't carry it, so read the recorded byte rather than guessing.
        private static byte ReadCodec(string packPath, long offset)
        {
            using var stream = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset + 8, SeekOrigin.Begin);
            var b = stream.ReadByte();
            if (b < 0) throw new InvalidDataException("truncated pack blob header at offset " + offset);
            return (byte)b;
        }

        private static bool IsAllZero(byte[] data)
        {
            foreach (var b in data)
            {
                if (b != 0) return false;
            }
            return true;
        }
    }
}
