using System;
using System.IO;
using System.Reflection;
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
                    case "--verify":
                        verb = "verify";
                        break;
                    case "--list":
                        verb = "list";
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
                if (cfg == null)
                {
                    log.Error("config not found or invalid: " + cfgPath);
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
                        using (var source = new FileBlockSource(volume))
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
                cfg.ChunkMib, cfg.Compression.Level, log);
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
            Console.WriteLine("  --image --volume <path> --name <snap>   Image a block source as a snapshot");
            Console.WriteLine("  --export --snapshot <gen> --out <path>  Export a snapshot to a file");
            Console.WriteLine("  --verify --snapshot <gen>              Verify a snapshot (exit 1 on failure)");
            Console.WriteLine("  --list                                 List snapshots (\"gen name\" lines)");
            Console.WriteLine("  --validate             Check config_corpus.yaml and exit");
            Console.WriteLine("  --config <path>        Config file (default: config_corpus.yaml beside the exe)");
            Console.WriteLine("  --version              Print version and exit");
            Console.WriteLine("  --help                 This text");
        }
    }
}
