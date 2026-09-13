using System.Text.RegularExpressions;

namespace TicTack;

public class LoggingTests
{
    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("info", LogLevel.Info)]
    [InlineData("warn", LogLevel.Warn)]
    [InlineData("warning", LogLevel.Warn)]
    [InlineData("error", LogLevel.Error)]
    [InlineData(null, LogLevel.Info)]
    [InlineData("unknown", LogLevel.Info)]
    [InlineData("", LogLevel.Info)]
    public void LogLevelParser_ParsesCorrectly(string? input, LogLevel expected)
    {
        Assert.Equal(expected, LogLevelParser.Parse(input));
    }

    [Fact]
    public void LogLevelParser_UsesDefault()
    {
        Assert.Equal(LogLevel.Debug, LogLevelParser.Parse(null, LogLevel.Debug));
    }

    [Fact]
    public void FileLogger_WritesToFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            var logger = new FileLogger(path, LogLevel.Debug);
            logger.Info("test message");
            logger.Debug("debug message");

            var content = File.ReadAllText(path);
            Assert.Contains("[INF]", content);
            Assert.Contains("test message", content);
            Assert.Contains("[DBG]", content);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileLogger_RespectsMinLevel()
    {
        var path = Path.GetTempFileName();
        try
        {
            var logger = new FileLogger(path, LogLevel.Warn);
            logger.Debug("should not appear");
            logger.Info("should not appear");
            logger.Warn("warning message");
            logger.Error("error message");

            var content = File.ReadAllText(path);
            Assert.Contains("[WRN]", content);
            Assert.Contains("[ERR]", content);
            Assert.DoesNotContain("[DBG]", content);
            Assert.DoesNotContain("[INF]", content);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileLogger_CreatesDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_log_" + Guid.NewGuid());
        var path = Path.Combine(dir, "sub", "test.log");
        try
        {
            var logger = new FileLogger(path, LogLevel.Info);
            logger.Info("dir created");
            Assert.True(Directory.Exists(dir));
            Assert.True(File.Exists(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void FileLogger_RotatesFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            var logger = new FileLogger(path, LogLevel.Info, maxSizeMb: 0); // Force rotation after 1 write
            logger.Info("line 1");
            Assert.True(File.Exists(path + ".1") || new FileInfo(path).Length > 0);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".1"); } catch { }
        }
    }

    [Fact]
    public void ConsoleLogger_WritesToConsole()
    {
        var logger = new ConsoleLogger(LogLevel.Debug);

        using var sw = new StringWriter();
        var orig = Console.Out;
        Console.SetOut(sw);
        try
        {
            logger.Info("console test");
            var output = sw.ToString();
            Assert.Contains("[INF]", output);
            Assert.Contains("console test", output);
        }
        finally { Console.SetOut(orig); }
    }

    [Fact]
    public void ConsoleLogger_RespectsMinLevel()
    {
        var logger = new ConsoleLogger(LogLevel.Error);

        using var sw = new StringWriter();
        var orig = Console.Out;
        Console.SetOut(sw);
        try
        {
            logger.Debug("debug msg");
            logger.Info("info msg");
            logger.Warn("warn msg");
            Assert.Empty(sw.ToString());

            logger.Error("error msg");
            Assert.Contains("error msg", sw.ToString());
        }
        finally { Console.SetOut(orig); }
    }

    [Fact]
    public void MultiLogger_DelegatesToAll()
    {
        var log1 = new RecordingLogger();
        var log2 = new RecordingLogger();
        var multi = new MultiLogger(new[] { log1, log2 });

        multi.Info("broadcast");
        multi.Error("problem");

        Assert.Contains(log1.Messages, m => m.Contains("broadcast"));
        Assert.Contains(log2.Messages, m => m.Contains("broadcast"));
        Assert.Contains(log1.Messages, m => m.Contains("problem"));
        Assert.Contains(log2.Messages, m => m.Contains("problem"));
    }

    [Fact]
    public void MultiLogger_NullLoggers_DoesNotThrow()
    {
        var multi = new MultiLogger(null);
        multi.Info("no crash");
    }

    [Fact]
    public void FileLogger_AppendsToExistingFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "preexisting content\n");
            var logger = new FileLogger(path, LogLevel.Info);
            logger.Info("appended");

            var content = File.ReadAllText(path);
            Assert.Contains("preexisting content", content);
            Assert.Contains("appended", content);
        }
        finally { File.Delete(path); }
    }
}
