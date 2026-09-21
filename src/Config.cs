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
        public List<SourceConfig> Sources { get; set; }
        public MonitorConfig Monitor { get; set; }
        public LoggingConfig Logging { get; set; }
        public List<JobConfig> Jobs { get; set; }
        public WatchdogConfig Watchdog { get; set; }
        public ExternalDrivesConfig ExternalDrives { get; set; }

        // Populated by ExpandPathsList for problems that are only visible
        // during expansion; Validate reports them.
        [YamlIgnore]
        public List<string> Errors { get; set; }

        public TicTackConfig()
        {
            Errors = new List<string>();
            Sources = new List<SourceConfig>();
            Monitor = new MonitorConfig();
            Logging = new LoggingConfig();
            Jobs = new List<JobConfig>();
            Watchdog = new WatchdogConfig();
            ExternalDrives = new ExternalDrivesConfig();
        }
    }

    public class SourceConfig
    {
        public string Path { get; set; }
        public List<string> Paths { get; set; }
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
        public List<string> Exclude { get; set; } = new List<string>();

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
        public int RetryLockMinutes { get; set; }
        public int DeleteThresholdCount { get; set; }
        public long? DeleteThresholdSizeGb { get; set; }
        public double DeleteThresholdPercent { get; set; }
        public int DeleteHoldDays { get; set; }
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
            RetryLockMinutes = 10;
            DeleteThresholdCount = 1000;
            DeleteThresholdSizeGb = 50;
            DeleteThresholdPercent = 50;
            DeleteHoldDays = DefaultDeleteHoldDays;
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
        public int MaxVersions { get; set; }
        public string? Path { get; set; }

        public VersioningConfig()
        {
            MaxVersions = 10;
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

        public MonitorConfig()
        {
            Type = "watcher";
            WatcherBufferKb = 64;
            PollingIntervalSeconds = 3600;
            RestartDelaySeconds = 10;
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
        public List<string> ExcludeDrives { get; set; }
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
            var destinations = new Dictionary<string, string>(PathComparer);
            foreach (var src in cfg.Sources)
            {
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
                case string s when long.TryParse(s, out var n) && n >= 0: limit = n; return true;
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
    }
}
