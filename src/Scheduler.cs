using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TicTack
{
    public interface IScheduler : IDisposable
    {
        void Start();
        void Stop();
    }

    public class TimerScheduler : IScheduler
    {
        private Timer? _timer;
        private readonly List<JobEntry> _jobs;
        private readonly ILogger _log;
        private readonly string? _sourcePaths;
        private readonly JobRunStore? _store;

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
            _log.Info("Scheduler started with " + _jobs.Count + " job(s)");
            _timer = new Timer(RunDueJobs, null, 0, 30000);
        }

        private void RunDueJobs(object? state)
        {
            var now = DateTime.Now;
            foreach (var job in _jobs)
            {
                if (IsDue(now, job.LastRunOn, job.TimeOfDay))
                {
                    job.LastRunOn = now.Date; // in-memory guard against same-day re-trigger
                    System.Threading.Tasks.Task.Run(() => RunJob(job));
                }
            }
        }

        private void RunJob(JobEntry job)
        {
            try
            {
                var cmd = job.Config.Command!;
                if (_sourcePaths != null)
                    cmd = cmd.Replace("{source}", _sourcePaths);

                var wd = job.Config.WorkingDir ?? AppDomain.CurrentDomain.BaseDirectory;

                var psi = new ProcessStartInfo
                {
                    WorkingDirectory = wd,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                if (OperatingSystem.IsWindows())
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/c " + cmd;
                }
                else
                {
                    psi.FileName = "/bin/sh";
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(cmd);
                }

                _log.Info("Running job '" + job.Config.Name + "': " + cmd);

                using (var p = Process.Start(psi))
                {
                    if (p == null) return;
                    if (!p.WaitForExit(600000))
                    {
                        try { p.Kill(); } catch { }
                        _log.Error("Job '" + job.Config.Name + "' timed out after 10 minutes, killed");
                    }
                    else if (p.ExitCode != 0)
                    {
                        var err = p.StandardError.ReadToEnd();
                        _log.Error("Job '" + job.Config.Name + "' exited " + p.ExitCode + ": " + err);
                    }
                    else
                    {
                        _log.Info("Job '" + job.Config.Name + "' completed");
                        try { _store?.SetLastRun(job.Config.Name!, DateTime.Today); }
                        catch (Exception ex) { _log.Error("Failed to persist run state for job '" + job.Config.Name + "'", ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Scheduled job '" + job.Config.Name + "'", ex);
            }
        }

        public void Stop()
        {
            if (_timer != null)
            {
                _timer.Dispose();
            }
            _timer = null;
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
