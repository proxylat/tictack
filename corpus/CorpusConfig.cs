using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using TicTack;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Corpus
{
    public class CorpusCompressionConfig
    {
        public int Level { get; set; }

        public CorpusCompressionConfig()
        {
            Level = 3;
        }
    }

    public class CorpusLoggingConfig
    {
        public string? Level { get; set; }
        public string? Path { get; set; }
        public bool Console { get; set; }

        public bool Backup { get; set; } = true;

        // Live console progress: auto = restic-style line when stdout is a
        // real console, classic % lines when redirected; on = force live;
        // off = classic % lines everywhere (no MFT filename map, zero cost).
        public string Progress { get; set; } = "auto";
        public bool ProgressName { get; set; } = true;
        public bool ProgressPath { get; set; }

        public CorpusLoggingConfig()
        {
            Level = "info";
            Console = true;
        }
    }

    public class CorpusRetentionConfig
    {
        public int KeepLast { get; set; }
        public int KeepWithinDays { get; set; }
        public List<int> Pin { get; set; } = new();

        public CorpusRetentionConfig()
        {
            KeepLast = 4;
            KeepWithinDays = 0;
        }
    }

    // Item 4 exclusion names; null = CapturePolicy defaults. Explicit yaml
    // replaces the whole list, never merges.
    public class CorpusConfig
    {
        // Chunk packs + manifests live here. Required.
        public string StorePath { get; set; }
        // Chunk-index .db root. Empty = platform default (see StateRoot).
        public string StateDbPath { get; set; }
        public int ChunkMib { get; set; }
        public CorpusCompressionConfig Compression { get; set; }
        public CorpusLoggingConfig Logging { get; set; }
        // GC roots: union of keep-last-N, keep-within-days, pinned gens.
        public CorpusRetentionConfig Retention { get; set; }
        // Item 4 exclusion names; null = CapturePolicy defaults.
        public List<string>? Exclude { get; set; }
        // Per-pack encryption hook: shell command with {input}/{output}
        // substitution (same shape as TicTack jobs). Empty = stored
        // unencrypted. Runs per pack after sealing, never per chunk; a hook
        // failure fails the job loud. Corpus ships zero builtin crypto.
        public string Encrypt { get; set; }
        // Service cadence (--service only; manual --gc ignores these):
        // daily GC wall-clock time and landing-optimize interval.
        public string GcDailyAt { get; set; }
        public string OptimizeEvery { get; set; }
        // Chunker: "fixed" (ChunkMib blocks) or "buzhash" (content-defined,
        // avg ~ChunkMib). Default fixed: old snapshots read unchanged.
        public string Chunker { get; set; }
        // Reed-Solomon parity per pack, percent of pack bytes. 0 = off.
        public int EccOverheadPercent { get; set; }
        // Background deep-verify: percent of packs re-hashed per run.
        // 0 = off (verify-on-write only).
        public int DeepVerifyPercent { get; set; }
        // WORM: refuse prune/GC/delete paths loud. Default false.
        public bool WormMode { get; set; }
        // Change alarm: Error + alert when one generation changes more
        // than this percent of stored bytes. 0 = off.
        public double ChangeAlarmPercent { get; set; }

        public CorpusConfig()
        {
            StorePath = string.Empty;
            StateDbPath = string.Empty;
            ChunkMib = 4;
            Encrypt = string.Empty;
            GcDailyAt = "02:00";
            OptimizeEvery = "00:15:00";
            Chunker = "fixed";
            EccOverheadPercent = 0;
            DeepVerifyPercent = 0;
            WormMode = false;
            ChangeAlarmPercent = 0;
            Compression = new CorpusCompressionConfig();
            Logging = new CorpusLoggingConfig();
            Retention = new CorpusRetentionConfig();
            Exclude = null;
        }

        // AOT: fixed config shape, rooted via TrimmerRootAssembly Corpus and
        // proven by --validate smoke on an AOT binary.
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Fixed config shape rooted via TrimmerRootAssembly Corpus; proven by smoke test.")]
        public static CorpusConfig? Load(string path)
        {
            if (!File.Exists(path)) return null;

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .WithDuplicateKeyChecking()
                .Build();

            using (var reader = new StreamReader(path))
                return deserializer.Deserialize<CorpusConfig>(reader);
        }

        public static bool Validate(CorpusConfig cfg, ILogger log)
        {
            var valid = true;
            if (string.IsNullOrEmpty(cfg.StorePath))
            {
                log.Error("store_path is required");
                valid = false;
            }
            if (cfg.ChunkMib < 1 || cfg.ChunkMib > 64)
            {
                log.Error("chunk_mib must be 1..64 (got: " + cfg.ChunkMib + ")");
                valid = false;
            }
            var level = cfg.Compression != null ? cfg.Compression.Level : 3;
            if (level < 1 || level > 22)
            {
                log.Error("compression.level must be 1..22 (got: " + level + ")");
                valid = false;
            }
            var retention = cfg.Retention ?? new CorpusRetentionConfig();
            if (retention.KeepLast < 1)
            {
                log.Error("retention.keep_last must be >= 1 (got: " + retention.KeepLast + ")");
                valid = false;
            }
            if (retention.KeepWithinDays < 0)
            {
                log.Error("retention.keep_within_days must be >= 0 (got: " + retention.KeepWithinDays + ")");
                valid = false;
            }
            foreach (var pin in retention.Pin ?? new List<int>())
            {
                if (pin <= 0)
                {
                    log.Error("retention.pin must hold positive generation numbers (got: " + pin + ")");
                    valid = false;
                    break;
                }
            }
            if (!TimeSpan.TryParse(cfg.GcDailyAt, out var dailyAt) || dailyAt < TimeSpan.Zero || dailyAt >= TimeSpan.FromDays(1))
            {
                log.Error("gc_daily_at must be HH:mm within a day (got: " + cfg.GcDailyAt + ")");
                valid = false;
            }
            if (!TimeSpan.TryParse(cfg.OptimizeEvery, out var every) || every <= TimeSpan.Zero)
            {
                log.Error("optimize_every must be a positive interval (got: " + cfg.OptimizeEvery + ")");
                valid = false;
            }
            if (cfg.Chunker != "fixed" && cfg.Chunker != "buzhash")
            {
                log.Error("chunker must be fixed or buzhash (got: " + cfg.Chunker + ")");
                valid = false;
            }
            if (cfg.EccOverheadPercent < 0 || cfg.EccOverheadPercent > 50)
            {
                log.Error("ecc_overhead_percent must be 0..50 (got: " + cfg.EccOverheadPercent + ")");
                valid = false;
            }
            if (cfg.DeepVerifyPercent < 0 || cfg.DeepVerifyPercent > 100)
            {
                log.Error("deep_verify_percent must be 0..100 (got: " + cfg.DeepVerifyPercent + ")");
                valid = false;
            }
            if (cfg.ChangeAlarmPercent < 0 || cfg.ChangeAlarmPercent > 100)
            {
                log.Error("change_alarm_percent must be 0..100 (got: " + cfg.ChangeAlarmPercent + ")");
                valid = false;
            }
            return valid;
        }

        // Chunk-index root: explicit config wins, else the platform store.
        // Same shape as TicTack's StateDbRoot, Corpus-owned default dir.
        public static string StateRoot(CorpusConfig cfg)
        {
            if (!string.IsNullOrEmpty(cfg.StateDbPath)) return cfg.StateDbPath;
            return OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Corpus")
                : "/var/lib/corpus";
        }

        // Log-backup target: output-file dir on the single-file lane when the
        // knob is on, else null (store lane keeps StateRoot, knob-off disables).
        // Pure (no probe) so Main can compare it against the resolved dir.
        internal static string? LogBackupTargetDir(CorpusConfig? cfg, string? toVhdx, string? toWim, string? toEsd)
        {
            if (cfg == null || cfg.Logging == null || !cfg.Logging.Backup)
                return null;
            var firstOutput = toVhdx ?? toWim ?? toEsd;
            if (string.IsNullOrEmpty(firstOutput))
                return null;
            return Path.GetDirectoryName(Path.GetFullPath(firstOutput));
        }

        // Resolved dir: target when writable, else StateRoot; null only when off.
        internal static string? ResolveLogBackupDir(CorpusConfig? cfg, string? toVhdx, string? toWim, string? toEsd, string stateRoot)
        {
            var target = LogBackupTargetDir(cfg, toVhdx, toWim, toEsd);
            if (string.IsNullOrEmpty(target))
                return cfg == null || cfg.Logging == null || cfg.Logging.Backup ? stateRoot : null;
            var probePath = Path.Combine(target, ".corpus-write-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose);
                return target;
            }
            catch
            {
                return stateRoot;
            }
        }
    }
}
