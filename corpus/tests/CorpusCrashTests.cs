using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Xunit;

namespace Corpus.Tests
{
    public sealed class CorpusCrashTests : IDisposable
    {
        private const int Mib = 1024 * 1024;
        private readonly string _dir;

        public CorpusCrashTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "corpus-crash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void ParallelHashPipeline_MatchesSingleThreadByteForByte()
        {
            var vol = Path.Combine(_dir, "vol.img");
            var data = new byte[8 * Mib];
            new Random(99).NextBytes(data);
            File.WriteAllBytes(vol, data);

            var s1Root = Path.Combine(_dir, "s1");
            var s2Root = Path.Combine(_dir, "s2");
            Directory.CreateDirectory(s1Root);
            Directory.CreateDirectory(s2Root);
            var key = ChunkId.EnsureKey(s1Root);
            File.WriteAllBytes(Path.Combine(s2Root, "id.key"), key);

            var log = new TestLogger();
            SnapshotManifest m1, m2;
            using (var store1 = new Store(s1Root, Path.Combine(_dir, "db1.db"), 1, 3, log) { Workers = 1 })
                m1 = store1.Image(new FileBlockSource(vol), "parity");

            using (var store2 = new Store(s2Root, Path.Combine(_dir, "db2.db"), 1, 3, log) { Workers = 4 })
            {
                m2 = store2.Image(new FileBlockSource(vol), "parity");
                Assert.True(store2.Verify(m2.Generation));
            }

            Assert.Equal(m1.Chunks.Count, m2.Chunks.Count);
            for (int i = 0; i < m1.Chunks.Count; i++)
                Assert.Equal(m1.Chunks[i].DigestHex, m2.Chunks[i].DigestHex);
        }

        [Fact]
        public void SubprocessKill_MidImage_PreviousGenIntactAndResumeMatches()
        {
            var corpusDll = FindCorpusDll();
            if (corpusDll == null)
                return; // Skip only if Corpus.dll hasn't been built yet.

            var storeRoot = Path.Combine(_dir, "store");
            var dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
            var log = new TestLogger();

            // 1. Commit Generation 1 cleanly.
            var vol1 = Path.Combine(_dir, "vol1.img");
            var data1 = new byte[2 * Mib];
            new Random(11).NextBytes(data1);
            File.WriteAllBytes(vol1, data1);

            using (var s = new Store(storeRoot, dbPath, 1, 3, log))
            {
                var g1 = s.Image(new FileBlockSource(vol1), "gen1");
                Assert.Equal(1, g1.Generation);
                Assert.True(s.Verify(1));
            }

            // 2. Write a 48 MiB volume and spawn a real Corpus subprocess, then SIGKILL it mid-write.
            var vol2 = Path.Combine(_dir, "vol2.img");
            var data2 = new byte[48 * Mib];
            new Random(22).NextBytes(data2);
            File.WriteAllBytes(vol2, data2);

            var cfgPath = Path.Combine(_dir, "corpus.yaml");
            File.WriteAllText(cfgPath,
                $"store_path: '{storeRoot}'\n" +
                $"state_db_path: '{Path.Combine(_dir, "state")}'\n" +
                $"chunk_mib: 1\n" +
                $"compression:\n  level: 3\n");

            var psi = new ProcessStartInfo("dotnet", $"\"{corpusDll}\" --config \"{cfgPath}\" --image --volume \"{vol2}\" --name gen2")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var proc = Process.Start(psi)!)
            {
                var packsDir = Path.Combine(storeRoot, "packs");
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!proc.HasExited && DateTime.UtcNow < deadline)
                {
                    if (Directory.Exists(packsDir) && Directory.GetFiles(packsDir, "*.tmp").Length > 0)
                        break;
                    Thread.Sleep(15);
                }
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(10_000);
                }
            }

            // 3. Generation 1 MUST still verify cleanly after sudden subprocess kill.
            using (var s = new Store(storeRoot, dbPath, 1, 3, log))
            {
                Assert.True(s.Verify(1));

                // 4. Re-run Generation 2 cleanly and verify integrity.
                var g2 = s.Image(new FileBlockSource(vol2), "gen2-recovered");
                Assert.True(s.Verify(g2.Generation));

                var restored = Path.Combine(_dir, "restored.img");
                s.Export(g2.Generation, restored);
                Assert.Equal(data2, File.ReadAllBytes(restored));
            }
        }

        private static string? FindCorpusDll()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "corpus", "bin", "Debug", "net10.0-windows", "Corpus.dll");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
