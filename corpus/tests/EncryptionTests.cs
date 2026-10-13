using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Corpus;

public sealed class EncryptionTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public EncryptionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_enc_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
        SeedVolume();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // 5 MiB, chunkMib 1: zeros | random | zeros | same random | text.
    // Chunks 0+2 dedup, 1+3 dedup → 3 unique of 5.
    private void SeedVolume() => WriteChunks(_volPath, new byte[Mib], RandomChunk(42), new byte[Mib], RandomChunk(42), TextChunk());

    private Store NewStore(TestLogger log, KeyStore keys) => new(_storeRoot, _dbPath, 1, 3, log, keys);

    private Store NewPlainStore(TestLogger log) => new(_storeRoot, _dbPath, 1, 3, log);

    private long IndexCount()
    {
        using var idx = new ChunkIndex(_dbPath);
        return idx.Count();
    }

    private static byte[] RandomChunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static byte[] TextChunk()
    {
        var text = new byte[Mib];
        var pattern = "The quick brown fox jumps over the lazy dog. "u8;
        for (var i = 0; i < text.Length; i++)
            text[i] = pattern[i % pattern.Length];
        return text;
    }

    private static void WriteChunks(string path, params byte[][] chunks)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var c in chunks)
            f.Write(c, 0, c.Length);
    }

    [Fact]
    public void Encrypt_ImageExportVerify_RoundTrip()
    {
        var (keyId, _) = KeyStore.Generate(_storeRoot);
        var log = new TestLogger();
        using var store = NewStore(log, new KeyStore(_storeRoot));
        using var source = new FileBlockSource(_volPath);
        var m = store.Image(source, "enc1");
        Assert.Equal(keyId, m.EncKeyId);
        Assert.Equal(5, m.Chunks.Count);

        var outPath = Path.Combine(_dir, "export.img");
        store.Export(1, outPath);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void Encrypt_CompressThenEncrypt_Order()
    {
        KeyStore.Generate(_storeRoot);
        var vol = Path.Combine(_dir, "text.img");
        WriteChunks(vol, TextChunk());
        using var store = NewStore(new TestLogger(), new KeyStore(_storeRoot));
        using var source = new FileBlockSource(vol);
        var m = store.Image(source, "text");
        Assert.Single(m.Chunks);
        using var idx = new ChunkIndex(_dbPath);
        Assert.True(idx.TryGet(Convert.FromHexString(m.Chunks[0].DigestHex), out var row));
        Assert.Equal((long)Mib, row.SizeBytes);
        Assert.True(row.StoredBytes < row.SizeBytes, $"stored {row.StoredBytes} should be < plain {row.SizeBytes}");
    }

    [Fact]
    public void Encrypt_CrossKeyNoDedup()
    {
        var idKey = new byte[32];
        var encA = RandomNumberGenerator.GetBytes(32);
        var encB = RandomNumberGenerator.GetBytes(32);
        var plain = TextChunk();
        // The rule lives in one function: same plaintext + different key = different id.
        Assert.Equal(ChunkId.ComputeId(plain, idKey), ChunkId.ComputeId(plain, idKey, null));
        Assert.NotEqual(
            ChunkId.ToHex(ChunkId.ComputeId(plain, idKey, encA)),
            ChunkId.ToHex(ChunkId.ComputeId(plain, idKey, encB)));

        KeyStore.Generate(_storeRoot);
        using (var store = NewStore(new TestLogger(), new KeyStore(_storeRoot)))
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "k1");
        Assert.Equal(3, IndexCount());

        KeyStore.Generate(_storeRoot); // rotate → K2
        using (var store = NewStore(new TestLogger(), new KeyStore(_storeRoot)))
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "k2");
        Assert.Equal(6, IndexCount());
    }

    [Fact]
    public void Encrypt_SameKeyDedups()
    {
        KeyStore.Generate(_storeRoot);
        using var store = NewStore(new TestLogger(), new KeyStore(_storeRoot));
        using var source = new FileBlockSource(_volPath);
        store.Image(source, "first");
        Assert.Equal(3, IndexCount());
        store.Image(source, "second");
        Assert.Equal(3, IndexCount());
        Assert.True(store.Verify(2));
    }

    [Fact]
    public void Encrypt_OldStoreStillReads()
    {
        SnapshotManifest m1;
        using (var store = NewPlainStore(new TestLogger()))
        using (var source = new FileBlockSource(_volPath))
            m1 = store.Image(source, "plain");
        Assert.Null(m1.EncKeyId);

        var (keyId, _) = KeyStore.Generate(_storeRoot);
        using var store2 = NewStore(new TestLogger(), new KeyStore(_storeRoot));
        using var source2 = new FileBlockSource(_volPath);
        var m2 = store2.Image(source2, "enc");
        Assert.Equal(keyId, m2.EncKeyId);

        var oldOut = Path.Combine(_dir, "old.img");
        store2.Export(1, oldOut);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(oldOut));
        Assert.True(store2.Verify(1));
        Assert.True(store2.Verify(2));
    }

    [Fact]
    public void Encrypt_Rotation_OldSnapshotVerifies()
    {
        KeyStore.Generate(_storeRoot);
        using (var store = NewStore(new TestLogger(), new KeyStore(_storeRoot)))
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "k1");

        KeyStore.Generate(_storeRoot); // rotate → K2, K1 retained
        using var store2 = NewStore(new TestLogger(), new KeyStore(_storeRoot));
        Assert.True(store2.Verify(1));
        var outPath = Path.Combine(_dir, "rot.img");
        store2.Export(1, outPath);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
    }

#if false // PENDING rung A (ChangeSource.cs): needs FakeChangeSource + ImageOptions. Restore verbatim in Phase 2.
    [Fact]
    public void Encrypt_RotatedIncremental_MixedKeysVerify()
    {
        var vol = Path.Combine(_dir, "incr.img");
        WriteChunks(vol, TextChunk(), RandomChunk(11), TextChunk());

        KeyStore.Generate(_storeRoot);
        SnapshotManifest parent;
        using (var store = NewStore(new TestLogger(), new KeyStore(_storeRoot)))
        using (var source = new FileBlockSource(vol))
            parent = store.Image(source, "full");

        // Mutate chunk 1 on disk, then rotate before the incremental.
        var mutated = RandomChunk(999);
        using (var f = new FileStream(vol, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            f.Seek(Mib, SeekOrigin.Begin);
            f.Write(mutated, 0, mutated.Length);
        }
        var (k2, _) = KeyStore.Generate(_storeRoot);

        using var store2 = NewStore(new TestLogger(), new KeyStore(_storeRoot));
        using var source2 = new FileBlockSource(vol);
        var fake = new FakeChangeSource((_, _, _) => new IncrementalPlan(new HashSet<long> { 1 }, "fake:1"));
        var child = store2.Image(source2, "incr", new ImageOptions(Incremental: true, ChangeSource: fake));
        Assert.Equal(1, child.ParentGeneration);
        Assert.Equal(k2, child.EncKeyId);
        Assert.Equal(3, child.Chunks.Count);
        Assert.Equal(parent.Chunks[0].DigestHex, child.Chunks[0].DigestHex);
        Assert.Equal(parent.Chunks[2].DigestHex, child.Chunks[2].DigestHex);
        Assert.NotEqual(parent.Chunks[1].DigestHex, child.Chunks[1].DigestHex);
        Assert.True(store2.Verify(2));
        var outPath = Path.Combine(_dir, "incr-out.img");
        store2.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
    }
#endif

    [Fact]
    public void Encrypt_MissingKey_FailsLoud()
    {
        var (keyId, _) = KeyStore.Generate(_storeRoot);
        using (var store = NewStore(new TestLogger(), new KeyStore(_storeRoot)))
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "enc");

        File.Delete(Path.Combine(_storeRoot, "keys", keyId + ".key"));
        var log = new TestLogger();
        using var store2 = NewStore(log, new KeyStore(_storeRoot));
        Assert.False(store2.Verify(1));
        Assert.Contains(log.Messages, m => m.Contains("unavailable"));
        Assert.Throws<InvalidDataException>(() => store2.Export(1, Path.Combine(_dir, "nope.img")));
    }

    [Fact]
    public void Encrypt_WrongKey_Attribution()
    {
        var (keyId, _) = KeyStore.Generate(_storeRoot);
        using (var store = NewStore(new TestLogger(), new KeyStore(_storeRoot)))
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "enc");

        var keyPath = Path.Combine(_storeRoot, "keys", keyId + ".key");
        var orig = File.ReadAllBytes(keyPath);
        var bad = RandomNumberGenerator.GetBytes(32);
        if (bad.SequenceEqual(orig)) bad[0] ^= 0xFF;
        File.WriteAllBytes(keyPath, bad);

        var log = new TestLogger();
        using var store2 = NewStore(log, new KeyStore(_storeRoot));
        Assert.False(store2.Verify(1));
        Assert.Contains(log.Messages, m => m.Contains("decrypt failed"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("content mismatch"));
    }

    [Fact]
    public void Encrypt_HoleBypassesCrypto()
    {
        var vol = Path.Combine(_dir, "hole.img");
        WriteChunks(vol, RandomChunk(42), new byte[Mib], TextChunk());
        KeyStore.Generate(_storeRoot);
        using var store = NewStore(new TestLogger(), new KeyStore(_storeRoot));
        using var sparse = new FakeSparseSource(vol, new (long Start, long Count)[] { (0, 256), (512, 256) });
        var m = store.Image(sparse, "sparse-enc");
        Assert.Equal(3, m.Chunks.Count);
        Assert.True(m.Chunks[1].IsHole);
        Assert.Equal("", m.Chunks[1].DigestHex);
        Assert.Equal(2, IndexCount());
        Assert.True(store.Verify(1));
        var outPath = Path.Combine(_dir, "hole-out.img");
        store.Export(1, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void PackV2_HeaderShape()
    {
        var chunk = TextChunk();
        var vol = Path.Combine(_dir, "v2.img");
        WriteChunks(vol, chunk);
        KeyStore.Generate(_storeRoot);
        var keys = new KeyStore(_storeRoot);
        using (var store = NewStore(new TestLogger(), keys))
        using (var source = new FileBlockSource(vol))
            store.Image(source, "v2");

        using var idx = new ChunkIndex(_dbPath);
        var manifest = ManifestStore.Load(
            Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "*.json")[0],
            ChunkId.EnsureKey(_storeRoot));
        Assert.True(idx.TryGet(Convert.FromHexString(manifest.Chunks[0].DigestHex), out var row));
        var packPath = Path.Combine(_storeRoot, "packs", row.Pack);
        var packBytes = File.ReadAllBytes(packPath);

        var rawCodec = packBytes[row.PackOffset + 8];
        Assert.NotEqual(0, rawCodec & PackWriter.CodecEncryptedFlag);
        Assert.Equal(new byte[] { (byte)'C', (byte)'P', (byte)'K', (byte)'2' }, packBytes[^4..]);

        var blob = PackReader.ReadBlob(packPath, row.PackOffset, (int)row.StoredBytes);
        Assert.Equal((byte)(rawCodec & ~PackWriter.CodecEncryptedFlag), blob.Codec);
        Assert.Equal(12, blob.Nonce.Length);
        Assert.Equal(packBytes.AsSpan((int)(row.PackOffset + 41), 12).ToArray(), blob.Nonce);
        Assert.Equal(chunk, Compression.Decode(ChunkCrypto.Decrypt(blob.Payload, keys.CurrentKey, blob.Nonce), blob.Codec));
    }

    [Fact]
    public void PackV1_StillReads()
    {
        var chunk = TextChunk();
        var vol = Path.Combine(_dir, "v1.img");
        WriteChunks(vol, chunk);
        using (var store = NewPlainStore(new TestLogger()))
        using (var source = new FileBlockSource(vol))
            store.Image(source, "v1");

        using var idx = new ChunkIndex(_dbPath);
        var manifest = ManifestStore.Load(
            Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "*.json")[0],
            ChunkId.EnsureKey(_storeRoot));
        Assert.True(idx.TryGet(Convert.FromHexString(manifest.Chunks[0].DigestHex), out var row));
        var packPath = Path.Combine(_storeRoot, "packs", row.Pack);
        var packBytes = File.ReadAllBytes(packPath);

        Assert.Equal(0, packBytes[row.PackOffset + 8] & PackWriter.CodecEncryptedFlag);
        Assert.Equal(new byte[] { (byte)'C', (byte)'P', (byte)'K', (byte)'1' }, packBytes[^4..]);

        var blob = PackReader.ReadBlob(packPath, row.PackOffset, (int)row.StoredBytes);
        Assert.Empty(blob.Nonce);
        Assert.Equal(chunk, Compression.Decode(blob.Payload, blob.Codec));
    }

    // CLI key verbs (--gen-key / --key-list) via the built Corpus.dll,
    // same dotnet-host pattern as CrashRecoveryTests.StartWorker.

    [Fact]
    public void Cli_GenKey_OutputShape()
    {
        var cfg = WriteCliConfig(Path.Combine(_dir, "cli1"));
        var (exit, stdout, _) = RunCorpus("--config", cfg, "--gen-key");
        Assert.Equal(0, exit);
        var match = Regex.Match(stdout, @"encryption key ([0-9a-f]{8}) active");
        Assert.True(match.Success, "stdout was: " + stdout);
        Assert.Contains("existing snapshots unchanged", stdout);
        var keyHex = Convert.ToHexString(File.ReadAllBytes(
            Path.Combine(_dir, "cli1", "store", "keys", match.Groups[1].Value + ".key"))).ToLowerInvariant();
        Assert.DoesNotContain(keyHex, stdout.ToLowerInvariant());
    }

    [Fact]
    public void Cli_KeyList_Empty()
    {
        var cfg = WriteCliConfig(Path.Combine(_dir, "cli2"));
        var (exit, stdout, _) = RunCorpus("--config", cfg, "--key-list");
        Assert.Equal(0, exit);
        Assert.Contains("no encryption keys (store is unencrypted)", stdout);
    }

    [Fact]
    public void Cli_KeyList_NonEmpty()
    {
        var home = Path.Combine(_dir, "cli3");
        var cfg = WriteCliConfig(home);
        Assert.Equal(0, RunCorpus("--config", cfg, "--gen-key").Exit);
        Assert.Equal(0, RunCorpus("--config", cfg, "--gen-key").Exit);
        var (exit, stdout, _) = RunCorpus("--config", cfg, "--key-list");
        Assert.Equal(0, exit);
        var ids = Directory.GetFiles(Path.Combine(home, "store", "keys"), "*.key")
            .Select(p => Path.GetFileNameWithoutExtension(p)).Order().ToList();
        Assert.Equal(2, ids.Count);
        Assert.True(
            stdout.IndexOf(ids[0], StringComparison.Ordinal) < stdout.IndexOf(ids[1], StringComparison.Ordinal),
            "stdout was: " + stdout);
        var current = File.ReadAllText(Path.Combine(home, "store", "keys", "current")).Trim();
        var line = stdout.Split('\n').First(l => l.Contains(current));
        Assert.Contains("current", line);
    }

    private string WriteCliConfig(string home)
    {
        var storeRoot = Path.Combine(home, "store");
        var state = Path.Combine(home, "state");
        Directory.CreateDirectory(state);
        var cfg = Path.Combine(home, "corpus.yaml");
        File.WriteAllText(cfg,
            "store_path: '" + storeRoot + "'\n" +
            "state_db_path: '" + state + "'\n" +
            "chunk_mib: 1\n" +
            "compression:\n  level: 3\n" +
            "logging:\n  level: info\n  path: '" + Path.Combine(home, "corpus.log") + "'\n  console: false\n");
        return cfg;
    }

    private static string CorpusDll()
    {
        var baseDir = AppContext.BaseDirectory;
        var configuration = new DirectoryInfo(baseDir).Parent!.Name;
        var tfm = new DirectoryInfo(baseDir).Name;
        var dll = Path.GetFullPath(Path.Combine(baseDir,
            "../../../../bin/" + configuration + "/" + tfm + "/Corpus.dll"));
        Assert.True(File.Exists(dll), "Corpus was not built: " + dll);
        return dll;
    }

    private static (int Exit, string Stdout, string Stderr) RunCorpus(params string[] args)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = host,
            WorkingDirectory = Path.GetDirectoryName(CorpusDll())!,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add(CorpusDll());
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        using var proc = Process.Start(startInfo)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(120_000), "Corpus CLI timed out: " + string.Join(' ', args));
        return (proc.ExitCode, stdout, stderr);
    }
}
