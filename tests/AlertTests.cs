namespace TicTack;

public class AlertTests : IDisposable
{
    public void Dispose() => DesktopAlert.Configure(null);

    [Fact]
    public void Write_CreatesFileThenCoolsDown()
    {
        // Single method: DesktopAlert has a process-wide 30 s cooldown, so a
        // second test calling Write could suppress this one's file. The logger
        // gate test below never reaches Write and stays independent.
        var dir = Path.Combine(Path.GetTempPath(), "tictack-alert-" + Guid.NewGuid().ToString("N"));
        var marker = "marker-" + Guid.NewGuid();
        try
        {
            DesktopAlert.Configure(dir);
            DesktopAlert.Write("WARN", marker);

            var files = Directory.GetFiles(dir);
            var file = Assert.Single(files);
            Assert.Contains(marker, File.ReadAllText(file));

            DesktopAlert.Write("WARN", "second-" + Guid.NewGuid());
            // Same-second writes share a filename, so a dead cooldown would
            // overwrite rather than add: content must still be the first write.
            Assert.Single(Directory.GetFiles(dir));
            Assert.Contains(marker, File.ReadAllText(file));

            DesktopAlert.Configure(null);
            DesktopAlert.Write("WARN", "nowhere"); // no-throw without a path
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void DesktopAlertLogger_BelowMinLevel_WritesNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tictack-alertlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new DesktopAlertLogger(LogLevel.Error, dir);
            logger.Warn("suppressed by level gate");

            Assert.False(Directory.Exists(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
