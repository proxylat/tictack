using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TicTack.Tests
{
    public class PreFlightGuardTests : IDisposable
    {
        private readonly string _tempDir;

        public PreFlightGuardTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "tictack-preflight-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        [Fact]
        public void DiskSpaceGuard_ClassifiesDiskFullAndEnforcesHeadroom()
        {
            var enospc = new IOException("No space left on device", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);
            Assert.True(DiskSpaceGuard.IsDiskFull(enospc));

            var otherIo = new IOException("Sharing violation", 32);
            Assert.False(DiskSpaceGuard.IsDiskFull(otherIo));

            // Optional toggle: minFreeBytes == 0 always allows
            Assert.True(DiskSpaceGuard.HasHeadroom(_tempDir, 1024, 0, out _, _ => 0));

            // When minFreeBytes > 0, enforces required + minFree
            Assert.False(DiskSpaceGuard.HasHeadroom(_tempDir, 1024, 5000, out var avail, _ => 5500));
            Assert.Equal(5500, avail);
            Assert.True(DiskSpaceGuard.HasHeadroom(_tempDir, 1024, 5000, out _, _ => 7000));
        }

        [Fact]
        public async Task CopyAction_LowFreeSpace_FailsFastNonRetryableAndPreservesDest()
        {
            var srcDir = Path.Combine(_tempDir, "src");
            var dstDir = Path.Combine(_tempDir, "dst");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);

            var srcFile = Path.Combine(srcDir, "data.bin");
            var dstFile = Path.Combine(dstDir, "data.bin");
            File.WriteAllText(srcFile, "new-larger-payload", Encoding.UTF8);
            File.WriteAllText(dstFile, "old-intact", Encoding.UTF8);

            Assert.True(FileSnapshot.TryRead(srcFile, out var snap));
            var copy = new CopyAction(new FileAccessor(), FileDurability.Full, new NullLogger())
            {
                MinFreeBytes = 100L * 1024 * 1024,
                FreeSpaceOverride = _ => 10L * 1024 * 1024,
            };

            var args = new FileActionArgs(new FileChangedEventArgs(ChangeType.Modified, srcFile), srcDir, dstDir, snap);
            var res = await copy.ExecuteAsync(args, CancellationToken.None);

            Assert.False(res.Success);
            Assert.False(res.Retryable);
            Assert.Equal("old-intact", File.ReadAllText(dstFile, Encoding.UTF8));
            Assert.Empty(Directory.GetFiles(dstDir, "*.tictack.tmp"));
        }

        [Fact]
        public async Task NetCheck_ParsesUrls_AndDistinguishesHealthyDegradedSevere()
        {
            Assert.Equal(("s3.amazonaws.com", 443), NetCheck.ParseHostPort("s3.amazonaws.com"));
            Assert.Equal(("s3.amazonaws.com", 443), NetCheck.ParseHostPort("https://s3.amazonaws.com/bucket/path"));
            Assert.Equal(("nas.local", 80), NetCheck.ParseHostPort("http://nas.local/repo"));
            Assert.Equal(("127.0.0.1", 8443), NetCheck.ParseHostPort("127.0.0.1:8443"));

            var log = new NullLogger();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int openPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            var acceptTask = Task.Run(async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    using var c = await listener.AcceptTcpClientAsync();
                }
            });

            var ok = await NetCheck.EvaluateAsync($"127.0.0.1:{openPort}", samples: 3, maxRttMs: 1000, waitSeconds: 0, pingGateway: false, log, CancellationToken.None);
            await acceptTask;
            Assert.NotEqual(NetCheckSeverity.Severe, ok.Severity);

            listener.Stop();
            var bad = await NetCheck.EvaluateAsync($"127.0.0.1:{openPort}", samples: 2, maxRttMs: 500, waitSeconds: 0, pingGateway: false, log, CancellationToken.None);
            Assert.Equal(NetCheckSeverity.Severe, bad.Severity);

            // Degraded warns only and continues; Severe prompts user choice.
            Assert.True(NetCheck.DecideOnResult(new NetCheckResult(NetCheckSeverity.Degraded, "high RTT"), "restic", log));
            Assert.True(NetCheck.DecideOnResult(bad, "restic", log, interactivePrompt: _ => true));
            Assert.False(NetCheck.DecideOnResult(bad, "restic", log, interactivePrompt: _ => false));
        }

        [Fact]
        public void Scheduler_NetCheck_InheritsFromDefaultsAndOverridesPerJob()
        {
            var defaultsHost = new JobsDefaultsConfig { NetCheckHost = "s3.example.com" };
            var defaultsLocal = new JobsDefaultsConfig { NetCheck = true };
            var jobInherit = new JobConfig();
            var jobOff = new JobConfig { NetCheck = false };
            var jobOverride = new JobConfig { NetCheckHost = "b2.example.com" };

            Assert.False(TimerScheduler.EffectiveNetCheckEnabled(jobInherit, null));
            Assert.True(TimerScheduler.EffectiveNetCheckEnabled(jobInherit, defaultsLocal));
            Assert.False(TimerScheduler.EffectiveNetCheckEnabled(jobOff, defaultsLocal));
            Assert.True(TimerScheduler.EffectiveNetCheckEnabled(jobInherit, defaultsHost));
            Assert.Equal("s3.example.com", TimerScheduler.EffectiveNetCheckHost(jobInherit, defaultsHost));
            Assert.Equal("b2.example.com", TimerScheduler.EffectiveNetCheckHost(jobOverride, defaultsHost));
        }

        [Fact]
        public void ProcessHardening_ApplyEcoQos_DoesNotThrow()
        {
            ProcessHardening.ApplyEcoQos(new NullLogger());
        }

        private sealed class NullLogger : ILogger
        {
            public void Debug(string msg) { }
            public void Info(string msg) { }
            public void Warn(string msg) { }
            public void Error(string msg, Exception? ex = null) { }
        }
    }
}
