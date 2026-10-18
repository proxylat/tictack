using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TicTack;

namespace Corpus
{
    // --extract failures. Distinct types (the CLI maps them to exit codes);
    // every message names the path (or store/generation) that failed.
    // Chunk/store-integrity failures reuse Store's InvalidDataException with
    // Store's exact messages, so the CLI maps them like --export errors.
    public sealed class ExtractPathNotFoundException : Exception
    {
        public string RequestedPath { get; }

        public ExtractPathNotFoundException(string requestedPath)
            : base($"extract: path '{requestedPath}' not found in snapshot")
        {
            RequestedPath = requestedPath;
        }
    }

    public sealed class ExtractRefusedException : Exception
    {
        public string? Path { get; }

        public ExtractRefusedException(string message, string? path = null)
            : base(message)
        {
            Path = path;
        }
    }

    public sealed class ExtractCorruptException : Exception
    {
        public int Generation { get; }

        public ExtractCorruptException(int generation, string detail)
            : base($"extract: snapshot {generation} unreadable ({detail})")
        {
            Generation = generation;
        }
    }

    // Files/Dirs = entries written; Bytes = file content bytes written.
    // Skipped = files/dirs/streams NOT restored, one per WRN (ADS streams,
    // EFS/reparse/attrlist/compressed files, reparse dirs). The ACL summary,
    // EFS-dir policy, and timestamp WRNs are metadata degradations on
    // otherwise-restored entries and do not add to Skipped.
    public sealed record ExtractSummary(int Files, int Directories, int Skipped, long Bytes);

    // Selective extract (spec §12.3): files/folders out of a snapshot with
    // no live disk and no FSCTLs. MFT bytes come from snapshot chunks via
    // the same read primitives Store.ExportToStream uses (ChunkIndex →
    // PackReader → Compression, landing read-through, same failure texts),
    // but with random access + a bounded chunk cache instead of a full
    // volume materialization (a whole-volume temp file would grow with user
    // data — scale rule). Decode (record parse, VCN-tracking run decode,
    // path map) lives here because the live-disk parsers answer different
    // questions: MftExtentReader drops sparse-run VCN positions (fine for
    // dirty-chunk mapping, wrong for content reconstruction) and
    // MftPathResolver matches exclusion policy, not user paths.
    public static class SelectiveExtract
    {
        // --extract --snapshot <generation> --paths <split on ','> --out <dir>
        // An empty/whitespace --paths ENTRY means the volume root; an empty
        // LIST refuses. Overwrites existing files, merges into existing dirs
        // (tar-like; re-extract is idempotent).
        public static ExtractSummary Extract(string storeRoot, string dbPath, int generation, IReadOnlyList<string> paths, string outDir, ILogger log, IDeltaCodec? deltaCodec = null)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            ArgumentNullException.ThrowIfNull(dbPath);
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(outDir);
            ArgumentNullException.ThrowIfNull(log);
            if (paths.Count == 0)
                throw new ExtractRefusedException("extract: no --paths given (nothing to extract)");
            if (generation <= 0)
                throw new ExtractRefusedException($"extract: invalid --snapshot '{generation}' (must be a positive generation number)");
            if (File.Exists(outDir))
                throw new ExtractRefusedException($"extract: --out '{outDir}' is a file, refusing", outDir);
            var keyPath = Path.Combine(storeRoot, "id.key");
            if (!File.Exists(keyPath))
                throw new ExtractRefusedException($"extract: store '{storeRoot}' has no id.key (not a corpus store?)", storeRoot);
            if (!File.Exists(dbPath))
                throw new ExtractRefusedException($"extract: chunk index '{dbPath}' not found", dbPath);
            var manifest = LoadGeneration(storeRoot, File.ReadAllBytes(keyPath), generation);
            using var vol = new SnapshotVolumeReader(storeRoot, dbPath, manifest, deltaCodec);
            var geo = NtfsBoot.Parse(vol, generation);
            var mft = new MftStream(vol, geo, generation);
            var job = new ExtractJob(vol, mft, geo, generation, outDir, log);
            job.BuildMap();
            foreach (var p in paths)
            {
                if (p == null)
                    throw new ArgumentException("extract: --paths contains null", nameof(paths));
                job.Enqueue(p);
            }
            Directory.CreateDirectory(outDir); // after all refusals: no empty dir left behind on failure
            return job.Run();
        }

        // Same lookup + same not-found text as Store (private there).
        private static SnapshotManifest LoadGeneration(string storeRoot, byte[] key, int generation)
        {
            var manifestsDir = Path.Combine(storeRoot, "manifests");
            foreach (var (gen, name) in ManifestStore.List(manifestsDir))
            {
                if (gen == generation)
                    return ManifestStore.Load(Path.Combine(manifestsDir, $"{gen:D6}-{name}.json"), key);
            }
            throw new InvalidDataException($"snapshot generation {generation} not found in {manifestsDir}");
        }

        // Requested path → segments. Case/separators normalize at match time;
        // ".." refuses (no escape even on crafted volumes), drive letters and
        // "." drop out, "" (or all-dropped) addresses the volume root.
        internal static List<string> SplitPath(string requested)
        {
            var segs = new List<string>();
            foreach (var raw in requested.Split('\\', '/'))
            {
                if (raw.Length == 0 || raw == ".")
                    continue;
                if (raw.Length == 2 && raw[1] == ':' && char.IsLetter(raw[0]))
                    continue;
                if (raw == "..")
                    throw new ExtractRefusedException($"extract: path '{requested}' escapes the snapshot (.. not allowed)", requested);
                segs.Add(raw);
            }
            return segs;
        }

        // Resolve + collect + emit. Per-record data is parsed twice (light in
        // BuildMap, full at emit): storing resident bytes for every record
        // would pin up to the whole MFT in RAM. Collect is iterative with an
        // active-stack cycle check (a crafted parent loop fails loud instead
        // of recursing forever); emission creates all dirs depth-first, then
        // files in record order (chunk-cache friendly), then dir timestamps
        // deepest-first (child writes would otherwise clobber parent times).
        private sealed class ExtractJob
        {
            // shortcut: 32 MiB readahead segments are arbitrary (one segment
            // fills half the 64 MiB decode cache, leaving headroom for MFT
            // re-reads); shrink it if small boxes show memory pressure.
            private const long PrefetchSegmentBytes = 32L * 1024 * 1024;
            private readonly SnapshotVolumeReader _vol;
            private readonly MftStream _mft;
            private readonly NtfsGeometry _geo;
            private readonly int _generation;
            private readonly string _outRoot;
            private readonly string _outPrefix;
            private readonly ILogger _log;
            private readonly StringComparison _pathCmp =
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            private readonly Dictionary<long, MftNode> _nodes = new();
            private readonly Dictionary<long, List<long>> _kids = new();
            private readonly Dictionary<long, ParsedFile> _dirParsed = new();
            private readonly List<(long Rec, List<string> Segs, string Src)> _seeds = new();
            private readonly List<(long Rec, string Out, string Src, int Depth, bool IsDir)> _collected = new();
            private readonly HashSet<string> _emitted;
            private int _files;
            private int _dirs;
            private int _skipped;
            private long _bytes;

            internal ExtractJob(SnapshotVolumeReader vol, MftStream mft, NtfsGeometry geo, int generation, string outDir, ILogger log)
            {
                _vol = vol;
                _mft = mft;
                _geo = geo;
                _generation = generation;
                _outRoot = Path.GetFullPath(outDir);
                _outPrefix = _outRoot.EndsWith(Path.DirectorySeparatorChar) ? _outRoot : _outRoot + Path.DirectorySeparatorChar;
                _log = log;
                _emitted = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            }

            internal void BuildMap()
            {
                for (var rec = 0L; rec < _mft.RecordCount; rec++)
                {
                    var bytes = _mft.ReadRecord(rec);
                    if (!MftRecordParser.ApplyFixup(bytes, _geo.BytesPerSector))
                        continue; // unparseable slot: loud only if a requested path needs it (then: not-found, or corrupt at emit)
                    if (MftRecordParser.LooksDead(bytes))
                        continue;
                    var node = MftRecordParser.ParseLight(bytes);
                    if (node == null || node.Names.Count == 0)
                        continue;
                    _nodes[rec] = node;
                }
                foreach (var (rec, node) in _nodes)
                {
                    foreach (var (parent, parentSeq, _) in node.Names)
                    {
                        if (parent == rec)
                            continue; // self-parent (root names itself): not a child edge
                        // Sequence cross-check (§4 ABA rule): a stale parent
                        // pointer must orphan, never cross-link to a reused slot.
                        if (!_nodes.TryGetValue(parent, out var pnode) || pnode.Seq != parentSeq)
                            continue;
                        if (!_kids.TryGetValue(parent, out var list))
                        {
                            list = new List<long>();
                            _kids[parent] = list;
                        }
                        if (!list.Contains(rec))
                            list.Add(rec);
                    }
                }
                var rootRef = (long)MftPathResolver.RootFileRef;
                if (!_nodes.TryGetValue(rootRef, out var root) || !root.IsDir)
                    throw new ExtractCorruptException(_generation, "root directory (MFT record 5) unreadable");
            }

            internal void Enqueue(string requested)
            {
                var rec = (long)MftPathResolver.RootFileRef;
                var segs = new List<string>();
                foreach (var seg in SplitPath(requested))
                {
                    var found = FindKid(rec, seg) ?? throw new ExtractPathNotFoundException(requested);
                    segs.Add(found.Name);
                    rec = found.Rec;
                }
                // Seeds preserve their path under --out (tar-like): flattening
                // would silently drop one of a\f.txt vs b\f.txt via the
                // overlap guard. On-disk names, so "DOCS/x" lands in docs\.
                var src = segs.Count == 0 ? "\\" : "\\" + string.Join('\\', segs);
                _seeds.Add((rec, segs, src));
            }

            private (long Rec, string Name)? FindKid(long parent, string seg)
            {
                if (!_kids.TryGetValue(parent, out var list))
                    return null;
                foreach (var kid in list)
                {
                    if (!_nodes.TryGetValue(kid, out var node))
                        continue;
                    foreach (var (p, _, name) in node.Names)
                    {
                        if (p == parent && string.Equals(name, seg, StringComparison.OrdinalIgnoreCase))
                            return (kid, name);
                    }
                }
                return null;
            }

            internal ExtractSummary Run()
            {
                foreach (var (rec, segs, src) in _seeds)
                {
                    var seedOut = _outRoot;
                    foreach (var seg in segs)
                        seedOut = CombineOrThrow(seedOut, seg, src);
                    Collect(rec, seedOut, src, 0);
                }
                Emit();
                if (_files + _dirs > 0)
                    _log.Warn($"extract: ACLs not restored ({_files} files, {_dirs} dirs extracted without security descriptors)");
                _log.Info($"extract: snapshot {_generation}: {_files} files, {_dirs} dirs, {_skipped} skipped, {_bytes} bytes -> {_outRoot}");
                return new ExtractSummary(_files, _dirs, _skipped, _bytes);
            }

            private void Collect(long rootRec, string rootOut, string rootSrc, int rootDepth)
            {
                var active = new HashSet<long>();
                var stack = new Stack<(long Rec, string Out, string Src, int Depth, bool Post)>();
                stack.Push((rootRec, rootOut, rootSrc, rootDepth, false));
                while (stack.Count > 0)
                {
                    var (rec, outPath, src, depth, post) = stack.Pop();
                    if (post)
                    {
                        active.Remove(rec);
                        continue;
                    }
                    if (!_emitted.Add(outPath))
                        continue; // overlapping --paths entries extract once
                    if (!active.Add(rec))
                        throw new ExtractCorruptException(_generation, $"MFT parent cycle at record {rec} (needed for '{src}')");
                    if (!_nodes.TryGetValue(rec, out var node))
                        throw new ExtractCorruptException(_generation, $"MFT record {rec} unreadable (needed for '{src}')");
                    if (!node.IsDir)
                    {
                        _collected.Add((rec, outPath, src, depth, false));
                        active.Remove(rec);
                        continue;
                    }
                    var parsed = ParseFullOrThrow(rec, src);
                    if (parsed.IsReparse)
                    {
                        _skipped++;
                        _log.Warn($"extract: skipped '{src}' (reparse point not restored)");
                        active.Remove(rec);
                        continue; // never descend into junctions/symlinks: targets may lie outside the selection
                    }
                    if (parsed.IsEfs)
                        _log.Warn($"extract: EFS policy not restored for '{src}' (directory restored unencrypted)");
                    WarnAds(parsed, src);
                    _collected.Add((rec, outPath, src, depth, true));
                    _dirParsed[rec] = parsed;
                    stack.Push((rec, outPath, src, depth, true));
                    if (!_kids.TryGetValue(rec, out var kids))
                        continue;
                    for (var i = kids.Count - 1; i >= 0; i--)
                    {
                        if (!_nodes.TryGetValue(kids[i], out var knode))
                            continue;
                        foreach (var (p, _, name) in knode.Names)
                        {
                            if (p != rec)
                                continue;
                            var kidSrc = src == "\\" ? "\\" + name : src + "\\" + name;
                            stack.Push((kids[i], CombineOrThrow(outPath, name, kidSrc), kidSrc, depth + 1, false));
                        }
                    }
                }
            }

            private void Emit()
            {
                var dirs = new List<(long Rec, string Out, string Src, int Depth)>();
                var files = new List<(long Rec, string Out, string Src)>();
                foreach (var c in _collected)
                {
                    if (c.IsDir)
                        dirs.Add((c.Rec, c.Out, c.Src, c.Depth));
                    else
                        files.Add((c.Rec, c.Out, c.Src));
                }
                dirs.Sort((a, b) => a.Depth.CompareTo(b.Depth));
                files.Sort((a, b) => a.Rec.CompareTo(b.Rec));
                var dirTimes = new List<(string Out, ParsedFile Parsed, string Src, int Depth)>(dirs.Count);
                foreach (var d in dirs)
                {
                    try
                    {
                        Directory.CreateDirectory(d.Out);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new ExtractRefusedException($"extract: cannot create directory '{d.Out}' ({ex.Message})", d.Out);
                    }
                    _dirs++;
                    dirTimes.Add((d.Out, _dirParsed[d.Rec], d.Src, d.Depth));
                }
                foreach (var f in files)
                    WriteFile(f.Rec, f.Out, f.Src);
                dirTimes.Sort((a, b) => b.Depth.CompareTo(a.Depth));
                foreach (var d in dirTimes)
                    SetTimes(d.Out, d.Parsed, d.Src);
            }

            private void WriteFile(long rec, string outPath, string src)
            {
                var parsed = ParseFullOrThrow(rec, src);
                if (parsed.IsEfs)
                {
                    _skipped++;
                    _log.Warn($"extract: skipped '{src}' (EFS-encrypted file, plaintext unavailable)");
                    return;
                }
                if (parsed.IsReparse)
                {
                    _skipped++;
                    _log.Warn($"extract: skipped '{src}' (reparse point not restored)");
                    return;
                }
                if (parsed.HasAttrList)
                {
                    _skipped++;
                    _log.Warn($"extract: skipped '{src}' (multi-record file with attribute list not supported)");
                    return;
                }
                if (parsed.IsCompressed)
                {
                    _skipped++;
                    _log.Warn($"extract: skipped '{src}' (compressed file not supported)");
                    return;
                }
                WarnAds(parsed, src);
                if (!parsed.HasData)
                    throw new ExtractCorruptException(_generation, $"file '{src}' has no $DATA");
                try
                {
                    // File seeds ("docs\big.bin") carry no ancestor entries;
                    // their parents are created plain (no MFT timestamps).
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    if (parsed.ResidentData != null)
                        output.Write(parsed.ResidentData, 0, parsed.ResidentData.Length);
                    else
                        WriteRuns(output, parsed, src);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ExtractRefusedException($"extract: cannot write '{outPath}' ({ex.Message})", outPath);
                }
                _files++;
                _bytes += parsed.DataSize;
                SetTimes(outPath, parsed, src);
            }

            private void WarnAds(ParsedFile parsed, string src)
            {
                foreach (var ads in parsed.Ads)
                {
                    _skipped++;
                    _log.Warn($"extract: skipped ADS '{src}:{ads}' (alternate data streams not restored)");
                }
            }

            private void WriteRuns(FileStream output, ParsedFile parsed, string src)
            {
                var stream = new MftRunStream(parsed.DataRuns!, _vol, _geo.BytesPerCluster, _generation, src);
                if (parsed.DataSize > (stream.EndVcn - stream.StartVcn + 1) * _geo.BytesPerCluster)
                    throw new ExtractCorruptException(_generation, $"file '{src}' runs shorter than its size");
                var buf = new byte[(int)Math.Min(parsed.DataSize, 1024 * 1024)];
                var pos = 0L;
                var nextPrefetch = 0L;
                while (pos < parsed.DataSize)
                {
                    if (pos >= nextPrefetch)
                    {
                        // Bounded readahead: the next segment's chunks are
                        // fetched (serial pack reads) and decoded in parallel
                        // before the loop below consumes them. Writes stay
                        // serial, so output bytes are identical at any thread
                        // count. Best-effort: a failed prefetch surfaces
                        // identically on the authoritative read below.
                        try
                        {
                            stream.PrefetchFileRange(pos, Math.Min(PrefetchSegmentBytes, parsed.DataSize - pos));
                        }
                        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or SqliteException)
                        {
                            // The read loop below re-fetches and throws identically.
                        }
                        nextPrefetch = pos + PrefetchSegmentBytes;
                    }
                    var take = (int)Math.Min(buf.Length, parsed.DataSize - pos);
                    stream.Read(pos, buf, 0, take, zeroUnmapped: false);
                    // Valid-data-length tail: NTFS reads [initSize, realSize) as zeros.
                    if (parsed.DataInitSize < pos + take)
                    {
                        var zeroFrom = (int)Math.Max(parsed.DataInitSize - pos, 0);
                        if (zeroFrom < take)
                            Array.Clear(buf, zeroFrom, take - zeroFrom);
                    }
                    output.Write(buf, 0, take);
                    pos += take;
                }
            }

            private void SetTimes(string outPath, ParsedFile parsed, string src)
            {
                try
                {
                    if (parsed.WrittenUtc != null)
                        File.SetLastWriteTimeUtc(outPath, parsed.WrittenUtc.Value);
                    if (parsed.AccessedUtc != null)
                        File.SetLastAccessTimeUtc(outPath, parsed.AccessedUtc.Value);
                    if (OperatingSystem.IsWindows() && parsed.CreatedUtc != null)
                        File.SetCreationTimeUtc(outPath, parsed.CreatedUtc.Value);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"extract: timestamps not restored for '{src}' ({ex.Message})");
                }
            }

            private ParsedFile ParseFullOrThrow(long rec, string src)
            {
                var bytes = _mft.ReadRecord(rec);
                if (!MftRecordParser.ApplyFixup(bytes, _geo.BytesPerSector) || MftRecordParser.LooksDead(bytes))
                    throw new ExtractCorruptException(_generation, $"MFT record {rec} unreadable (needed for '{src}')");
                return MftRecordParser.ParseFull(bytes)
                    ?? throw new ExtractCorruptException(_generation, $"MFT record {rec} unparseable (needed for '{src}')");
            }

            // Every output segment is validated (crafted MFT names can hold
            // separators/relatives) plus a full-path containment backstop.
            private string CombineOrThrow(string parentOut, string name, string src)
            {
                if (name.Length == 0 || name == "." || name == ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
                    throw new ExtractRefusedException($"extract: entry '{src}' has an unrestorable name, refusing", src);
                var full = Path.GetFullPath(Path.Combine(parentOut, name));
                if (!full.StartsWith(_outPrefix, _pathCmp))
                    throw new ExtractRefusedException($"extract: entry '{src}' escapes --out, refusing", src);
                return full;
            }
        }
    }

    // Random-access volume bytes over snapshot chunks. Same fetch order and
    // failure texts as Store's export path (index → pack → decode, landing
    // read-through, holes seek as zeros). The decode cache is
    // content-addressed (digest-keyed: deduped chunks sharing one digest
    // decode once) under a hard MB cap, so cached RAM never grows with
    // volume size; sequential MFT scans still decode each chunk once, and
    // re-reads (MFT pass 2, overlapping --paths) hit until evicted.
    internal sealed class SnapshotVolumeReader : IDisposable
    {
        // shortcut: 64 MiB cap is arbitrary headroom for one prefetch segment
        // (32 MiB) plus MFT re-reads on small backup boxes; raise it if
        // profiles show cache thrash on large extracts, never uncap it.
        private const long MaxCacheBytes = 64L * 1024 * 1024;
        // Bounded parallel-decode width, same clamp as Store.EncodeWindow.
        private static readonly int DecodeDop = Math.Clamp(Environment.ProcessorCount, 2, 16);
        internal static int? DecodeDopOverride; // tests only: forces worker count
        private readonly SnapshotManifest _manifest;
        private readonly int _generation;
        private readonly string _packsDir;
        private readonly string _landingDir;
        private readonly int _chunkBytes;
        private readonly ChunkIndex _index;
        // §12.7 seam: null = no delta codec wired (old behavior; delta reads
        // refuse loud). The winning codec plugs in here post-spike.
        private readonly IDeltaCodec? _deltaCodec;
        private readonly DeltaChunkReader? _deltaReader;
        private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();
        private long _cacheBytes;
        private bool _disposed;

        public long Length => _manifest.VolumeLength;

        internal int CachedChunkCount => _cache.Count;
        internal long CachedBytes => _cacheBytes;

        internal SnapshotVolumeReader(string storeRoot, string dbPath, SnapshotManifest manifest, IDeltaCodec? deltaCodec = null)
        {
            _manifest = manifest;
            _generation = manifest.Generation;
            _packsDir = Path.Combine(storeRoot, "packs");
            _landingDir = Path.Combine(storeRoot, "landing");
            _chunkBytes = manifest.ChunkMib * 1024 * 1024;
            _index = new ChunkIndex(dbPath);
            _deltaCodec = deltaCodec;
            // ChunkId key is store-local; the id key binds digests, so the
            // resolver needs it for the reconstruction hash-check.
            _deltaReader = deltaCodec != null ? new DeltaChunkReader(_index, _packsDir, _landingDir, ChunkId.EnsureKey(storeRoot), deltaCodec) : null;
        }

        public void Read(long offset, byte[] buffer, int bufOffset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(buffer);
            if (bufOffset < 0 || bufOffset > buffer.Length || count < 0 || count > buffer.Length - bufOffset)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (offset < 0 || count > _manifest.VolumeLength - offset)
                throw new ExtractCorruptException(_generation, "read past end of volume");
            var remaining = count;
            var bpos = bufOffset;
            var pos = offset;
            while (remaining > 0)
            {
                var chunkIdx = pos / _chunkBytes;
                if (chunkIdx >= _manifest.Chunks.Count)
                    throw new ExtractCorruptException(_generation, "chunk map shorter than volume");
                var entry = _manifest.Chunks[(int)chunkIdx];
                var chunkOff = (int)(pos % _chunkBytes);
                if (chunkOff >= entry.Size)
                    throw new ExtractCorruptException(_generation, $"chunk {chunkIdx} map inconsistent");
                var take = (int)Math.Min(entry.Size - chunkOff, remaining);
                if (entry.IsHole)
                {
                    if (entry.DigestHex != "" || entry.Size <= 0 || entry.Size > _chunkBytes)
                        throw new InvalidDataException($"snapshot {_generation} chunk {chunkIdx}: corrupt hole entry");
                    Array.Clear(buffer, bpos, take);
                }
                else
                {
                    Buffer.BlockCopy(GetChunk(chunkIdx), chunkOff, buffer, bpos, take);
                }
                pos += take;
                bpos += take;
                remaining -= take;
            }
        }

        public byte[] ReadExact(long offset, int count)
        {
            var buf = new byte[count];
            Read(offset, buf, 0, count);
            return buf;
        }

        private byte[] GetChunk(long index)
        {
            var entry = _manifest.Chunks[(int)index];
            if (!entry.IsHole && _cache.TryGetValue(entry.DigestHex, out var hit))
                return hit;
            var chunk = FetchChunk(index);
            if (!entry.IsHole)
                AddToCache(entry.DigestHex, chunk);
            return chunk;
        }

        private void AddToCache(string digestHex, byte[] plain)
        {
            if (_cache.ContainsKey(digestHex))
                return;
            while (_cacheBytes + plain.Length > MaxCacheBytes && _order.Count > 0)
            {
                var oldest = _order.Dequeue();
                if (_cache.Remove(oldest, out var evicted))
                    _cacheBytes -= evicted.Length;
            }
            _cache[digestHex] = plain;
            _order.Enqueue(digestHex);
            _cacheBytes += plain.Length;
        }

        // Best-effort readahead for a volume span: missing chunks are
        // fetched serially in volume order (pack reads stay sequential for
        // readahead) and decoded in parallel; every failure mode is the
        // caller's Read path re-throwing identically, so prefetch never
        // changes observable behavior — it only warms the cache.
        internal void PrefetchRange(long volumeOffset, long byteCount)
        {
            if (_disposed || byteCount <= 0 || volumeOffset < 0 || volumeOffset >= _manifest.VolumeLength)
                return;
            var end = Math.Min(volumeOffset + byteCount, _manifest.VolumeLength);
            List<long>? missing = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pos = volumeOffset;
            while (pos < end)
            {
                var chunkIdx = pos / _chunkBytes;
                if (chunkIdx >= _manifest.Chunks.Count)
                    return;
                var entry = _manifest.Chunks[(int)chunkIdx];
                var chunkOff = pos % _chunkBytes;
                if (chunkOff >= entry.Size)
                    return;
                var take = Math.Min(entry.Size - chunkOff, end - pos);
                if (take <= 0)
                    return;
                if (!entry.IsHole && !_cache.ContainsKey(entry.DigestHex) && seen.Add(entry.DigestHex))
                    (missing ??= new()).Add(chunkIdx);
                pos += take;
            }
            if (missing == null || missing.Count == 0)
                return;
            var blobs = new (string Digest, byte[] Payload, byte Codec, long Size)[missing.Count];
            for (var i = 0; i < missing.Count; i++)
            {
                var entry = _manifest.Chunks[(int)missing[i]];
                var (payload, codec) = FetchPayload(missing[i], entry);
                blobs[i] = (entry.DigestHex, payload, codec, entry.Size);
            }
            var plains = new byte[blobs.Length][];
            if (blobs.Length == 1 || AllRaw(blobs))
            {
                // Nothing to parallelize (same skip as Compression.EncodeBatch).
                for (var i = 0; i < blobs.Length; i++)
                    plains[i] = CheckSize(blobs[i], missing[i]);
            }
            else
            {
                try
                {
                    Parallel.For(0, blobs.Length, new ParallelOptions { MaxDegreeOfParallelism = DecodeDopOverride ?? DecodeDop },
                        i => { plains[i] = Compression.Decode(blobs[i].Payload, blobs[i].Codec); });
                }
                catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
                }
                for (var i = 0; i < blobs.Length; i++)
                {
                    if (plains[i] == null || plains[i].Length != blobs[i].Size)
                        throw new InvalidDataException($"snapshot {_generation} chunk {missing[i]}: content mismatch");
                }
            }
            for (var i = 0; i < blobs.Length; i++)
                AddToCache(blobs[i].Digest, plains[i]);
        }

        private static bool AllRaw((string Digest, byte[] Payload, byte Codec, long Size)[] blobs)
        {
            foreach (var b in blobs)
            {
                if (b.Codec != Compression.CodecRaw)
                    return false;
            }
            return true;
        }

        private byte[] CheckSize((string Digest, byte[] Payload, byte Codec, long Size) b, long index)
        {
            var plain = Compression.Decode(b.Payload, b.Codec);
            if (plain.Length != b.Size)
                throw new InvalidDataException($"snapshot {_generation} chunk {index}: content mismatch");
            return plain;
        }

        private byte[] FetchChunk(long index)
        {
            var entry = _manifest.Chunks[(int)index];
            var (payload, codec) = FetchPayload(index, entry);
            var plain = Compression.Decode(payload, codec);
            if (plain.Length != entry.Size)
                throw new InvalidDataException($"snapshot {_generation} chunk {index}: content mismatch");
            return plain;
        }

        // Index → pack → payload (landing read-through); decode and size
        // checks stay with the caller so serial and prefetch paths share
        // the exact failure texts.
        private (byte[] Payload, byte Codec) FetchPayload(long index, ManifestEntry entry)
        {
            byte[] digest;
            try
            {
                digest = Convert.FromHexString(entry.DigestHex);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"snapshot {_generation} chunk {index}: corrupt digest", ex);
            }
            if (digest.Length != 32)
                throw new InvalidDataException($"snapshot {_generation} chunk {index}: corrupt digest");
            if (_index.TryGet(digest, out var row))
            {
                var blob = PackReader.ReadBlob(Path.Combine(_packsDir, row.Pack), row.PackOffset, (int)row.StoredBytes);
                if (entry.BaseDigestHex != null)
                {
                    // Deltas resolve serially here (index IO is not
                    // thread-safe); the prefetch parallel loop below then
                    // only ever sees raw/whole codecs.
                    return (ResolveDelta(index, entry, blob.Payload, blob.Codec), Compression.CodecRaw);
                }
                return (blob.Payload, blob.Codec);
            }
            var landingPath = Path.Combine(_landingDir, entry.DigestHex + ".chunk");
            if (File.Exists(landingPath))
            {
                var landed = File.ReadAllBytes(landingPath);
                if (landed.Length != entry.Size)
                    throw new InvalidDataException($"chunk {entry.DigestHex} of snapshot {_generation} landing size mismatch ({landed.Length} != {entry.Size})");
                return (landed, Compression.CodecRaw);
            }
            throw new InvalidDataException($"chunk {entry.DigestHex} of snapshot {_generation} missing from index");
        }

        // Delta fetch funnel: kind check here, reconstruct + hash-check in
        // the shared reader. Every failure names the chunk, old-reader case
        // included (no codec wired = loud refusal, never a silent whole).
        private byte[] ResolveDelta(long index, ManifestEntry entry, byte[] payload, byte codec)
        {
            if (_deltaCodec == null || _deltaReader == null)
                throw new InvalidDataException($"snapshot {_generation} chunk {index}: delta entry needs codec {entry.DeltaCodec} (old reader cannot read deltas)");
            if (codec != _deltaCodec.CodecId)
                throw new InvalidDataException($"snapshot {_generation} chunk {index}: delta entry stored as codec {codec}, expected {_deltaCodec.CodecId}");
            return _deltaReader.Resolve(entry, payload, _generation, index);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _index.Dispose();
        }
    }

    internal sealed record NtfsGeometry(int BytesPerSector, int BytesPerCluster, int RecordBytes, long MftByteOffset);

    // NTFS boot sector (BPB) parse. Anything outside sane NTFS geometry
    // fails loud — never guess offsets on a non-NTFS or torn boot sector.
    internal static class NtfsBoot
    {
        internal static NtfsGeometry Parse(SnapshotVolumeReader vol, int generation)
        {
            ArgumentNullException.ThrowIfNull(vol);
            if (vol.Length < 512)
                throw new ExtractCorruptException(generation, "volume smaller than one sector");
            var boot = vol.ReadExact(0, 512);
            if (boot[0x1FE] != 0x55 || boot[0x1FF] != 0xAA || !IsNtfsOem(boot))
                throw new ExtractCorruptException(generation, "not an NTFS volume (boot signature)");
            var bps = BitConverter.ToUInt16(boot, 0x0B);
            if (bps is not (512 or 1024 or 2048 or 4096))
                throw new ExtractCorruptException(generation, $"unsupported bytes-per-sector {bps}");
            var spc = boot[0x0D];
            if (spc == 0 || spc > 128)
                throw new ExtractCorruptException(generation, $"unsupported sectors-per-cluster {spc}");
            var bpc = (int)bps * spc;
            var cpr = (sbyte)boot[0x40];
            int recordBytes;
            if (cpr < 0)
            {
                if (cpr > -9 || cpr < -20)
                    throw new ExtractCorruptException(generation, $"unsupported file-record size code {cpr}");
                recordBytes = 1 << -cpr;
            }
            else
            {
                if (cpr == 0)
                    throw new ExtractCorruptException(generation, $"unsupported file-record size code {cpr}");
                recordBytes = cpr * bpc;
                if (recordBytes > 1024 * 1024)
                    throw new ExtractCorruptException(generation, $"unsupported file-record size {recordBytes}");
            }
            if (recordBytes % bps != 0)
                throw new ExtractCorruptException(generation, $"file-record size {recordBytes} not sector-aligned");
            var mftLcn = BitConverter.ToUInt64(boot, 0x30);
            if (mftLcn > (ulong)(vol.Length / bpc))
                throw new ExtractCorruptException(generation, "MFT location past end of volume");
            var mftOff = (long)mftLcn * bpc;
            if (mftOff > vol.Length - recordBytes)
                throw new ExtractCorruptException(generation, "MFT record 0 past end of volume");
            return new NtfsGeometry(bps, bpc, recordBytes, mftOff);
        }

        private static bool IsNtfsOem(byte[] boot)
        {
            return boot[3] == (byte)'N' && boot[4] == (byte)'T' && boot[5] == (byte)'F' && boot[6] == (byte)'S'
                && boot[7] == (byte)' ' && boot[8] == (byte)' ' && boot[9] == (byte)' ' && boot[10] == (byte)' ';
        }
    }

    // One data run: VCN-ordered, Lcn -1 = sparse hole (reads as zeros).
    internal sealed record MftRun(long VcnStart, long Lcn, long Count);

    // VCN → LCN map with binary search (MFT/file runs stay sorted by
    // construction) + cluster-granular reads that coalesce contiguous LCNs.
    internal sealed class MftRunStream
    {
        private readonly List<MftRun> _runs;
        private readonly SnapshotVolumeReader _vol;
        private readonly int _bpc;
        private readonly int _generation;
        private readonly string _context;

        public long StartVcn { get; }
        public long EndVcn { get; }

        internal MftRunStream(List<MftRun> runs, SnapshotVolumeReader vol, int bpc, int generation, string context)
        {
            ArgumentNullException.ThrowIfNull(runs);
            ArgumentNullException.ThrowIfNull(vol);
            ArgumentNullException.ThrowIfNull(context);
            _runs = runs;
            _vol = vol;
            _bpc = bpc;
            _generation = generation;
            _context = context;
            if (runs.Count > 0)
            {
                StartVcn = runs[0].VcnStart;
                var last = runs[runs.Count - 1];
                EndVcn = last.VcnStart + last.Count - 1;
            }
            else
            {
                StartVcn = 0;
                EndVcn = -1;
            }
        }

        public void Read(long byteOffset, byte[] buffer, int bufOffset, int count, bool zeroUnmapped)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (byteOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(byteOffset));
            if (bufOffset < 0 || bufOffset > buffer.Length || count < 0 || count > buffer.Length - bufOffset)
                throw new ArgumentOutOfRangeException(nameof(count));
            var remaining = count;
            var bpos = bufOffset;
            var pos = byteOffset;
            while (remaining > 0)
            {
                var vcn = pos / _bpc;
                var vcnOff = (int)(pos % _bpc);
                if (!TryMap(vcn, out var lcn, out var runEnd) || lcn < 0)
                {
                    if (!zeroUnmapped && lcn >= 0)
                        throw new ExtractCorruptException(_generation, $"file '{_context}' has a gap in its data runs");
                    var zeros = (int)Math.Min(remaining, (long)_bpc - vcnOff);
                    Array.Clear(buffer, bpos, zeros);
                    pos += zeros;
                    bpos += zeros;
                    remaining -= zeros;
                    continue;
                }
                // Coalesce contiguous LCNs, capped so adversarial run lengths
                // can never overflow the arithmetic (want ≤ 1 MiB ⇒ the
                // clamped span is provably small).
                var want = Math.Min(remaining, 1024 * 1024);
                var clustersNeeded = (want + vcnOff + _bpc - 1) / _bpc;
                if (vcn + clustersNeeded - 1 > runEnd)
                    want = (int)((runEnd - vcn) * _bpc + (_bpc - vcnOff));
                var volOff = lcn * _bpc + vcnOff;
                if (volOff < 0 || volOff > _vol.Length - want)
                    throw new ExtractCorruptException(_generation, $"file '{_context}' references data outside the volume");
                _vol.Read(volOff, buffer, bpos, want);
                pos += want;
                bpos += want;
                remaining -= want;
            }
        }

        // Best-effort readahead for a file span: walks VCNs exactly like
        // Read (same TryMap, sparse runs skipped) and prefetches each
        // contiguous mapped volume extent. Never throws outward — the
        // caller swallows so the authoritative Read keeps its exact errors.
        internal void PrefetchFileRange(long fileOffset, long count)
        {
            if (count <= 0 || fileOffset < 0)
                return;
            var remaining = count;
            var pos = fileOffset;
            long extStart = -1;
            long extLen = 0;
            while (remaining > 0)
            {
                var vcn = pos / _bpc;
                var vcnOff = (int)(pos % _bpc);
                if (!TryMap(vcn, out var lcn, out var runEnd) || lcn < 0)
                {
                    if (extStart >= 0)
                    {
                        _vol.PrefetchRange(extStart, extLen);
                        extStart = -1;
                        extLen = 0;
                    }
                    var skip = Math.Min(remaining, (long)_bpc - vcnOff);
                    pos += skip;
                    remaining -= skip;
                    continue;
                }
                var want = Math.Min(remaining, (runEnd - vcn) * _bpc + (_bpc - vcnOff));
                var volOff = lcn * _bpc + vcnOff;
                if (volOff < 0 || want <= 0)
                {
                    if (extStart >= 0)
                    {
                        _vol.PrefetchRange(extStart, extLen);
                        extStart = -1;
                        extLen = 0;
                    }
                    var step = Math.Min(remaining, (long)_bpc - vcnOff);
                    pos += step;
                    remaining -= step;
                    continue;
                }
                if (extStart < 0)
                {
                    extStart = volOff;
                    extLen = want;
                }
                else if (volOff == extStart + extLen)
                {
                    extLen += want;
                }
                else
                {
                    _vol.PrefetchRange(extStart, extLen);
                    extStart = volOff;
                    extLen = want;
                }
                pos += want;
                remaining -= want;
            }
            if (extStart >= 0)
                _vol.PrefetchRange(extStart, extLen);
        }

        private bool TryMap(long vcn, out long lcn, out long runEnd)
        {
            lcn = -1;
            runEnd = -1;
            var lo = 0;
            var hi = _runs.Count - 1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                var r = _runs[mid];
                if (vcn < r.VcnStart)
                    hi = mid - 1;
                else if (vcn >= r.VcnStart + r.Count)
                    lo = mid + 1;
                else
                {
                    runEnd = r.VcnStart + r.Count - 1;
                    lcn = r.Lcn < 0 ? -1 : r.Lcn + (vcn - r.VcnStart);
                    return true;
                }
            }
            return false;
        }
    }

    // $MFT stream access: record 0 is read optimistically (contiguous from
    // the boot MFT LCN — true whenever the first MFT run covers it, i.e.
    // always in practice) and then VERIFIED through its own parsed runs, so
    // a fragmented-inside-record-0 MFT fails loud instead of misparsing.
    internal sealed class MftStream
    {
        private readonly MftRunStream _stream;
        private readonly NtfsGeometry _geo;

        public long RecordCount { get; }

        internal MftStream(SnapshotVolumeReader vol, NtfsGeometry geo, int generation)
        {
            ArgumentNullException.ThrowIfNull(vol);
            ArgumentNullException.ThrowIfNull(geo);
            _geo = geo;
            var rec0 = vol.ReadExact(geo.MftByteOffset, geo.RecordBytes);
            var raw0 = (byte[])rec0.Clone();
            if (!MftRecordParser.ApplyFixup(rec0, geo.BytesPerSector))
                throw new ExtractCorruptException(generation, "MFT record 0 failed fixup");
            var parsed = MftRecordParser.ParseFull(rec0);
            if (parsed == null || !parsed.HasData || parsed.ResidentData != null || parsed.DataRuns == null || parsed.DataSize <= 0)
                throw new ExtractCorruptException(generation, "MFT record 0 has no non-resident $DATA");
            _stream = new MftRunStream(parsed.DataRuns, vol, geo.BytesPerCluster, generation, "MFT");
            RecordCount = Math.Min(parsed.DataSize / geo.RecordBytes, vol.Length / geo.RecordBytes);
            if (RecordCount <= (long)MftPathResolver.RootFileRef)
                throw new ExtractCorruptException(generation, "MFT too small for a root directory");
            var check = new byte[geo.RecordBytes];
            _stream.Read(0, check, 0, check.Length, zeroUnmapped: true);
            if (!BytesEqual(check, raw0))
                throw new ExtractCorruptException(generation, "MFT record 0 spans non-contiguous clusters (unsupported)");
        }

        public byte[] ReadRecord(long recNo)
        {
            if (recNo < 0 || recNo >= RecordCount)
                throw new ArgumentOutOfRangeException(nameof(recNo));
            var buf = new byte[_geo.RecordBytes];
            _stream.Read(recNo * _geo.RecordBytes, buf, 0, buf.Length, zeroUnmapped: true);
            return buf;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }
    }

    // Light map entry (pass 1): identity only, no content. ParentSeq is the
    // §4 ABA cross-check: stale parent pointers orphan instead of linking.
    internal sealed class MftNode
    {
        public ushort Seq;
        public bool IsDir;
        public readonly List<(long Parent, ushort ParentSeq, string Name)> Names = new();
    }

    // Full record decode (pass 2, extracted paths only). DataRuns covers the
    // unnamed $DATA iff non-resident (the caller builds the run stream).
    internal sealed class ParsedFile
    {
        public bool IsDir;
        public DateTime? CreatedUtc;
        public DateTime? WrittenUtc;
        public DateTime? AccessedUtc;
        public bool HasData;
        public byte[]? ResidentData;
        public List<MftRun>? DataRuns;
        public long DataSize;
        public long DataInitSize;
        public readonly List<string> Ads = new();
        public bool IsEfs;
        public bool IsReparse;
        public bool HasAttrList;
        public bool IsCompressed;
    }

    // Raw FILE-record decode (fixup + attributes). FSCTL records arrive with
    // fixup applied; snapshot bytes are raw, so ApplyFixup runs first and
    // any structural surprise returns null (caller skips or fails loud with
    // the affected path — never silently wrong). Unknown attribute types are
    // skipped for forward compatibility; malformed KNOWN attributes are null.
    internal static class MftRecordParser
    {
        private const uint AttrStandardInformation = 0x10;
        private const uint AttrAttributeList = 0x20;
        private const uint AttrFileName = 0x30;
        private const uint AttrData = 0x80;
        private const uint AttrReparsePoint = 0xC0;
        private const uint AttrLoggedUtilityStream = 0x100;
        private const uint AttrEnd = 0xFFFFFFFF;
        private const int NameSpaceDos = 2;
        private const uint SiEncrypted = 0x4000;
        private const long MaxFileTime = 2650467743999999999L; // DateTime.MaxValue as FILETIME

        internal static bool ApplyFixup(byte[] record, int bytesPerSector)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (record.Length < 8 || bytesPerSector <= 0 || record.Length % bytesPerSector != 0)
                return false;
            var usOffset = BitConverter.ToUInt16(record, 0x04);
            var usCount = BitConverter.ToUInt16(record, 0x06);
            var sectors = record.Length / bytesPerSector;
            if (usCount != sectors + 1 || usOffset < 8 || usOffset + usCount * 2 > record.Length)
                return false;
            var usn = BitConverter.ToUInt16(record, usOffset);
            for (var s = 0; s < sectors; s++)
            {
                if (BitConverter.ToUInt16(record, (s + 1) * bytesPerSector - 2) != usn)
                    return false;
            }
            for (var s = 0; s < sectors; s++)
            {
                var trailer = (s + 1) * bytesPerSector - 2;
                record[trailer] = record[usOffset + 2 + s * 2];
                record[trailer + 1] = record[usOffset + 3 + s * 2];
            }
            return true;
        }

        internal static bool LooksDead(byte[] record)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (record.Length < 48)
                return true;
            if (record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E')
                return true;
            if ((BitConverter.ToUInt16(record, 0x16) & 1) == 0)
                return true;
            return BitConverter.ToUInt64(record, 0x20) != 0; // extension record: owned by its base
        }

        internal static MftNode? ParseLight(byte[] record)
        {
            ArgumentNullException.ThrowIfNull(record);
            var spans = AttributeSpans(record);
            if (spans == null)
                return null;
            var node = new MftNode
            {
                Seq = BitConverter.ToUInt16(record, 0x10),
                IsDir = (BitConverter.ToUInt16(record, 0x16) & 2) != 0,
            };
            var dosOnly = new List<(long, ushort, string)>();
            foreach (var (off, len) in spans)
            {
                if (BitConverter.ToUInt32(record, off) != AttrFileName)
                    continue;
                if (!TryFileName(record, off, len, out var parent, out var parentSeq, out var name, out var ns, out _))
                    return null;
                if (ns == NameSpaceDos)
                    dosOnly.Add((parent, parentSeq, name));
                else
                    node.Names.Add((parent, parentSeq, name));
            }
            if (node.Names.Count == 0)
                node.Names.AddRange(dosOnly); // DOS-only record (8.3 alias without a Win32 name)
            return node;
        }

        internal static ParsedFile? ParseFull(byte[] record)
        {
            ArgumentNullException.ThrowIfNull(record);
            var spans = AttributeSpans(record);
            if (spans == null)
                return null;
            var parsed = new ParsedFile { IsDir = (BitConverter.ToUInt16(record, 0x16) & 2) != 0 };
            var haveSi = false;
            var haveFn = false;
            long fnCreated = 0, fnWritten = 0, fnAccessed = 0;
            foreach (var (off, len) in spans)
            {
                switch (BitConverter.ToUInt32(record, off))
                {
                    case AttrStandardInformation:
                        if (!haveSi)
                        {
                            if (!TryResidentValue(record, off, len, out var siOff, out var siLen) || siLen < 32)
                                return null;
                            haveSi = true;
                            parsed.CreatedUtc = FileTime(BitConverter.ToInt64(record, siOff));
                            parsed.WrittenUtc = FileTime(BitConverter.ToInt64(record, siOff + 8));
                            parsed.AccessedUtc = FileTime(BitConverter.ToInt64(record, siOff + 24));
                            if (siLen >= 36 && (BitConverter.ToUInt32(record, siOff + 32) & SiEncrypted) != 0)
                                parsed.IsEfs = true;
                        }
                        break;
                    case AttrAttributeList:
                        parsed.HasAttrList = true;
                        break;
                    case AttrReparsePoint:
                        parsed.IsReparse = true;
                        break;
                    case AttrLoggedUtilityStream:
                        parsed.IsEfs = true;
                        break;
                    case AttrFileName:
                        if (!haveFn)
                        {
                            if (!TryFileName(record, off, len, out _, out _, out _, out _, out var fnOff))
                                return null;
                            haveFn = true;
                            fnCreated = BitConverter.ToInt64(record, fnOff + 8);
                            fnWritten = BitConverter.ToInt64(record, fnOff + 16);
                            fnAccessed = BitConverter.ToInt64(record, fnOff + 32);
                        }
                        break;
                    case AttrData:
                        if (!TryData(record, off, len, parsed))
                            return null;
                        break;
                    default:
                        break; // unknown attribute: skip (forward compatibility)
                }
            }
            if (!haveSi && haveFn)
            {
                parsed.CreatedUtc = FileTime(fnCreated);
                parsed.WrittenUtc = FileTime(fnWritten);
                parsed.AccessedUtc = FileTime(fnAccessed);
            }
            return parsed;
        }

        // NTFS data-run varint decode with VCN tracking: [lenBytes:offBytes]
        // [length][signed LCN delta]... 0x00-terminated. Sparse runs (offBytes
        // 0) keep their VCN slot with Lcn -1 (reads as zeros). Null on garbage.
        internal static List<MftRun>? DecodeRuns(ReadOnlySpan<byte> runs, long startVcn)
        {
            var decoded = new List<MftRun>();
            var lcn = 0L;
            var vcn = startVcn;
            var i = 0;
            while (i < runs.Length)
            {
                var header = runs[i++];
                if (header == 0)
                    break;
                var lenBytes = header & 0x0F;
                var offBytes = (header >> 4) & 0x0F;
                if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || i + lenBytes + offBytes > runs.Length)
                    return null;
                var count = 0L;
                for (var b = 0; b < lenBytes; b++)
                    count |= (long)runs[i + b] << (8 * b);
                i += lenBytes;
                if (count <= 0 || vcn < 0 || count > long.MaxValue - vcn)
                    return null;
                if (offBytes == 0)
                {
                    decoded.Add(new MftRun(vcn, -1, count));
                    vcn += count;
                    continue;
                }
                var delta = 0L;
                for (var b = 0; b < offBytes; b++)
                    delta |= (long)runs[i + b] << (8 * b);
                i += offBytes;
                if (offBytes < 8 && (runs[i - 1] & 0x80) != 0)
                    delta |= -1L << (8 * offBytes);
                lcn += delta;
                if (lcn < 0)
                    return null;
                decoded.Add(new MftRun(vcn, lcn, count));
                vcn += count;
            }
            return decoded;
        }

        private static bool TryData(byte[] record, int off, int len, ParsedFile parsed)
        {
            if (len < 16)
                return false;
            var nameLen = record[off + 9];
            var nameOff = BitConverter.ToUInt16(record, off + 10);
            if (nameOff + nameLen * 2 > len)
                return false;
            if (nameLen > 0)
            {
                // Named $DATA (ADS): skipped by the writer, so only the name
                // is decoded — its runs are never touched.
                parsed.Ads.Add(Encoding.Unicode.GetString(record, off + nameOff, nameLen * 2));
                return true;
            }
            if (parsed.HasData)
                return false; // second unnamed $DATA: corrupt, fail loud rather than pick one silently
            if (record[off + 8] == 0)
            {
                if (!TryResidentValue(record, off, len, out var valueOff, out var valueLen))
                    return false;
                var bytes = new byte[valueLen];
                Buffer.BlockCopy(record, valueOff, bytes, 0, (int)valueLen);
                parsed.ResidentData = bytes;
                parsed.DataSize = valueLen;
                parsed.DataInitSize = valueLen;
                parsed.HasData = true;
                return true;
            }
            if (len < 64)
                return false;
            var startVcn = (long)BitConverter.ToUInt64(record, off + 16);
            if (startVcn != 0)
                return false; // extension-style $DATA in a base record: attr-list territory, never guess
            var runOff = BitConverter.ToUInt16(record, off + 32);
            if (runOff >= len)
                return false;
            var realSize = (long)BitConverter.ToUInt64(record, off + 48);
            var initSize = (long)BitConverter.ToUInt64(record, off + 56);
            if (realSize < 0 || initSize < 0)
                return false;
            var runs = DecodeRuns(record.AsSpan(off + runOff, len - runOff), startVcn);
            if (runs == null)
                return false;
            parsed.DataRuns = runs;
            parsed.DataSize = realSize;
            parsed.DataInitSize = initSize;
            parsed.IsCompressed = BitConverter.ToUInt16(record, off + 34) != 0;
            parsed.HasData = true;
            return true;
        }

        private static bool TryFileName(byte[] record, int off, int len, out long parent, out ushort parentSeq, out string name, out int ns, out int valueOff)
        {
            parent = 0;
            parentSeq = 0;
            name = "";
            ns = 0;
            valueOff = 0;
            if (!TryResidentValue(record, off, len, out var voff, out var vlen) || vlen < 66)
                return false;
            var nameLen = record[voff + 64];
            if (nameLen == 0 || 66 + nameLen * 2 > vlen)
                return false;
            var parentRef = BitConverter.ToUInt64(record, voff);
            parent = (long)(parentRef & 0xFFFF_FFFF_FFFF);
            parentSeq = (ushort)(parentRef >> 48);
            ns = record[voff + 65];
            name = Encoding.Unicode.GetString(record, voff + 66, nameLen * 2);
            valueOff = voff;
            return true;
        }

        private static bool TryResidentValue(byte[] record, int off, int len, out int valueOff, out uint valueLen)
        {
            valueOff = 0;
            valueLen = 0;
            if (len < 22 || record[off + 8] != 0)
                return false;
            valueLen = BitConverter.ToUInt32(record, off + 16);
            valueOff = off + BitConverter.ToUInt16(record, off + 20);
            if (valueOff > off + len || valueLen > (uint)(off + len - valueOff))
                return false;
            return true;
        }

        private static List<(int Offset, int Length)>? AttributeSpans(byte[] record)
        {
            if (record.Length < 48)
                return null;
            var firstAttr = BitConverter.ToUInt16(record, 0x14);
            var bytesInUse = BitConverter.ToUInt32(record, 0x18);
            var end = (int)Math.Min(bytesInUse, (uint)record.Length);
            if (firstAttr < 42 || firstAttr >= end)
                return null;
            var spans = new List<(int Offset, int Length)>();
            var off = (int)firstAttr;
            while (true)
            {
                if (off + 4 > end)
                    return null;
                var type = BitConverter.ToUInt32(record, off);
                if (type == AttrEnd)
                    break;
                if (off + 8 > end)
                    return null;
                var len = BitConverter.ToUInt32(record, off + 4);
                if (len < 8 || len > (uint)(end - off))
                    return null;
                spans.Add((off, (int)len));
                off += (int)len;
            }
            return spans;
        }

        private static DateTime? FileTime(long ft) =>
            ft >= 0 && ft <= MaxFileTime ? DateTime.FromFileTimeUtc(ft) : null;
    }
}
