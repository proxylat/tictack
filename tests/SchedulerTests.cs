using System;
using System.IO;
using Xunit;

namespace TicTack;

public class SchedulerTests
{
    [Theory]
    [InlineData("2026-08-29 15:00", "0001-01-01 00:00", "20:00", true)]   // never run, before time -> catch-up
    [InlineData("2026-08-29 21:00", "0001-01-01 00:00", "20:00", true)]   // never run, after time -> run
    [InlineData("2026-08-29 15:00", "2026-08-29 00:00", "20:00", false)]  // ran today, before time
    [InlineData("2026-08-29 21:00", "2026-08-29 00:00", "20:00", false)]  // ran today, after time
    [InlineData("2026-08-29 15:00", "2026-08-28 00:00", "20:00", false)]  // ran yesterday, before time (normal wait)
    [InlineData("2026-08-29 21:00", "2026-08-28 00:00", "20:00", true)]   // ran yesterday, after time
    [InlineData("2026-08-29 15:00", "2026-08-27 00:00", "20:00", true)]   // missed 08-28, before time -> catch-up
    public void IsDue_RuleMatrix(string nowStr, string lastStr, string timeStr, bool expected)
    {
        var now = DateTime.Parse(nowStr);
        var last = DateTime.Parse(lastStr);
        var time = TimeSpan.Parse(timeStr);
        Assert.Equal(expected, TimerScheduler.IsDue(now, last, time));
    }

    [Fact]
    public async Task RunJob_DrainsLargeOutput_WithoutDeadlock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_job_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var log = new RecordingLogger();
        var cfg = new TicTackConfig
        {
            Sources = new List<SourceConfig>
            {
                new SourceConfig { Path = dir, Destination = dir, StateDbPath = Path.Combine(dir, "state.db") }
            },
            Jobs = new List<JobConfig>
            {
                new JobConfig
                {
                    Name = "spam",
                    Time = "00:00",
                    Command = OperatingSystem.IsWindows() ? "type big.txt" : "cat big.txt",
                    WorkingDir = dir
                }
            }
        };
        var scheduler = new TimerScheduler(cfg, log);
        try
        {
            // 8 MB is past any platform's pipe buffer: the job blocks on
            // write unless RunJob drains stdout while waiting for exit.
            File.WriteAllText(Path.Combine(dir, "big.txt"), new string('x', 8 * 1024 * 1024));
            scheduler.Start();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!log.Messages.Any(m => m.Contains("completed")) && DateTime.UtcNow < deadline)
                await Task.Delay(200);
            Assert.Contains(log.Messages, m => m.Contains("completed"));
            Assert.DoesNotContain(log.Messages, m => m.Contains("timed out"));
        }
        finally
        {
            scheduler.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void JobRunStore_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_jobs_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var db = new JobRunStore(Path.Combine(dir, "jobs.db"));
        try
        {
            Assert.Null(db.GetLastRun("missing"));
            db.SetLastRun("backup", new DateTime(2026, 8, 29));
            Assert.Equal(new DateTime(2026, 8, 29), db.GetLastRun("backup"));
            db.SetLastRun("backup", new DateTime(2026, 8, 30)); // overwrite
            Assert.Equal(new DateTime(2026, 8, 30), db.GetLastRun("backup"));
        }
        finally
        {
            db.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
