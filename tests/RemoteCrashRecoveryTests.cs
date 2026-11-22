using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TicTack.Tests
{
    public class RemoteCrashRecoveryTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _vmRoot;
        private readonly string _hostDest;

        public RemoteCrashRecoveryTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "tictack-vmcrash-" + Guid.NewGuid().ToString("N"));
            _vmRoot = Path.Combine(_tempDir, "vm");
            _hostDest = Path.Combine(_tempDir, "dest");
            Directory.CreateDirectory(_vmRoot);
            Directory.CreateDirectory(_hostDest);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        [Fact]
        public async Task VmCrashMidTransfer_PreservesExistingDestAndConvergesOnRestart()
        {
            var psk = RemoteProto.ResolvePsk("crash-psk", null, null);
            var log = new NullLogger();
            var srcFile = Path.Combine(_vmRoot, "big.bin");
            var dstFile = Path.Combine(_hostDest, "big.bin");

            // 1. Seed v1 and sync cleanly to host.
            var v1 = Encoding.UTF8.GetBytes("version-1-intact-payload");
            File.WriteAllBytes(srcFile, v1);

            var agent1 = new RemoteAgentServer(_vmRoot, "127.0.0.1:0", "127.0.0.1", 8, useTls: true, psk: psk, log: log);
            agent1.Start();
            int port = agent1.LocalPort;

            var cfg = BuildConfig(port);
            var resolved = Config.ResolveRemoteSources(cfg)[0];

            using (var p1 = Program.BuildPipeline(resolved, cfg, log))
                Assert.True(await p1.RunOnceAsync());

            Assert.Equal(v1, File.ReadAllBytes(dstFile));

            // 2. Write 16 MiB v2 on VM, begin streaming read via CopyAction, and kill VM mid-transfer.
            var v2 = new byte[16 * 1024 * 1024];
            new Random(77).NextBytes(v2);
            File.WriteAllBytes(srcFile, v2);

            using (var accessor = new RemoteFileAccessor($"127.0.0.1:{port}", useTls: true, psk: psk))
            {
                var remotePath = $"{accessor.BaseUri}/big.bin";
                Assert.True(accessor.TryStat(remotePath, out var snap));
                var copyAction = new CopyAction(accessor, FileDurability.Full, log);
                var args = new FileActionArgs(new FileChangedEventArgs(ChangeType.Modified, remotePath), accessor.BaseUri, _hostDest, snap);

                // Kill the VM agent immediately after copy begins so socket drops mid-stream.
                var killTask = Task.Run(() =>
                {
                    Thread.Sleep(5);
                    agent1.Dispose();
                });

                var res = await copyAction.ExecuteAsync(args, CancellationToken.None);
                await killTask;

                // Even if the copy failed mid-stream, destination must NEVER be half-written:
                // either v1 is completely intact (if killed mid-transfer) or v2 is complete (if loopback beat the 5ms timer).
                var onDisk = File.ReadAllBytes(dstFile);
                if (!res.Success)
                    Assert.Equal(v1, onDisk);
                else
                    Assert.Equal(v2, onDisk);
                Assert.Empty(Directory.GetFiles(_hostDest, "*.tictack.tmp"));
            }

            // 3. Restart VM on the same port and confirm host converges to v2.
            using var agent2 = new RemoteAgentServer(_vmRoot, $"127.0.0.1:{port}", "127.0.0.1", 8, useTls: true, psk: psk, log: log);
            agent2.Start();

            using (var p2 = Program.BuildPipeline(resolved, cfg, log))
                Assert.True(await p2.RunOnceAsync());

            Assert.Equal(v2, File.ReadAllBytes(dstFile));
            Assert.Empty(Directory.GetFiles(_hostDest, "*.tictack.tmp"));
        }

        [Fact]
        public async Task ParallelWorkers_ConcurrentRemoteSync_TransfersAllFilesCleanly()
        {
            var psk = RemoteProto.ResolvePsk("crash-psk", null, null);
            var log = new NullLogger();
            var rng = new Random(123);

            for (int i = 0; i < 20; i++)
            {
                var data = new byte[64 * 1024 + i * 1024];
                rng.NextBytes(data);
                File.WriteAllBytes(Path.Combine(_vmRoot, $"file_{i:D2}.bin"), data);
            }

            using var agent = new RemoteAgentServer(_vmRoot, "127.0.0.1:0", "127.0.0.1", 8, useTls: true, psk: psk, log: log);
            agent.Start();

            var cfg = BuildConfig(agent.LocalPort, workers: 4);
            var resolved = Config.ResolveRemoteSources(cfg)[0];

            using var pipeline = Program.BuildPipeline(resolved, cfg, log);
            Assert.True(await pipeline.RunOnceAsync());

            for (int i = 0; i < 20; i++)
            {
                var name = $"file_{i:D2}.bin";
                Assert.Equal(
                    File.ReadAllBytes(Path.Combine(_vmRoot, name)),
                    File.ReadAllBytes(Path.Combine(_hostDest, name)));
            }
        }

        [Fact]
        public async Task ResumableTransfer_MidFileAbort_PreservesTempAndResumesFromOffset()
        {
            var psk = RemoteProto.ResolvePsk("crash-psk", null, null);
            var log = new NullLogger();
            var srcFile = Path.Combine(_vmRoot, "large.bin");
            var dstFile = Path.Combine(_hostDest, "large.bin");
            var tmpFile = dstFile + ".tictack.tmp";
            var resumeFile = tmpFile + ".resume";

            var payload = new byte[2 * 1024 * 1024];
            new Random(313).NextBytes(payload);
            File.WriteAllBytes(srcFile, payload);

            using var agent = new RemoteAgentServer(_vmRoot, "127.0.0.1:0", "127.0.0.1", 8, useTls: true, psk: psk, log: log);
            agent.Start();

            using var accessor = new RemoteFileAccessor($"127.0.0.1:{agent.LocalPort}", useTls: true, psk: psk);
            var remotePath = $"{accessor.BaseUri}/large.bin";
            Assert.True(accessor.TryStat(remotePath, out var snap));

            // 1. Simulate an interrupted copy after 1 MiB landed in .tictack.tmp + .resume sidecar.
            File.WriteAllBytes(tmpFile, payload.AsSpan(0, 1024 * 1024).ToArray());
            File.WriteAllText(resumeFile, $"{snap.Length}|{snap.LastWriteTimeUtcTicks}|{1024 * 1024}");

            // PowerGuard.Cleanup must preserve .tictack.tmp when .resume exists.
            var cfg = BuildConfig(agent.LocalPort);
            PowerGuard.Cleanup(cfg, log);
            Assert.True(File.Exists(tmpFile));
            Assert.True(File.Exists(resumeFile));

            // 2. Resume via CopyAction (verifies OP_HASH_PREFIX and streams only remaining 1 MiB).
            var copyAction = new CopyAction(accessor, FileDurability.Full, log)
            {
                ResumeThresholdBytes = 512 * 1024,
            };
            var args = new FileActionArgs(new FileChangedEventArgs(ChangeType.Created, remotePath), accessor.BaseUri, _hostDest, snap);
            var res = await copyAction.ExecuteAsync(args, CancellationToken.None);

            Assert.True(res.Success);
            Assert.Equal(payload, File.ReadAllBytes(dstFile));
            Assert.False(File.Exists(resumeFile));
        }

        private TicTackConfig BuildConfig(int port, int workers = 2) => new TicTackConfig
        {
            Sources =
            {
                new SourceConfig
                {
                    Path = _vmRoot,
                    Destination = Path.Combine(_tempDir, "unused-local"),
                    DebounceSeconds = 0.2,
                    Sync = new SyncConfig
                    {
                        Verification = "hash",
                        InitialSyncWorkers = workers,
                    },
                }
            },
            RemoteSystem = new RemoteSystemConfig
            {
                Host = $"127.0.0.1:{port}",
                Destination = _hostDest,
                Tls = true,
                Psk = "crash-psk",
            }
        };

        private sealed class NullLogger : ILogger
        {
            public void Debug(string msg) { }
            public void Info(string msg) { }
            public void Warn(string msg) { }
            public void Error(string msg, Exception? ex = null) { }
        }
    }
}
