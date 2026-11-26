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
    public sealed class TimerScheduler : IDisposable
    {
        private Timer? _timer;
        private readonly List<JobEntry> _jobs;
        private readonly ILogger _log;
        private readonly string? _sourcePaths;
        private readonly JobRunStore? _store;
        private readonly List<Task> _running = new List<Task>();
        private readonly object _runningLock = new object();
        private readonly List<IFileMonitor> _watchMonitors = new List<IFileMonitor>();
        private readonly List<Timer> _watchTimers = new List<Timer>();
        private readonly List<StateDb> _watchStateDbs = new List<StateDb>();
        private readonly MonitorConfig _monitorConfig;
        private readonly string _stateDir;
        private CancellationTokenSource? _cts;

        public TimerScheduler(TicTackConfig config, ILogger log)
        {
            _log = log;
            _jobs = new List<JobEntry>();
            _monitorConfig = config.Monitor ?? new MonitorConfig();
            _stateDir = ResolveStateDir(config);

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
                var entry = TryBuildEntry(job, config, defaults, defaultMissed, log);
                if (entry == null) continue;
                SeedJobHistory(job, entry, log);
                SeedJobEntry(job, entry, log);
                _jobs.Add(entry);
            }
        }

        private static JobEntry? TryBuildEntry(JobConfig job, TicTackConfig config, JobsDefaultsConfig? defaults, MissedPolicy defaultMissed, ILogger log)
        {
            var hasEvery = !string.IsNullOrWhiteSpace(job.Every);
            if (string.IsNullOrEmpty(job.Name) || string.IsNullOrEmpty(job.Command)
                || (!job.Watch && !hasEvery && string.IsNullOrEmpty(job.Time)))
            {
                log.Warn("Skipping incomplete job '" + (job.Name ?? "<unnamed>") + "' (needs name, command, and time — or every / watch)");
                return null;
            }
            if (job.TimeoutMinutes < 0)
            {
                log.Warn("Invalid timeout_minutes '" + job.TimeoutMinutes + "' for job '" + job.Name + "'");
                return null;
            }
            var entry = new JobEntry
            {
                Config = job,
                TimeoutMinutes = EffectiveTimeout(job, defaults),
                ShowTerminal = EffectiveShowTerminal(job, defaults),
                Sandbox = job.Sandbox ?? defaults?.Sandbox ?? false,
                LowPriority = job.LowPriority ?? defaults?.LowPriority ?? ((job.Inherit ?? defaults?.Inherit ?? false) && config.EcoQos),
                PreventSleep = job.PreventSleep || ((job.Inherit ?? defaults?.Inherit ?? false) && config.PreventSleep),
                NetCheckHost = EffectiveNetCheckHost(job, defaults),
                NetCheckEnabled = EffectiveNetCheckEnabled(job, defaults),
                NetCheckSamples = job.NetCheckSamples ?? defaults?.NetCheckSamples ?? 3,
                NetCheckMaxMs = job.NetCheckMaxMs ?? defaults?.NetCheckMaxMs ?? 500,
                NetCheckWaitSeconds = job.NetCheckWaitSeconds ?? defaults?.NetCheckWaitSeconds ?? 0,
                NetCheckIcmp = job.NetCheckIcmp ?? defaults?.NetCheckIcmp ?? true,
                UseVss = string.Equals(job.FileAccess ?? defaults?.FileAccess, "vss", StringComparison.OrdinalIgnoreCase),
                LockHandling = job.LockHandling ?? defaults?.LockHandling,
                LockWaitSeconds = Math.Max(0, job.LockWaitSeconds ?? defaults?.LockWaitSeconds ?? 0),
                MinFreeGb = job.MinFreeGb ?? defaults?.MinFreeGb,
                Destination = job.Destination,
                Retry = (job.Retry ?? defaults?.Retry) is { MaxAttempts: > 1 } r ? r : null,
            };
            if (job.Watch)
            {
                if (!ConfigureWatchJob(job, entry, config, defaults, log)) return null;
            }
            else
            {
                WarnInapplicableWatchFields(job, hasEvery ? "interval" : "calendar", log);
                if (!ParseSchedule(job, entry, defaultMissed, log)) return null;
            }
            return entry;
        }

        private static void WarnInapplicableWatchFields(JobConfig job, string trigger, ILogger log)
        {
            if (!string.IsNullOrWhiteSpace(job.Source))
                log.Warn($"Job '{job.Name}': field 'source' does not apply to {trigger} trigger (ignored)");
            if (job.Inherit.HasValue)
                log.Warn($"Job '{job.Name}': field 'inherit' does not apply to {trigger} trigger (ignored)");
            if (job.DebounceSeconds.HasValue)
                log.Warn($"Job '{job.Name}': field 'debounce_seconds' does not apply to {trigger} trigger (ignored)");
            if (job.Exclude != null && job.Exclude.Count > 0)
                log.Warn($"Job '{job.Name}': field 'exclude' does not apply to {trigger} trigger (ignored)");
            if (job.MaxFileSizeMb != null)
                log.Warn($"Job '{job.Name}': field 'max_file_size_mb' does not apply to {trigger} trigger (ignored)");
        }

        private static void WarnInapplicableTimerFieldsOnWatch(JobConfig job, ILogger log)
        {
            if (!string.IsNullOrWhiteSpace(job.Time))
                log.Warn($"Job '{job.Name}': field 'time' does not apply to watch trigger (ignored)");
            if (!string.IsNullOrWhiteSpace(job.Every))
                log.Warn($"Job '{job.Name}': field 'every' does not apply to watch trigger (ignored)");
            if (!string.IsNullOrWhiteSpace(job.Schedule))
                log.Warn($"Job '{job.Name}': field 'schedule' does not apply to watch trigger (ignored)");
            if (!string.IsNullOrWhiteSpace(job.Day))
                log.Warn($"Job '{job.Name}': field 'day' does not apply to watch trigger (ignored)");
            if (job.Days != null && job.Days.Count > 0)
                log.Warn($"Job '{job.Name}': field 'days' does not apply to watch trigger (ignored)");
            if (!string.IsNullOrWhiteSpace(job.RunOnceOn))
                log.Warn($"Job '{job.Name}': field 'run_once_on' does not apply to watch trigger (ignored)");
            if (!string.IsNullOrWhiteSpace(job.Jitter))
                log.Warn($"Job '{job.Name}': field 'jitter' does not apply to watch trigger (ignored)");
            if (!string.IsNullOrWhiteSpace(job.MissedRun))
                log.Warn($"Job '{job.Name}': field 'missed_run' does not apply to watch trigger (ignored)");
        }

        private static bool ConfigureWatchJob(JobConfig job, JobEntry entry, TicTackConfig config, JobsDefaultsConfig? defaults, ILogger log)
        {
            WarnInapplicableTimerFieldsOnWatch(job, log);

            bool inherit = job.Inherit ?? defaults?.Inherit ?? false;
            var targetSources = ResolveWatchTargets(job, config, inherit, log);
            if (targetSources.Count == 0)
            {
                log.Warn($"Job '{job.Name}': watch job needs 'source:' when zero or multiple sources exist without inherit: true, skipping");
                return false;
            }

            foreach (var (path, matched) in targetSources)
            {
                double debounce = Math.Max(0, job.DebounceSeconds
                    ?? (inherit ? matched?.DebounceSeconds : null)
                    ?? defaults?.DebounceSeconds
                    ?? 0.0);
                IList<string>? exclude = (job.Exclude != null && job.Exclude.Count > 0)
                    ? job.Exclude
                    : (inherit && matched?.Filter?.Exclude != null && matched.Filter.Exclude.Count > 0
                        ? matched.Filter.Exclude
                        : defaults?.Exclude);
                object? rawMax = job.MaxFileSizeMb ?? (inherit ? matched?.Filter?.MaxFileSizeMb : null) ?? defaults?.MaxFileSizeMb;

                entry.WatchTargets.Add(new WatchTarget
                {
                    WatchPath = path,
                    Debounce = TimeSpan.FromSeconds(debounce),
                    ExcludeFilter = exclude != null && exclude.Count > 0 ? new PatternFilter(exclude.ToArray()) : null,
                    MaxSizeMb = Config.ParseFileSizeLimit(rawMax),
                    StateDbDir = !string.IsNullOrEmpty(matched?.StateDbPath) ? matched.StateDbPath : ResolveStateDir(config),
                    Churn = CreateWatchChurnMonitor(job, defaults, matched, config, log),
                    Monitor = job.Monitor ?? defaults?.Monitor
                });
                if (entry.Retry == null && inherit && matched?.Sync?.Retry != null && matched.Sync.Retry.MaxAttempts > 1)
                    entry.Retry = matched.Sync.Retry;
                if (inherit && matched?.Sync != null)
                {
                    if (job.FileAccess == null && defaults?.FileAccess == null && string.Equals(matched.Sync.FileAccess, "vss", StringComparison.OrdinalIgnoreCase))
                        entry.UseVss = true;
                    if (job.LockHandling == null && defaults?.LockHandling == null && !string.IsNullOrWhiteSpace(matched.Sync.LockHandling))
                        entry.LockHandling = matched.Sync.LockHandling;
                    if (!job.LockWaitSeconds.HasValue && defaults?.LockWaitSeconds == null && matched.Sync.LockWaitSeconds > 0)
                        entry.LockWaitSeconds = matched.Sync.LockWaitSeconds;
                    if (!job.MinFreeGb.HasValue && defaults?.MinFreeGb == null && matched.Sync.MinFreeGb.HasValue)
                        entry.MinFreeGb = matched.Sync.MinFreeGb;
                }
            }

            WarnIfWatchWithoutUsnOrCatchup(job, config, log);
            entry.Kind = JobKind.Watch;
            ParseExecutionOptions(job, entry, MissedPolicy.Skip, log);
            return true;
        }

        private static List<(string path, SourceConfig? matched)> ResolveWatchTargets(JobConfig job, TicTackConfig config, bool inherit, ILogger log)
        {
            var sources = config.Sources ?? Array.Empty<SourceConfig>();
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var targetSources = new List<(string path, SourceConfig? matched)>();
            if (!string.IsNullOrWhiteSpace(job.Source))
            {
                var matched = inherit ? sources.FirstOrDefault(s => string.Equals(s.Path, job.Source, cmp)) : null;
                targetSources.Add((job.Source!, matched));
            }
            else if (inherit && sources.Count > 0)
            {
                foreach (var s in sources)
                    if (!string.IsNullOrEmpty(s.Path)) targetSources.Add((s.Path, s));
            }
            else if (sources.Count == 1 && !string.IsNullOrEmpty(sources[0].Path))
            {
                targetSources.Add((sources[0].Path, null));
            }
            return targetSources;
        }

        private static ChurnMonitor? CreateWatchChurnMonitor(JobConfig job, JobsDefaultsConfig? defaults, SourceConfig? matched, TicTackConfig config, ILogger log)
        {
            var sync = (job.Inherit ?? defaults?.Inherit ?? false) ? matched?.Sync : null;
            var windowMin = job.SpikeWindowMinutes ?? defaults?.SpikeWindowMinutes ?? sync?.SpikeWindowMinutes ?? 60;
            var bytesGb = job.SpikeBytesGb ?? defaults?.SpikeBytesGb ?? sync?.SpikeBytesGb ?? 0;
            var files = job.SpikeFiles ?? defaults?.SpikeFiles ?? sync?.SpikeFiles ?? 0;
            var renames = job.SpikeRenames ?? defaults?.SpikeRenames ?? sync?.SpikeRenames ?? 0;
            if (bytesGb <= 0 && files <= 0 && renames <= 0) return null;
            var dir = !string.IsNullOrEmpty(matched?.StateDbPath) ? matched.StateDbPath : ResolveStateDir(config);
            var safeName = string.Concat(job.Name!.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
            var marker = Path.Combine(dir, ".tictack-watch-churn-" + safeName + "-hold");
            var window = TimeSpan.FromMinutes(Math.Max(1, windowMin));
            var bytesTrip = bytesGb > 0 ? bytesGb * 1024L * 1024L * 1024L : 0L;
            return new ChurnMonitor(marker, window, bytesTrip, files, renames, log);
        }

        private static void WarnIfWatchWithoutUsnOrCatchup(JobConfig job, TicTackConfig config, ILogger log)
        {
            var monType = config.Monitor?.Type ?? "watcher";
            var usesUsn = OperatingSystem.IsWindows() &&
                (string.Equals(monType, "usn", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(monType, "composite", StringComparison.OrdinalIgnoreCase) && config.Monitor?.Usn == true));
            if (usesUsn) return;
            var hasCatchup = config.Jobs != null && config.Jobs.Any(j => !j.Watch && !string.IsNullOrWhiteSpace(j.Name));
            if (!hasCatchup)
                log.Warn($"Job '{job.Name}': uses watch: without USN or a periodic catch-up job — changes made while TicTack is stopped won't be detected.");
        }

        private void SeedJobEntry(JobConfig job, JobEntry entry, ILogger log)
        {
        if (entry.Kind != JobKind.Interval && entry.Kind != JobKind.Watch)
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

        private static string ResolveStateDir(TicTackConfig config) =>
            config.Sources != null && config.Sources.Count > 0 && !string.IsNullOrEmpty(config.Sources[0].StateDbPath)
                ? config.Sources[0].StateDbPath
                : AppContext.BaseDirectory;

        private JobRunStore CreateStore(TicTackConfig config)
        {
            var dbPath = Path.Combine(ResolveStateDir(config), "jobs.db");
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
            if (!Config.TryParseDuration(job.Every, out var interval) || interval < TimeSpan.FromSeconds(1))
            {
                log.Warn("Invalid every '" + job.Every + "' for job '" + job.Name + "' (use 1s/15m/6h/1d, minimum 1s), skipping");
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

        internal static bool EffectiveShowTerminal(JobConfig job, JobsDefaultsConfig? defaults) =>
            job.ShowTerminal ?? defaults?.ShowTerminal ?? false;

        internal static string? EffectiveNetCheckHost(JobConfig job, JobsDefaultsConfig? defaults) =>
            !string.IsNullOrWhiteSpace(job.NetCheckHost) ? job.NetCheckHost.Trim()
            : !string.IsNullOrWhiteSpace(defaults?.NetCheckHost) ? defaults.NetCheckHost.Trim()
            : null;

        internal static bool EffectiveNetCheckEnabled(JobConfig job, JobsDefaultsConfig? defaults) =>
            job.NetCheck ?? defaults?.NetCheck ?? !string.IsNullOrWhiteSpace(EffectiveNetCheckHost(job, defaults));

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
            var intervals = _jobs.Where(j => j.Kind == JobKind.Interval).Select(j => j.Interval);
            _timer = new Timer(RunDueJobs, null, 0, (int)TickFor(intervals).TotalMilliseconds);
            StartWatchers();
            ReplayPendingWatchFiles();
        }

        private void ReplayPendingWatchFiles()
        {
            if (_store == null) return;
            foreach (var job in _jobs)
            {
                if (job.Kind != JobKind.Watch || string.IsNullOrEmpty(job.Config.Name)) continue;
                IReadOnlyDictionary<string, string[]> pending;
                try { pending = _store.GetPendingWatchFiles(job.Config.Name); }
                catch (Exception ex)
                {
                    _log.Debug("Pending watch replay query failed for '" + job.Config.Name + "': " + ex.Message);
                    continue;
                }
                foreach (var (watchPath, relPaths) in pending)
                {
                    var existing = new List<string>(relPaths.Length);
                    var missing = new List<string>();
                    foreach (var rel in relPaths)
                    {
                        var abs = Path.Combine(watchPath, rel);
                        if (File.Exists(abs)) existing.Add(rel);
                        else missing.Add(rel);
                    }
                    if (missing.Count > 0)
                    {
                        try { _store.RemoveWatchFiles(job.Config.Name, watchPath, missing); }
                        catch (Exception ex) { _log.Debug("Stale pending watch cleanup failed: " + ex.Message); }
                    }
                    if (existing.Count > 0)
                    {
                        _log.Info($"Job '{job.Config.Name}': replaying {existing.Count} pending watch file(s) from previous run");
                        TryLaunch(job, DateTime.Now, watchPath, existing.ToArray());
                    }
                }
            }
        }

        private void StartWatchers()
        {
            var pairs = _jobs
                .Where(j => j.Kind == JobKind.Watch)
                .SelectMany(j => j.WatchTargets.Select(t => (Job: j, Target: t)))
                .Where(p => !string.IsNullOrEmpty(p.Target.WatchPath))
                .ToList();
            if (pairs.Count == 0) return;

            var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            foreach (var group in pairs.GroupBy(p => p.Target.WatchPath, cmp))
                foreach (var monGroup in group.GroupBy(p => p.Target.Monitor))
                    StartWatchGroup(group.Key, monGroup.Key ?? _monitorConfig, monGroup.ToList());
        }

        private void StartWatchGroup(string path, MonitorConfig monCfg, List<(JobEntry Job, WatchTarget Target)> members)
        {
            var timers = new Dictionary<WatchTarget, Timer>();
            foreach (var (job, target) in members)
            {
                var j = job;
                var tgt = target;
                var t = new Timer(_ =>
                {
                    var drained = DrainPendingFiles(tgt);
                    TryLaunch(j, DateTime.Now, tgt.WatchPath, drained);
                }, null, Timeout.Infinite, Timeout.Infinite);
                timers[tgt] = t;
                _watchTimers.Add(t);
            }

            var stateDir = members.Select(m => m.Target.StateDbDir).FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? _stateDir;
            var watchDb = OpenWatchStateDbOrNull(path, stateDir, _log);
            if (watchDb != null) _watchStateDbs.Add(watchDb);

            var syntheticSrc = new SourceConfig { Path = path };
            var (monitor, compositeMembers) = Program.CreateMonitor(monCfg, syntheticSrc, watchDb, _log, logMonitorStartup: true);
            monitor.Changed += (_, e) => EnqueueWatchChange(path, e.FullPath, members, timers);

            int gapScanActive = 0;
            void HandleGap(object? s, EventArgs e)
            {
                if (Interlocked.Exchange(ref gapScanActive, 1) == 1) return;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try { ScanWatchPathOnGap(path, members, timers); }
                    catch (Exception ex) { _log.Debug("Watch gap scan failed for '" + path + "': " + ex.Message); }
                    finally { Interlocked.Exchange(ref gapScanActive, 0); }
                });
            }
            if (monitor is IGapSource directGap) directGap.GapDetected += HandleGap;
            if (compositeMembers != null)
            {
                foreach (var m in compositeMembers)
                    if (m is IGapSource memberGap) memberGap.GapDetected += HandleGap;
            }

            monitor.Start();
            _watchMonitors.Add(monitor);

            if (!OperatingSystem.IsWindows() && watchDb != null)
            {
                _ = System.Threading.Tasks.Task.Run(() => CatchUpWatchPathFromStateDbAsync(path, watchDb, members, timers));
            }
        }

        private async Task CatchUpWatchPathFromStateDbAsync(string watchPath, StateDb watchDb, List<(JobEntry Job, WatchTarget Target)> members, Dictionary<WatchTarget, Timer> timers)
        {
            try
            {
                if (!Directory.Exists(watchPath)) return;
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                var files = new List<(string Rel, long Size, long Mtime)>();
                foreach (var full in Directory.EnumerateFiles(watchPath, "*", opts))
                {
                    try
                    {
                        var fi = new FileInfo(full);
                        if (!fi.Exists) continue;
                        files.Add((Path.GetRelativePath(watchPath, full), fi.Length, fi.LastWriteTimeUtc.Ticks));
                    }
                    catch { }
                }
                if (files.Count == 0) return;

                var existing = await watchDb.GetStatesAsync(files.Select(f => f.Rel)).ConfigureAwait(false);
                var hadBaseline = existing.Count > 0;
                var changed = new List<string>();
                foreach (var (rel, size, mtime) in files)
                {
                    if (!existing.TryGetValue(rel, out var prev) || prev.size != size || prev.mtime != mtime)
                        if (hadBaseline) changed.Add(rel);
                }

                await watchDb.UpsertBatchAsync(files.Select(f => (f.Rel, f.Size, f.Mtime))).ConfigureAwait(false);
                if (changed.Count == 0) return;

                foreach (var (job, tgt) in members)
                {
                    var matched = changed.Where(rel => ShouldIncludeWatchEvent(tgt, Path.Combine(watchPath, rel))).ToList();
                    if (matched.Count == 0) continue;
                    foreach (var rel in matched) tgt.PendingRelPaths.TryAdd(rel, 0);
                    try { _store?.EnqueueWatchFiles(job.Config.Name!, watchPath, matched); }
                    catch (Exception ex) { _log.Debug("Offline catch-up persist failed: " + ex.Message); }
                    var dueMs = Math.Max(0, (int)tgt.Debounce.TotalMilliseconds);
                    try { timers[tgt].Change(dueMs, Timeout.Infinite); }
                    catch (ObjectDisposedException) { }
                }
            }
            catch (Exception ex)
            {
                _log.Debug("Offline watch catch-up failed for '" + watchPath + "': " + ex.Message);
            }
        }

        private void EnqueueWatchChange(string watchPath, string fullPath, List<(JobEntry Job, WatchTarget Target)> members, Dictionary<WatchTarget, Timer> timers)
        {
            var rel = Path.GetRelativePath(watchPath, fullPath);
            foreach (var (job, tgt) in members)
            {
                if (!ShouldIncludeWatchEvent(tgt, fullPath)) continue;
                tgt.PendingRelPaths.TryAdd(rel, 0);
                try { _store?.EnqueueWatchFiles(job.Config.Name!, watchPath, new[] { rel }); }
                catch (Exception ex) { _log.Debug("Pending watch persist failed: " + ex.Message); }
                var dueMs = Math.Max(0, (int)tgt.Debounce.TotalMilliseconds);
                try { timers[tgt].Change(dueMs, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }

        private void ScanWatchPathOnGap(string watchPath, List<(JobEntry Job, WatchTarget Target)> members, Dictionary<WatchTarget, Timer> timers)
        {
            if (!Directory.Exists(watchPath)) return;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var perMember = new Dictionary<WatchTarget, List<string>>();
            foreach (var (_, tgt) in members) perMember[tgt] = new List<string>();

            foreach (var fullPath in Directory.EnumerateFiles(watchPath, "*", opts))
            {
                var rel = Path.GetRelativePath(watchPath, fullPath);
                foreach (var (_, tgt) in members)
                {
                    if (!ShouldIncludeWatchEvent(tgt, fullPath)) continue;
                    tgt.PendingRelPaths.TryAdd(rel, 0);
                    perMember[tgt].Add(rel);
                }
            }

            foreach (var (job, tgt) in members)
            {
                var list = perMember[tgt];
                if (list.Count == 0) continue;
                try { _store?.EnqueueWatchFiles(job.Config.Name!, watchPath, list); }
                catch (Exception ex) { _log.Debug("Gap scan persist failed: " + ex.Message); }
                var dueMs = Math.Max(0, (int)tgt.Debounce.TotalMilliseconds);
                try { timers[tgt].Change(dueMs, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }

        private static StateDb? OpenWatchStateDbOrNull(string watchPath, string stateDir, ILogger log)
        {
            try
            {
                var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(watchPath));
                var hash = Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
                var dbPath = Path.Combine(stateDir, "watch-usn-" + hash + ".db");
                return new StateDb(dbPath, log);
            }
            catch (Exception ex)
            {
                log.Warn("Watch StateDB init failed, continuing without USN checkpoint: " + ex.Message);
                return null;
            }
        }

        private static bool ShouldIncludeWatchEvent(WatchTarget tgt, string fullPath)
        {
            if (tgt.ExcludeFilter != null && !tgt.ExcludeFilter.ShouldProcess(fullPath))
                return false;
            if (!tgt.MaxSizeMb.HasValue) return true;
            if (tgt.MaxSizeMb.Value <= 0) return false;
            return new SizeFilter(tgt.MaxSizeMb.Value).ShouldProcess(fullPath);
        }

        private static string[] DrainPendingFiles(WatchTarget tgt)
        {
            var keys = tgt.PendingRelPaths.Keys.ToArray();
            var existing = new List<string>(keys.Length);
            foreach (var k in keys)
            {
                tgt.PendingRelPaths.TryRemove(k, out _);
                var abs = Path.Combine(tgt.WatchPath, k);
                if (File.Exists(abs)) existing.Add(k);
            }
            return existing.ToArray();
        }

        // A fixed 30s tick cannot honour sub-minute intervals. Half the
        // shortest interval keeps the due check ahead of each fire without
        // spinning on a 1s job's exact boundary.
        internal static TimeSpan TickFor(IEnumerable<TimeSpan> intervals)
        {
            var tick = TimeSpan.FromSeconds(30);
            foreach (var interval in intervals)
            {
                var half = TimeSpan.FromTicks(interval.Ticks / 2);
                if (half < tick) tick = half;
            }
            return tick;
        }

        private void RunDueJobs(object? state)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            var now = DateTime.Now;
            foreach (var job in _jobs)
            {
                if (job.Kind == JobKind.Watch) continue;
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
                TryLaunch(job, now);
            }
        }

        private void TryLaunch(JobEntry job, DateTime now, string? watchPath = null, string[]? changedFiles = null)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            lock (job.RunLock)
            {
                switch (DecideOverlap(job.Running, job.Queued, job.Overlap))
                {
                    case OverlapDecision.Skip:
                        _log.Debug("Job '" + job.Config.Name + "' skipped, previous run still active");
                        return;
                    case OverlapDecision.Chain:
                        job.Queued = true;
                        var chained = job.Running!.ContinueWith(_ => RunJobAsync(job, watchPath, changedFiles), TaskScheduler.Default).Unwrap();
                        job.Running = chained;
                        Track(chained);
                        return;
                    default:
                        break;
                }
                if (job.Kind == JobKind.Interval) job.LastRunAt = now;
                else if (job.Kind != JobKind.Watch) job.LastRunOn = now.Date;
#pragma warning disable MA0134 // Fire-and-forget kill at shutdown: awaiting would block process exit on a hung child.
                var task = System.Threading.Tasks.Task.Run(() => RunJobAsync(job, watchPath, changedFiles));
#pragma warning restore MA0134
                job.Running = task;
                Track(task);
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

        private async Task RunJobAsync(JobEntry job, string? watchPath = null, string[]? changedFiles = null)
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
                if (job.Kind == JobKind.Watch && !string.IsNullOrEmpty(watchPath))
                {
                    if (!DriveGuard.IsReady(watchPath))
                    {
                        _log.Warn("Job '" + job.Config.Name + "' deferred, watch path drive not ready: " + watchPath);
                        return;
                    }
                    if (!CheckWatchChurnGuard(job, watchPath, changedFiles))
                        return;
                }
                if (!HasDestinationHeadroom(job, watchPath, changedFiles))
                    return;
                if (job.NetCheckEnabled)
                {
                    var netResult = await NetCheck.EvaluateAsync(
                        job.NetCheckHost, job.NetCheckSamples, job.NetCheckMaxMs,
                        job.NetCheckWaitSeconds, job.NetCheckIcmp, _log, ct).ConfigureAwait(false);
                    if (!NetCheck.DecideOnResult(netResult, job.Config.Name ?? "job", _log))
                        return;
                }
                using (SleepInhibit.Hold(job.PreventSleep, job.Config.Name, job.TimeoutMinutes, _log))
                    await RunJobWithManifestsAsync(job, watchPath, changedFiles, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Error("Scheduled job '" + job.Config.Name + "'", ex);
            }
        }

        private async Task RunJobWithManifestsAsync(JobEntry job, string? watchPath, string[]? changedFiles, CancellationToken ct)
        {
            var cfg = job.Config;
            var filesList = changedFiles ?? Array.Empty<string>();
            string? relManifest = null;
            string? absManifest = null;
            using var vss = (job.UseVss && !string.IsNullOrEmpty(watchPath))
                ? SnapshotMount.TryCreate(watchPath, _log)
                : null;
            var effectiveWatchPath = vss?.MountPath ?? watchPath;
            try
            {
                if (vss == null && !string.IsNullOrEmpty(watchPath) && filesList.Length > 0)
                {
                    if (string.Equals(job.LockHandling, "skip", StringComparison.OrdinalIgnoreCase))
                    {
                        var locked = FindLockedFile(watchPath, filesList);
                        if (locked != null)
                        {
                            _log.Warn($"Job '{cfg.Name}' skipped (lock_handling: skip), file is locked: {locked}");
                            return;
                        }
                    }
                    else if (job.LockWaitSeconds > 0)
                    {
                        await WaitForFilesUnlockedAsync(watchPath, filesList, job.LockWaitSeconds, cfg.Name, ct).ConfigureAwait(false);
                    }
                }
                if (ContainsPlaceholder(cfg, "{files}"))
                {
                    relManifest = Path.Combine(Path.GetTempPath(), "tictack-files-" + Guid.NewGuid().ToString("n") + ".txt");
                    File.WriteAllLines(relManifest, filesList);
                }
                if (ContainsPlaceholder(cfg, "{files_abs}"))
                {
                    absManifest = Path.Combine(Path.GetTempPath(), "tictack-files-abs-" + Guid.NewGuid().ToString("n") + ".txt");
                    var absLines = !string.IsNullOrEmpty(effectiveWatchPath) ? filesList.Select(f => Path.Combine(effectiveWatchPath, f)) : filesList;
                    File.WriteAllLines(absManifest, absLines);
                }
                await RunJobBodyAsync(job, effectiveWatchPath, watchPath, filesList, relManifest, absManifest, ct).ConfigureAwait(false);
            }
            finally
            {
                if (relManifest != null) { try { File.Delete(relManifest); } catch { } }
                if (absManifest != null) { try { File.Delete(absManifest); } catch { } }
            }
        }

        private bool HasDestinationHeadroom(JobEntry job, string? watchPath, string[]? changedFiles)
        {
            if (!job.MinFreeGb.HasValue || job.MinFreeGb.Value <= 0 || string.IsNullOrWhiteSpace(job.Destination))
                return true;
            var minBytes = DiskSpaceGuard.GbToBytes(job.MinFreeGb.Value);
            long requiredBytes = 0;
            if (!string.IsNullOrEmpty(watchPath) && changedFiles != null)
            {
                foreach (var rel in changedFiles)
                {
                    try
                    {
                        var fi = new FileInfo(Path.Combine(watchPath, rel));
                        if (fi.Exists) requiredBytes += fi.Length;
                    }
                    catch { }
                }
            }
            if (!DiskSpaceGuard.HasHeadroom(job.Destination, requiredBytes, minBytes, out var availBytes))
            {
                _log.Error($"Job '{job.Config.Name}' blocked: free space on '{job.Destination}' ({availBytes / (1024 * 1024)} MB) is below min_free_gb ({job.MinFreeGb.Value:0.##} GB)");
                return false;
            }
            return true;
        }

        private static string? FindLockedFile(string watchPath, string[] filesList)
        {
            foreach (var rel in filesList)
            {
                var abs = Path.Combine(watchPath, rel);
                if (!File.Exists(abs)) continue;
                try
                {
                    using var fs = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException) { return rel; }
                catch { }
            }
            return null;
        }

        private async Task WaitForFilesUnlockedAsync(string watchPath, string[] filesList, int lockWaitSeconds, string? jobName, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(lockWaitSeconds);
            while (true)
            {
                var lockedFile = FindLockedFile(watchPath, filesList);
                if (lockedFile == null) return;
                if (DateTime.UtcNow >= deadline)
                {
                    _log.Warn($"Job '{jobName}': file '{lockedFile}' still locked after {lockWaitSeconds}s, proceeding");
                    return;
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }

        private bool CheckWatchChurnGuard(JobEntry job, string watchPath, string[]? changedFiles)
        {
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var tgt = job.WatchTargets.FirstOrDefault(t => string.Equals(t.WatchPath, watchPath, cmp));
            if (tgt?.Churn == null) return true;
            if (tgt.Churn.IsHeld())
            {
                _log.Warn("Job '" + job.Config.Name + "' held by spike guard");
                return false;
            }
            if (changedFiles != null && changedFiles.Length > 0)
            {
                long totalBytes = 0;
                foreach (var rel in changedFiles)
                {
                    try
                    {
                        var fi = new FileInfo(Path.Combine(watchPath, rel));
                        if (fi.Exists) totalBytes += fi.Length;
                    }
                    catch { }
                }
                tgt.Churn.ReportBulkDelete(totalBytes, changedFiles.Length);
                if (tgt.Churn.IsHeld()) return false;
            }
            return true;
        }

        private static bool ContainsPlaceholder(JobConfig cfg, string token) =>
            (cfg.Command != null && cfg.Command.Contains(token, StringComparison.Ordinal))
            || (cfg.PreCommand != null && cfg.PreCommand.Contains(token, StringComparison.Ordinal))
            || (cfg.PostCommand != null && cfg.PostCommand.Contains(token, StringComparison.Ordinal))
            || (cfg.OnFailure != null && cfg.OnFailure.Contains(token, StringComparison.Ordinal));

        private async Task RunJobBodyAsync(JobEntry job, string? effectiveWatchPath, string? storeWatchPath, string[] filesList, string? relManifest, string? absManifest, CancellationToken ct)
        {
            var cfg = job.Config;
            var wd = cfg.WorkingDir ?? AppDomain.CurrentDomain.BaseDirectory;
            var timeoutMs = job.TimeoutMinutes * 60000;
            var low = job.LowPriority;
            string Sub(string cmd)
            {
                var s = job.Kind == JobKind.Watch && !string.IsNullOrEmpty(effectiveWatchPath)
                    ? cmd.Replace("{source}", "\"" + effectiveWatchPath + "\"")
                    : (_sourcePaths != null ? cmd.Replace("{source}", _sourcePaths) : cmd);
                if (relManifest != null) s = s.Replace("{files}", relManifest);
                if (absManifest != null) s = s.Replace("{files_abs}", absManifest);
                return s;
            }

            if (!string.IsNullOrWhiteSpace(cfg.PreCommand))
            {
                if (!await RunPreCommandAsync(job, wd, Sub, timeoutMs, low, ct).ConfigureAwait(false)) return;
            }

            var cmd = Sub(cfg.Command!);
            _log.Info("Running job '" + cfg.Name + "': " + cmd);
            var (exitCode, _, err, timedOut) = await RunCommandWithRetryAsync(job, cmd, wd, timeoutMs, low, ct).ConfigureAwait(false);

            if (timedOut)
            {
                var msg = TimeoutMessage(cfg.Name, job.TimeoutMinutes);
                _log.Error(msg);
                DesktopAlert.Notify("TicTack Job Timeout", msg, "ERROR");
                await RunFailureHookAsync(job, wd, Sub, timeoutMs, low, ct).ConfigureAwait(false);
            }
            else if (exitCode != 0)
            {
                var msg = "Job '" + cfg.Name + "' exited " + exitCode + ": " + err;
                _log.Error(msg);
                DesktopAlert.Notify("TicTack Job Failed", msg, "ERROR");
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
                RecordJobSuccess(job, storeWatchPath, filesList);
            }

            // Post runs whenever main ran (success, failure, or timeout):
            // cleanup must not depend on the outcome. A failing post is
            // logged only — it neither unstamps a success nor pages failure.
            if (!string.IsNullOrWhiteSpace(cfg.PostCommand))
            {
                await RunPostCommandAsync(job, wd, Sub, timeoutMs, low, ct).ConfigureAwait(false);
            }
        }

        private void RecordJobSuccess(JobEntry job, string? watchPath, string[] filesList)
        {
            var cfg = job.Config;
            _log.Info("Job '" + cfg.Name + "' completed");
            if (job.Kind == JobKind.Watch && !string.IsNullOrEmpty(watchPath) && filesList.Length > 0)
            {
                try { _store?.RemoveWatchFiles(cfg.Name!, watchPath, filesList); }
                catch (Exception ex) { _log.Debug("Pending watch clear failed for '" + cfg.Name + "': " + ex.Message); }
            }
            try
            {
                if (job.Kind == JobKind.Interval) _store?.SetLastRunAt(cfg.Name!, DateTime.Now);
                else if (job.Kind != JobKind.Watch) _store?.SetLastRun(cfg.Name!, DateTime.Today);
            }
            catch (Exception ex) { _log.Error("Failed to persist run state for job '" + cfg.Name + "'", ex); }
        }

        private async Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> RunCommandWithRetryAsync(
            JobEntry job, string cmd, string wd, int timeoutMs, bool low, CancellationToken ct)
        {
            if (job.Retry == null || job.Retry.MaxAttempts <= 1)
                return await ProcessRunner.RunAsync(cmd, wd, redirect: !job.ShowTerminal, ct, timeoutMs, low, job.Sandbox).ConfigureAwait(false);

            var retry = new ExponentialBackoffRetry(job.Retry.MaxAttempts, job.Retry.DelayMs, job.Retry.Backoff, _log);
            (int ExitCode, string StdOut, string StdErr, bool TimedOut) last = default;
            try
            {
                return await retry.ExecuteAsync(async () =>
                {
                    last = await ProcessRunner.RunAsync(cmd, wd, redirect: !job.ShowTerminal, ct, timeoutMs, low, job.Sandbox).ConfigureAwait(false);
                    if (last.TimedOut) return last;
                    if (last.ExitCode != 0) throw new InvalidOperationException("exit " + last.ExitCode);
                    return last;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return last;
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
            foreach (var t in _watchTimers) { try { t.Dispose(); } catch { } }
            _watchTimers.Clear();
            foreach (var m in _watchMonitors) { try { m.Stop(); (m as IDisposable)?.Dispose(); } catch { } }
            _watchMonitors.Clear();
            foreach (var db in _watchStateDbs) { try { db.Dispose(); } catch { } }
            _watchStateDbs.Clear();
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

        private enum JobKind { Daily, Weekly, Monthly, Once, Interval, Watch }

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
            public bool LowPriority { get; set; }
            public bool PreventSleep { get; set; }
            public RetryConfig? Retry { get; set; }
            public bool UseVss { get; set; }
            public string? LockHandling { get; set; }
            public int LockWaitSeconds { get; set; }
            public double? MinFreeGb { get; set; }
            public string? Destination { get; set; }
            public List<WatchTarget> WatchTargets { get; } = new List<WatchTarget>();
            public MissedPolicy Missed { get; set; }
            public OverlapPolicy Overlap { get; set; }
            public TimeSpan Jitter { get; set; }
            public int TimeoutMinutes { get; set; }
            public bool ShowTerminal { get; set; }
            public bool Sandbox { get; set; }
            public string? NetCheckHost { get; set; }
            public bool NetCheckEnabled { get; set; }
            public int NetCheckSamples { get; set; }
            public int NetCheckMaxMs { get; set; }
            public int NetCheckWaitSeconds { get; set; }
            public bool NetCheckIcmp { get; set; }
            public Task? Running { get; set; }
            public bool Queued { get; set; }
            public object RunLock { get; } = new object();
        }

        private sealed class WatchTarget
        {
            public string WatchPath { get; set; } = string.Empty;
            public TimeSpan Debounce { get; set; }
            public PatternFilter? ExcludeFilter { get; set; }
            public long? MaxSizeMb { get; set; }
            public string? StateDbDir { get; set; }
            public ChurnMonitor? Churn { get; set; }
            public MonitorConfig? Monitor { get; set; }
            public System.Collections.Concurrent.ConcurrentDictionary<string, byte> PendingRelPaths { get; } =
                new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
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
