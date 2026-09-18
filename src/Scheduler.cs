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
                var entry = new JobEntry { Config = job, TimeOfDay = ts };
                var last = _store.GetLastRun(job.Name!);
                entry.LastRunOn = last ?? DateTime.MinValue;
                _jobs.Add(entry);
            }
        }

        private static JobRunStore CreateStore(TicTackConfig config)
        {
            string dir;
            if (config.Sources != null && config.Sources.Count > 0 && !string.IsNullOrEmpty(config.Sources[0].StateDbPath))
                dir = Path.GetDirectoryName(config.Sources[0].StateDbPath) ?? AppContext.BaseDirectory;
            else
                dir = AppContext.BaseDirectory;
            return new JobRunStore(Path.Combine(dir, "jobs.db"));
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
                if (IsDue(now, job.LastRunOn, job.TimeOfDay))
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
                    cmd, wd, redirect: true, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

                if (timedOut)
                {
                    _log.Error(TimeoutMessage(job.Config.Name));
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

        internal static string TimeoutMessage(string? jobName) =>
            "Job '" + jobName + "' timed out after " + (ProcessRunner.TimeoutMs / 60000) + " minutes, killed";

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

        private class JobEntry
        {
            public JobConfig Config { get; set; } = null!;
            public TimeSpan TimeOfDay { get; set; }
            public DateTime LastRunOn { get; set; }
        }
    }
}
