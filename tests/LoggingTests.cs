using System.Text.RegularExpressions;

namespace TicTack;

// Console.SetOut is process-global: serialize against every other collection
// so a parallel test class cannot write into (or read from) the redirected
// writer.
[CollectionDefinition("SerialConsole", DisableParallelization = true)]
public sealed class SerialConsoleCollection { }

[Collection("SerialConsole")]
public class LoggingTests
{
    [Theory]
    [InlineData("debug", LogLevel.Info, LogLevel.Debug)]
    [InlineData("info", LogLevel.Info, LogLevel.Info)]
    [InlineData("warn", LogLevel.Info, LogLevel.Warn)]
    [InlineData("warning", LogLevel.Info, LogLevel.Warn)]
    [InlineData("error", LogLevel.Info, LogLevel.Error)]
    [InlineData(null, LogLevel.Info, LogLevel.Info)]
    [InlineData("unknown", LogLevel.Info, LogLevel.Info)]
    [InlineData("", LogLevel.Info, LogLevel.Info)]
    [InlineData(null, LogLevel.Debug, LogLevel.Debug)]
    public void LogLevelParser_ParsesCorrectly(string? input, LogLevel defaultLevel, LogLevel expected)
    {
        Assert.Equal(expected, LogLevelParser.Parse(input, defaultLevel));
    }

    [Fact]
    public void FileLogger_WritesToFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var logger = new FileLogger(path, LogLevel.Debug))
            {
                logger.Info("test message");
                logger.Debug("debug message");
            }

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
            using (var logger = new FileLogger(path, LogLevel.Warn))
            {
                logger.Debug("should not appear");
                logger.Info("should not appear");
                logger.Warn("warning message");
                logger.Error("error message");
            }

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
            using (var logger = new FileLogger(path, LogLevel.Info))
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
            using (var logger = new FileLogger(path, LogLevel.Info, maxSizeMb: 0))
            {
                logger.Info("line 1"); // appends to the empty temp file, no rotation
                logger.Info("line 2"); // existing length > 0: rotates, then appends
            }
            Assert.True(File.Exists(path + ".1"));
            Assert.Contains("line 1", File.ReadAllText(path + ".1"));
            Assert.Contains("line 2", File.ReadAllText(path));
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
        Assert.Null(Record.Exception(() => multi.Info("no crash")));
    }

    [Fact]
    public void FileLogger_AppendsToExistingFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "preexisting content\n");
            using (var logger = new FileLogger(path, LogLevel.Info))
                logger.Info("appended");

            var content = File.ReadAllText(path);
            Assert.Contains("preexisting content", content);
            Assert.Contains("appended", content);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BufferedLogger_FlushesOnDispose()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var logger = new BufferedLogger(new FileLogger(path, LogLevel.Debug)))
                logger.Info("buffered message");

            Assert.Contains("buffered message", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileLogger_CollapsesConsecutiveRepeats()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var logger = new FileLogger(path, LogLevel.Debug))
            {
                logger.Warn("same storm");
                logger.Warn("same storm");
                logger.Warn("same storm");
                logger.Info("after");
            }

            var content = File.ReadAllText(path);
            // First occurrence + one closing count line; the two middle
            // repeats never hit disk.
            Assert.Equal(2, Regex.Matches(content, "same storm").Count);
            Assert.Contains("(repeated 2x)", content);
            Assert.Contains("after", content);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileLogger_FlushesStreakOnDispose()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var logger = new FileLogger(path, LogLevel.Debug))
            {
                logger.Warn("tail storm");
                logger.Warn("tail storm");
            }
            Assert.Contains("(repeated 1x)", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DailyBackupLogger_SameDay_AppendsWithoutRolling()
    {
        var path = Path.GetTempFileName();
        try
        {
            var logger = new DailyBackupLogger(path, LogLevel.Info);
            logger.Info("a");
            logger.Info("b");

            Assert.False(File.Exists(path + ".1"));
            var content = File.ReadAllText(path);
            Assert.Contains("a", content);
            Assert.Contains("b", content);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DailyBackupLogger_NewDay_RollsPreviousToBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_bak_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tictack.log.bak");
        try
        {
            File.WriteAllText(path, "yesterday\n");
            File.SetLastWriteTime(path, DateTime.Now.AddDays(-1));

            var logger = new DailyBackupLogger(path, LogLevel.Info);
            logger.Info("today");

            Assert.True(File.Exists(path + ".1"));
            Assert.Contains("yesterday", File.ReadAllText(path + ".1"));
            Assert.Contains("today", File.ReadAllText(path));
            Assert.DoesNotContain("yesterday", File.ReadAllText(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void FileLogger_HeldHandle_NoLossOrDupAcrossRotations()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var logger = new FileLogger(path, LogLevel.Info, maxSizeMb: 0, maxFiles: 10))
                for (int i = 0; i < 5; i++)
                    logger.Info("held-line-" + i);

            var all = File.ReadAllText(path);
            for (int i = 1; i <= 10; i++)
                try { if (File.Exists(path + "." + i)) all += File.ReadAllText(path + "." + i); } catch { }
            for (int i = 0; i < 5; i++)
                Assert.Equal(1, Regex.Matches(all, "held-line-" + i).Count);
        }
        finally
        {
            for (int i = 0; i <= 10; i++)
                try { File.Delete(i == 0 ? path : path + "." + i); } catch { }
        }
    }

    [Fact]
    public void FileLogger_Dispose_FlushesBufferedContent()
    {
        var path = Path.GetTempFileName();
        try
        {
            var logger = new FileLogger(path, LogLevel.Debug);
            logger.Info("buffered tiny line"); // far under any flush threshold
            logger.Dispose();
            Assert.Contains("buffered tiny line", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileLogger_DisposeThenWrite_Reopens()
    {
        var path = Path.GetTempFileName();
        try
        {
            var logger = new FileLogger(path, LogLevel.Debug);
            logger.Info("before dispose");
            logger.Dispose();
            logger.Info("after dispose");
            logger.Dispose();
            var content = File.ReadAllText(path);
            Assert.Contains("before dispose", content);
            Assert.Contains("after dispose", content);
        }
        finally { File.Delete(path); }
    }
}
