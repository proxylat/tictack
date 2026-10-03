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

    // 2026-08-29 is a Saturday.
    [Theory]
    [InlineData("2026-08-29 10:00", "0001-01-01 00:00", "09:00", "Saturday", true)]   // target day, never run
    [InlineData("2026-08-29 10:00", "2026-08-22 00:00", "09:00", "Saturday", true)]   // last week ran, this week due
    [InlineData("2026-08-29 10:00", "2026-08-29 00:00", "09:00", "Saturday", false)]  // already ran today
    [InlineData("2026-08-29 10:00", "2026-08-22 00:00", "09:00", "Sunday", true)]     // missed Sunday 08-23 -> catch-up
    [InlineData("2026-08-30 10:00", "2026-08-22 00:00", "09:00", "Saturday", true)]   // Sunday, Saturday occurrence passed
    [InlineData("2026-09-12 10:00", "2026-08-22 00:00", "09:00", "Saturday", true)]   // two missed weeks -> catch-up
    [InlineData("2026-08-29 10:00", "2026-08-22 21:00", "20:00", "Saturday", false)]  // last week done, today's time not reached
    public void IsDueWeekly_RuleMatrix(string nowStr, string lastStr, string timeStr, string day, bool expected)
    {
        var now = DateTime.Parse(nowStr);
        var last = DateTime.Parse(lastStr);
        Assert.Equal(expected, TimerScheduler.IsDueWeekly(now, last, TimeSpan.Parse(timeStr), Enum.Parse<DayOfWeek>(day)));
    }

    [Theory]
    [InlineData("2026-08-29 10:00", "0001-01-01 00:00", 15, true)]   // never run, 15th passed
    [InlineData("2026-08-29 10:00", "2026-08-15 10:00", 15, false)]  // ran after the 15th's slot
    [InlineData("2026-08-14 10:00", "2026-07-15 10:00", 15, false)]  // July done, August 15th not reached
    [InlineData("2026-08-16 10:00", "2026-07-15 00:00", 15, true)]   // 15th passed, July ran
    [InlineData("2026-02-28 10:00", "2026-01-31 00:00", 31, true)]   // 31 clamps to Feb 28th
    [InlineData("2026-02-27 10:00", "2026-01-31 10:00", 31, false)]  // January done, clamped Feb 28th not reached
    public void IsDueMonthly_RuleMatrix(string nowStr, string lastStr, int day, bool expected)
    {
        var now = DateTime.Parse(nowStr);
        var last = DateTime.Parse(lastStr);
        Assert.Equal(expected, TimerScheduler.IsDueMonthly(now, last, TimeSpan.Parse("09:00"), day));
    }

    [Theory]
    [InlineData("2026-08-29 10:00", "2026-08-29", true)]    // date reached, never ran
    [InlineData("2026-08-28 10:00", "2026-08-29", false)]   // date not reached
    [InlineData("2026-08-30 10:00", "2026-08-29", true)]    // past date, never ran -> catch-up
    [InlineData("2026-08-30 10:00", "2026-08-29", false, "2026-08-30 00:00")] // already ran
    public void IsDueOnce_RuleMatrix(string nowStr, string dateStr, bool expected, string? lastStr = null)
    {
        var now = DateTime.Parse(nowStr);
        var last = lastStr == null ? DateTime.MinValue : DateTime.Parse(lastStr);
        Assert.Equal(expected, TimerScheduler.IsDueOnce(now, last, TimeSpan.Parse("09:00"), DateTime.Parse(dateStr)));
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
        // Fresh-store seeding (missing history = today) would hold this
        // job until its next slot — pre-seed yesterday so it runs now.
        using (var seed = new JobRunStore(Path.Combine(dir, "state.db", "jobs.db")))
            seed.SetLastRun("spam", DateTime.Today.AddDays(-1));
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
    public async Task FreshStore_DailyJob_WaitsForNextSlot()
    {
        // Fresh jobs.db (first install or post-quarantine rebuild) must
        // not fire periodic jobs on the first tick: missing history seeds
        // to today, and the job waits for its next scheduled time.
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_jobseed_" + Guid.NewGuid());
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
                    Name = "nightly",
                    Time = "00:00",
                    Command = "echo should-not-run",
                    WorkingDir = dir
                }
            }
        };
        var scheduler = new TimerScheduler(cfg, log);
        try
        {
            scheduler.Start();
            await Task.Delay(2000);
            Assert.DoesNotContain(log.Messages, m => m.Contains("Running job"));
            // The seed persisted: the next start reads history, not MinValue.
            using var db = new JobRunStore(Path.Combine(dir, "state.db", "jobs.db"));
            var seeded = db.GetLastRun("nightly");
            Assert.True(seeded == DateTime.Today || seeded == DateTime.Today.AddDays(-1));
        }
        finally
        {
            scheduler.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task FreshStore_OnceJob_StillCatchesUp()
    {
        // Carve-out: a reached run_once_on with no record has never fired,
        // so it must run on the first tick — seeding it would skip it forever.
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_jobonce_" + Guid.NewGuid());
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
                    Name = "migrate",
                    Time = "00:00",
                    RunOnceOn = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"),
                    Command = "echo once",
                    WorkingDir = dir
                }
            }
        };
        var scheduler = new TimerScheduler(cfg, log);
        try
        {
            scheduler.Start();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!log.Messages.Any(m => m.Contains("completed")) && DateTime.UtcNow < deadline)
                await Task.Delay(200);
            Assert.Contains(log.Messages, m => m.Contains("completed"));
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

    [Fact]
    public void TimeoutMessage_ReportsTheConfiguredMinutes()
    {
        Assert.Equal("Job 'backup' timed out after 10 minutes, killed",
            TimerScheduler.TimeoutMessage("backup", 10));
        Assert.Equal("Job 'workbck' timed out after 45 minutes, killed",
            TimerScheduler.TimeoutMessage("workbck", 45));
    }
}
