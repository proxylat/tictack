using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using TicTack;

namespace Corpus
{
    // System imaging CLI (system-image-future.md). Verbs: image a block
    // source, export/verify/list snapshots, validate config.
    static class Program
    {
        static int Main(string[] args)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var cfgPath = Path.Combine(baseDir, "config_corpus.yaml");
            string? verb = null;
            string? volume = null;
            string? name = null;
            string? snapshotArg = null;
            string? outPath = null;
            string? device = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--config":
                        if (i + 1 < args.Length) cfgPath = args[++i];
                        break;
                    case "--help":
                    case "-h":
                    case "-?":
                        PrintHelp();
                        return 0;
                    case "--version":
                    case "-v":
                        PrintVersion();
                        return 0;
                    case "--validate":
                        verb = "validate";
                        break;
                    case "--image":
                        verb = "image";
                        break;
                    case "--export":
                        verb = "export";
                        break;
                    case "--export-vhdx":
                        verb = "export-vhdx";
                        break;
                    case "--verify":
                        verb = "verify";
                        break;
                    case "--list":
                        verb = "list";
                        break;
                    case "--gen-key":
                        verb = "gen-key";
                        break;
                    case "--key-list":
                        verb = "key-list";
                        break;
                    case "--vss-list":
                        verb = "vss-list";
                        break;
                    case "--vss-snap":
                        verb = "vss-snap";
                        break;
                    case "--vss-read":
                        verb = "vss-read";
                        break;
                    case "--vss-dump":
                        verb = "vss-dump";
                        break;
                    case "--bitmap-stats":
                        verb = "bitmap-stats";
                        break;
                    case "--bitmap-dump":
                        verb = "bitmap-dump";
                        break;
                    case "--device":
                        if (i + 1 < args.Length) device = args[++i];
                        break;
                    case "--volume":
                        if (i + 1 < args.Length) volume = args[++i];
                        break;
                    case "--name":
                        if (i + 1 < args.Length) name = args[++i];
                        break;
                    case "--snapshot":
                        if (i + 1 < args.Length) snapshotArg = args[++i];
                        break;
                    case "--out":
                        if (i + 1 < args.Length) outPath = args[++i];
                        break;
                    default:
                        Console.Error.WriteLine("Unknown argument: " + args[i]);
                        PrintHelp();
                        return 1;
                }
            }

            if (verb == null)
            {
                PrintHelp();
                return 1;
            }

            var cfg = CorpusConfig.Load(cfgPath);
            var log = CreateLogger(cfg, baseDir);
            try
            {
                // Probes are config-free diagnostics: they run before the
                // store-config gate so a bare exe can check VSS health.
                switch (verb)
                {
                    case "vss-list":
                        return VssList(baseDir);
                    case "vss-snap":
                    case "vss-read":
                    case "vss-dump":
                    case "bitmap-stats":
                        var probeVol = ProbeVolume(volume, log);
                        if (probeVol == null) return 1;
                        return RunProbe(verb, probeVol, log);
                    case "bitmap-dump":
                        return RunBitmapDump(volume, device, outPath, log);
                    default:
                        break;
                }

                if (cfg == null)
                {
                    if (!File.Exists(cfgPath))
                        log.Error("config not found: " + cfgPath + " (copy config_corpus.yaml.example to config_corpus.yaml beside Corpus.exe, or pass --config <path>)");
                    else
                        log.Error("config invalid (empty?): " + cfgPath);
                    return 1;
                }
                if (!CorpusConfig.Validate(cfg, log))
                    return 1;

                switch (verb)
                {
                    case "validate":
                        log.Info("store: " + cfg.StorePath);
                        log.Info("state root: " + CorpusConfig.StateRoot(cfg));
                        log.Info("chunk: " + cfg.ChunkMib + " MiB, zstd level " + cfg.Compression.Level);
                        log.Info("Corpus config valid");
                        return 0;
                    case "image":
                        if (string.IsNullOrEmpty(volume) || string.IsNullOrEmpty(name))
                        {
                            log.Error("--image requires --volume <path> --name <snapshot>");
                            return 1;
                        }
                        using (var store = CreateStore(cfg, log))
                        using (var source = OpenSource(volume, log))
                        {
                            var m = store.Image(source, name);
                            log.Info($"snapshot {m.Generation:D6}-{m.Name}: {m.Chunks.Count} chunks");
                        }
                        return 0;
                    case "export":
                        if (!TrySnapshot(snapshotArg, log, out var exportGen) || string.IsNullOrEmpty(outPath))
                        {
                            log.Error("--export requires --snapshot <gen> --out <path>");
                            return 1;
                        }
                        using (var store = CreateStore(cfg, log))
                            store.Export(exportGen, outPath);
                        return 0;
                    case "export-vhdx":
                        if (!TrySnapshot(snapshotArg, log, out var vhdxGen) || string.IsNullOrEmpty(outPath))
                        {
                            log.Error("--export-vhdx requires --snapshot <gen> --out <path>");
                            return 1;
                        }
                        using (var store = CreateStore(cfg, log))
                            store.ExportVhdx(vhdxGen, outPath);
                        return 0;
                    case "verify":
                        if (!TrySnapshot(snapshotArg, log, out var verifyGen))
                        {
                            log.Error("--verify requires --snapshot <gen>");
                            return 1;
                        }
                        using (var store = CreateStore(cfg, log))
                            return store.Verify(verifyGen) ? 0 : 1;
                    case "list":
                        using (var store = CreateStore(cfg, log))
                        {
                            foreach (var (gen, snap) in store.ListSnapshots())
                                Console.WriteLine(gen + " " + snap);
                        }
                        return 0;
                    case "gen-key":
                        {
                            var (keyId, _) = KeyStore.Generate(cfg.StorePath);
                            Console.WriteLine($"encryption key {keyId} active (existing snapshots unchanged)");
                        }
                        return 0;
                    case "key-list":
                        {
                            var keys = new KeyStore(cfg.StorePath);
                            var all = new List<(string KeyId, byte[] Key)>(keys.AllKeys);
                            if (all.Count == 0)
                            {
                                Console.WriteLine("no encryption keys (store is unencrypted)");
                                return 0;
                            }
                            all.Sort((a, b) => string.Compare(a.KeyId, b.KeyId, StringComparison.Ordinal));
                            foreach (var (id, _) in all)
                                Console.WriteLine(id + (id == keys.CurrentKeyId ? " [current]" : ""));
                        }
                        return 0;
                    default:
                        return 1;
                }
            }
            catch (Exception ex)
            {
                log.Error("corpus " + verb + " failed", ex);
                return 1;
            }
            finally
            {
                (log as IDisposable)?.Dispose();
            }
        }

        private static ILogger CreateLogger(CorpusConfig? cfg, string baseDir)
        {
            var logPath = cfg != null && cfg.Logging != null && !string.IsNullOrEmpty(cfg.Logging.Path)
                ? cfg.Logging.Path
                : Path.Combine(baseDir, "corpus.log");
            var backupDir = cfg != null ? CorpusConfig.StateRoot(cfg) : baseDir;
            // Shared factory (primitive params, no TicTack config model):
            // size-rotated primary + time-bounded .bak + optional console.
            return LoggerFactory.Create(
                cfg != null && cfg.Logging != null ? cfg.Logging.Level : null,
                logPath, 10, 5, null,
                console: cfg == null || cfg.Logging == null || cfg.Logging.Console,
                eventLog: false,
                backupPath: Path.Combine(backupDir, "corpus.log.bak"));
        }

        private static Store CreateStore(CorpusConfig cfg, ILogger log)
        {
            return new Store(
                cfg.StorePath,
                Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"),
                cfg.ChunkMib, cfg.Compression.Level, log,
                new KeyStore(cfg.StorePath));
        }

        // Drive-letter-colon ("T:") images the live volume through VSS;
        // anything else is a file path for FileBlockSource.
        private static IBlockSource OpenSource(string volume, ILogger log) =>
            volume.Length == 2 && volume[1] == ':' && char.IsAsciiLetter(volume[0])
                ? new VssBlockSource(volume, log)
                : new FileBlockSource(volume);

        private static string? ProbeVolume(string? volume, ILogger log)
        {
            if (string.IsNullOrEmpty(volume))
            {
                log.Error("needs --volume <drive, e.g. T:>");
                return null;
            }
            return volume;
        }

        private static int VssList(string baseDir)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("--vss-list needs Windows");
                return 1;
            }
            var r = ProcessRunner.RunAsync("vssadmin list writers", baseDir, true).GetAwaiter().GetResult();
            Console.WriteLine(r.Stdout);
            if (r.TimedOut) Console.Error.WriteLine("vssadmin timed out");
            if (!string.IsNullOrEmpty(r.Stderr)) Console.Error.WriteLine(r.Stderr);
            return r.TimedOut ? 1 : r.ExitCode;
        }

        private static int RunProbe(string verb, string volume, ILogger log)
        {
            // Probes never Complete: AbortBackup is the honest teardown for
            // reads that capture nothing (see VssSnapshot).
            switch (verb)
            {
                case "vss-snap":
                    using (var s = VssSnapshot.Create(volume, log))
                        log.Info("device: " + s.DeviceObject);
                    return 0;
                case "vss-read":
                    using (var s = VssSnapshot.Create(volume, log))
                    {
                        var dev = s.DeviceObject.TrimEnd('\\');
                        using var raw = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        // True device length (not MaxValue): the segment
                        // reader sizes fills from it, and an overlong Length
                        // would overread past EOF into a short-read throw.
                        // Callback unobserved (probes never Complete).
                        using var fs = new VssReadStream(raw, VssBlockSource.DeviceLength(volume, dev), () => { });
                        var buf = new byte[64 * 1024];
                        var filled = 0;
                        int n;
                        while (filled < buf.Length && (n = fs.Read(buf, filled, buf.Length - filled)) > 0)
                            filled += n;
                        using var sha = SHA256.Create();
                        log.Info($"read {filled} bytes: sha256={Convert.ToHexString(sha.ComputeHash(buf, 0, filled))} head={Convert.ToHexString(buf, 0, Math.Min(8, filled))}");
                    }
                    return 0;
                case "vss-dump":
                    // Transport-vs-isolation probe: ONE snapshot, read TWICE
                    // end to end (same chunker shape as imaging). Differing
                    // hashes = unreliable snapshot reads; matching hashes
                    // that differ from live = VSS isolation failure.
                    using (var s = VssSnapshot.Create(volume, log))
                    {
                        var dev = s.DeviceObject.TrimEnd('\\');
                        var vlen = VssBlockSource.DeviceLength(volume, dev);
                        string h1, h2;
                        using (var raw = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var fs = new VssReadStream(raw, vlen, () => { }))
                            h1 = HashStream(fs);
                        using (var raw = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var fs = new VssReadStream(raw, vlen, () => { }))
                            h2 = HashStream(fs);
                        log.Info($"vss-dump {volume} pass1={h1} pass2={h2} stable={h1 == h2}");
                    }
                    return 0;
                case "bitmap-stats":
                    using (var s = VssSnapshot.Create(volume, log))
                    {
                        var bpc = VolumeBitmap.BytesPerCluster(volume + "\\");
                        var total = VolumeBitmap.TotalBytes(volume + "\\");
                        var runs = VolumeBitmap.ReadRuns(s.DeviceObject.TrimEnd('\\'), total / bpc);
                        long used = 0;
                        foreach (var r in runs) used += r.Count;
                        log.Info($"{volume} {used}/{total / bpc} clusters used ({used * bpc}/{total} bytes) in {runs.Count} runs");
                        for (var i = 0; i < Math.Min(5, runs.Count); i++)
                            log.Info($"  run {i}: LCN {runs[i].Start} +{runs[i].Count}");
                    }
                    return 0;
                default:
                    return 1;
            }
        }

        // Runs dump for harness-side masked compares. --volume gives geometry
        // (identical on live root and its shadows); --device reads an existing
        // frozen device instead of snapshotting (zero skew vs a hashed shadow).
        private static int RunBitmapDump(string? volume, string? device, string? outPath, ILogger log)
        {
            if (string.IsNullOrEmpty(outPath))
            {
                log.Error("--bitmap-dump requires --out <path>");
                return 1;
            }
            var vol = ProbeVolume(volume, log);
            if (vol == null) return 1;
            var bpc = VolumeBitmap.BytesPerCluster(vol + "\\");
            var total = VolumeBitmap.TotalBytes(vol + "\\");
            List<(long Start, long Count)> runs;
            if (!string.IsNullOrEmpty(device))
            {
                runs = VolumeBitmap.ReadRuns(device, total / bpc);
            }
            else
            {
                using var s = VssSnapshot.Create(vol, log);
                runs = VolumeBitmap.ReadRuns(s.DeviceObject.TrimEnd('\\'), total / bpc);
            }
            VolumeBitmap.WriteRuns(outPath, bpc, total / bpc, runs);
            log.Info($"bitmap-dump {vol}: {runs.Count} runs -> {outPath}");
            return 0;
        }

        private static string HashStream(Stream s)
        {
            using var sha = SHA256.Create();
            var buf = new byte[1024 * 1024];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                sha.TransformBlock(buf, 0, n, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }

        private static bool TrySnapshot(string? arg, ILogger log, out int generation)
        {
            generation = 0;
            if (string.IsNullOrEmpty(arg) || !int.TryParse(arg, out generation) || generation <= 0)
            {
                log.Error("invalid --snapshot (want a positive generation number, got: " + arg + ")");
                return false;
            }
            return true;
        }

        static void PrintVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Console.WriteLine("Corpus " + (info ?? asm.GetName().Version?.ToString() ?? "unknown"));
        }

        static void PrintHelp()
        {
            Console.WriteLine("Corpus — system imaging");
            Console.WriteLine();
            Console.WriteLine("  --image --volume <path|X:> --name <snap>   Image a file, or live volume X: via VSS");
            Console.WriteLine("  --export --snapshot <gen> --out <path>  Export a snapshot to a file");
            Console.WriteLine("  --export-vhdx --snapshot <gen> --out <path>  Export a snapshot to dynamic VHDX");
            Console.WriteLine("  --vss-list                   Relay 'vssadmin list writers' (Windows)");
            Console.WriteLine("  --vss-snap --volume <X:>     Create+delete a VSS snapshot probe (Windows, elevated)");
            Console.WriteLine("  --vss-read --volume <X:>     Read a 64 KiB extent via snapshot probe (Windows, elevated)");
            Console.WriteLine("  --vss-dump --volume <X:>     Hash one snapshot twice (read-stability probe)");
            Console.WriteLine("  --bitmap-stats --volume <X:> Used-cluster stats over a snapshot (Windows, elevated)");
            Console.WriteLine("  --bitmap-dump --volume <X:> [--device <dev>] --out <file>  Dump allocated runs (Windows, elevated)");
            Console.WriteLine("  --verify --snapshot <gen>              Verify a snapshot (exit 1 on failure)");
            Console.WriteLine("  --list                                 List snapshots (\"gen name\" lines)");
            Console.WriteLine("  --gen-key                              Generate an encryption key and activate it (existing snapshots stay as-is; only new images encrypt)");
            Console.WriteLine("  --key-list                             List encryption keys (\"<id> [current]\" lines)");
            Console.WriteLine("  --validate             Check config_corpus.yaml and exit");
            Console.WriteLine("  --config <path>        Config file (default: config_corpus.yaml beside the exe)");
            Console.WriteLine("  --version              Print version and exit");
            Console.WriteLine("  --help                 This text");
            Console.WriteLine();
            Console.WriteLine("  encryption covers new images only; existing snapshots stay as-is");
            Console.WriteLine("  keys are raw 32-byte files at keys/<keyid>.key, active key named by keys/current");
        }
    }
}
