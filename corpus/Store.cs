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
        private readonly KeyStore? _keys;
        private readonly byte[]? _encKey;
        private readonly string? _encKeyId;
        private readonly HashSet<string> _mixedKeyNoted = new(StringComparer.Ordinal);
        private readonly ChunkIndex _index;
        private bool _disposed;

        public Store(string storeRoot, string dbPath, int chunkMib, int zstdLevel, ILogger log, KeyStore? keys = null)
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
            _keys = keys;
            if (keys != null && keys.HasCurrent)
            {
                _encKey = keys.CurrentKey;
                _encKeyId = keys.CurrentKeyId;
            }
            else if (keys != null && keys.AllKeys.Count > 0)
            {
                // Pointer deleted by hand: keys exist but none is current —
                // image unencrypted rather than guess (never silent-pick).
                _log.Warn("encryption keys present but no current key selected, new images will be unencrypted");
            }
            _index = new ChunkIndex(dbPath);
        }

        public SnapshotManifest Image(IBlockSource source, string name) =>
            Image(source, name, new ImageOptions());

        public SnapshotManifest Image(IBlockSource source, string name, ImageOptions opts)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(opts);
            var generation = ManifestStore.NextGeneration(_manifestsDir);
            var existing = ManifestStore.List(_manifestsDir);
            SnapshotManifest? latest = null;
            if (existing.Count > 0)
            {
                var (gen, snap) = existing[existing.Count - 1];
                latest = ManifestStore.Load(Path.Combine(_manifestsDir, $"{gen:D6}-{snap}.json"), _key);
            }
            var prevDigest = latest?.Hmac ?? "";
            SnapshotManifest? parent = null;
            if (!opts.ForceFull && (opts.Incremental || opts.ParentGeneration > 0))
            {
                int? wantGen = opts.ParentGeneration > 0 ? opts.ParentGeneration : latest?.Generation;
                if (wantGen == null)
                {
                    _log.Info($"no parent snapshot, full image ('{name}' will be gen {generation})");
                }
                else if (latest != null && wantGen.Value == latest.Generation)
                {
                    parent = CheckParent(latest, source);
                }
                else
                {
                    string? parentName = null;
                    foreach (var (gen, snap) in existing)
                    {
                        if (gen == wantGen.Value)
                        {
                            parentName = snap;
                            break;
                        }
                    }
                    if (parentName == null)
                    {
                        if (opts.ParentGeneration > 0)
                            throw new InvalidDataException($"--parent gen {wantGen.Value} not found in {_manifestsDir}");
                        _log.Info($"parent gen {wantGen.Value} missing, full image");
                    }
                    else
                    {
                        // HMAC-checked; corrupt → throw (never image onto a corrupt parent).
                        parent = CheckParent(ManifestStore.Load(Path.Combine(_manifestsDir, $"{wantGen.Value:D6}-{parentName}.json"), _key), source);
                    }
                }
            }
            if (parent != null)
            {
                var plan = (opts.ChangeSource ?? DefaultChangeSource()).Plan(parent, source, _chunkMib * 1024 * 1024, _log);
                if (plan != null)
                    return ImageIncremental(source, name, generation, prevDigest, parent, plan);
            }
            return ImageFull(source, name, generation, prevDigest);
        }

        private static IChangeSource DefaultChangeSource() => new UsnChangeSource();

        // Parent usability gate: resize / re-chunk / grid drift → log the
        // reason and return null (caller falls back to full re-read).
        private SnapshotManifest? CheckParent(SnapshotManifest parent, IBlockSource source)
        {
            if (parent.VolumeLength != source.Length)
            {
                _log.Info($"full image: parent gen {parent.Generation} volume length {parent.VolumeLength} != source length {source.Length}");
                return null;
            }
            if (parent.ChunkMib != _chunkMib)
            {
                _log.Info($"full image: parent gen {parent.Generation} chunk {parent.ChunkMib} MiB != current {_chunkMib} MiB");
                return null;
            }
            var grid = GridCount(source.Length, _chunkMib);
            if (parent.Chunks.Count != grid)
            {
                _log.Info($"full image: parent gen {parent.Generation} has {parent.Chunks.Count} chunks, current grid is {grid}");
                return null;
            }
            return parent;
        }

        private static int GridCount(long volumeLength, int chunkMib)
        {
            var chunkBytes = (long)chunkMib * 1024 * 1024;
            return (int)((volumeLength + chunkBytes - 1) / chunkBytes);
        }

        private SnapshotManifest ImageFull(IBlockSource source, string name, int generation, string prevDigest)
        {
            var entries = new List<ManifestEntry>(GridCount(source.Length, _chunkMib));
            var pending = new List<ChunkRow>();
            // Digests packed but not yet flushed: TryGet can't see them, so
            // without this set intra-image duplicates pack twice and the
            // index ends up pointing at the last copy. Cleared on every
            // flush (≤500 entries — never grows with volume size).
            var packedThisImage = new HashSet<string>(StringComparer.Ordinal);
            var newChunks = 0;
            var totalBytes = 0L;
            var holes = 0;
            var chunkBytes = _chunkMib * 1024 * 1024;
            using (var pack = new PackWriter(_packsDir, v2: _encKey != null))
            using (var stream = source.OpenRead())
            {
                bool[]? holeMap = null;
                if (source is ISparseBlockSource sparse)
                    holeMap = Chunker.BuildHoleMap(sparse.GetAllocatedRuns(), sparse.BytesPerCluster, source.Length, chunkBytes);
                var enumerable = holeMap != null
                    ? Chunker.ReadSparse(stream, chunkBytes, i => i < holeMap.Length && holeMap[i])
                    : Chunker.Read(stream, chunkBytes);
                foreach (var chunk in enumerable)
                {
                    if (chunk.IsHole)
                    {
                        var holeSize = Math.Min((long)chunkBytes, source.Length - chunk.Offset);
                        entries.Add(ManifestEntry.Hole(holeSize));
                        totalBytes += holeSize;
                        holes++;
                        continue;
                    }
                    var digest = ChunkId.ComputeId(chunk.Data, _key, _encKey);
                    var hex = ChunkId.ToHex(digest);
                    entries.Add(new ManifestEntry(hex, chunk.Data.Length));
                    totalBytes += chunk.Data.Length;
                    if (packedThisImage.Contains(hex) || _index.TryGet(digest, out _))
                        continue;
                    var (payload, codec) = Compression.Encode(chunk.Data, _zstdLevel);
                    byte[]? nonce = null;
                    if (_encKey != null)
                    {
                        // Random 96-bit nonce per blob: no counter state to
                        // crash-lose (counter reuse across crashes is fatal).
                        nonce = RandomNumberGenerator.GetBytes(ChunkCrypto.NonceBytes);
                        payload = ChunkCrypto.Encrypt(payload, _encKey, nonce);
                    }
                    var (packName, offset) = pack.Add(digest, payload, chunk.Data.Length, codec, nonce);
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
                ChunkId.KeyId(_key), prevDigest, entries,
                changeCursor: UsnChangeSource.TryCurrentCursor(source, _log), encKeyId: _encKeyId);
            ManifestStore.Save(_manifestsDir, manifest, _key);
            _log.Info($"imaged '{name}' gen {generation}: {entries.Count} chunks ({holes} holes), {newChunks} new, {totalBytes} bytes");
            return manifest;
        }

        // Incremental capture: clean chunks reuse the parent's digests with
        // ZERO reads; only dirty chunks are seek-read, hashed, and packed.
        // The child manifest is self-contained (full chunk list) — export
        // and standalone verify never touch the parent.
        private SnapshotManifest ImageIncremental(IBlockSource source, string name, int generation, string prevDigest, SnapshotManifest parent, IncrementalPlan plan)
        {
            var chunkBytes = _chunkMib * 1024 * 1024;
            var grid = GridCount(source.Length, _chunkMib);
            var entries = new List<ManifestEntry>(parent.Chunks.Count);
            var pending = new List<ChunkRow>();
            var packedThisImage = new HashSet<string>(StringComparer.Ordinal);
            var reread = 0;
            var newChunks = 0;
            using (var pack = new PackWriter(_packsDir, v2: _encKey != null))
            using (var stream = source.OpenRead())
            {
                bool[]? holeMap = null;
                if (source is ISparseBlockSource sparse)
                    holeMap = Chunker.BuildHoleMap(sparse.GetAllocatedRuns(), sparse.BytesPerCluster, source.Length, chunkBytes);
                for (var i = 0; i < grid; i++)
                {
                    if (holeMap != null && i < holeMap.Length && holeMap[i])
                    {
                        entries.Add(ManifestEntry.Hole(Math.Min((long)chunkBytes, source.Length - i * (long)chunkBytes)));
                        continue; // hole wins over dirty — no read either way
                    }
                    if (!plan.DirtyChunks.Contains(i))
                    {
                        entries.Add(parent.Chunks[i]); // clean: reuse digest, ZERO reads
                        continue;
                    }
                    var data = ReadChunkAt(stream, i, chunkBytes, source.Length);
                    reread++;
                    var digest = ChunkId.ComputeId(data, _key, _encKey);
                    var hex = ChunkId.ToHex(digest);
                    entries.Add(new ManifestEntry(hex, data.Length));
                    if (packedThisImage.Contains(hex) || _index.TryGet(digest, out _))
                        continue;
                    var (payload, codec) = Compression.Encode(data, _zstdLevel);
                    byte[]? nonce = null;
                    if (_encKey != null)
                    {
                        // Random 96-bit nonce per blob: no counter state to
                        // crash-lose (counter reuse across crashes is fatal).
                        nonce = RandomNumberGenerator.GetBytes(ChunkCrypto.NonceBytes);
                        payload = ChunkCrypto.Encrypt(payload, _encKey, nonce);
                    }
                    var (packName, offset) = pack.Add(digest, payload, data.Length, codec, nonce);
                    pending.Add(new ChunkRow(digest, data.Length, payload.Length, packName, offset));
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
                ChunkId.KeyId(_key), prevDigest, entries, parent.Generation, plan.NewCursor, _encKeyId);
            ManifestStore.Save(_manifestsDir, manifest, _key);
            _log.Info($"incremental gen {generation} from parent {parent.Generation}: {reread}/{grid} chunks re-read, {newChunks} new");
            return manifest;
        }

        private static byte[] ReadChunkAt(Stream stream, long index, int chunkBytes, long volumeLength)
        {
            stream.Seek(index * (long)chunkBytes, SeekOrigin.Begin);
            var want = (int)Math.Min((long)chunkBytes, volumeLength - index * (long)chunkBytes);
            var data = new byte[want];
            var filled = 0;
            int n;
            while (filled < want && (n = stream.Read(data, filled, want - filled)) > 0)
                filled += n;
            if (filled != want)
                throw new IOException($"short read at chunk {index}: {filled} of {want}");
            return data;
        }

        public void Export(int generation, string outPath)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var manifest = GetManifest(generation);
            using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            ExportToStream(generation, output);
            output.SetLength(manifest.VolumeLength);
            _log.Info($"exported snapshot {generation} ({manifest.Chunks.Count} chunks) to {outPath}");
        }

        // VHDX edge render: same decoded bytes as Export, written into a
        // dynamic VHDX Content stream. Zero chunks seek past (no allocation);
        // no SetLength — capacity was fixed at create, tail reads as zero.
        public void ExportVhdx(int generation, string outPath)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var manifest = GetManifest(generation);
            VhdxExporter.Export(outPath, manifest.VolumeLength, content => ExportToStream(generation, content));
            _log.Info($"exported snapshot {generation} ({manifest.Chunks.Count} chunks) to VHDX {outPath}");
        }

        // Manifest read for restore preflight/verify (HMAC-checked).
        public SnapshotManifest GetManifest(int generation) => LoadGeneration(generation);

        // Streamed export for restore: the same decoded bytes as Export,
        // chunk by chunk, with cumulative logical-byte progress (holes
        // included, so the final report equals VolumeLength).
        public void ExportToStream(int generation, Stream output, IProgress<long>? progress = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(output);
            WriteSnapshotToStream(LoadGeneration(generation), output, progress);
        }

        private void WriteSnapshotToStream(SnapshotManifest manifest, Stream output, IProgress<long>? progress = null)
        {
            var reported = 0L;
            foreach (var entry in manifest.Chunks)
            {
                if (entry.IsHole) { output.Seek(entry.Size, SeekOrigin.Current); reported += entry.Size; progress?.Report(reported); continue; }
                var digest = Convert.FromHexString(entry.DigestHex);
                if (!_index.TryGet(digest, out var row))
                    throw new InvalidDataException($"chunk {entry.DigestHex} of snapshot {manifest.Generation} missing from index");
                var blob = PackReader.ReadBlob(Path.Combine(_packsDir, row.Pack), row.PackOffset, (int)row.StoredBytes);
                var plain = DecodeEntry(entry, row, blob, manifest.EncKeyId);
                if (IsAllZero(plain))
                    output.Seek(plain.Length, SeekOrigin.Current);
                else
                    output.Write(plain, 0, plain.Length);
                reported += plain.Length;
                progress?.Report(reported);
            }
        }

        // Per-chunk decode shared by export + verify: decrypt (manifest key
        // first, then every other known key for rotated-incremental children
        // that mix scopes) then decompress. Holes never reach here.
        private byte[] DecodeEntry(ManifestEntry e, ChunkRow row, PackBlob blob, string? manifestEncKeyId)
        {
            if (blob.Nonce.Length == 0)
                return Compression.Decode(blob.Payload, blob.Codec);
            if (string.IsNullOrEmpty(manifestEncKeyId))
                throw new InvalidDataException($"chunk {e.DigestHex}: encrypted blob but manifest declares no encryption key");
            var primary = ResolveKey(manifestEncKeyId);
            try
            {
                return Compression.Decode(ChunkCrypto.Decrypt(blob.Payload, primary, blob.Nonce), blob.Codec);
            }
            catch (CryptographicException)
            {
                // Rotation fallback: a post-rotation incremental reuses clean
                // parent chunks under the OLD key — try each retained key.
                if (_keys != null)
                {
                    foreach (var (id, key) in _keys.AllKeys)
                    {
                        if (id == manifestEncKeyId)
                            continue;
                        try
                        {
                            var plain = Compression.Decode(ChunkCrypto.Decrypt(blob.Payload, key, blob.Nonce), blob.Codec);
                            if (_mixedKeyNoted.Add(manifestEncKeyId))
                                _log.Debug($"snapshot mixes encryption keys (manifest key {manifestEncKeyId}), trying known keys");
                            return plain;
                        }
                        catch (CryptographicException)
                        {
                            // Wrong retained key — keep trying.
                        }
                    }
                }
                throw;
            }
        }

        private byte[] ResolveKey(string keyId)
        {
            if (_keys != null && _keys.TryGet(keyId, out var key))
                return key;
            throw new InvalidDataException($"encryption key {keyId} unavailable (need keys/{keyId}.key in the store)");
        }

        // Digest re-check for encrypted chunks: the digest binds the chunk's
        // own key, so try the manifest key first, then retained keys. Key
        // count is ~ones — bounded, no per-chunk resolve caching needed.
        private bool VerifyDigest(byte[] plain, byte[] digest, string? manifestEncKeyId)
        {
            if (manifestEncKeyId != null && _keys != null && _keys.TryGet(manifestEncKeyId, out var primary)
                && CryptographicOperations.FixedTimeEquals(ChunkId.ComputeId(plain, _key, primary), digest))
                return true;
            if (_keys != null)
            {
                foreach (var (id, key) in _keys.AllKeys)
                {
                    if (id == manifestEncKeyId)
                        continue;
                    if (CryptographicOperations.FixedTimeEquals(ChunkId.ComputeId(plain, _key, key), digest))
                        return true;
                }
            }
            return false;
        }

        // Full re-read: index → pack → decode → id compare. Never throws;
        // every failure path logs and returns false for CLI exit codes.
        // chain=true additionally walks ParentGeneration links to genesis
        // (HMAC loads + PrevDigest linkage; chunk bytes re-read for the
        // target alone — ancestors are provenance, not data).
        public bool Verify(int generation, bool chain = false)
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
                    if (entry.IsHole)
                    {
                        if (entry.DigestHex != "" || entry.Size <= 0 || entry.Size > manifest.ChunkMib * 1024 * 1024)
                        { failed = true; if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: corrupt hole entry"); }
                        continue;
                    }
                    var digest = Convert.FromHexString(entry.DigestHex);
                    if (!_index.TryGet(digest, out var row))
                    {
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: missing from index");
                        continue;
                    }
                    byte[] plain;
                    PackBlob blob;
                    try
                    {
                        blob = PackReader.ReadBlob(Path.Combine(_packsDir, row.Pack), row.PackOffset, (int)row.StoredBytes);
                        plain = DecodeEntry(entry, row, blob, manifest.EncKeyId);
                    }
                    catch (CryptographicException)
                    {
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: decrypt failed (wrong key or tampered)");
                        continue;
                    }
                    catch (InvalidDataException ex) when (ex.Message.Contains("unavailable", StringComparison.Ordinal))
                    {
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: {ex.Message}");
                        continue;
                    }
                    // Broad catch: zstd throws plain System.Exception on corrupt
                    // frames, IO/pack faults throw their own — all mean failure.
                    catch (Exception ex)
                    {
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: unreadable ({ex.Message})");
                        continue;
                    }
                    var verified = plain.Length == entry.Size && (blob.Nonce.Length == 0
                        ? CryptographicOperations.FixedTimeEquals(ChunkId.ComputeId(plain, _key), digest)
                        : VerifyDigest(plain, digest, manifest.EncKeyId));
                    if (!verified)
                    {
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: content mismatch");
                    }
                }
                var total = 0L;
                foreach (var e in manifest.Chunks) total += e.Size;
                if (total != manifest.VolumeLength)
                {
                    failed = true;
                    _log.Error($"snapshot {generation} verify failed: size sum {total} != volume length {manifest.VolumeLength}");
                }
                if (chain && !VerifyChainLinks(generation))
                    failed = true;
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

        // Chain provenance: walk ParentGeneration to genesis, HMAC-loading
        // each link and checking PrevDigest linkage. A gap fails loud
        // (provenance is unverifiable); the target's standalone result is
        // unaffected — it was computed from the target manifest alone.
        private bool VerifyChainLinks(int generation)
        {
            var known = new HashSet<int>();
            foreach (var (gen, _) in ManifestStore.List(_manifestsDir))
                known.Add(gen);
            var current = LoadGeneration(generation);
            while (current.ParentGeneration > 0)
            {
                var parentGen = current.ParentGeneration;
                if (!known.Contains(parentGen))
                {
                    _log.Error($"snapshot {generation} chain: provenance gap (parent gen {parentGen} of gen {current.Generation} missing; standalone verify of the target still passes)");
                    return false;
                }
                SnapshotManifest parent;
                try
                {
                    parent = LoadGeneration(parentGen);
                }
                catch (InvalidDataException ex)
                {
                    _log.Error($"snapshot {generation} chain: gen {parentGen} unreadable ({ex.Message})");
                    return false;
                }
                // PrevDigest seals ADJACENT generations (each manifest carries the
                // previous HMAC); an explicit older --parent skips gens, so
                // linkage is enforced only across adjacent links. Every
                // visited manifest is still HMAC-authenticated by the load.
                if (current.Generation == parentGen + 1 && current.PrevDigest != parent.Hmac)
                {
                    _log.Error($"snapshot {generation} chain: gen {current.Generation} PrevDigest does not match parent gen {parentGen} HMAC");
                    return false;
                }
                current = parent;
            }
            return true;
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
