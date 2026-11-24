using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace TicTack.Tests
{
    public class RemoteAgentTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _remoteRoot;
        private readonly string _destDir;
        private readonly string _outsideDir;

        public RemoteAgentTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "tictack-remote-test-" + Guid.NewGuid().ToString("N"));
            _remoteRoot = Path.Combine(_tempDir, "vm-root");
            _destDir = Path.Combine(_tempDir, "host-dest");
            _outsideDir = Path.Combine(_tempDir, "outside");
            Directory.CreateDirectory(_remoteRoot);
            Directory.CreateDirectory(_destDir);
            Directory.CreateDirectory(_outsideDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        [Fact]
        public void ResolveJailedPath_RejectsTraversalUncAdsAndSymlinkEscape()
        {
            var log = new NullLogger();
            using var agent = new RemoteAgentServer(_remoteRoot, "127.0.0.1:0", "127.0.0.1", 2, false, Array.Empty<byte>(), log);

            Assert.Throws<UnauthorizedAccessException>(() => agent.ResolveJailedPath("../outside/secret.txt"));
            Assert.Throws<UnauthorizedAccessException>(() => agent.ResolveJailedPath("sub/../../outside/secret.txt"));
            Assert.Throws<UnauthorizedAccessException>(() => agent.ResolveJailedPath("/etc/passwd"));
            Assert.Throws<UnauthorizedAccessException>(() => agent.ResolveJailedPath(@"\\.\pipe\test"));
            Assert.Throws<UnauthorizedAccessException>(() => agent.ResolveJailedPath("file.txt:ads"));

            var outsideFile = Path.Combine(_outsideDir, "secret.txt");
            File.WriteAllText(outsideFile, "secret");
            var linkPath = Path.Combine(_remoteRoot, "escape-link.txt");
            try
            {
                File.CreateSymbolicLink(linkPath, outsideFile);
                Assert.Throws<UnauthorizedAccessException>(() => agent.ResolveJailedPath("escape-link.txt"));
            }
            catch (IOException)
            {
                // Unprivileged Windows without Developer Mode cannot create symlinks; skip symlink check there.
            }

            var ok = agent.ResolveJailedPath("sub/good.txt");
            Assert.StartsWith(_remoteRoot, ok);
        }

        [Fact]
        public void Config_RemoteSystem_InheritsAndOverridesOnlyRemote()
        {
            var yamlPath = Path.Combine(_tempDir, "config.yaml");
            File.WriteAllText(yamlPath, """
                sources:
                  - path: /tmp/local-src
                    destination: /tmp/local-dst
                    debounce_seconds: 20
                    filter:
                      exclude:
                        - "*.tmp"
                    sync:
                      verification: date_and_size
                remote_sync:
                  host: "192.168.122.50:27200"
                  destination: /tmp/vm-dst
                  debounce_seconds: 10
                  psk: "test-secret"
                """);

            var cfg = Config.Load(yamlPath);
            Assert.NotNull(cfg);
            Assert.Equal(20, cfg!.Sources[0].DebounceSeconds);

            var resolved = Config.ResolveRemoteSources(cfg);
            Assert.Single(resolved);
            Assert.Equal(10, resolved[0].DebounceSeconds);
            Assert.Equal(20, cfg.Sources[0].DebounceSeconds);
            Assert.Contains("*.tmp", resolved[0].Filter.Exclude);
            Assert.Equal("date_and_size", resolved[0].Sync.Verification);
            Assert.Equal("remote://192.168.122.50:27200", resolved[0].Path);
        }

        [Fact]
        public async Task EndToEnd_RemoteSync_MirrorsAndValidatesHash()
        {
            var sub = Path.Combine(_remoteRoot, "docs");
            Directory.CreateDirectory(sub);
            var f1 = Path.Combine(_remoteRoot, "hello.txt");
            var f2 = Path.Combine(sub, "nested.bin");
            File.WriteAllText(f1, "hello from vm", Encoding.UTF8);
            var bin = new byte[128 * 1024];
            new Random(42).NextBytes(bin);
            File.WriteAllBytes(f2, bin);

            var psk = RemoteProto.ResolvePsk("vm-sync-psk", null, null);
            var log = new NullLogger();
            using var agent = new RemoteAgentServer(_remoteRoot, "127.0.0.1:0", "127.0.0.1", 4, useTls: true, psk: psk, log: log);
            agent.Start();

            var cfg = new TicTackConfig
            {
                Sources =
                {
                    new SourceConfig
                    {
                        Path = _remoteRoot,
                        Destination = Path.Combine(_tempDir, "unused-local"),
                        DebounceSeconds = 0.2,
                        Sync = new SyncConfig { Verification = "hash" },
                    }
                },
                RemoteSync = new RemoteSystemConfig
                {
                    Host = $"127.0.0.1:{agent.LocalPort}",
                    Destination = _destDir,
                    Tls = true,
                    Psk = "vm-sync-psk",
                }
            };

            var resolved = Config.ResolveRemoteSources(cfg);
            Assert.Single(resolved);
            using var pipeline = Program.BuildPipeline(resolved[0], cfg, log);
            Assert.True(await pipeline.RunOnceAsync());

            Assert.Equal("hello from vm", File.ReadAllText(Path.Combine(_destDir, "hello.txt"), Encoding.UTF8));
            Assert.Equal(bin, File.ReadAllBytes(Path.Combine(_destDir, "docs", "nested.bin")));
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
