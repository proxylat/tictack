using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Core;

namespace TicTack;

public class ConfigTests
{
    [Fact]
    public void Load_ReturnsNull_WhenFileMissing()
    {
        var cfg = Config.Load(@"C:\nonexistent\path.yaml");
        Assert.Null(cfg);
    }

    [Fact]
    public void ParseVerification_ReturnsCorrectLevel()
    {
        Assert.Equal(VerificationLevel.Size, Config.ParseVerification("size"));
        Assert.Equal(VerificationLevel.DateAndSize, Config.ParseVerification("date_and_size"));
        Assert.Equal(VerificationLevel.DateAndSize, Config.ParseVerification("date-and-size"));
        Assert.Equal(VerificationLevel.DateAndSize, Config.ParseVerification("dateandsize"));
        Assert.Equal(VerificationLevel.Hash, Config.ParseVerification("hash"));
        Assert.Equal(VerificationLevel.Full, Config.ParseVerification("full"));
        Assert.Equal(VerificationLevel.DateAndSize, Config.ParseVerification(null!));
        Assert.Equal(VerificationLevel.DateAndSize, Config.ParseVerification("unknown"));
    }

    [Fact]
    public void ParseFileSizeLimit_ReturnsCorrectValues()
    {
        Assert.Null(Config.ParseFileSizeLimit(null));
        Assert.Null(Config.ParseFileSizeLimit("no-limit"));
        Assert.Null(Config.ParseFileSizeLimit("none"));
        Assert.Equal(500, Config.ParseFileSizeLimit(500));
        Assert.Equal(1000L, Config.ParseFileSizeLimit(1000L));
        Assert.Equal(200L, Config.ParseFileSizeLimit("200"));
        Assert.Null(Config.ParseFileSizeLimit("abc"));
    }

    [Fact]
    public void Validate_ReturnsTrue_ForValidConfig()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst" });
        var log = new RecordingLogger();
        Assert.True(Config.Validate(cfg, log));
    }

    [Fact]
    public void Validate_AllowsRenameOnlyDurability()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { Durability = "rename-only" } });
        Assert.True(Config.Validate(cfg, new RecordingLogger()));
    }

    [Fact]
    public void Validate_RejectsUnknownDurability()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { Durability = "unsafe" } });
        var log = new RecordingLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("Durability"));
    }

    [Fact]
    public void Validate_AllowsReadyQueueDrainStrategy()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { DrainStrategy = "ready_queue" } });
        Assert.True(Config.Validate(cfg, new RecordingLogger()));
    }

    [Fact]
    public void Validate_RejectsUnknownDrainStrategy()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { DrainStrategy = "heap" } });
        var log = new RecordingLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("DrainStrategy"));
    }

    [Fact]
    public void Validate_AllowsFdatasyncDurability()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { Durability = "fdatasync" } });
        Assert.True(Config.Validate(cfg, new RecordingLogger()));
    }

    [Fact]
    public void Validate_AllowsPerBatchDirSync()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { DirSync = "per-batch" } });
        Assert.True(Config.Validate(cfg, new RecordingLogger()));
    }

    [Fact]
    public void Validate_RejectsUnknownDirSync()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { DirSync = "hourly" } });
        var log = new RecordingLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("DirSync"));
    }

    [Fact]
    public void Validate_AllowsPipelinedCompleteMode()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { CompleteMode = "pipelined" } });
        Assert.True(Config.Validate(cfg, new RecordingLogger()));
    }

    [Fact]
    public void Validate_RejectsUnknownCompleteMode()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { CompleteMode = "parallel" } });
        var log = new RecordingLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("CompleteMode"));
    }

    [Theory]
    [InlineData("hashh")]
    [InlineData("md5")]
    [InlineData("")]
    public void Validate_RejectsUnknownVerification(string verification)
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { Verification = verification } });
        var log = new RecordingLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("Verification"));
    }

    [Fact]
    public void Validate_AllowsVerificationAliases()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src", Destination = @"D:\dst", Sync = new SyncConfig { Verification = "Date-And-Size" } });
        Assert.True(Config.Validate(cfg, new RecordingLogger()));
    }

    [Fact]
    public void Validate_ReturnsFalse_WhenNoSources()
    {
        var cfg = new TicTackConfig();
        var log = new RecordingLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("No sources"));
    }

    [Fact]
    public void Validate_ReturnsFalse_WhenSourceMissingPath()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Destination = @"D:\dst" });
        var log = new RecordingLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("Source path"));
    }

    [Fact]
    public void Validate_ReturnsFalse_WhenSourceMissingDestination()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src" });
        var log = new RecordingLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("Destination is required"));
    }

    [Fact]
    public void Load_ParsesValidYaml()
    {
        var yaml = @"
sources:
  - path: C:\src
    destination: D:\dst
    filter:
      max_file_size_mb: 100
      exclude:
        - '*.tmp'
    sync:
      verification: hash
      retry:
        max_attempts: 3
        delay_ms: 500
        backoff: 2.0
      versioning:
        max_versions: 5
        path: D:\versions
      deletion:
        mode: mirror
monitor:
  type: watcher
  watcher_buffer_kb: 128
logging:
  level: debug
  path: C:\log.txt
  max_size_mb: 20
  max_files: 3
  console: true
watchdog:
  enabled: false
  interval_minutes: 60
";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, yaml);
            var cfg = Config.Load(path)!;
            Assert.NotNull(cfg);
            Assert.Single(cfg.Sources);
            Assert.Equal(@"C:\src", cfg.Sources[0].Path);
            Assert.Equal(@"D:\dst", cfg.Sources[0].Destination);
            Assert.Equal("hash", cfg.Sources[0].Sync.Verification);
            Assert.Equal(3, cfg.Sources[0].Sync.Retry.MaxAttempts);
            Assert.Equal(100L, Config.ParseFileSizeLimit(cfg.Sources[0].Filter.MaxFileSizeMb));
            Assert.Contains("*.tmp", cfg.Sources[0].Filter.Exclude);
            Assert.Equal("mirror", cfg.Sources[0].Sync.Deletion!.Mode);
            Assert.Equal("watcher", cfg.Monitor.Type);
            Assert.Equal("debug", cfg.Logging.Level);
            Assert.False(cfg.Watchdog.Enabled);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ExpandsPathsListIntoMultipleSources()
    {
        const string userDir = @"C:\Users\TestUser";
        var yaml = @"
sources:
  - paths:
      - '" + userDir + @"\Desktop'
      - '" + userDir + @"\Documents'
    destination: D:\Sync
    sync:
      verification: size
";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, yaml);
            var cfg = Config.Load(path)!;
            Assert.NotNull(cfg);
            Assert.Equal(2, cfg.Sources.Count);
            Assert.Contains("Desktop", cfg.Sources[0].Path);
            Assert.Contains("Documents", cfg.Sources[1].Path);
            Assert.Contains("Desktop", cfg.Sources[0].Destination);
            Assert.Contains("Documents", cfg.Sources[1].Destination);
            Assert.Equal(Path.Combine(@"D:\Sync", "Desktop"), cfg.Sources[0].Destination);
            Assert.Equal(Path.Combine(@"D:\Sync", "Documents"), cfg.Sources[1].Destination);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_LeavesEnvironmentVariablesLiteral()
    {
        var yaml = @"
sources:
  - path: '%USERPROFILE%\Desktop'
    destination: D:\dst
logging:
  path: '%LOCALAPPDATA%\tictack.log'
";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, yaml);
            var cfg = Config.Load(path)!;
            Assert.Equal(@"%USERPROFILE%\Desktop", cfg.Sources[0].Path);
            Assert.Equal(@"%LOCALAPPDATA%\tictack.log", cfg.Logging.Path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_RejectsUnknownProperty()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "sources: []\nunexpected: true\n");
            Assert.Throws<YamlException>(() => Config.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_RejectsDuplicateProperty()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "sources: []\nmonitor:\n  type: watcher\n  type: polling\n");
            Assert.Throws<YamlException>(() => Config.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validate_RejectsDuplicateDestination()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = "/tmp/tt-src-a", Destination = "/tmp/tt-dst" });
        cfg.Sources.Add(new SourceConfig { Path = "/tmp/tt-src-b", Destination = "/tmp/tt-dst" });
        var log = new RecordingLogger();

        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("same destination"));
    }

    [Fact]
    public void Validate_CaseDistinctPaths_OverlapIsOsAware()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = "/tmp/tt-Data", Destination = "/tmp/tt-data/sub" });
        var log = new RecordingLogger();

        // Linux is case-sensitive, so these are not overlapping.
        bool valid = Config.Validate(cfg, log);
        Assert.Equal(!OperatingSystem.IsWindows(), valid);
    }

    [Fact]
    public void Validate_RejectsInvalidFileSizeLimit()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig
        {
            Path = "/tmp/tt-src",
            Destination = "/tmp/tt-dst",
            Filter = new FilterConfig { MaxFileSizeMb = "abc" }
        });
        var log = new RecordingLogger();

        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("max_file_size_mb"));
    }

    [Fact]
    public void Validate_RejectsUnknownMonitorType()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = "/tmp/tt-src", Destination = "/tmp/tt-dst" });
        cfg.Monitor = new MonitorConfig { Type = "watchr" };
        var log = new RecordingLogger();

        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("monitor.type"));
    }

    [Fact]
    public void Validate_ReportsPathAndPathsConflict()
    {
        var yaml = "sources:\n  - path: '/tmp/tt-src'\n    paths:\n      - '/tmp/tt-other'\n    destination: '/tmp/tt-dst'\n";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, yaml);
            var cfg = Config.Load(path)!;
            var log = new RecordingLogger();

            Assert.False(Config.Validate(cfg, log));
            Assert.Contains(log.Messages, m => m.Contains("both 'path' and 'paths'"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validate_ReportsNullSyncOnExpandedPaths()
    {
        var yaml = "sources:\n  - paths:\n      - '/tmp/tt-src'\n    destination: '/tmp/tt-dst'\n    sync:\n";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, yaml);
            var cfg = Config.Load(path)!;
            var log = new RecordingLogger();

            Assert.False(Config.Validate(cfg, log));
            Assert.Contains(log.Messages, m => m.Contains("missing 'sync' or 'state_db_path'"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DefaultValues_AreSet()
    {
        Assert.Equal(10.0, new SourceConfig().DebounceSeconds);
        Assert.Equal("watcher", new MonitorConfig().Type);
        Assert.Equal(LogLevel.Info, LogLevelParser.Parse(new LoggingConfig().Level));
        Assert.True(new WatchdogConfig().Enabled);
    }

    [Fact]
    public void DeleteHoldDays_DefaultsToTheSharedConstant()
    {
        Assert.Equal(7, SyncConfig.DefaultDeleteHoldDays);
        Assert.Equal(SyncConfig.DefaultDeleteHoldDays, new SyncConfig().DeleteHoldDays);
    }
}
