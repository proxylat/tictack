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
