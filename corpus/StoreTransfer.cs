using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using TicTack;

namespace Corpus
{
    // Git-like delta transfer: copy snapshots between two stores, moving only
    // missing packs. Manifests + packs copy verbatim (content-addressed —
    // digest strings need no key threading), so neither side changes format.
    // Digests + manifest HMACs are key-bound under per-store id.key: a
    // missing dest key is seeded from the source, a different one fails loud
    // (never silently mix identities). Crash order is packs → index → (re-run
    // heals: existing pack files are skipped, missing ones re-copied).
    public sealed record TransferOptions(int Snapshot = 0, bool IncludeChain = false);

    public sealed record TransferResult(int ManifestsCopied, int PacksCopied, long PackBytesCopied, int LandingCopied, int LayoutsCopied);

    public static class StoreTransfer
    {
        private const int IndexBatch = 500;

        // Store-local index (recovery-media layout) wins when present, else
        // the shared state-root db the CLI images through (restore's rule).
        public static string ResolveIndexPath(string storeRoot, string defaultDbPath)
        {
            ArgumentException.ThrowIfNullOrEmpty(storeRoot);
            ArgumentException.ThrowIfNullOrEmpty(defaultDbPath);
            var local = Path.Combine(storeRoot, "corpus-chunks.db");
            return File.Exists(local) ? local : defaultDbPath;
        }

        public static TransferResult Copy(string fromRoot, string fromDb, string toRoot, string toDb, TransferOptions? opts, ILogger log)
        {
            ArgumentException.ThrowIfNullOrEmpty(fromRoot);
            ArgumentException.ThrowIfNullOrEmpty(fromDb);
            ArgumentException.ThrowIfNullOrEmpty(toRoot);
            ArgumentException.ThrowIfNullOrEmpty(toDb);
            ArgumentNullException.ThrowIfNull(log);
            opts ??= new TransferOptions();
            if (opts.Snapshot < 0)
                throw new ArgumentOutOfRangeException(nameof(opts));
            if (string.Equals(Path.GetFullPath(fromRoot), Path.GetFullPath(toRoot), StringComparison.Ordinal))
                throw new InvalidOperationException("transfer needs two different stores (--from and --to point at the same dir)");

            var fromManifests = Path.Combine(fromRoot, "manifests");
            var listed = ManifestStore.List(fromManifests);
            if (listed.Count == 0)
                throw new InvalidDataException($"no snapshots in {fromManifests} (nothing to transfer)");
            if (!File.Exists(fromDb))
                throw new InvalidDataException($"source index not found: {fromDb}");
            var key = SyncIdentityKey(fromRoot, toRoot, log);

            var wanted = SelectGenerations(fromManifests, listed, opts, key);
            var skip = SkipPresent(fromManifests, toRoot, wanted, key, log);
            var toCopy = new List<int>();
            foreach (var gen in wanted)
            {
                if (!skip.Contains(gen))
                    toCopy.Add(gen);
            }
            if (toCopy.Count == 0)
            {
                log.Info("dest already has every selected snapshot");
                return new TransferResult(0, 0, 0, 0, 0);
            }

            var manifests = new Dictionary<int, SnapshotManifest>();
            var hexes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gen in toCopy)
            {
                var m = ManifestStore.Load(ManifestPath(fromManifests, listed, gen), key);
                manifests[gen] = m;
                foreach (var entry in m.Chunks)
                {
                    if (!entry.IsHole && entry.DigestHex != "")
                        hexes.Add(entry.DigestHex);
                }
            }

            var fromPacks = Path.Combine(fromRoot, "packs");
            var toPacks = Path.Combine(toRoot, "packs");
            var fromLanding = Path.Combine(fromRoot, "landing");
            var toLanding = Path.Combine(toRoot, "landing");
            Directory.CreateDirectory(toPacks);
            var neededPacks = new SortedSet<string>(StringComparer.Ordinal);
            var pending = new List<ChunkRow>();
            var landing = new List<string>();
            int packsCopied;
            long packBytes;
            using (var srcIdx = new ChunkIndex(fromDb))
            using (var dstIdx = new ChunkIndex(toDb))
            {
                foreach (var hex in hexes)
                {
                    var digest = Convert.FromHexString(hex);
                    if (dstIdx.TryGet(digest, out var dstRow) && File.Exists(Path.Combine(toPacks, dstRow.Pack)))
                        continue;
                    if (srcIdx.TryGet(digest, out var srcRow))
                    {
                        neededPacks.Add(srcRow.Pack);
                        pending.Add(srcRow);
                        if (pending.Count >= IndexBatch)
                        {
                            dstIdx.UpsertBatch(pending);
                            pending.Clear();
                        }
                        continue;
                    }
                    if (File.Exists(Path.Combine(fromLanding, hex + ".chunk")))
                    {
                        landing.Add(hex);
                        continue;
                    }
                    throw new InvalidDataException($"chunk {hex} missing from source index {fromDb} (optimize the source store first?)");
                }
                (packsCopied, packBytes) = CopyPacks(fromPacks, toPacks, neededPacks, log);
                CopyLanding(fromLanding, toLanding, landing);
                dstIdx.UpsertBatch(pending);
                pending.Clear();
            }

            var toManifests = Path.Combine(toRoot, "manifests");
            Directory.CreateDirectory(toManifests);
            foreach (var gen in toCopy)
                File.Copy(ManifestPath(fromManifests, listed, gen), Path.Combine(toManifests, Path.GetFileName(ManifestPath(fromManifests, listed, gen))));
            var layoutsCopied = CopyLayouts(Path.Combine(fromRoot, "layouts"), Path.Combine(toRoot, "layouts"));

            log.Info($"transfer: {toCopy.Count} manifests, {packsCopied} packs ({packBytes} bytes), {landing.Count} landing, {layoutsCopied} layouts");
            return new TransferResult(toCopy.Count, packsCopied, packBytes, landing.Count, layoutsCopied);
        }

        // Selection: one snapshot, optionally with its ParentGeneration chain
        // (oldest first); empty selection = every source snapshot.
        private static List<int> SelectGenerations(string fromManifests, List<(int Generation, string Name)> listed, TransferOptions opts, byte[] key)
        {
            var byGen = new Dictionary<int, string>();
            foreach (var (gen, snap) in listed)
                byGen[gen] = $"{gen:D6}-{snap}.json";
            if (opts.Snapshot <= 0)
            {
                var all = new List<int>(byGen.Keys);
                all.Sort();
                return all;
            }
            if (!byGen.ContainsKey(opts.Snapshot))
                throw new InvalidDataException($"snapshot gen {opts.Snapshot} not found in {fromManifests}");
            if (!opts.IncludeChain)
                return new List<int> { opts.Snapshot };
            var chain = new List<int>();
            var cur = opts.Snapshot;
            while (true)
            {
                if (!byGen.TryGetValue(cur, out var file))
                    throw new InvalidDataException($"snapshot gen {cur} (ancestor of gen {opts.Snapshot}) missing in {fromManifests}");
                var m = ManifestStore.Load(Path.Combine(fromManifests, file), key);
                chain.Add(cur);
                if (m.ParentGeneration <= 0)
                    break;
                cur = m.ParentGeneration;
            }
            chain.Reverse();
            return chain;
        }

        // Dest gens with equal HMAC are already there (skip); same gen with a
        // different HMAC is a collision — never overwrite.
        private static HashSet<int> SkipPresent(string fromManifests, string toRoot, List<int> wanted, byte[] key, ILogger log)
        {
            var skip = new HashSet<int>();
            var toManifests = Path.Combine(toRoot, "manifests");
            if (!Directory.Exists(toManifests))
                return skip;
            var destHmac = new Dictionary<int, string>();
            foreach (var (gen, snap) in ManifestStore.List(toManifests))
                destHmac[gen] = ManifestStore.Load(Path.Combine(toManifests, $"{gen:D6}-{snap}.json"), key).Hmac;
            var names = new Dictionary<int, string>();
            foreach (var (gen, snap) in ManifestStore.List(fromManifests))
                names[gen] = snap;
            foreach (var gen in wanted)
            {
                if (!destHmac.TryGetValue(gen, out var hmac))
                    continue;
                var src = ManifestStore.Load(Path.Combine(fromManifests, $"{gen:D6}-{names[gen]}.json"), key);
                if (!string.Equals(src.Hmac, hmac, StringComparison.Ordinal))
                    throw new InvalidDataException($"gen {gen} exists in both stores with different content (refusing to overwrite)");
                log.Info($"gen {gen} already in dest, skipping");
                skip.Add(gen);
            }
            return skip;
        }

        private static string ManifestPath(string manifestsDir, List<(int Generation, string Name)> listed, int gen)
        {
            foreach (var (g, snap) in listed)
            {
                if (g == gen)
                    return Path.Combine(manifestsDir, $"{g:D6}-{snap}.json");
            }
            throw new InvalidDataException($"snapshot gen {gen} not found in {manifestsDir}");
        }

        private static byte[] SyncIdentityKey(string fromRoot, string toRoot, ILogger log)
        {
            var src = Path.Combine(fromRoot, "id.key");
            if (!File.Exists(src))
                throw new InvalidDataException($"source store has no id.key: {src}");
            var key = File.ReadAllBytes(src);
            var dst = Path.Combine(toRoot, "id.key");
            if (!File.Exists(dst))
            {
                Directory.CreateDirectory(toRoot);
                File.WriteAllBytes(dst, key);
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(dst, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                log.Info("seeded dest identity key from source (same key = same content addresses)");
                return key;
            }
            if (!CryptographicOperations.FixedTimeEquals(key, File.ReadAllBytes(dst)))
                throw new InvalidDataException($"stores have different id.key files ({src} vs {dst} — refusing to mix identities)");
            return key;
        }

        private static (int Count, long Bytes) CopyPacks(string fromPacks, string toPacks, SortedSet<string> needed, ILogger log)
        {
            var count = 0;
            long bytes = 0;
            foreach (var pack in needed)
            {
                var src = Path.Combine(fromPacks, pack);
                if (!File.Exists(src))
                    throw new InvalidDataException($"source pack missing: {src} (source store corrupt?)");
                var dst = Path.Combine(toPacks, pack);
                if (File.Exists(dst))
                {
                    if (new FileInfo(src).Length != new FileInfo(dst).Length)
                        throw new InvalidDataException($"pack {pack} exists in dest with a different size (refusing to overwrite)");
                    continue;
                }
                var tmp = dst + "." + Guid.NewGuid().ToString("n") + ".tmp";
                File.Copy(src, tmp);
                File.Move(tmp, dst);
                count++;
                bytes += new FileInfo(dst).Length;
                log.Info($"copied pack {pack}");
            }
            return (count, bytes);
        }

        private static void CopyLanding(string fromLanding, string toLanding, List<string> hexes)
        {
            if (hexes.Count == 0)
                return;
            Directory.CreateDirectory(toLanding);
            foreach (var hex in hexes)
            {
                var dst = Path.Combine(toLanding, hex + ".chunk");
                if (File.Exists(dst))
                    continue;
                var tmp = dst + "." + Guid.NewGuid().ToString("n") + ".tmp";
                File.Copy(Path.Combine(fromLanding, hex + ".chunk"), tmp);
                File.Move(tmp, dst);
            }
        }

        // Layout sidecars are key-sealed like manifests (same key here), so
        // missing ones copy verbatim; disk-image transfer stays restorable.
        private static int CopyLayouts(string fromLayouts, string toLayouts)
        {
            if (!Directory.Exists(fromLayouts))
                return 0;
            Directory.CreateDirectory(toLayouts);
            var files = new List<string>(Directory.EnumerateFiles(fromLayouts, "*.json"));
            files.Sort(StringComparer.Ordinal);
            var count = 0;
            foreach (var src in files)
            {
                var dst = Path.Combine(toLayouts, Path.GetFileName(src));
                if (File.Exists(dst))
                    continue;
                File.Copy(src, dst);
                count++;
            }
            return count;
        }
    }
}
