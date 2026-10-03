using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public class TimerScheduler
    {
        private Timer? _timer;
        private readonly List<JobEntry> _jobs;
        private readonly ILogger _log;
        private readonly string? _sourcePaths;
        private readonly JobRunStore? _store;
        private readonly List<Task> _running = new List<Task>();
        private readonly object _runningLock = new object();
        private CancellationTokenSource? _cts;

        public TimerScheduler(TicTackConfig config, ILogger log)
        {
            _log = log;
            _jobs = new List<JobEntry>();

            if (config.Jobs == null || config.Jobs.Count == 0)
            {
                _store = null;
                return;
            }

            if (config.Sources != null && config.Sources.Count > 0)
                _sourcePaths = string.Join(" ", config.Sources.ConvertAll(s => "\"" + s.Path + "\""));

            _store = CreateStore(config);

            foreach (var job in config.Jobs)
            {
                if (string.IsNullOrEmpty(job.Name) || string.IsNullOrEmpty(job.Time) || string.IsNullOrEmpty(job.Command))
                {
                    log.Warn("Skipping incomplete job '" + (job.Name ?? "<unnamed>") + "'");
                    continue;
                }
                if (!TimeSpan.TryParse(job.Time, out var ts))
                {
                    log.Warn("Invalid time '" + job.Time + "' for job '" + job.Name + "'");
                    continue;
                }
                if (job.TimeoutMinutes <= 0)
                {
                    log.Warn("Invalid timeout_minutes '" + job.TimeoutMinutes + "' for job '" + job.Name + "'");
                    continue;
                }
                var entry = new JobEntry { Config = job, TimeOfDay = ts };
                if (!ParseSchedule(job, entry, log)) continue;
                var last = _store.GetLastRun(job.Name!);
                if (last == null && entry.Kind != JobKind.Once)
                {
                    // Fresh store (first install or post-quarantine rebuild):
                    // no history would read as overdue-since-forever and
                    // fire every periodic job on the first tick. Seed today
                    // instead — the job waits for its next scheduled slot.
                    // One-shots keep MinValue: a reached run_once_on with no
                    // record has never fired and must still catch up.
                    entry.LastRunOn = DateTime.Today;
                    try { _store.SetLastRun(job.Name!, DateTime.Today); }
                    catch (Exception ex) { log.Debug("Job seed failed for '" + job.Name + "': " + ex.Message); }
                    log.Info("Job '" + job.Name + "' has no run history, waiting for its next scheduled time");
                }
                else
                {
                    entry.LastRunOn = last ?? DateTime.MinValue;
                }
                _jobs.Add(entry);
            }
        }

        private JobRunStore CreateStore(TicTackConfig config)
        {
            string dir;
            // StateDbPath is a directory root (GetStateDbPath appends
            // "<sourcename>.db"), so use it directly — GetDirectoryName
            // would strip it and land jobs.db one level too high.
            if (config.Sources != null && config.Sources.Count > 0 && !string.IsNullOrEmpty(config.Sources[0].StateDbPath))
                dir = config.Sources[0].StateDbPath;
            else
                dir = AppContext.BaseDirectory;
            var dbPath = Path.Combine(dir, "jobs.db");
            try
            {
                return new JobRunStore(dbPath);
            }
            catch (CorruptDatabaseException ex)
            {
                // Bootstrap already moved the bad file aside; reopening
                // creates a fresh store. Missing rows seed to today in the
                // ctor below, so periodic jobs wait for their next slot
                // instead of all firing at startup — only past-due one-shots
                // catch up. Skipped runs, never duplicate destruction.
                _log.Error("Job store corrupt, quarantined (" + ex.QuarantinePath + "), rebuilding: " + dbPath);
                return new JobRunStore(dbPath);
            }
        }

        // Run if not already run today AND (time reached today OR at least one full day was missed).
        public static bool IsDue(DateTime now, DateTime lastRun, TimeSpan timeOfDay)
        {
            var today = now.Date;
            if (lastRun >= today) return false;
            if (now.TimeOfDay >= timeOfDay) return true;
            if (lastRun < today.AddDays(-1)) return true;
            return false;
        }

        // Weekly: due when the most recent (weekday, time) occurrence is
        // newer than the last run. Missed weeks stay due via catch-up.
        // Date granularity (like the daily rule): the store stamps days,
        // so a same-day run satisfies today's slot and restarts can't
        // double-fire it.
        public static bool IsDueWeekly(DateTime now, DateTime lastRun, TimeSpan timeOfDay, DayOfWeek target)
        {
            var daysBack = ((int)now.DayOfWeek - (int)target + 7) % 7;
            var occurrence = now.Date.AddDays(-daysBack).Add(timeOfDay);
            if (occurrence > now) occurrence = occurrence.AddDays(-7);
            return lastRun.Date < occurrence.Date;
        }

        // Monthly: target day clamped to short months (31 runs on the
        // 30th/28th). Same most-recent-occurrence rule as weekly.
        public static bool IsDueMonthly(DateTime now, DateTime lastRun, TimeSpan timeOfDay, int dayOfMonth)
        {
            var occurrence = ClampToMonth(now.Year, now.Month, dayOfMonth).Add(timeOfDay);
            if (occurrence > now)
            {
                var prev = now.AddMonths(-1);
                occurrence = ClampToMonth(prev.Year, prev.Month, dayOfMonth).Add(timeOfDay);
            }
            return lastRun.Date < occurrence.Date;
        }

        private static DateTime ClampToMonth(int year, int month, int dayOfMonth) =>
            new DateTime(year, month, Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month)));

        // One-shot: due once the stamped date+time passes, never again after
        // a successful run stamps the store. Failed runs retry next tick.
        public static bool IsDueOnce(DateTime now, DateTime lastRun, TimeSpan timeOfDay, DateTime runDate) =>
            lastRun == DateTime.MinValue && now >= runDate.Date.Add(timeOfDay);

        // Schedule/day/run_once_on validation. Returns false (skip job) on
        // contradictory or unparseable cadence config.
        private static bool ParseSchedule(JobConfig job, JobEntry entry, ILogger log)
        {
            var schedule = (job.Schedule ?? "daily").Trim().ToLowerInvariant();
            var hasOnce = !string.IsNullOrWhiteSpace(job.RunOnceOn);
            if (hasOnce && (schedule != "daily" || !string.IsNullOrWhiteSpace(job.Day)))
            {
                log.Warn("Job '" + job.Name + "': run_once_on is mutually exclusive with schedule/day, skipping");
                return false;
            }
            if (schedule == "daily" && !hasOnce)
            {
                if (!string.IsNullOrWhiteSpace(job.Day))
                    log.Warn("Job '" + job.Name + "': day is ignored for daily schedules");
                entry.Kind = JobKind.Daily;
                return true;
            }
            if (schedule == "weekly")
            {
                if (!Enum.TryParse<DayOfWeek>(job.Day?.Trim(), ignoreCase: true, out var weekday))
                {
                    log.Warn("Invalid day '" + job.Day + "' for weekly job '" + job.Name + "' (use Monday-Sunday)");
                    return false;
                }
                entry.Kind = JobKind.Weekly;
                entry.TargetDay = weekday;
                return true;
            }
            if (schedule == "monthly")
            {
                if (!int.TryParse(job.Day?.Trim(), out var dom) || dom < 1 || dom > 31)
                {
                    log.Warn("Invalid day '" + job.Day + "' for monthly job '" + job.Name + "' (use 1-31)");
                    return false;
                }
                entry.Kind = JobKind.Monthly;
                entry.DayOfMonth = dom;
                return true;
            }
            if (schedule == "once" || hasOnce)
            {
                if (!DateTime.TryParse(job.RunOnceOn?.Trim(), out var date))
                {
                    log.Warn("Invalid run_once_on '" + job.RunOnceOn + "' for job '" + job.Name + "' (use YYYY-MM-DD)");
                    return false;
                }
                entry.Kind = JobKind.Once;
                entry.RunDate = date.Date;
                return true;
            }
            log.Warn("Invalid schedule '" + job.Schedule + "' for job '" + job.Name + "' (use daily/weekly/monthly/once)");
            return false;
        }

        private static bool EntryIsDue(DateTime now, JobEntry job) => job.Kind switch
        {
            JobKind.Weekly => IsDueWeekly(now, job.LastRunOn, job.TimeOfDay, job.TargetDay),
            JobKind.Monthly => IsDueMonthly(now, job.LastRunOn, job.TimeOfDay, job.DayOfMonth),
            JobKind.Once => IsDueOnce(now, job.LastRunOn, job.TimeOfDay, job.RunDate),
            _ => IsDue(now, job.LastRunOn, job.TimeOfDay),
        };

        public void Start()
        {
            if (_jobs.Count == 0) return;
            _cts = new CancellationTokenSource();
            _log.Info("Scheduler started with " + _jobs.Count + " job(s)");
            _timer = new Timer(RunDueJobs, null, 0, 30000);
        }

        private void RunDueJobs(object? state)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            var now = DateTime.Now;
            foreach (var job in _jobs)
            {
                if (EntryIsDue(now, job))
                {
                    job.LastRunOn = now.Date; // in-memory guard against same-day re-trigger
                    // Deliberate fire-and-forget: daily jobs must not block the
                    // timer. Tracked so Stop() can cancel and wait instead of
                    // letting a job race into a disposed store/logger.
#pragma warning disable MA0134 // Observe result of async calls
                    var task = System.Threading.Tasks.Task.Run(() => RunJob(job));
#pragma warning restore MA0134
                    lock (_runningLock)
                    {
                        _running.Add(task);
                        _running.RemoveAll(t => t.IsCompleted);
                    }
                }
            }
        }

        private async Task RunJob(JobEntry job)
        {
            try
            {
                var cmd = job.Config.Command!;
                if (_sourcePaths != null)
                    cmd = cmd.Replace("{source}", _sourcePaths);

                var wd = job.Config.WorkingDir ?? AppDomain.CurrentDomain.BaseDirectory;
                _log.Info("Running job '" + job.Config.Name + "': " + cmd);

                var (exitCode, _, err, timedOut) = await ProcessRunner.RunAsync(
                    cmd, wd, redirect: true, _cts?.Token ?? CancellationToken.None,
                    timeoutMs: (job.Config.TimeoutMinutes > 0 ? job.Config.TimeoutMinutes : 10) * 60000).ConfigureAwait(false);

                if (timedOut)
                {
                    _log.Error(TimeoutMessage(job.Config.Name, job.Config.TimeoutMinutes > 0 ? job.Config.TimeoutMinutes : 10));
                    return;
                }
                if (exitCode != 0)
                {
                    _log.Error("Job '" + job.Config.Name + "' exited " + exitCode + ": " + err);
                }
                else
                {
                    _log.Info("Job '" + job.Config.Name + "' completed");
                    try { _store?.SetLastRun(job.Config.Name!, DateTime.Today); }
                    catch (Exception ex) { _log.Error("Failed to persist run state for job '" + job.Config.Name + "'", ex); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Error("Scheduled job '" + job.Config.Name + "'", ex);
            }
        }

        internal static string TimeoutMessage(string? jobName, int timeoutMinutes) =>
            "Job '" + jobName + "' timed out after " + timeoutMinutes + " minutes, killed";

        public void Stop()
        {
            if (_timer != null)
            {
                _timer.Dispose();
            }
            _timer = null;
            _cts?.Cancel();
            Task[] pending;
            lock (_runningLock) pending = _running.ToArray();
            if (pending.Length > 0)
            {
                // Bounded wait: in-flight jobs are killed via the cancellation
                // token, so they cannot outlive a disposed store/logger.
                try { Task.WaitAll(pending, TimeSpan.FromSeconds(10)); }
                catch (AggregateException) { }
            }
            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose()
        {
            Stop();
            _store?.Dispose();
        }

        private enum JobKind { Daily, Weekly, Monthly, Once }

        private class JobEntry
        {
            public JobConfig Config { get; set; } = null!;
            public TimeSpan TimeOfDay { get; set; }
            public DateTime LastRunOn { get; set; }
            public JobKind Kind { get; set; }
            public DayOfWeek TargetDay { get; set; }
            public int DayOfMonth { get; set; }
            public DateTime RunDate { get; set; }
        }
    }
}
