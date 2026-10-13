using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using TicTack;

namespace Corpus
{
    public sealed record RestoreOptions(
        int Generation,
        string Target,
        string? StorePath = null,
        string? IndexPath = null,
        string? ConfirmToken = null,
        bool AllowBootDisk = false,
        bool SkipVerify = false,
        bool DryRun = false);

    // PE restore orchestration: preflight -> safety gate -> stream -> full
    // verify. Returns 0/1 matching the Verify exit posture. Never mints
    // keys: the id.key check runs before the injected Store is touched, and
    // the CLI checks again before constructing one (no Store ctor change).
    public sealed class RestoreOrchestrator
    {
        private readonly Store _store;
        private readonly ILogger _log;
        private readonly Func<string, IBlockTarget> _targets;

        public RestoreOrchestrator(Store store, ILogger log, Func<string, IBlockTarget> targetFactory)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(log);
            ArgumentNullException.ThrowIfNull(targetFactory);
            _store = store;
            _log = log;
            _targets = targetFactory;
        }

        public static bool HasIdentityKey(string storeRoot) =>
            !string.IsNullOrEmpty(storeRoot) && File.Exists(Path.Combine(storeRoot, "id.key"));

        // Default factory: disk number | \\.\PhysicalDriveN | file path.
        public static IBlockTarget OpenTarget(string spec)
        {
            ArgumentNullException.ThrowIfNull(spec);
            if (int.TryParse(spec, out var number))
                return new RawDiskTarget($@"\\.\PhysicalDrive{number}");
            if (spec.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
                return new RawDiskTarget(spec);
            return new FileBlockTarget(spec);
        }

        public int Restore(RestoreOptions opts, IProgress<long>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(opts);
            // 1. No-key-mint pre-check, before the store is even touched.
            if (!string.IsNullOrEmpty(opts.StorePath) && !HasIdentityKey(opts.StorePath))
            {
                _log.Error($"refusing restore: no id.key in {opts.StorePath} (restore never creates one — check --store)");
                return 1;
            }
            SnapshotManifest manifest;
            try
            {
                manifest = _store.GetManifest(opts.Generation);
            }
            catch (Exception ex)
            {
                _log.Error($"cannot load snapshot {opts.Generation}", ex);
                return 1;
            }
            using var target = _targets(opts.Target);
            // 2. Preflight before any write: capacity, then locked BitLocker.
            if (target.Capacity < manifest.VolumeLength)
            {
                _log.Error($"refusing restore: target {target.DisplayName} holds {target.Capacity} bytes, snapshot {manifest.Generation} needs {manifest.VolumeLength} (no bytes written)");
                return 1;
            }
            if (RefuseLockedBitLocker(target))
                return 1;
            // 3. Safety gate: the token is REQUIRED always (even --dry-run);
            // --allow-boot-disk still needs it.
            var expected = BootDiskGuard.TargetToken(target.DisplayName, target.Capacity);
            var supplied = opts.ConfirmToken;
            if (string.IsNullOrEmpty(supplied) && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                Console.Write($"Type '{expected}' to confirm restore to {target.DisplayName}: ");
                supplied = Console.ReadLine()?.Trim();
            }
            _log.Info($"restore confirm token: '{supplied}'");
            if (!BootDiskGuard.Confirm(supplied, expected))
            {
                _log.Error($"refusing restore: --confirm-type must equal '{expected}'");
                return 1;
            }
            if (!opts.AllowBootDisk && BootDiskGuard.IsBootDisk(target, out var boot))
            {
                _log.Error($"refusing restore: {target.DisplayName} is the boot disk ({boot}) — re-run with --allow-boot-disk and --confirm-type '{expected}'");
                return 1;
            }
            _log.Info($"restore snapshot {manifest.Generation} ({manifest.Chunks.Count} chunks, {manifest.VolumeLength} bytes) -> {target.DisplayName} [{expected}]");
            if (opts.DryRun)
            {
                _log.Info("dry run: plan only, no bytes written");
                return 0;
            }
            // 4. Stream chunk-by-chunk; the log gets % + MB/s at 2 Hz while
            // every cumulative report is forwarded to progress untouched.
            // A mid-stream failure (bad pack, dead device) returns 1 here —
            // it never reaches verify, so the message says so.
            var sw = Stopwatch.StartNew();
            try
            {
                using (var stream = target.OpenWrite())
                    _store.ExportToStream(opts.Generation, stream, new ThrottledProgress(_log, manifest.VolumeLength, sw, progress));
                if (target is FileBlockTarget file)
                    FixFileLength(file.Path, manifest.VolumeLength);
            }
            catch (Exception ex)
            {
                _log.Error("restore streaming failed before any offsets could verify", ex);
                return 1;
            }
            // 5. Verify-after-write: FULL re-read, per manifest entry in
            // order — target bytes hashed with ChunkId vs the digest
            // (holes: expect zeros). A corrupt pack decodes to wrong bytes
            // that stream fine; this is where that surfaces.
            if (opts.SkipVerify)
            {
                _log.Warn("skipping post-restore verify (--skip-verify)");
            }
            else
            {
                List<long> bad;
                try
                {
                    bad = VerifyTarget(target, manifest, opts.StorePath);
                }
                catch (Exception ex)
                {
                    _log.Error("restore verify failed to re-read the target", ex);
                    return 1;
                }
                if (bad.Count > 0)
                {
                    for (var i = 0; i < Math.Min(5, bad.Count); i++)
                        _log.Error($"restore verify mismatch at offset {bad[i]}");
                    _log.Error($"restore verify failed: {bad.Count} corrupt ranges (re-run restore; do not boot this target)");
                    return 1;
                }
            }
            sw.Stop();
            _log.Info($"restored snapshot {manifest.Generation}: {manifest.VolumeLength} bytes written and verified in {sw.Elapsed.TotalSeconds:F1}s");
            return 0;
        }

        // Best-effort locked-BitLocker refuse (Windows file targets only):
        // a locked volume fails writes anyway — refuse with a clear line
        // instead. Absent manage-bde (or any error) means proceed.
        private bool RefuseLockedBitLocker(IBlockTarget target)
        {
            if (!OperatingSystem.IsWindows() || target is not FileBlockTarget file)
                return false;
            try
            {
                var root = Path.GetPathRoot(System.IO.Path.GetFullPath(file.Path));
                if (string.IsNullOrEmpty(root) || root.Length < 2 || !char.IsAsciiLetter(root[0]))
                    return false;
                var r = ProcessRunner.RunAsync($"manage-bde -status {root}", AppDomain.CurrentDomain.BaseDirectory, redirect: true, timeoutMs: 15000).GetAwaiter().GetResult();
                if (r.TimedOut || r.ExitCode != 0)
                    return false;
                if (r.Stdout.Contains("Locked", StringComparison.OrdinalIgnoreCase)
                    && !r.Stdout.Contains("Unlocked", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Error($"refusing restore: volume {root} is BitLocker-locked (unlock with manage-bde -unlock {root} first)");
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void FixFileLength(string path, long volumeLength)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            if (fs.Length != volumeLength)
                fs.SetLength(volumeLength); // extend zero-fills; truncate drops pre-existing tail
        }

        // Verify-after-write: FULL re-read, per manifest entry in order —
        // target bytes hashed with ChunkId vs the manifest digest (holes:
        // expect zeros). Keys come from the store dir (id.key + KeyStore);
        // the injected Store is not involved, so this works for any target.
        private static List<long> VerifyTarget(IBlockTarget target, SnapshotManifest manifest, string? storeRoot)
        {
            if (string.IsNullOrEmpty(storeRoot) || !File.Exists(Path.Combine(storeRoot, "id.key")))
                throw new InvalidDataException("post-restore verify needs the store id.key");
            var idKey = File.ReadAllBytes(Path.Combine(storeRoot, "id.key"));
            KeyStore? keys = null;
            try
            {
                keys = new KeyStore(storeRoot);
            }
            catch (Exception)
            {
                keys = null; // unreadable key files: unencrypted still verifies, encrypted fails closed below
            }
            var bad = new List<long>();
            using var read = OpenTargetRead(target);
            var buf = new byte[4 * 1024 * 1024];
            var position = 0L;
            foreach (var entry in manifest.Chunks)
            {
                if (entry.IsHole)
                {
                    if (!ReadZeros(read, position, entry.Size, buf))
                        bad.Add(position);
                }
                else if (!VerifyRange(read, position, entry, manifest.EncKeyId, idKey, keys, buf))
                {
                    bad.Add(position);
                }
                position += entry.Size;
            }
            return bad;
        }

        private static bool VerifyRange(Stream read, long position, ManifestEntry entry, string? encKeyId, byte[] idKey, KeyStore? keys, byte[] buf)
        {
            var digest = Convert.FromHexString(entry.DigestHex);
            if (string.IsNullOrEmpty(encKeyId))
                return CryptographicOperations.FixedTimeEquals(HashRange(read, position, entry.Size, idKey, null, buf), digest);
            // Encrypted: manifest key first, then retained keys — a
            // post-rotation incremental mixes scopes (same as Store.Verify).
            if (keys != null)
            {
                if (keys.TryGet(encKeyId, out var primary)
                    && CryptographicOperations.FixedTimeEquals(HashRange(read, position, entry.Size, idKey, primary, buf), digest))
                    return true;
                foreach (var (id, key) in keys.AllKeys)
                {
                    if (id == encKeyId)
                        continue;
                    if (CryptographicOperations.FixedTimeEquals(HashRange(read, position, entry.Size, idKey, key, buf), digest))
                        return true;
                }
            }
            return false;
        }

        // ChunkId.ComputeId incrementally (O(1) memory): SHA-256 over
        // plaintext, then idKey, then encKey — the same append order.
        private static byte[] HashRange(Stream read, long position, long size, byte[] idKey, byte[]? encKey, byte[] buf)
        {
            read.Seek(position, SeekOrigin.Begin);
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var left = size;
            while (left > 0)
            {
                var n = read.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0)
                    throw new IOException($"short target read at offset {position + (size - left)}: target smaller than the snapshot");
                h.AppendData(buf, 0, n);
                left -= n;
            }
            h.AppendData(idKey);
            if (encKey != null)
                h.AppendData(encKey);
            return h.GetHashAndReset();
        }

        private static bool ReadZeros(Stream read, long position, long size, byte[] buf)
        {
            read.Seek(position, SeekOrigin.Begin);
            var left = size;
            while (left > 0)
            {
                var n = read.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0)
                    break; // missing tail reads as zero
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] != 0)
                        return false;
                }
                left -= n;
            }
            return true;
        }

        private static Stream OpenTargetRead(IBlockTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (target is FileBlockTarget file)
                return new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (target is RawDiskTarget raw)
                return raw.OpenRead();
            throw new InvalidOperationException($"cannot re-read target {target.DisplayName} for verify (unknown target kind)");
        }

        // Synchronous fan-out: forward every cumulative report, log % + MB/s
        // at most every 500 ms (plus the final report). Sync delivery keeps
        // progress monotonic — Progress<T> would hop threads and reorder.
        private sealed class ThrottledProgress : IProgress<long>
        {
            private readonly ILogger _log;
            private readonly long _total;
            private readonly Stopwatch _sw;
            private readonly IProgress<long>? _fwd;
            private long _lastMs;

            public ThrottledProgress(ILogger log, long total, Stopwatch sw, IProgress<long>? fwd)
            {
                _log = log;
                _total = total;
                _sw = sw;
                _fwd = fwd;
                _lastMs = -1000;
            }

            public void Report(long bytes)
            {
                _fwd?.Report(bytes);
                var ms = _sw.ElapsedMilliseconds;
                if (ms - _lastMs < 500 && bytes < _total)
                    return;
                _lastMs = ms;
                var pct = _total <= 0 ? 100 : bytes * 100 / _total;
                var mbps = _sw.Elapsed.TotalSeconds > 0 ? bytes / _sw.Elapsed.TotalSeconds / (1024 * 1024) : 0;
                _log.Info($"restore {pct}% ({mbps:F1} MB/s)");
            }
        }
    }
}
