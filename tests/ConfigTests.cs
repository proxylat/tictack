using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TicTack;

public class ConfigTests
{
    [Fact]
    public void Load_ReturnsNull_WhenFileMissing()
    {
        var cfg = Config.Load("/nonexistent/path.yaml");
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
        var log = new MockLogger();
        Assert.True(Config.Validate(cfg, log));
    }

    [Fact]
    public void Validate_ReturnsFalse_WhenNoSources()
    {
        var cfg = new TicTackConfig();
        var log = new MockLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("No sources"));
    }

    [Fact]
    public void Validate_ReturnsFalse_WhenSourceMissingPath()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Destination = @"D:\dst" });
        var log = new MockLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.Contains("Source path"));
    }

    [Fact]
    public void Validate_ReturnsFalse_WhenSourceMissingDestination()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = @"C:\src" });
        var log = new MockLogger();
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
            var cfg = Config.Load(path);
            Assert.NotNull(cfg);
            Assert.Single(cfg.Sources);
            Assert.Equal(@"C:\src", cfg.Sources[0].Path);
            Assert.Equal(@"D:\dst", cfg.Sources[0].Destination);
            Assert.Equal("hash", cfg.Sources[0].Sync.Verification);
            Assert.Equal(3, cfg.Sources[0].Sync.Retry.MaxAttempts);
            Assert.Equal(100L, Config.ParseFileSizeLimit(cfg.Sources[0].Filter.MaxFileSizeMb));
            Assert.Contains("*.tmp", cfg.Sources[0].Filter.Exclude);
            Assert.Equal("mirror", cfg.Sources[0].Sync.Deletion.Mode);
            Assert.Equal("watcher", cfg.Monitor.Type);
            Assert.Equal("debug", cfg.Logging.Level);
            Assert.False(cfg.Watchdog.Enabled);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ExpandsPathsListIntoMultipleSources()
    {
        var userDir = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
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
            var cfg = Config.Load(path);
            Assert.NotNull(cfg);
            Assert.Equal(2, cfg.Sources.Count);
            Assert.Contains("Desktop", cfg.Sources[0].Path);
            Assert.Contains("Documents", cfg.Sources[1].Path);
            Assert.Contains("Desktop", cfg.Sources[0].Destination);
            Assert.Contains("Documents", cfg.Sources[1].Destination);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_HandlesMissingFileGracefully()
    {
        var result = Config.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.Null(result);
    }

    [Fact]
    public void EnvVars_AreExpanded()
    {
        var yaml = @"
sources:
  - path: '%TESTROOT%\Desktop'
    destination: D:\dst
logging:
  path: '%TESTROOT%\tictack.log'
";
        var path = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable("TESTROOT", "/tmp/tictack_test");
            File.WriteAllText(path, yaml);
            var cfg = Config.Load(path);
            Assert.NotNull(cfg);
            Assert.Equal("/tmp/tictack_test" + @"\Desktop", cfg.Sources[0].Path);
            Assert.Equal("/tmp/tictack_test" + @"\tictack.log", cfg.Logging.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESTROOT", null);
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultValues_AreSet()
    {
        Assert.Equal(10.0, new SourceConfig().DebounceSeconds);
        Assert.Equal("composite", new MonitorConfig().Type);
        Assert.Equal(LogLevel.Info, LogLevelParser.Parse(new LoggingConfig().Level));
        Assert.True(new WatchdogConfig().Enabled);
    }
}
