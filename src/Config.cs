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
        public bool RenameDetection { get; set; }
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
            RenameDetection = true;
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
            Type = "composite";
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
                .IgnoreUnmatchedProperties()
                .Build();

            using (var reader = new StreamReader(path))
            {
                var cfg = deserializer.Deserialize<TicTackConfig>(reader);
                ExpandEnvVars(cfg);
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
                if (src.Sync != null && !string.Equals(src.Sync.Durability, "full", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(src.Sync.Durability, "rename-only", StringComparison.OrdinalIgnoreCase))
                {
                    log.Error("Durability must be 'full' or 'rename-only' for source: " + src.Path);
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

        static void ExpandEnvVars(TicTackConfig cfg)
        {
            var expanded = new List<SourceConfig>();
            foreach (var src in cfg.Sources)
            {
                if (src.Path != null) src.Path = Expand(src.Path);
                if (src.Destination != null) src.Destination = Expand(src.Destination);
                if (src.StateDbPath != null) src.StateDbPath = Expand(src.StateDbPath);
                if (src.Sync != null)
                {
                    if (src.Sync.Versioning != null && src.Sync.Versioning.Path != null)
                        src.Sync.Versioning.Path = Expand(src.Sync.Versioning.Path);
                    if (src.Sync.Deletion != null && src.Sync.Deletion.Path != null)
                        src.Sync.Deletion.Path = Expand(src.Sync.Deletion.Path);
                }

                if (src.Paths != null && src.Paths.Count > 0)
                {
                    foreach (var p in src.Paths)
                    {
                        var pExp = Expand(p);
                        var folder = Path.GetFileName(pExp.TrimEnd('\\', '/'));
                        expanded.Add(new SourceConfig
                        {
                            Path = pExp,
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

            if (cfg.Logging != null && cfg.Logging.Path != null)
                cfg.Logging.Path = Expand(cfg.Logging.Path);
            if (cfg.Logging != null && cfg.Logging.AlertPath != null)
                cfg.Logging.AlertPath = Expand(cfg.Logging.AlertPath);
        }

        static string Expand(string s)
        {
            return Environment.ExpandEnvironmentVariables(s);
        }

        public static VerificationLevel ParseVerification(string? value)
        {
            switch (value != null ? value.ToLowerInvariant().Replace("_", "").Replace("-", "") : null)
            {
                case "size": return VerificationLevel.Size;
                case "dateandsize": case "date_size": case "date-and-size": return VerificationLevel.DateAndSize;
                case "hash": return VerificationLevel.Hash;
                case "full": return VerificationLevel.Full;
                default: return VerificationLevel.DateAndSize;
            }
        }
    }
}
