using System;
using System.IO;
using System.Text;
using Xunit;

namespace Corpus.Tests
{
    public class SecurityHardeningCorpusTests : IDisposable
    {
        private readonly string _tempDir;

        public SecurityHardeningCorpusTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "corpus-sec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(_tempDir, recursive: true);
                }
            }
            catch { }
        }

        [Fact]
        public void WormSeal_SetsReadOnlyOnManifestAndPack()
        {
            var packsDir = Path.Combine(_tempDir, "packs");
            var manifestsDir = Path.Combine(_tempDir, "manifests");
            var key = ChunkId.EnsureKey(_tempDir);

            using (var pw = new PackWriter(packsDir, wormMode: true))
            {
                var data = Encoding.UTF8.GetBytes("worm-test-blob");
                var id = ChunkId.ComputeId(data, key);
                pw.Add(id, data, data.Length, 0);
                pw.Complete();
            }

            var manifest = new SnapshotManifest
            {
                Generation = 1,
                Name = "worm-test",
                ChunkMib = 4,
                VolumeLength = 14,
            };
            var mPath = ManifestStore.Save(manifestsDir, manifest, key, wormMode: true);

            Assert.True((File.GetAttributes(mPath) & FileAttributes.ReadOnly) != 0);
            foreach (var p in Directory.EnumerateFiles(packsDir, "*.pack"))
                Assert.True((File.GetAttributes(p) & FileAttributes.ReadOnly) != 0);
        }

        [Fact]
        public void OffStoreKeyPath_CreatesAndLoadsKeyOutsideStore()
        {
            var storeDir = Path.Combine(_tempDir, "store");
            var extKeyPath = Path.Combine(_tempDir, "keys", "external-id.key");
            Directory.CreateDirectory(storeDir);

            try
            {
                ChunkId.ConfigureOverride(extKeyPath, null);
                var k1 = ChunkId.EnsureKey(storeDir);
                Assert.True(File.Exists(extKeyPath));
                Assert.False(File.Exists(Path.Combine(storeDir, "id.key")));
                var k2 = ChunkId.EnsureKey(storeDir);
                Assert.Equal(k1, k2);
            }
            finally
            {
                ChunkId.ConfigureOverride(null, null);
            }
        }

        [Fact]
        public void AtomicFile_WritesAndLeavesNoTempFiles()
        {
            var path = Path.Combine(_tempDir, "meta.json");
            AtomicFile.WriteAllText(path, "{\"ok\":1}");
            Assert.Equal("{\"ok\":1}", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));

            // overwrite: false throws when target already exists and leaves no .tmp behind.
            Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllText(path, "{\"ok\":2}", overwrite: false));
            Assert.Equal("{\"ok\":1}", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
        }

        [Fact]
        public void StoreImage_BelowMinFreeBytes_FailsFastBeforeWriting()
        {
            var storeRoot = Path.Combine(_tempDir, "low-space-store");
            var vol = Path.Combine(_tempDir, "vol.img");
            File.WriteAllBytes(vol, new byte[1024 * 1024]);

            using var store = new Store(storeRoot, Path.Combine(_tempDir, "db.db"), 1, 3, new TestLogger())
            {
                MinFreeBytes = 512L * 1024 * 1024,
                FreeSpaceOverride = _ => 10L * 1024 * 1024,
            };

            Assert.Throws<IOException>(() => store.Image(new FileBlockSource(vol), "low-space"));
        }
    }
}
