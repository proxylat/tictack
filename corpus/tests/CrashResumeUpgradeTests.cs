using System;
using System.IO;
using Xunit;

namespace Corpus.Tests
{
    public sealed class CrashResumeUpgradeTests : IDisposable
    {
        private const int Mib = 1024 * 1024;
        private readonly string _dir;

        public CrashResumeUpgradeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "corpus-upgrade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void WholeImageFitCheck_RefusesUpfrontWhenInsufficientSpace()
        {
            var storeRoot = Path.Combine(_dir, "store-fit");
            var vol = Path.Combine(_dir, "vol.img");
            var data = new byte[4 * Mib];
            new Random(7).NextBytes(data);
            File.WriteAllBytes(vol, data);

            using var store = new Store(storeRoot, Path.Combine(_dir, "fit.db"), 1, 3, new TestLogger())
            {
                StrictFitCheck = true,
                FreeSpaceOverride = _ => 2 * Mib,
            };

            var ex = Assert.Throws<IOException>(() => store.Image(new FileBlockSource(vol), "fit-fail"));
            Assert.Contains("insufficient for planned", ex.Message);
        }

        [Fact]
        public void RecoverOrphanPacks_ReindexesSealedCpk1PackAndAvoidsRewriting()
        {
            var storeRoot = Path.Combine(_dir, "store-orphan");
            var packsDir = Path.Combine(storeRoot, "packs");
            var vol = Path.Combine(_dir, "vol.img");
            var data = new byte[4 * Mib];
            new Random(55).NextBytes(data);
            File.WriteAllBytes(vol, data);

            var key = ChunkId.EnsureKey(storeRoot);
            using (var pw = new PackWriter(packsDir, maxPackBytes: 2 * Mib))
            {
                for (int i = 0; i < 4; i++)
                {
                    var slice = data.AsSpan(i * Mib, Mib).ToArray();
                    var digest = ChunkId.ComputeId(slice, key);
                    pw.Add(digest, slice, slice.Length, 0);
                }
                pw.Complete();
            }

            // Simulate a crash right after PackWriter sealed packs/ but before ChunkIndex upsert.
            int packsBefore = Directory.GetFiles(packsDir, "pack-*.pack").Length;
            Assert.True(packsBefore >= 2);

            using var store = new Store(storeRoot, Path.Combine(_dir, "orphan.db"), 1, 3, new TestLogger());
            int recovered = store.RecoverOrphanPacks();
            Assert.Equal(4, recovered);

            // Imaging the same volume now finds all 4 chunks already indexed -> 0 new packs written.
            var m = store.Image(new FileBlockSource(vol), "after-recover");
            Assert.Equal(packsBefore, Directory.GetFiles(packsDir, "pack-*.pack").Length);
            Assert.True(store.Verify(m.Generation));
        }
    }
}
