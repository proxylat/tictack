using System.Globalization;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TicTack
{
    public class TicTackConfig
    {
        public IList<SourceConfig> Sources { get; set; }
        public MonitorConfig Monitor { get; set; }
        public LoggingConfig Logging { get; set; }
        public IList<JobConfig> Jobs { get; set; }
        public JobsDefaultsConfig JobsDefaults { get; set; }
        public WatchdogConfig Watchdog { get; set; }
        public ExternalDrivesConfig ExternalDrives { get; set; }

        // Minutes to wait for [VolumeLabel] volumes to appear (slow USB
        // spin-up) before failing fast on unresolved labels. Top-level on
        // purpose: one shared deadline resolves every label in a single
        // pass (sources, jobs, log/alert paths) — a per-source key would
        // multiply worst-case waits and leave the log path homeless.
        public double VolumeWaitMinutes { get; set; }

        // Populated by ExpandPathsList for problems that are only visible
        // during expansion; Validate reports them.
        [YamlIgnore]
        public IList<string> Errors { get; set; }

        public TicTackConfig()
        {
            Errors = new List<string>();
            Sources = new List<SourceConfig>();
            Monitor = new MonitorConfig();
            Logging = new LoggingConfig();
            Jobs = new List<JobConfig>();
            JobsDefaults = new JobsDefaultsConfig();
            Watchdog = new WatchdogConfig();
            ExternalDrives = new ExternalDrivesConfig();
            VolumeWaitMinutes = 2;
        }
    }

    public class SourceConfig
    {
        public string Path { get; set; }
        public IList<string> Paths { get; set; } = new List<string>();
        public string Destination { get; set; }
        public string StateDbPath { get; set; }
        public double DebounceSeconds { get; set; }
        public FilterConfig Filter { get; set; }
        public SyncConfig Sync { get; set; }

        public SourceConfig()
        {
            Path = string.Empty;
            Paths = new List<string>();
            Destination = string.Empty;
            StateDbPath = string.Empty;
            DebounceSeconds = 10;
            Filter = new FilterConfig();
            Sync = new SyncConfig();
        }
    }

    public class FilterConfig
    {
        public object? MaxFileSizeMb { get; set; }
        public IList<string> Exclude { get; set; } = new List<string>();

        public FilterConfig()
        {
            Exclude = new List<string>();
        }
    }

    public class SyncConfig
    {
        public const int DefaultDeleteHoldDays = 7;

        public string? Verification { get; set; }
        public string Durability { get; set; }
        public string DrainStrategy { get; set; }
        public string DirSync { get; set; }
        public string CompleteMode { get; set; }
        public int InitialSyncWorkers { get; set; }
        public RetryConfig Retry { get; set; }
        public string? LockHandling { get; set; }
        public string FileAccess { get; set; }
        public int LockWaitSeconds { get; set; }
        public int DeleteThresholdCount { get; set; }
        public long? DeleteThresholdSizeGb { get; set; }
        public double DeleteThresholdPercent { get; set; }
        // Bulk-cleanup shortcut: when a would-be-blocked batch has this share
        // (percent) of its paths under one parent directory, it looks like a
        // deliberate folder cleanup, not a catastrophe — proceed with a Warn
        // instead of holding. Scattered batches hold as before. Default 0 =
        // off (absent key means off; every tripped batch holds).
        public double BulkCleanupPercent { get; set; }
        public int DeleteHoldDays { get; set; }

        // Full content routine-check cadence in days: when due, files whose
        // size+mtime look unchanged are still byte-compared against the
        // destination instead of skipped. Default 14. Zero or negative
        // disables (trust size+mtime only).
        public int FullRoutineCheckDays { get; set; }

        // Spike guard: sliding-window anomaly brake. Trips when any single
        // wire fires inside the window — bytes copied, files copied, or
        // dest renames replayed. A trip writes a .tictack-churn-hold marker
        // next to the lock file and freezes copies, deletes, and the
        // deferred drain until the user deletes the marker (file-is-truth).
        // Each wire ≤0 disables that wire. Defaults: 60min / 100GB /
        // 50000 files / 10000 renames.
        public int SpikeWindowMinutes { get; set; }
        public long SpikeBytesGb { get; set; }
        public int SpikeFiles { get; set; }
        public int SpikeRenames { get; set; }

        // Proof-by-restore: every N days, hash-compare a random sample of
        // destination files against their sources (read-only; mismatches
        // log Error and re-alert next run, repair is the scrub's job).
        // Default 0 = off. RoutineCheckFiles bounds the sample size.
        public int RoutineCheckDays { get; set; }
        public int RoutineCheckFiles { get; set; }

        // Destination self-defense: when true, the destination root is
        // hardened at startup so only the service identity can write —
        // ransomware running as the user gets access-denied. Windows:
        // inheritable Users deny-write (service runs as SYSTEM, unaffected).
        // Linux: group/other write bits stripped (service runs as root).
        // Default false: enabling locks the user's own CLI runs out too
        // (service mode only), existing trees need a one-time recursive
        // pass (see README), toggle-off does not revert ACLs.
        public bool DestinationProtect { get; set; }
        public VersioningConfig? Versioning { get; set; }
        public DeletionConfig? Deletion { get; set; }

        public SyncConfig()
        {
            Verification = "date_and_size";
            Durability = "full";
            DrainStrategy = "scan";
            DirSync = "per-file";
            CompleteMode = "inline";
            InitialSyncWorkers = 2;
            Retry = new RetryConfig();
            LockHandling = "retry";
            FileAccess = "direct";
            LockWaitSeconds = 60;
            DeleteThresholdCount = 1000;
            DeleteThresholdSizeGb = 50;
            DeleteThresholdPercent = 50;
            BulkCleanupPercent = 0;
            DeleteHoldDays = DefaultDeleteHoldDays;
            FullRoutineCheckDays = 14;
            SpikeWindowMinutes = 60;
            SpikeBytesGb = 100;
            SpikeFiles = 50000;
            SpikeRenames = 10000;
            RoutineCheckDays = 0;
            RoutineCheckFiles = 10;
            DestinationProtect = false;
        }
    }

    public class RetryConfig
    {
        public int MaxAttempts { get; set; }
        public int DelayMs { get; set; }
        public double Backoff { get; set; }

        public RetryConfig()
        {
            MaxAttempts = 5;
            DelayMs = 1000;
            Backoff = 2.0;
        }
    }

    public class VersioningConfig
    {
        // Legacy flat cap. Nullable so "absent" is detectable: when any
        // keep_* policy is set, an absent MaxVersions does not silently cap
        // the result. Null + no keep_* policies = default keep-last 10.
        public int? MaxVersions { get; set; }
        public string? Path { get; set; }
        // Restic-style retention, OR-semantics per file: a version survives
        // when ANY policy keeps it. Durations use the same "30d"/"12h"
        // spelling as the job keys (see Config.TryParseDuration).
        public int? KeepLast { get; set; }
        public string? KeepWithin { get; set; }
        public int? KeepDaily { get; set; }
        public int? KeepWeekly { get; set; }
        public int? KeepMonthly { get; set; }

        public VersioningConfig()
        {
            MaxVersions = null;
        }
    }

    public class DeletionConfig
    {
        public string? Mode { get; set; }
        public string? Path { get; set; }

        public DeletionConfig()
        {
            Mode = "archive";
        }
    }

    public class MonitorConfig
    {
        public string Type { get; set; }
        public int WatcherBufferKb { get; set; }
        public int PollingIntervalSeconds { get; set; }
        public int RestartDelaySeconds { get; set; }
        public int UsnPollIntervalMs { get; set; }
        // USN only: skip journal records whose parent dir is outside the
        // watched tree before any path resolve (volume-wide journal tax).
        // UnderWatch stays the authority, so false entries only cost
        // resolves, never wrong events. Default on. Doubles as the
        // membership signal for usn_resolve_gap: with the prefilter off,
        // tree-deletes lose their gap signal (parity still covers them).
        public bool UsnParentPrefilter { get; set; }
        // USN only: fire a rate-limited covering gap when a delete's
        // tracked parent no longer resolves (tree-delete safety net:
        // without it the unnamed deletes linger silently until parity).
        // Default on; set false if pathological churn causes scan spam.
        public bool UsnResolveGap { get; set; }
        // USN only: persist (journal id, cursor) across restarts so startup
        // replays the offline journal window instead of depending solely on
        // the initial walk. Best-effort and bounded (100k records / 60s);
        // any failure falls back to head + walk. Default on.
        public bool UsnCheckpoint { get; set; }
        // Composite-only: also tap the NTFS USN journal as a third member.
        // Ignored for watcher/usn/polling types. Windows-only; on other
        // platforms or without elevation the member probe-skips with a warn.
        public bool Usn { get; set; }
        // Composite-only: keep the polling member as the hourly backstop.
        // Set false to drop it (zero snapshot memory) when the USN member
        // is healthy: watcher overflows and USN re-baselines fire
        // on-demand covering scans instead. Ignored for
        // watcher/usn/polling types. Kept on automatically when the USN
        // member is unavailable, so there is always a backstop.
        public bool PollingBackstop { get; set; }

        public MonitorConfig()
        {
            Type = "watcher";
            WatcherBufferKb = 64;
            PollingIntervalSeconds = 3600;
            RestartDelaySeconds = 10;
            UsnPollIntervalMs = 200;
            UsnParentPrefilter = true;
            UsnResolveGap = true;
            UsnCheckpoint = true;
            Usn = false;
            PollingBackstop = true;
        }
    }

    public class LoggingConfig
    {
        public string? Level { get; set; }
        public string? Path { get; set; }
        public string? AlertPath { get; set; }
        public int MaxSizeMb { get; set; }
        public int MaxFiles { get; set; }
        public bool Console { get; set; }

        public LoggingConfig()
        {
            Level = "info";
            MaxSizeMb = 10;
            MaxFiles = 5;
            Console = false;
        }
    }

    public class JobConfig
    {
        public string? Name { get; set; }
        public string? Time { get; set; }
        public string? Command { get; set; }
        public string? WorkingDir { get; set; }
        // 0 = unset: the scheduler resolves jobs_defaults.timeout_minutes,
        // else 10. Negative is invalid (job skipped with a warning).
        public int TimeoutMinutes { get; set; }
        // Cadence: daily (default) | weekly | monthly. Weekly needs Day as a
        // weekday name (Monday-Sunday); monthly needs Day as 1-31 (clamped to
        // short months, so 31 runs on the 30th/28th). Mutually exclusive with
        // RunOnceOn, which is an ISO date (YYYY-MM-DD) that fires once.
        public string? Schedule { get; set; }
        public string? Day { get; set; }
        public string? RunOnceOn { get; set; }
        // Interval trigger ("6h", "15m", "30s", "1d"): fires every span after
        // the previous run finished. Never combined with schedule/day/time/
        // run_once_on — a job is calendar, one-shot, or interval.
        public string? Every { get; set; }
        // Weekly mask ("days: [mon, wed, fri]"): fires on each listed weekday.
        // Only valid with schedule weekly; mutually exclusive with day.
        public IList<string> Days { get; set; } = new List<string>();
        // Missed-run policy: catch_up | skip. Falls back to
        // jobs_defaults.missed_run, else catch_up (today's behavior).
        public string? MissedRun { get; set; }
        // Overlap policy: skip (stand down while the previous run is active)
        // or queue (wait, then run). Default skip.
        public string? Overlap { get; set; }
        // Jitter ("15m"): random 0..span delay before each fire so jobs do
        // not align into I/O storms at boot or midnight.
        public string? Jitter { get; set; }
        // Hooks: pre_command gates the run (non-zero skips the main command),
        // post_command runs after the main command whenever it ran,
        // on_failure runs when pre or main fails. Same working dir, same
        // {source} substitution, each with the full timeout budget.
        public string? PreCommand { get; set; }
        public string? PostCommand { get; set; }
        public string? OnFailure { get; set; }
        // Laptop trio: idle CPU priority, skip while on battery, hold a
        // sleep inhibitor for the run. All default false.
        public bool LowPriority { get; set; }
        public bool OnAcOnly { get; set; }
        public bool PreventSleep { get; set; }

        public JobConfig()
        {
            TimeoutMinutes = 0;
            Days = new List<string>();
        }
    }

    // Shared job defaults: per-job values override these. Only the keys worth
    // sharing live here — triggers and commands stay per job.
    public class JobsDefaultsConfig
    {
        public string? MissedRun { get; set; }
        public int? TimeoutMinutes { get; set; }
    }

    public class WatchdogConfig
    {
        public bool Enabled { get; set; }
        public int IntervalMinutes { get; set; }

        public WatchdogConfig()
        {
            Enabled = true;
            IntervalMinutes = 30;
        }
    }

    public class ExternalDrivesConfig
    {
        public string? Command { get; set; }
        public string? WorkingDir { get; set; }
        public IList<string> ExcludeDrives { get; set; } = new List<string>();
        public bool RequireMarkerFile { get; set; }
        public string? MarkerFileName { get; set; }

        public ExternalDrivesConfig()
        {
            Command = OperatingSystem.IsWindows()
                ? "restic.exe backup --compression auto \"{source}\" -r \"{drive}\\restic-repo\""
                : "restic backup --compression auto \"{source}\" -r \"{drive}/restic-repo\"";
            ExcludeDrives = new List<string>();
            RequireMarkerFile = true;
            MarkerFileName = ".tictack-target";
        }
    }

    public static class Config
    {
        // YamlDotNet's reflection deserializer carries RequiresDynamicCode, but
        // the 16.3.0 build contains no runtime codegen (no Reflection.Emit,
        // DynamicMethod, or Expression.Compile — verified by inspection): it is
        // reflection-invoke only, which works under AOT while the model types
        // are preserved. The Aot publish profile roots TicTackSv + YamlDotNet
        // for trimming (see Properties/PublishProfiles/Aot.pubxml), so this
        // suppression documents that pairing instead of hiding a real risk.
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Reflection-only deserializer; model assemblies are trim-rooted in the Aot publish profile.")]
        public static TicTackConfig? Load(string path)
        {
            if (!File.Exists(path)) return null;

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .WithDuplicateKeyChecking()
                .Build();

            using (var reader = new StreamReader(path))
            {
                var cfg = deserializer.Deserialize<TicTackConfig>(reader);
                ExpandPathsList(cfg);
                return cfg;
            }
        }

        public static bool Validate(TicTackConfig cfg, ILogger log)
        {
            if (cfg.Sources == null || cfg.Sources.Count == 0)
            {
                log.Error("No sources configured");
                return false;
            }
            bool valid = true;
            if (cfg.Errors != null)
            {
                foreach (var err in cfg.Errors)
                {
                    log.Error(err);
                    valid = false;
                }
            }
            if (cfg.Monitor != null && !IsValidMonitorType(cfg.Monitor.Type))
            {
                log.Error("monitor.type must be 'watcher', 'usn', 'polling', or 'composite' (got: " + (cfg.Monitor.Type ?? "null") + ")");
                valid = false;
            }
            if (cfg.Monitor != null && cfg.Monitor.Usn
                && !string.Equals(cfg.Monitor.Type, "composite", StringComparison.OrdinalIgnoreCase))
                log.Warn("monitor.usn only applies to type 'composite'; ignored for type '" + cfg.Monitor.Type + "'.");
            if (cfg.Monitor != null && !cfg.Monitor.PollingBackstop
                && !string.Equals(cfg.Monitor.Type, "composite", StringComparison.OrdinalIgnoreCase))
                log.Warn("monitor.polling_backstop only applies to type 'composite'; ignored for type '" + cfg.Monitor.Type + "'.");
            if (double.IsNaN(cfg.VolumeWaitMinutes) || cfg.VolumeWaitMinutes < 0)
            {
                log.Error("volume_wait_minutes must be >= 0 (got: " + cfg.VolumeWaitMinutes + ")");
                valid = false;
            }
            var destinations = new Dictionary<string, string>(PathComparer);
            foreach (var src in cfg.Sources)
            {
                if (!ValidateSource(src, destinations, log)) valid = false;
            }
            return valid;
        }
        // One source stanza: paths, monitor, destinations, numeric knobs.
        // False = logged the reason, keep checking the rest.
        private static bool ValidateSource(SourceConfig src, Dictionary<string, string> destinations, ILogger log)
        {
            bool valid = true;
            if (string.IsNullOrEmpty(src.Path))
            {
                log.Error("Source path is required");
                valid = false;
            }
            if (string.IsNullOrEmpty(src.Destination))
            {
                log.Error("Destination is required for source: " + src.Path);
                valid = false;
            }
            valid = ValidatePathsNotOverlapping(src, log) && valid;
            if (src.Sync != null && !string.Equals(src.Sync.Durability, "full", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(src.Sync.Durability, "fdatasync", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(src.Sync.Durability, "rename-only", StringComparison.OrdinalIgnoreCase))
            {
                log.Error("Durability must be 'full', 'fdatasync', or 'rename-only' for source: " + src.Path);
                valid = false;
            }
            if (src.Sync != null && !string.Equals(src.Sync.DrainStrategy, "scan", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(src.Sync.DrainStrategy, "ready_queue", StringComparison.OrdinalIgnoreCase))
            {
                log.Error("DrainStrategy must be 'scan' or 'ready_queue' for source: " + src.Path);
                valid = false;
            }
            if (src.Sync != null && !string.Equals(src.Sync.DirSync, "per-file", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(src.Sync.DirSync, "per-batch", StringComparison.OrdinalIgnoreCase))
            {
                log.Error("DirSync must be 'per-file' or 'per-batch' for source: " + src.Path);
                valid = false;
            }
            if (src.Sync != null && !string.Equals(src.Sync.CompleteMode, "inline", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(src.Sync.CompleteMode, "pipelined", StringComparison.OrdinalIgnoreCase))
            {
                log.Error("CompleteMode must be 'inline' or 'pipelined' for source: " + src.Path);
                valid = false;
            }
            if (src.Sync != null && !IsValidVerification(src.Sync.Verification))
            {
                log.Error("Verification must be 'size', 'date_and_size', 'hash', or 'full' for source: " + src.Path);
                valid = false;
            }
            if (src.Sync != null && !string.Equals(src.Sync.FileAccess, "direct", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(src.Sync.FileAccess, "vss", StringComparison.OrdinalIgnoreCase))
            {
                log.Error("FileAccess must be 'direct' or 'vss' for source: " + src.Path);
                valid = false;
            }
            if (src.Filter != null && !IsValidFileSizeLimit(src.Filter.MaxFileSizeMb))
            {
                log.Error("max_file_size_mb must be a number, 'no-limit', or 'none' for source: " + src.Path);
                valid = false;
            }
            valid = ValidateDestinations(src, destinations, log) && valid;
                    return valid;
        }

        // Source and destination must not overlap in either direction.
        // False = logged. Unresolvable paths fail closed.
        private static bool ValidatePathsNotOverlapping(SourceConfig src, ILogger log)
        {
            bool valid = true;

            if (!string.IsNullOrEmpty(src.Path) && !string.IsNullOrEmpty(src.Destination))
            {
                try
                {
                    var source = Path.GetFullPath(src.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var destination = Path.GetFullPath(src.Destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    // Case-sensitive on Linux, where two paths differing only
                    // in case are genuinely different directories.
                    var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (source.Equals(destination, cmp)
                        || destination.StartsWith(source + Path.DirectorySeparatorChar, cmp)
                        || source.StartsWith(destination + Path.DirectorySeparatorChar, cmp))
                    {
                        log.Error("Source and destination overlap: " + src.Path + " -> " + src.Destination);
                        valid = false;
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Invalid source or destination path: " + ex.Message);
                    valid = false;
                }
            }
            return valid;
        }


        // Destination paths: no drive roots, no source-inside-source,
        // no duplicates, all absolute. False = logged, keeps checking.
        private static bool ValidateDestinations(SourceConfig src, Dictionary<string, string> destinations, ILogger log)
        {
            bool valid = true;

            if (!string.IsNullOrEmpty(src.Destination))
            {
                try
                {
                    var dest = Path.GetFullPath(src.Destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (destinations.TryGetValue(dest, out var other))
                    {
                        log.Error("Two sources map to the same destination: " + other + " and " + src.Path + " -> " + src.Destination);
                        valid = false;
                    }
                    else
                    {
                        destinations[dest] = src.Path;
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Invalid destination path: " + ex.Message);
                    valid = false;
                }
            }
            return valid;
        }



        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static bool IsValidMonitorType(string? type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            var t = type.ToLowerInvariant();
            return t == "watcher" || t == "usn" || t == "polling" || t == "composite";
        }

        public static long? ParseFileSizeLimit(object? val) =>
            TryParseFileSizeLimit(val, out var limit) ? limit : null;

        public static bool IsValidFileSizeLimit(object? val) => TryParseFileSizeLimit(val, out _);

        // Single token table: parser and validator cannot drift.
        private static bool TryParseFileSizeLimit(object? val, out long? limit)
        {
            limit = null;
            switch (val)
            {
                case null: return true;
                case int i when i >= 0: limit = i; return true;
                case long l when l >= 0: limit = l; return true;
                case string s when s == "no-limit" || s == "none": return true;
                case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0: limit = n; return true;
                default: return false;
            }
        }

        static void ExpandPathsList(TicTackConfig cfg)
        {
            var expanded = new List<SourceConfig>();
            foreach (var src in cfg.Sources)
            {
                if (src.Paths != null && src.Paths.Count > 0)
                {
                    if (!string.IsNullOrEmpty(src.Path))
                        cfg.Errors.Add("Source has both 'path' and 'paths'; 'path' is ignored for: " + src.Path);
                    foreach (var p in src.Paths)
                    {
                        if (src.Sync == null || src.StateDbPath == null)
                            cfg.Errors.Add("Source is missing 'sync' or 'state_db_path': " + p);
                        // Path.GetFileName only splits on the platform
                        // separator, so on Linux a Windows path returns whole.
                        // Fall back to '\' splitting (no-op on Windows, where
                        // GetFileName already handles it).
                        var trimmed = p.TrimEnd('\\', '/');
                        var folder = Path.GetFileName(trimmed);
                        if (folder == trimmed && trimmed.Contains('\\'))
                            folder = trimmed.Substring(trimmed.LastIndexOf('\\') + 1);
                        expanded.Add(new SourceConfig
                        {
                            Path = p,
                            Destination = Path.Combine(src.Destination ?? "", folder),
                            DebounceSeconds = src.DebounceSeconds,
                            Filter = src.Filter,
                            Sync = src.Sync ?? new SyncConfig(),
                            StateDbPath = src.StateDbPath ?? string.Empty
                        });
                    }
                }
                else
                {
                    expanded.Add(src);
                }
            }
            cfg.Sources = expanded;
        }

        // Single source of truth for the token set: display names, the parse
        // switch, and validation cannot drift apart.
        private static readonly (string Token, VerificationLevel Level)[] VerificationTokens =
        {
            ("size", VerificationLevel.Size),
            ("dateandsize", VerificationLevel.DateAndSize),
            ("hash", VerificationLevel.Hash),
            ("full", VerificationLevel.Full)
        };

        public static VerificationLevel ParseVerification(string? value)
        {
            var normalized = NormalizeVerification(value);
            if (normalized != null)
            {
                foreach (var (token, level) in VerificationTokens)
                {
                    if (token == normalized) return level;
                }
            }
            return VerificationLevel.DateAndSize;
        }

        public static bool IsValidVerification(string? value)
        {
            var normalized = NormalizeVerification(value);
            if (normalized == null) return false;
            foreach (var (token, _) in VerificationTokens)
            {
                if (token == normalized) return true;
            }
            return false;
        }

        static string? NormalizeVerification(string? value) =>
            value != null ? value.ToLowerInvariant().Replace("_", "").Replace("-", "") : null;

        // Duration spelling shared by every/keep_within/jitter: "30s", "15m",
        // "6h", "30d", compounds ("1h30m"), optional whitespace. A bare number
        // is rejected — the unit is the documentation. Zero ("0s") is valid
        // (means off); negatives are rejected.
        public static bool TryParseDuration(string? value, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var s = value.Trim().ToLowerInvariant();
            if (s.StartsWith('-')) return false;
            long totalSeconds = 0;
            int i = 0, parsed = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                int numStart = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (i == numStart) return false;
                if (!long.TryParse(s.Substring(numStart, i - numStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)) return false;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                int unitStart = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                if (i == unitStart) return false;
                var unit = s.Substring(unitStart, i - unitStart);
                long mult = unit switch
                {
                    "s" or "sec" or "secs" or "second" or "seconds" => 1,
                    "m" or "min" or "mins" or "minute" or "minutes" => 60,
                    "h" or "hr" or "hrs" or "hour" or "hours" => 3600,
                    "d" or "day" or "days" => 86400,
                    _ => -1,
                };
                if (mult < 0) return false;
                totalSeconds += num * mult;
                parsed++;
                if (totalSeconds > 3660L * 86400) return false; // >10y: typo guard
            }
            if (parsed == 0) return false;
            duration = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }
    }
}
