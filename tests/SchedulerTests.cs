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
    public void JobFailureHint_CmdUnrecognized_NamesToolAndScript()
    {
        var hint = TimerScheduler.JobFailureHint(
            "'restic' is not recognized as an internal or external command", "restic snapshots");
        Assert.NotNull(hint);
        Assert.Contains("restic", hint);
        Assert.Contains("add-tool-env", hint);
    }

    [Fact]
    public void JobFailureHint_ShNotFound_NamesToolAndScript()
    {
        var hint = TimerScheduler.JobFailureHint("sh: 1: restic: not found", "restic snapshots");
        Assert.NotNull(hint);
        Assert.Contains("restic", hint);
        Assert.Contains("add-tool-env", hint);
    }

    [Fact]
    public void JobFailureHint_Unparseable_FallsBackToCommandWord()
    {
        var hint = TimerScheduler.JobFailureHint("command not found", "mytool --full");
        Assert.NotNull(hint);
        Assert.Contains("mytool", hint);
    }

    [Fact]
    public void JobFailureHint_AccessDenied_MentionsServiceAccount()
    {
        var hint = TimerScheduler.JobFailureHint("Access is denied.", "robocopy a b");
        Assert.NotNull(hint);
        Assert.Contains(OperatingSystem.IsWindows() ? "LocalSystem" : "service user", hint);
    }

    [Fact]
    public void JobFailureHint_PlainFailure_ReturnsNull()
    {
        Assert.Null(TimerScheduler.JobFailureHint("backup set incomplete, 3 errors", "restic check"));
        Assert.Null(TimerScheduler.JobFailureHint("", "whatever"));
    }

    [Theory]
    [InlineData("2026-08-29 15:00", "2026-08-27 00:00", "20:00", false)]  // missed 08-28, before time -> stays missed
    [InlineData("2026-08-29 21:00", "2026-08-27 00:00", "20:00", true)]   // today's slot reached -> run
    [InlineData("2026-08-29 21:00", "2026-08-29 00:00", "20:00", false)]  // ran today
    [InlineData("2026-08-29 15:00", "2026-08-28 00:00", "20:00", false)]  // before today's slot
    public void IsDueSkip_RuleMatrix(string nowStr, string lastStr, string timeStr, bool expected)
    {
        var now = DateTime.Parse(nowStr);
        var last = DateTime.Parse(lastStr);
        Assert.Equal(expected, TimerScheduler.IsDueSkip(now, last, TimeSpan.Parse(timeStr)));
    }

    // 2026-08-29 is a Saturday.
    [Theory]
    [InlineData("2026-08-29 10:00", "0001-01-01 00:00", "09:00", "Saturday", true)]   // target day, never run
    [InlineData("2026-08-29 10:00", "2026-08-22 00:00", "09:00", "Saturday", true)]   // last week ran, this week due
    [InlineData("2026-08-29 10:00", "2026-08-29 00:00", "09:00", "Saturday", false)]  // already ran today
    [InlineData("2026-08-29 10:00", "0001-01-01 00:00", "09:00", "Sunday", false)]    // missed Sunday stays missed
    [InlineData("2026-08-29 08:00", "2026-08-22 00:00", "09:00", "Saturday", false)]  // today's time not reached
    public void IsDueWeeklySkip_RuleMatrix(string nowStr, string lastStr, string timeStr, string day, bool expected)
    {
        var now = DateTime.Parse(nowStr);
        var last = DateTime.Parse(lastStr);
        Assert.Equal(expected, TimerScheduler.IsDueWeeklySkip(now, last, TimeSpan.Parse(timeStr), Enum.Parse<DayOfWeek>(day)));
    }

    [Fact]
    public void IsDueWeeklyMask_RuleMatrix()
    {
        var mask = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };
        var time = TimeSpan.Parse("09:00");
        // Sat 08-29: most recent mask occurrence is Fri 08-28 09:00.
        Assert.False(TimerScheduler.IsDueWeeklyMask(DateTime.Parse("2026-08-29 10:00"), DateTime.Parse("2026-08-28 00:00"), time, mask));
        Assert.True(TimerScheduler.IsDueWeeklyMask(DateTime.Parse("2026-08-29 10:00"), DateTime.Parse("2026-08-27 00:00"), time, mask));
        // Mon 08-31 10:00: today's occurrence passed, last ran Friday -> due.
        Assert.True(TimerScheduler.IsDueWeeklyMask(DateTime.Parse("2026-08-31 10:00"), DateTime.Parse("2026-08-28 00:00"), time, mask));
        // Mon 08-31 08:00: today's occurrence not reached, Friday done -> wait.
        Assert.False(TimerScheduler.IsDueWeeklyMask(DateTime.Parse("2026-08-31 08:00"), DateTime.Parse("2026-08-28 00:00"), time, mask));
        // Mask skip: Saturday is not in the mask, never-run still waits.
        Assert.False(TimerScheduler.IsDueWeeklyMaskSkip(DateTime.Parse("2026-08-29 10:00"), DateTime.MinValue, time, mask));
        Assert.True(TimerScheduler.IsDueWeeklyMaskSkip(DateTime.Parse("2026-08-31 10:00"), DateTime.Parse("2026-08-28 00:00"), time, mask));
    }

    [Theory]
    [InlineData("2026-08-15 10:00", "2026-07-15 00:00", 15, true)]   // occurrence day, time reached
    [InlineData("2026-08-16 10:00", "2026-07-15 00:00", 15, false)]  // day passed -> stays missed
    [InlineData("2026-08-15 08:00", "2026-07-15 00:00", 15, false)]  // occurrence day, too early
    [InlineData("2026-02-28 10:00", "2026-01-31 00:00", 31, true)]   // clamped Feb 28th counts
    [InlineData("2026-02-27 10:00", "2026-01-31 00:00", 31, false)]  // day before the clamp
    public void IsDueMonthlySkip_RuleMatrix(string nowStr, string lastStr, int day, bool expected)
    {
        var now = DateTime.Parse(nowStr);
        var last = DateTime.Parse(lastStr);
        Assert.Equal(expected, TimerScheduler.IsDueMonthlySkip(now, last, TimeSpan.Parse("09:00"), day));
    }

    [Theory]
    [InlineData("2026-08-29 10:00", "2026-08-29", true)]   // stamped day, time reached
    [InlineData("2026-08-29 08:00", "2026-08-29", false)]  // stamped day, too early
    [InlineData("2026-08-30 10:00", "2026-08-29", false)]  // date passed -> never fires
    [InlineData("2026-08-28 10:00", "2026-08-29", false)]  // date not reached
    public void IsDueOnceSkip_RuleMatrix(string nowStr, string dateStr, bool expected)
    {
        var now = DateTime.Parse(nowStr);
        Assert.Equal(expected, TimerScheduler.IsDueOnceSkip(now, DateTime.MinValue, TimeSpan.Parse("09:00"), DateTime.Parse(dateStr)));
    }

    [Theory]
    [InlineData(120, 60, true)]   // 2h elapsed, 1h span -> due
    [InlineData(30, 60, false)]   // 30m elapsed, 1h span -> wait
    [InlineData(60, 60, true)]    // exact boundary counts as due
    public void IsDueInterval_RuleMatrix(int elapsedMinutes, int spanMinutes, bool expected)
    {
        var now = DateTime.Now;
        Assert.Equal(expected, TimerScheduler.IsDueInterval(now, now.AddMinutes(-elapsedMinutes), TimeSpan.FromMinutes(spanMinutes)));
    }

    [Fact]
    public void IsDueInterval_NeverRan_IsDue()
    {
        Assert.True(TimerScheduler.IsDueInterval(DateTime.Now, DateTime.MinValue, TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData("mon", DayOfWeek.Monday)]
    [InlineData("Monday", DayOfWeek.Monday)]
    [InlineData("TUES", DayOfWeek.Tuesday)]
    [InlineData("wednesday", DayOfWeek.Wednesday)]
    [InlineData("Thu", DayOfWeek.Thursday)]
    [InlineData("FRI", DayOfWeek.Friday)]
    [InlineData("sat", DayOfWeek.Saturday)]
    [InlineData("Sunday", DayOfWeek.Sunday)]
    public void TryParseWeekday_AcceptsNamesAndAbbreviations(string raw, DayOfWeek expected)
    {
        Assert.True(TimerScheduler.TryParseWeekday(raw, out var day));
        Assert.Equal(expected, day);
    }

    [Theory]
    [InlineData("Funday")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseWeekday_RejectsGarbage(string? raw)
    {
        Assert.False(TimerScheduler.TryParseWeekday(raw, out _));
    }

    [Theory]
    [InlineData("catch_up", TimerScheduler.MissedPolicy.CatchUp)]
    [InlineData("catch-up", TimerScheduler.MissedPolicy.CatchUp)]
    [InlineData("SKIP", TimerScheduler.MissedPolicy.Skip)]
    public void TryParseMissedPolicy_AcceptsTokens(string raw, TimerScheduler.MissedPolicy expected)
    {
        Assert.True(TimerScheduler.TryParseMissedPolicy(raw, out var policy));
        Assert.Equal(expected, policy);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("catch_up_once")]  // cut: two names for one branch was cruft
    [InlineData("once")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseMissedPolicy_RejectsGarbage(string? raw)
    {
        Assert.False(TimerScheduler.TryParseMissedPolicy(raw, out _));
    }

    [Theory]
    [InlineData("skip", TimerScheduler.OverlapPolicy.Skip)]
    [InlineData("QUEUE", TimerScheduler.OverlapPolicy.Queue)]
    public void TryParseOverlapPolicy_AcceptsTokens(string raw, TimerScheduler.OverlapPolicy expected)
    {
        Assert.True(TimerScheduler.TryParseOverlapPolicy(raw, out var policy));
        Assert.Equal(expected, policy);
    }

    [Fact]
    public void DecideOverlap_Matrix()
    {
        var busy = new TaskCompletionSource().Task;
        Assert.Equal(TimerScheduler.OverlapDecision.Launch, TimerScheduler.DecideOverlap(null, false, TimerScheduler.OverlapPolicy.Skip));
        Assert.Equal(TimerScheduler.OverlapDecision.Launch, TimerScheduler.DecideOverlap(Task.CompletedTask, false, TimerScheduler.OverlapPolicy.Queue));
        Assert.Equal(TimerScheduler.OverlapDecision.Skip, TimerScheduler.DecideOverlap(busy, false, TimerScheduler.OverlapPolicy.Skip));
        Assert.Equal(TimerScheduler.OverlapDecision.Chain, TimerScheduler.DecideOverlap(busy, false, TimerScheduler.OverlapPolicy.Queue));
        Assert.Equal(TimerScheduler.OverlapDecision.Skip, TimerScheduler.DecideOverlap(busy, true, TimerScheduler.OverlapPolicy.Queue));
    }

    [Fact]
    public void EffectiveTimeout_ResolutionOrder()
    {
        Assert.Equal(45, TimerScheduler.EffectiveTimeout(new JobConfig { TimeoutMinutes = 45 }, new JobsDefaultsConfig { TimeoutMinutes = 30 }));
        Assert.Equal(30, TimerScheduler.EffectiveTimeout(new JobConfig(), new JobsDefaultsConfig { TimeoutMinutes = 30 }));
        Assert.Equal(10, TimerScheduler.EffectiveTimeout(new JobConfig(), null));
        Assert.Equal(10, TimerScheduler.EffectiveTimeout(new JobConfig(), new JobsDefaultsConfig()));
        Assert.Equal(10, TimerScheduler.EffectiveTimeout(new JobConfig(), new JobsDefaultsConfig { TimeoutMinutes = -5 }));
    }

    [Fact]
    public void TriggerConflicts_SkipJobWithWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_jobconflict_" + Guid.NewGuid());
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
                new JobConfig { Name = "both", Every = "1h", Time = "00:00", Command = "echo x" },
                new JobConfig { Name = "mask", Schedule = "weekly", Day = "Monday", Days = new List<string> { "tue" }, Time = "00:00", Command = "echo x" },
            }
        };
        var scheduler = new TimerScheduler(cfg, log);
        try
        {
            Assert.Contains(log.Messages, m => m.Contains("Job 'both'") && m.Contains("every is mutually exclusive"));
            Assert.Contains(log.Messages, m => m.Contains("Job 'mask'") && m.Contains("day and days are mutually exclusive"));
        }
        finally
        {
            scheduler.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ExplicitDaily_StillAccepted()
    {
        // schedule: daily is redundant (time alone means daily) but must keep
        // working so old configs do not warn.
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_jobdaily_" + Guid.NewGuid());
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
                new JobConfig { Name = "daily", Schedule = "daily", Time = "00:00", Command = "echo x", WorkingDir = dir }
            }
        };
        var scheduler = new TimerScheduler(cfg, log);
        try
        {
            Assert.DoesNotContain(log.Messages, m => m.Contains("Skipping") || m.Contains("Invalid"));
        }
        finally
        {
            scheduler.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task FreshStore_IntervalJob_WaitsOneInterval()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_jobinterval_" + Guid.NewGuid());
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
                new JobConfig { Name = "hourly", Every = "1h", Command = "echo should-not-run", WorkingDir = dir }
            }
        };
        var scheduler = new TimerScheduler(cfg, log);
        try
        {
            scheduler.Start();
            await Task.Delay(2000);
            Assert.DoesNotContain(log.Messages, m => m.Contains("Running job"));
            Assert.DoesNotContain(log.Messages, m => m.Contains("Skipping incomplete"));
            using var db = new JobRunStore(Path.Combine(dir, "state.db", "jobs.db"));
            var seeded = db.GetLastRunAt("hourly");
            Assert.NotNull(seeded);
            Assert.True((DateTime.Now - seeded.Value).TotalMinutes < 2);
        }
        finally
        {
            scheduler.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static TicTackConfig HookCfg(string dir, JobConfig job) => new TicTackConfig
    {
        Sources = new List<SourceConfig>
        {
            new SourceConfig { Path = dir, Destination = dir, StateDbPath = Path.Combine(dir, "state.db") }
        },
        Jobs = new List<JobConfig> { job }
    };

    static void PreseedDaily(string dir, string name)
    {
        using var seed = new JobRunStore(Path.Combine(dir, "state.db", "jobs.db"));
        seed.SetLastRun(name, DateTime.Today.AddDays(-1));
    }

    [Fact]
    public async Task Hooks_PreFailure_GatesMain_RunsOnFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_jobhooks_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var log = new RecordingLogger();
        PreseedDaily(dir, "gated");
        var scheduler = new TimerScheduler(HookCfg(dir, new JobConfig
        {
            Name = "gated",
            Time = "00:00",
            Command = "echo main > main.txt",
            PreCommand = "exit 1",
            PostCommand = "echo post > post.txt",
            OnFailure = "echo failed > failed.txt",
            WorkingDir = dir
        }), log);
        try
        {
            scheduler.Start();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(dir, "failed.txt")) && DateTime.UtcNow < deadline)
                await Task.Delay(200);
            Assert.True(File.Exists(Path.Combine(dir, "failed.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "main.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "post.txt")));
            Assert.Contains(log.Messages, m => m.Contains("pre-command") && m.Contains("1"));
        }
        finally
        {
            scheduler.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Hooks_Success_RunsPost_SkipsOnFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_jobhooks_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var log = new RecordingLogger();
        PreseedDaily(dir, "hooked");
        var scheduler = new TimerScheduler(HookCfg(dir, new JobConfig
        {
            Name = "hooked",
            Time = "00:00",
            Command = "echo main > main.txt",
            PreCommand = "echo pre",
            PostCommand = "echo post > post.txt",
            OnFailure = "echo failed > failed.txt",
            WorkingDir = dir
        }), log);
        try
        {
            scheduler.Start();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(dir, "post.txt")) && DateTime.UtcNow < deadline)
                await Task.Delay(200);
            Assert.True(File.Exists(Path.Combine(dir, "main.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "post.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "failed.txt")));
        }
        finally
        {
            scheduler.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
