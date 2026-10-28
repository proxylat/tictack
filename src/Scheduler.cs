using System.Globalization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public sealed class TimerScheduler
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
                _sourcePaths = string.Join(" ", config.Sources.Select(s => "\"" + s.Path + "\""));

            _store = CreateStore(config);

            var defaultMissed = MissedPolicy.CatchUp;
            var defaults = config.JobsDefaults;
            if (defaults != null && !string.IsNullOrWhiteSpace(defaults.MissedRun))
            {
                if (TryParseMissedPolicy(defaults.MissedRun, out var parsed))
                    defaultMissed = parsed;
                else
                    log.Warn("Invalid jobs_defaults.missed_run '" + defaults.MissedRun + "' (use catch_up/skip), using catch_up");
            }
            if (defaults?.TimeoutMinutes != null && defaults.TimeoutMinutes <= 0)
                log.Warn("Invalid jobs_defaults.timeout_minutes '" + defaults.TimeoutMinutes + "', using 10");

            foreach (var job in config.Jobs)
            {
                var hasEvery = !string.IsNullOrWhiteSpace(job.Every);
                if (string.IsNullOrEmpty(job.Name) || string.IsNullOrEmpty(job.Command)
                    || (!hasEvery && string.IsNullOrEmpty(job.Time)))
                {
                    log.Warn("Skipping incomplete job '" + (job.Name ?? "<unnamed>") + "' (needs name, command, and time — or every)");
                    continue;
                }
                if (job.TimeoutMinutes < 0)
                {
                    log.Warn("Invalid timeout_minutes '" + job.TimeoutMinutes + "' for job '" + job.Name + "'");
                    continue;
                }
                var entry = new JobEntry
                {
                    Config = job,
                    TimeoutMinutes = EffectiveTimeout(job, defaults),
                };
                if (!ParseSchedule(job, entry, defaultMissed, log)) continue;
                SeedJobHistory(job, entry, log);
                SeedJobEntry(job, entry, log);
                _jobs.Add(entry);
            }
        }

        private void SeedJobEntry(JobConfig job, JobEntry entry, ILogger log)
        {
        if (entry.Kind != JobKind.Interval)
        {
            var last = _store!.GetLastRun(job.Name!); // set unless zero jobs; runs once per parsed job
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
        }
        }

        private void SeedJobHistory(JobConfig job, JobEntry entry, ILogger log)
        {
            if (entry.Kind == JobKind.Interval)
            {
                var lastAt = _store!.GetLastRunAt(job.Name!); // set unless zero jobs; runs once per parsed job
                if (lastAt == null)
                {
                    // Fresh store: the clock starts now, so the first fire
                    // waits one full interval (systemd OnUnitActiveSec).
                    entry.LastRunAt = DateTime.Now;
                    try { _store.SetLastRunAt(job.Name!, entry.LastRunAt); }
                    catch (Exception ex) { log.Debug("Job seed failed for '" + job.Name + "': " + ex.Message); }
                    log.Info("Job '" + job.Name + "' has no run history, waiting one interval (" + job.Every + ")");
                }
                else
                {
                    entry.LastRunAt = lastAt.Value;
                }
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

        // Skip-policy daily: only today's reached slot counts, missed days
        // never fire. Compare IsDue's final branch (the catch-up).
        public static bool IsDueSkip(DateTime now, DateTime lastRun, TimeSpan timeOfDay) =>
            lastRun < now.Date && now.TimeOfDay >= timeOfDay;

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

        // Skip-policy weekly: today must be the target day with its time
        // reached; a missed week stays missed.
        public static bool IsDueWeeklySkip(DateTime now, DateTime lastRun, TimeSpan timeOfDay, DayOfWeek target) =>
            now.DayOfWeek == target && lastRun.Date < now.Date && now.TimeOfDay >= timeOfDay;

        // Weekday-mask weekly (days: [mon, wed, fri]): the most recent
        // occurrence across the whole mask decides, same date granularity
        // as the single-day rule.
        public static bool IsDueWeeklyMask(DateTime now, DateTime lastRun, TimeSpan timeOfDay, IReadOnlyList<DayOfWeek> mask)
        {
            DateTime? best = null;
            foreach (var target in mask)
            {
                var daysBack = ((int)now.DayOfWeek - (int)target + 7) % 7;
                var occurrence = now.Date.AddDays(-daysBack).Add(timeOfDay);
                if (occurrence > now) occurrence = occurrence.AddDays(-7);
                if (best == null || occurrence > best) best = occurrence;
            }
            return best != null && lastRun.Date < best.Value.Date;
        }

        public static bool IsDueWeeklyMaskSkip(DateTime now, DateTime lastRun, TimeSpan timeOfDay, IReadOnlyList<DayOfWeek> mask)
        {
            foreach (var target in mask)
                if (now.DayOfWeek == target)
                    return lastRun.Date < now.Date && now.TimeOfDay >= timeOfDay;
            return false;
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

        // Skip-policy monthly: today must be the (clamped) occurrence day.
        public static bool IsDueMonthlySkip(DateTime now, DateTime lastRun, TimeSpan timeOfDay, int dayOfMonth)
        {
            var occurrence = ClampToMonth(now.Year, now.Month, dayOfMonth).Add(timeOfDay);
            return occurrence.Date == now.Date && now >= occurrence && lastRun.Date < now.Date;
        }

        private static DateTime ClampToMonth(int year, int month, int dayOfMonth) =>
            new DateTime(year, month, Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month)));

        // One-shot: due once the stamped date+time passes, never again after
        // a successful run stamps the store. Failed runs retry next tick.
        public static bool IsDueOnce(DateTime now, DateTime lastRun, TimeSpan timeOfDay, DateTime runDate) =>
            lastRun == DateTime.MinValue && now >= runDate.Date.Add(timeOfDay);

        // Skip-policy one-shot: only the stamped day itself counts — a date
        // that passed while the box was off never fires.
        public static bool IsDueOnceSkip(DateTime now, DateTime lastRun, TimeSpan timeOfDay, DateTime runDate) =>
            lastRun == DateTime.MinValue && now.Date == runDate.Date && now >= runDate.Date.Add(timeOfDay);

        // Interval: due once a full span elapsed since the previous run
        // finished (the store stamps completion, the in-memory guard stamps
        // launch so a failure retries after one span, not every tick).
        public static bool IsDueInterval(DateTime now, DateTime lastRunAt, TimeSpan interval) =>
            lastRunAt == DateTime.MinValue || now - lastRunAt >= interval;

        internal static bool TryParseMissedPolicy(string? value, out MissedPolicy policy)
        {
            policy = MissedPolicy.CatchUp;
            if (string.IsNullOrWhiteSpace(value)) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "catch_up":
                case "catch-up":
                case "catchup":
                    policy = MissedPolicy.CatchUp;
                    return true;
                case "skip":
                    policy = MissedPolicy.Skip;
                    return true;
                default:
                    return false;
            }
        }

        internal static bool TryParseOverlapPolicy(string? value, out OverlapPolicy policy)
        {
            policy = OverlapPolicy.Skip;
            if (string.IsNullOrWhiteSpace(value)) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "skip":
                    policy = OverlapPolicy.Skip;
                    return true;
                case "queue":
                    policy = OverlapPolicy.Queue;
                    return true;
                default:
                    return false;
            }
        }

        // Full weekday names plus 3-letter abbreviations, any case. day:
        // keeps the strict full-name parse it always had; the days: mask is
        // the lenient one (masks are typed far more often).
        internal static bool TryParseWeekday(string? value, out DayOfWeek weekday)
        {
            weekday = DayOfWeek.Monday;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var s = value.Trim().ToLowerInvariant();
            switch (s)
            {
                case "mon":
                case "monday": weekday = DayOfWeek.Monday; return true;
                case "tue":
                case "tues":
                case "tuesday": weekday = DayOfWeek.Tuesday; return true;
                case "wed":
                case "wednesday": weekday = DayOfWeek.Wednesday; return true;
                case "thu":
                case "thur":
                case "thurs":
                case "thursday": weekday = DayOfWeek.Thursday; return true;
                case "fri":
                case "friday": weekday = DayOfWeek.Friday; return true;
                case "sat":
                case "saturday": weekday = DayOfWeek.Saturday; return true;
                case "sun":
                case "sunday": weekday = DayOfWeek.Sunday; return true;
                default: return false;
            }
        }

        // Trigger + modifier validation. Returns false (skip job) on
        // contradictory or unparseable cadence config. Modifier typos
        // (missed_run/overlap/jitter) warn and fall back instead of killing
        // the job — a misspelled knob must not silence a backup.
        private static bool ParseSchedule(JobConfig job, JobEntry entry, MissedPolicy defaultMissed, ILogger log)
        {
            var schedule = (job.Schedule ?? "daily").Trim().ToLowerInvariant();
            var hasEvery = !string.IsNullOrWhiteSpace(job.Every);
            var hasOnce = !string.IsNullOrWhiteSpace(job.RunOnceOn);
            var hasDay = !string.IsNullOrWhiteSpace(job.Day);
            var hasDays = job.Days != null && job.Days.Count > 0;
            var hasTime = !string.IsNullOrWhiteSpace(job.Time);

            if (hasEvery)
            {
                if (!ParseIntervalOption(job, entry, schedule, hasDay, hasDays, hasOnce, hasTime, log)) return false;
            }
            else if (!ValidateScheduleConflicts(job, schedule, hasDay, hasDays, hasOnce, log)) return false; // fall through to kind dispatch when clean
            else if (schedule == "daily" && !hasOnce)
            {
                ParseDailyOption(job, entry, hasDay, log);
            }
            else if (schedule == "weekly")
            {
                if (!ParseWeeklyOption(job, entry, hasDays, log)) return false;
            }
            else if (schedule == "monthly")
            {
                if (!ParseMonthlyOption(job, entry, log)) return false;
            }
            else if (schedule == "once" || hasOnce)
            {
                if (!ParseOnceOption(job, entry, log)) return false;
            }
            else
            {
                log.Warn("Invalid schedule '" + job.Schedule + "' for job '" + job.Name + "' (use daily/weekly/monthly/once)");
                return false;
            }

            if (hasTime)
            {
                if (!TimeSpan.TryParse(job.Time, out var ts))
                {
                    log.Warn("Invalid time '" + job.Time + "' for job '" + job.Name + "'");
                    return false;
                }
                entry.TimeOfDay = ts;
            }

            ParseExecutionOptions(job, entry, defaultMissed, log);

            return true;
        }


        private static bool ParseIntervalOption(JobConfig job, JobEntry entry, string schedule, bool hasDay, bool hasDays, bool hasOnce, bool hasTime, ILogger log)
        {
            if (schedule != "daily" || hasDay || hasDays || hasOnce || hasTime)
            {
                log.Warn("Job '" + job.Name + "': every is mutually exclusive with schedule/day/days/time/run_once_on, skipping");
                return false;
            }
            if (!Config.TryParseDuration(job.Every, out var interval) || interval < TimeSpan.FromSeconds(30))
            {
                log.Warn("Invalid every '" + job.Every + "' for job '" + job.Name + "' (use 30s/15m/6h/1d, minimum 30s), skipping");
                return false;
            }
            entry.Kind = JobKind.Interval;
            entry.Interval = interval;
            return true;
        }

        private static bool ValidateScheduleConflicts(JobConfig job, string schedule, bool hasDay, bool hasDays, bool hasOnce, ILogger log)
        {
            if (hasDays && schedule != "weekly")
            {
                log.Warn("Job '" + job.Name + "': days is only valid with schedule weekly, skipping");
                return false;
            }
            if (hasDays && hasDay)
            {
                log.Warn("Job '" + job.Name + "': day and days are mutually exclusive, skipping");
                return false;
            }
            if (hasOnce && (schedule != "daily" || hasDay))
            {
                log.Warn("Job '" + job.Name + "': run_once_on is mutually exclusive with schedule/day, skipping");
                return false;
            }
            return true;
        }

        private static void ParseDailyOption(JobConfig job, JobEntry entry, bool hasDay, ILogger log)
        {
            if (hasDay)
                log.Warn("Job '" + job.Name + "': day is ignored for daily schedules");
            entry.Kind = JobKind.Daily;
        }

        private static bool ParseWeeklyOption(JobConfig job, JobEntry entry, bool hasDays, ILogger log)
        {
            if (hasDays)
            {
                var mask = new List<DayOfWeek>();
                foreach (var raw in job.Days!)
                {
                    if (!TryParseWeekday(raw, out var weekday))
                    {
                        log.Warn("Invalid days entry '" + raw + "' for weekly job '" + job.Name + "' (use mon-sun), skipping");
                        return false;
                    }
                    if (!mask.Contains(weekday)) mask.Add(weekday);
                }
                if (mask.Count == 0)
                {
                    log.Warn("Empty days for weekly job '" + job.Name + "', skipping");
                    return false;
                }
                entry.Kind = JobKind.Weekly;
                entry.MaskDays = mask;
            }
            else
            {
                if (!Enum.TryParse<DayOfWeek>(job.Day?.Trim(), ignoreCase: true, out var weekday))
                {
                    log.Warn("Invalid day '" + job.Day + "' for weekly job '" + job.Name + "' (use Monday-Sunday)");
                    return false;
                }
                entry.Kind = JobKind.Weekly;
                entry.TargetDay = weekday;
            }
            return true;
        }

        private static bool ParseMonthlyOption(JobConfig job, JobEntry entry, ILogger log)
        {
            if (!int.TryParse(job.Day?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dom) || dom < 1 || dom > 31)
            {
                log.Warn("Invalid day '" + job.Day + "' for monthly job '" + job.Name + "' (use 1-31)");
                return false;
            }
            entry.Kind = JobKind.Monthly;
            entry.DayOfMonth = dom;
            return true;
        }

        private static bool ParseOnceOption(JobConfig job, JobEntry entry, ILogger log)
        {
            if (!DateTime.TryParse(job.RunOnceOn?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                log.Warn("Invalid run_once_on '" + job.RunOnceOn + "' for job '" + job.Name + "' (use YYYY-MM-DD)");
                return false;
            }
            entry.Kind = JobKind.Once;
            entry.RunDate = date.Date;
            return true;
        }

        private static void ParseExecutionOptions(JobConfig job, JobEntry entry, MissedPolicy defaultMissed, ILogger log)
        {
            if (string.IsNullOrWhiteSpace(job.MissedRun))
                entry.Missed = defaultMissed;
            else if (!TryParseMissedPolicy(job.MissedRun, out var missed))
            {
                log.Warn("Invalid missed_run '" + job.MissedRun + "' for job '" + job.Name + "' (use catch_up/skip), using default");
                entry.Missed = defaultMissed;
            }
            else
            {
                entry.Missed = missed;
            }

            if (string.IsNullOrWhiteSpace(job.Overlap))
                entry.Overlap = OverlapPolicy.Skip;
            else if (!TryParseOverlapPolicy(job.Overlap, out var overlap))
            {
                log.Warn("Invalid overlap '" + job.Overlap + "' for job '" + job.Name + "' (use skip/queue), using skip");
                entry.Overlap = OverlapPolicy.Skip;
            }
            else
            {
                entry.Overlap = overlap;
            }

            if (!string.IsNullOrWhiteSpace(job.Jitter))
            {
                if (!Config.TryParseDuration(job.Jitter, out var jitter))
                {
                    log.Warn("Invalid jitter '" + job.Jitter + "' for job '" + job.Name + "' (use 30s/15m), ignoring");
                    entry.Jitter = TimeSpan.Zero;
                }
                else
                {
                    entry.Jitter = jitter;
                }
            }
        }

        // Pure overlap decision, extracted for tests: an idle job (no run or
        // a settled one) always launches; a busy job stands down under skip,
        // chains one follow-up run under queue (further ticks while queued
        // collapse — the queue holds at most one).
        public enum OverlapDecision { Launch, Skip, Chain }
        internal static OverlapDecision DecideOverlap(Task? running, bool queued, OverlapPolicy overlap)
        {
            if (running == null || running.IsCompleted) return OverlapDecision.Launch;
            if (overlap == OverlapPolicy.Skip) return OverlapDecision.Skip;
            return queued ? OverlapDecision.Skip : OverlapDecision.Chain;
        }

        // Pure timeout resolution for tests: per-job wins, then the shared
        // default, then the 10-minute built-in. Zeros mean unset.
        internal static int EffectiveTimeout(JobConfig job, JobsDefaultsConfig? defaults) =>
            job.TimeoutMinutes > 0 ? job.TimeoutMinutes
            : defaults?.TimeoutMinutes > 0 ? defaults.TimeoutMinutes.Value : 10;

        private static bool EntryIsDue(DateTime now, JobEntry job)
        {
            var skip = job.Missed == MissedPolicy.Skip;
            return job.Kind switch
            {
                JobKind.Weekly when job.MaskDays != null && skip => IsDueWeeklyMaskSkip(now, job.LastRunOn, job.TimeOfDay, job.MaskDays),
                JobKind.Weekly when job.MaskDays != null => IsDueWeeklyMask(now, job.LastRunOn, job.TimeOfDay, job.MaskDays),
                JobKind.Weekly when skip => IsDueWeeklySkip(now, job.LastRunOn, job.TimeOfDay, job.TargetDay),
                JobKind.Weekly => IsDueWeekly(now, job.LastRunOn, job.TimeOfDay, job.TargetDay),
                JobKind.Monthly when skip => IsDueMonthlySkip(now, job.LastRunOn, job.TimeOfDay, job.DayOfMonth),
                JobKind.Monthly => IsDueMonthly(now, job.LastRunOn, job.TimeOfDay, job.DayOfMonth),
                JobKind.Once when skip => IsDueOnceSkip(now, job.LastRunOn, job.TimeOfDay, job.RunDate),
                JobKind.Once => IsDueOnce(now, job.LastRunOn, job.TimeOfDay, job.RunDate),
                JobKind.Interval => IsDueInterval(now, job.LastRunAt, job.Interval),
                _ when skip => IsDueSkip(now, job.LastRunOn, job.TimeOfDay),
                _ => IsDue(now, job.LastRunOn, job.TimeOfDay),
            };
        }

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
                // Interval + skip backlog resync: overdue by 2+ spans means
                // the box was away long enough that the backlog is stale —
                // restart the clock silently instead of firing catch-up.
                if (job.Kind == JobKind.Interval && job.Missed == MissedPolicy.Skip
                    && job.LastRunAt != DateTime.MinValue && now - job.LastRunAt >= job.Interval + job.Interval)
                {
                    job.LastRunAt = now;
                    try { _store?.SetLastRunAt(job.Config.Name!, now); }
                    catch (Exception ex) { _log.Debug("Job resync failed for '" + job.Config.Name + "': " + ex.Message); }
                    continue;
                }
                if (!EntryIsDue(now, job)) continue;
                lock (job.RunLock)
                {
                    // Queue chaining: the follow-up fires once the in-flight
                    // run settles (success or fail). Stop() cancels the token
                    // the chained run checks first, so chains cannot outlive
                    // the scheduler.
                    switch (DecideOverlap(job.Running, job.Queued, job.Overlap))
                    {
                        case OverlapDecision.Skip:
                            _log.Debug("Job '" + job.Config.Name + "' skipped, previous run still active");
                            continue;
                        case OverlapDecision.Chain:
                            job.Queued = true;
                            var chained = job.Running!.ContinueWith(_ => RunJobAsync(job), TaskScheduler.Default).Unwrap();
                            job.Running = chained;
                            Track(chained);
                            continue;
                        default:
                            break;
                    }
                    // In-memory guard against re-trigger while running (the
                    // store stamps only on success). Interval jobs stamp the
                    // launch instant: a failure retries after one span, and a
                    // run that overruns its span re-fires right after finish.
                    if (job.Kind == JobKind.Interval) job.LastRunAt = now;
                    else job.LastRunOn = now.Date;
                    // Deliberate fire-and-forget: jobs must not block the
                    // timer. Tracked so Stop() can cancel and wait instead of
                    // letting a job race into a disposed store/logger.
#pragma warning disable MA0134 // Fire-and-forget kill at shutdown: awaiting would block process exit on a hung child.
                    var task = System.Threading.Tasks.Task.Run(() => RunJobAsync(job));
#pragma warning restore MA0134
                    job.Running = task;
                    Track(task);
                }
            }
        }

        private void Track(Task task)
        {
            lock (_runningLock)
            {
                _running.Add(task);
                _running.RemoveAll(t => t.IsCompleted);
            }
        }

        private async Task RunJobAsync(JobEntry job)
        {
            lock (job.RunLock) { job.Queued = false; }
            var ct = _cts?.Token ?? CancellationToken.None;
            try
            {
                if (job.Jitter > TimeSpan.Zero)
                {
                    var delay = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * job.Jitter.TotalMilliseconds);
                    _log.Debug("Job '" + job.Config.Name + "' jittered +" + (int)delay.TotalSeconds + "s");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                // AC gate sits after the jitter so the reading is closest to
                // launch. Skipped slots are not stamped: catch-up policies
                // retry when AC returns, skip policies drop the slot.
                if (job.Config.OnAcOnly && !IsOnAcPower())
                {
                    _log.Info("Job '" + job.Config.Name + "' skipped, running on battery power");
                    return;
                }
                using (SleepInhibit.Hold(job.Config.PreventSleep, job.Config.Name, job.TimeoutMinutes, _log))
                    await RunJobBodyAsync(job, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Error("Scheduled job '" + job.Config.Name + "'", ex);
            }
        }

        private async Task RunJobBodyAsync(JobEntry job, CancellationToken ct)
        {
            var cfg = job.Config;
            var wd = cfg.WorkingDir ?? AppDomain.CurrentDomain.BaseDirectory;
            var timeoutMs = job.TimeoutMinutes * 60000;
            var low = cfg.LowPriority;
            string Sub(string cmd) => _sourcePaths != null ? cmd.Replace("{source}", _sourcePaths) : cmd;

            if (!string.IsNullOrWhiteSpace(cfg.PreCommand))
            {
                if (!await RunPreCommandAsync(job, wd, Sub, timeoutMs, low, ct).ConfigureAwait(false)) return;
            }

            var cmd = Sub(cfg.Command!);
            _log.Info("Running job '" + cfg.Name + "': " + cmd);
            var (exitCode, _, err, timedOut) = await ProcessRunner.RunAsync(
                cmd, wd, redirect: true, ct, timeoutMs, low).ConfigureAwait(false);

            if (timedOut)
            {
                _log.Error(TimeoutMessage(cfg.Name, job.TimeoutMinutes));
                await RunFailureHookAsync(job, wd, Sub, timeoutMs, low, ct).ConfigureAwait(false);
            }
            else if (exitCode != 0)
            {
                _log.Error("Job '" + cfg.Name + "' exited " + exitCode + ": " + err);
                // Jobs run as the service account (LocalSystem on Windows,
                // the systemd unit user on Linux): bare commands that work
                // in a terminal fail here on machine-PATH/ACL grounds.
                // Name the fix at the failure point, not in a wiki.
                var hint = JobFailureHint(err, cfg.Command);
                if (hint != null) _log.Error(hint);
                await RunFailureHookAsync(job, wd, Sub, timeoutMs, low, ct).ConfigureAwait(false);
            }
            else
            {
                _log.Info("Job '" + cfg.Name + "' completed");
                try
                {
                    if (job.Kind == JobKind.Interval) _store?.SetLastRunAt(cfg.Name!, DateTime.Now);
                    else _store?.SetLastRun(cfg.Name!, DateTime.Today);
                }
                catch (Exception ex) { _log.Error("Failed to persist run state for job '" + cfg.Name + "'", ex); }
            }

            // Post runs whenever main ran (success, failure, or timeout):
            // cleanup must not depend on the outcome. A failing post is
            // logged only — it neither unstamps a success nor pages failure.
            if (!string.IsNullOrWhiteSpace(cfg.PostCommand))
            {
                await RunPostCommandAsync(job, wd, Sub, timeoutMs, low, ct).ConfigureAwait(false);
            }
        }

        private async Task RunFailureHookAsync(JobEntry job, string wd, Func<string, string> sub, int timeoutMs, bool low, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(job.Config.OnFailure)) return;
            var hook = sub(job.Config.OnFailure);
            _log.Info("Running job '" + job.Config.Name + "' on-failure: " + hook);
            // No recursion: a failing pager is logged, never re-paged.
            var (exit, _, err, timedOut) = await ProcessRunner.RunAsync(
                hook, wd, redirect: true, ct, timeoutMs, low).ConfigureAwait(false);
            if (timedOut) _log.Error(TimeoutMessage(job.Config.Name + " on-failure", job.TimeoutMinutes));
            else if (exit != 0) _log.Error("Job '" + job.Config.Name + "' on-failure exited " + exit + ": " + err);
            else _log.Info("Job '" + job.Config.Name + "' on-failure completed");
        }

        // AC-line reading for on_ac_only. Unknown means run: desktops and
        // VMs have no battery API, and a missing reading must never silence
        // a backup — only a positive "on battery" skips.
        internal static bool IsOnAcPower()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var status = new SystemPowerStatus();
                    if (GetSystemPowerStatus(ref status) && status.ACLineStatus == 0)
                        return false;
                    return true;
                }
                var supplies = "/sys/class/power_supply";
                if (Directory.Exists(supplies))
                {
                    var sawMains = false;
                    foreach (var dir in Directory.EnumerateDirectories(supplies))
                    {
                        try
                        {
                            if (!File.ReadAllText(Path.Combine(dir, "type")).Trim().Equals("Mains", StringComparison.OrdinalIgnoreCase))
                                continue;
                            sawMains = true;
                            if (File.ReadAllText(Path.Combine(dir, "online")).Trim() == "1")
                                return true;
                        }
                        catch { }
                    }
                    if (sawMains) return false;
                }
                return true;
            }
            catch
            {
                return true;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;

        // Sleep inhibitor for prevent_sleep, held for one run. Windows:
        // thread execution state. Linux: a systemd-inhibit companion
        // sleeping out the timeout budget (killed on dispose). Anything
        // missing degrades to no-op with a debug line — inhibition is a
        // courtesy, never a gate.
        private sealed class SleepInhibit : IDisposable
        {
            private Process? _companion;
            private bool _windowsHeld;
            private bool _disposed;

            public static SleepInhibit Hold(bool enabled, string? jobName, int timeoutMinutes, ILogger log)
            {
                var h = new SleepInhibit();
                if (!enabled) return h;
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        SetThreadExecutionState(EsContinuous | EsSystemRequired);
                        h._windowsHeld = true;
                    }
                    else
                    {
                        var exe = FindOnPath("systemd-inhibit");
                        if (exe == null)
                        {
                            log.Debug("Sleep inhibit unavailable (no systemd-inhibit), job runs uninhibited");
                            return h;
                        }
                        var secs = Math.Min(Math.Max(timeoutMinutes, 1), 1440) * 60 + 300;
                        var psi = new ProcessStartInfo
                        {
                            FileName = exe,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        psi.ArgumentList.Add("--what=sleep:idle");
                        psi.ArgumentList.Add("--who=TicTackSv");
                        psi.ArgumentList.Add("--why=scheduled job " + (jobName ?? "unnamed"));
                        psi.ArgumentList.Add("--mode=block");
                        psi.ArgumentList.Add("sleep");
                        psi.ArgumentList.Add(secs.ToString(CultureInfo.InvariantCulture));
                        h._companion = Process.Start(psi);
                    }
                }
                catch (Exception ex)
                {
                    log.Debug("Sleep inhibit failed, job runs uninhibited: " + ex.Message);
                }
                return h;
            }

            private static string? FindOnPath(string name)
            {
                var path = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(path)) return null;
                foreach (var dir in path.Split(Path.PathSeparator))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim(), name);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
                return null;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_windowsHeld)
                {
                    try { SetThreadExecutionState(EsContinuous); } catch { }
                    _windowsHeld = false;
                }
                if (_companion != null)
                {
                    try { if (!_companion.HasExited) _companion.Kill(entireProcessTree: true); } catch { }
                    try { _companion.Dispose(); } catch { }
                    _companion = null;
                }
            }
        }

        internal static string TimeoutMessage(string? jobName, int timeoutMinutes) =>
            "Job '" + jobName + "' timed out after " + timeoutMinutes + " minutes, killed";

        // Failure-point hint for the two service-account signatures. Null for
        // everything else (a plain nonzero exit is the command's own story).
        internal static string? JobFailureHint(string stderr, string? command)
        {
            if (stderr.IndexOf("is not recognized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                stderr.IndexOf("command not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                stderr.IndexOf(": not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var tool = ParseMissingTool(stderr) ?? FirstWord(command);
                var script = OperatingSystem.IsWindows() ? @"service\win\add-tool-env.bat" : "service/linux/add-tool-env.sh";
                return "Job runs as the service account (machine environment, not your PATH): " +
                    (tool != null ? "'" + tool + "' must be resolvable — run " + script + " " + tool + ", then restart the service."
                                   : "bare commands must be resolvable — run " + script + " <name>, then restart the service.");
            }
            if (stderr.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                stderr.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OperatingSystem.IsWindows()
                    ? "Job runs as LocalSystem: grant NT AUTHORITY\\SYSTEM read/execute on the paths this command touches."
                    : "Job runs as the service user: grant it read/execute on the paths this command touches.";
            }
            return null;
        }

        // cmd.exe: 'restic' is not recognized... / sh: 1: restic: not found /
        // restic: command not found. Quoted token first, then the token
        // before the marker.
        internal static string? ParseMissingTool(string stderr)
        {
            var open = stderr.IndexOf('\'');
            if (open >= 0)
            {
                var close = stderr.IndexOf('\'', open + 1);
                if (close > open + 1)
                    return stderr.Substring(open + 1, close - open - 1);
            }
            foreach (var marker in new[] { ": command not found", ": not found" })
            {
                var at = stderr.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at <= 0) continue;
                var head = stderr.Substring(0, at).TrimEnd();
                var cut = head.LastIndexOfAny(new[] { ':', '/', '\\', ' ', '\t' });
                var tool = cut >= 0 ? head.Substring(cut + 1) : head;
                if (tool.Length > 0) return tool;
            }
            return null;
        }

        internal static string? FirstWord(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            var word = command.TrimStart().Split(new[] { ' ', '\t' }, 2)[0].Trim('"');
            return word.Length > 0 ? word : null;
        }

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

        private enum JobKind { Daily, Weekly, Monthly, Once, Interval }

        public enum MissedPolicy { CatchUp, Skip }

        public enum OverlapPolicy { Skip, Queue }

        private class JobEntry
        {
            public JobConfig Config { get; set; } = null!;
            public TimeSpan TimeOfDay { get; set; }
            public DateTime LastRunOn { get; set; }
            public DateTime LastRunAt { get; set; }
            public JobKind Kind { get; set; }
            public DayOfWeek TargetDay { get; set; }
            public List<DayOfWeek>? MaskDays { get; set; }
            public int DayOfMonth { get; set; }
            public DateTime RunDate { get; set; }
            public TimeSpan Interval { get; set; }
            public MissedPolicy Missed { get; set; }
            public OverlapPolicy Overlap { get; set; }
            public TimeSpan Jitter { get; set; }
            public int TimeoutMinutes { get; set; }
            public Task? Running { get; set; }
            public bool Queued { get; set; }
            public object RunLock { get; } = new object();
        }

        private async Task<bool> RunPreCommandAsync(JobEntry job, string wd, Func<string, string> sub, int timeoutMs, bool low, CancellationToken ct)
        {
            var cfg = job.Config;
            if (cfg.PreCommand == null)
                return true;
            var pre = sub(cfg.PreCommand);
            _log.Info("Running job '" + cfg.Name + "' pre-command: " + pre);
            var (preExit, _, preErr, preTimedOut) = await ProcessRunner.RunAsync(
                pre, wd, redirect: true, ct, timeoutMs, low).ConfigureAwait(false);
            if (preTimedOut || preExit != 0)
            {
                _log.Error(preTimedOut
                    ? TimeoutMessage(cfg.Name + " pre-command", job.TimeoutMinutes)
                    : "Job '" + cfg.Name + "' pre-command exited " + preExit + ": " + preErr);
                await RunFailureHookAsync(job, wd, sub, timeoutMs, low, ct).ConfigureAwait(false);
                return false; // gated: main never ran, no stamp, post skipped
            }
            return true;
        }

        private async Task RunPostCommandAsync(JobEntry job, string wd, Func<string, string> sub, int timeoutMs, bool low, CancellationToken ct)
        {
            var cfg = job.Config;
            if (cfg.PostCommand == null)
                return;
            var post = sub(cfg.PostCommand);
            _log.Info("Running job '" + cfg.Name + "' post-command: " + post);
            var (postExit, _, postErr, postTimedOut) = await ProcessRunner.RunAsync(
                post, wd, redirect: true, ct, timeoutMs, low).ConfigureAwait(false);
            if (postTimedOut) _log.Error(TimeoutMessage(cfg.Name + " post-command", job.TimeoutMinutes));
            else if (postExit != 0) _log.Error("Job '" + cfg.Name + "' post-command exited " + postExit + ": " + postErr);
            else _log.Info("Job '" + cfg.Name + "' post-command completed");
        }
    }
}
