using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using Xunit;

namespace TicTack.Tests
{
    public class SecurityHardeningTests : IDisposable
    {
        private readonly string _tempDir;

        public SecurityHardeningTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "tictack-sec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        [Fact]
        public void ConfigGuard_FlagsPermissiveFiles_AndStrictFails()
        {
            if (OperatingSystem.IsWindows()) return; // POSIX mode bits test

            var log = new ListLogger();
            var cfg = Path.Combine(_tempDir, "config.yaml");
            var key = Path.Combine(_tempDir, "psk.key");
            File.WriteAllText(cfg, "sources: []");
            File.WriteAllText(key, "super-secret");

            File.SetUnixFileMode(cfg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
            File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

            Assert.True(ConfigGuard.CheckConfigFile(cfg, log, strict: false));
            Assert.False(ConfigGuard.CheckConfigFile(cfg, log, strict: true));
            Assert.False(ConfigGuard.CheckSecretFile(key, log, strict: true));

            File.SetUnixFileMode(cfg, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.True(ConfigGuard.CheckConfigFile(cfg, log, strict: true));
            Assert.True(ConfigGuard.CheckSecretFile(key, log, strict: true));
        }

        [Fact]
        public void ProcessHardening_Apply_DoesNotThrow()
        {
            var log = new ListLogger();
            ProcessHardening.Apply(log, blockChildProcesses: false);
            Assert.Empty(log.Errors);
        }

        [Fact]
        public void RemoteAgent_AuthLockout_BansPeerAfterFiveFailures()
        {
            var root = Path.Combine(_tempDir, "root");
            Directory.CreateDirectory(root);
            var psk = RemoteProto.ResolvePsk("real-secret", null, null);
            var wrong = RemoteProto.ResolvePsk("wrong-secret", null, null);
            var log = new ListLogger();

            using var agent = new RemoteAgentServer(root, "127.0.0.1:0", "127.0.0.1", 4, useTls: false, psk: psk, log: log);
            agent.Start();

            for (int i = 0; i < 5; i++)
            {
                using var tcp = new TcpClient();
                tcp.Connect("127.0.0.1", agent.LocalPort);
                Assert.ThrowsAny<Exception>(() => RemoteProto.AuthClient(tcp.GetStream(), tcp.Client, wrong));
            }

            // 6th attempt even with valid PSK should be dropped immediately on accept while banned.
            using (var bannedTcp = new TcpClient())
            {
                bannedTcp.Connect("127.0.0.1", agent.LocalPort);
                Assert.ThrowsAny<Exception>(() => RemoteProto.AuthClient(bannedTcp.GetStream(), bannedTcp.Client, psk));
            }
        }

        private sealed class ListLogger : ILogger
        {
            public List<string> Errors { get; } = new List<string>();
            public void Debug(string msg) { }
            public void Info(string msg) { }
            public void Warn(string msg) { }
            public void Error(string msg, Exception? ex = null) => Errors.Add(msg);
        }
    }
}
