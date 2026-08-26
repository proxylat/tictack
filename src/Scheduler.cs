using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        public TimerScheduler(TicTackConfig config, ILogger log)
        {
            _log = log;
            _jobs = new List<JobEntry>();

            if (config.Jobs == null || config.Jobs.Count == 0)
                return;

            if (config.Sources != null && config.Sources.Count > 0)
                _sourcePaths = string.Join(" ", config.Sources.ConvertAll(s => "\"" + s.Path + "\""));

            foreach (var job in config.Jobs)
            {
                if (string.IsNullOrEmpty(job.Time) || string.IsNullOrEmpty(job.Command))
                {
                    log.Warn("Skipping incomplete job '" + job.Name + "'");
                    continue;
                }
                TimeSpan ts;
                if (TimeSpan.TryParse(job.Time, out ts))
                    _jobs.Add(new JobEntry { Config = job, TimeOfDay = ts });
                else
                    log.Warn("Invalid time '" + job.Time + "' for job '" + job.Name + "'");
            }
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
            var today = now.Date;
            foreach (var job in _jobs)
            {
                if (job.LastRunOn < today && job.TimeOfDay <= now.TimeOfDay)
                {
                    job.LastRunOn = today;
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
        }

        private class JobEntry
        {
            public JobConfig Config { get; set; } = null!;
            public TimeSpan TimeOfDay { get; set; }
            public DateTime LastRunOn { get; set; }
        }
    }
}
