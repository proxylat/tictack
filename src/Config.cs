using System;
using System.Collections.Generic;
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

        public TicTackConfig()
        {
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
        public string? Verification { get; set; }
        public string Durability { get; set; }
        public int InitialSyncWorkers { get; set; }
        public RetryConfig Retry { get; set; }
        public string? LockHandling { get; set; }
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
            InitialSyncWorkers = 2;
            Retry = new RetryConfig();
            LockHandling = "retry";
            RetryLockMinutes = 10;
            DeleteThresholdCount = 1000;
            DeleteThresholdSizeGb = 50;
            DeleteThresholdPercent = 50;
            DeleteHoldDays = 7;
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
            Command = "restic.exe backup --compression auto \"{source}\" -r \"{drive}\\restic-repo\"";
            ExcludeDrives = new List<string>();
            RequireMarkerFile = true;
            MarkerFileName = ".tictack-target";
        }
    }

    public static class Config
    {
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
                        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)
                            || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            || source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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
                    && !string.Equals(src.Sync.Durability, "rename-only", StringComparison.OrdinalIgnoreCase))
                {
                    log.Error("Durability must be 'full' or 'rename-only' for source: " + src.Path);
                    valid = false;
                }
                if (src.Sync != null && !IsValidVerification(src.Sync.Verification))
                {
                    log.Error("Verification must be 'size', 'date_and_size', 'hash', or 'full' for source: " + src.Path);
                    valid = false;
                }
            }
            return valid;
        }

        public static long? ParseFileSizeLimit(object? val)
        {
            if (val == null) return null;
            if (val is int) return (long)(int)val;
            if (val is long) return (long)val;
            string? s = val as string;
            if (s != null)
            {
                if (s == "no-limit" || s == "none") return null;
                long n;
                if (long.TryParse(s, out n)) return n;
            }
            return null;
        }

        static void ExpandPathsList(TicTackConfig cfg)
        {
            var expanded = new List<SourceConfig>();
            foreach (var src in cfg.Sources)
            {
                if (src.Paths != null && src.Paths.Count > 0)
                {
                    foreach (var p in src.Paths)
                    {
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
                            Sync = src.Sync!,
                            StateDbPath = src.StateDbPath!
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

        public static VerificationLevel ParseVerification(string? value)
        {
            switch (NormalizeVerification(value))
            {
                case "size": return VerificationLevel.Size;
                case "dateandsize": return VerificationLevel.DateAndSize;
                case "hash": return VerificationLevel.Hash;
                case "full": return VerificationLevel.Full;
                default: return VerificationLevel.DateAndSize;
            }
        }

        public static bool IsValidVerification(string? value)
        {
            switch (NormalizeVerification(value))
            {
                case "size":
                case "dateandsize":
                case "hash":
                case "full":
                    return true;
                default:
                    return false;
            }
        }

        static string? NormalizeVerification(string? value) =>
            value != null ? value.ToLowerInvariant().Replace("_", "").Replace("-", "") : null;
    }
}
