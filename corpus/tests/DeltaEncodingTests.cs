using System.Buffers.Binary;

namespace Corpus;

// §12.7 phases 2-6 behind the IDeltaCodec seam (corpus/DeltaCodec.cs): the
// real codec is picked post-spike, so every test here drives Store +
// SnapshotVolumeReader through this fake. Framing: [runCount u32 LE]
// [offset u32 LE, len u32 LE, bytes]* over equal-length chunks, runs merged
// across gaps < 16 bytes, >1024 merged runs = no compact patch.
internal sealed class FakeDeltaCodec : IDeltaCodec
{
    // Test-only id: production takes 2 (ZstdDeltaCodec, the first delta
    // id); the fake stays off the product allocation so the two never
    // share a codec byte in one store.
    internal const byte Id = 9;
    public byte CodecId => Id;

    public bool TryCreatePatch(byte[] baseWhole, byte[] current, out byte[] patch)
    {
        patch = Array.Empty<byte>();
        if (baseWhole.Length != current.Length || baseWhole.Length == 0)
            return false;
        var runs = new List<(int Off, int Len)>();
        var n = baseWhole.Length;
        var i = 0;
        while (i < n)
        {
            if (baseWhole[i] == current[i])
            {
                i++;
                continue;
            }
            var start = i;
            while (i < n && baseWhole[i] != current[i])
                i++;
            runs.Add((start, i - start));
            if (runs.Count > 4096)
                return false;
        }
        var merged = new List<(int Off, int Len)>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && r.Off - (merged[^1].Off + merged[^1].Len) < 16)
            {
                var p = merged[^1];
                merged[^1] = (p.Off, r.Off + r.Len - p.Off);
            }
            else
            {
                merged.Add(r);
            }
            if (merged.Count > 1024)
                return false;
        }
        var buf = new byte[4 + merged.Sum(r => 8 + r.Len)];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), merged.Count);
        var p2 = 4;
        foreach (var (off, len) in merged)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(p2, 4), off);
            p2 += 4;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(p2, 4), len);
            p2 += 4;
            Buffer.BlockCopy(current, off, buf, p2, len);
            p2 += len;
        }
        patch = buf;
        return true;
    }

    public byte[] ApplyPatch(byte[] baseWhole, byte[] patch)
    {
        if (patch.Length < 4)
            throw new InvalidDataException("corrupt delta patch (truncated header)");
        var count = BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(0, 4));
        if (count < 0 || count > 1024)
            throw new InvalidDataException("corrupt delta patch (run count)");
        var result = (byte[])baseWhole.Clone();
        var p = 4;
        for (var r = 0; r < count; r++)
        {
            if (p + 8 > patch.Length)
                throw new InvalidDataException("corrupt delta patch (truncated run)");
            var off = BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(p, 4));
            var len = BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(p + 4, 4));
            p += 8;
            if (off < 0 || len < 0 || off + len > result.Length || p + len > patch.Length)
                throw new InvalidDataException("corrupt delta patch (run out of range)");
            Buffer.BlockCopy(patch, p, result, off, len);
            p += len;
        }
        if (p != patch.Length)
            throw new InvalidDataException("corrupt delta patch (trailing bytes)");
        return result;
    }
}

public sealed class DeltaEncodingTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public DeltaEncodingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_delta_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Store NewStore(TestLogger? log = null, IDeltaCodec? codec = null) =>
        new(_storeRoot, _dbPath, 1, 3, log ?? new TestLogger(), null, codec ?? new FakeDeltaCodec());

    private static FakeChangeSource Dirty(params long[] chunks) =>
        new FakeChangeSource((_, _, _) => new IncrementalPlan(new HashSet<long>(chunks), "fake:1"));

    private SnapshotManifest LoadGen(int gen)
    {
        var manDir = Path.Combine(_storeRoot, "manifests");
        var paths = Directory.GetFiles(manDir, $"{gen:D6}-*.json");
        Assert.Single(paths);
        return ManifestStore.Load(paths[0], ChunkId.EnsureKey(_storeRoot));
    }

    private static byte[] RandomChunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static byte[] TextChunk(byte start = (byte)'a')
    {
        var text = new byte[Mib];
        for (var i = 0; i < text.Length; i++)
            text[i] = (byte)(start + (i % 26));
        return text;
    }

    private static void WriteChunks(string path, params byte[][] chunks)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var c in chunks)
            f.Write(c, 0, c.Length);
    }

    private static void MutateByte(string path, long offset, byte value)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        f.Seek(offset, SeekOrigin.Begin);
        f.WriteByte(value);
    }

    private static byte[] ReadVolumeReader(string storeRoot, string dbPath, SnapshotManifest manifest, IDeltaCodec codec)
    {
        using var vol = new SnapshotVolumeReader(storeRoot, dbPath, manifest, codec);
        return vol.ReadExact(0, (int)vol.Length);
    }

    [Fact]
    public void Delta_RoundTrip_AllFourPaths()
    {
        WriteChunks(_volPath, TextChunk(), TextChunk(10), RandomChunk(7));
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent");
        var parent = LoadGen(1);

        MutateByte(_volPath, 12345, 0xFF);
        var child = store.Image(new FileBlockSource(_volPath), "child",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));

        var entry = child.Chunks[0];
        Assert.Equal(parent.Chunks[0].DigestHex, entry.BaseDigestHex);
        Assert.Equal(FakeDeltaCodec.Id, entry.DeltaCodec);
        Assert.Equal(parent.Chunks[0].Size, entry.Size);
        // Untouched chunks stay whole.
        Assert.Null(child.Chunks[1].BaseDigestHex);
        Assert.Null(child.Chunks[2].BaseDigestHex);

        // Restore + export share WriteSnapshotToStream; exercise both the
        // stream funnel (restore path) and the file export.
        using (var ms = new MemoryStream())
        {
            store.ExportToStream(2, ms);
            Assert.Equal(File.ReadAllBytes(_volPath), ms.ToArray());
        }
        var outPath = Path.Combine(_dir, "out.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
        // Full re-read verify reconstructs the delta.
        Assert.True(store.Verify(2));
        // Extract path (SnapshotVolumeReader fetch + prefetch).
        Assert.Equal(File.ReadAllBytes(_volPath), ReadVolumeReader(_storeRoot, _dbPath, child, new FakeDeltaCodec()));
    }

    [Fact]
    public void Delta_SmallerWins_BothDirections()
    {
        WriteChunks(_volPath, TextChunk(), RandomChunk(11));
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent");

        // Chunk 0: 1-byte edit on compressible data → delta must win.
        MutateByte(_volPath, 999, 0x42);
        // Chunk 1: wholly unrelated random bytes → runs explode, whole wins.
        using (var f = new FileStream(_volPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            f.Seek(Mib, SeekOrigin.Begin);
            var rep = RandomChunk(12);
            f.Write(rep, 0, rep.Length);
        }
        var child = store.Image(new FileBlockSource(_volPath), "child",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0, 1)));

        Assert.NotNull(child.Chunks[0].BaseDigestHex);
        Assert.Null(child.Chunks[1].BaseDigestHex);
        Assert.Equal(0, child.Chunks[1].DeltaCodec);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(ExportTo(child.Generation, store)));
        Assert.True(store.Verify(child.Generation));
    }

    [Fact]
    public void Delta_ParentDelta_NeverABase()
    {
        WriteChunks(_volPath, TextChunk(), TextChunk(20));
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "gen1");

        MutateByte(_volPath, 100, 0x01);
        var gen2 = store.Image(new FileBlockSource(_volPath), "gen2",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        Assert.NotNull(gen2.Chunks[0].BaseDigestHex);

        // Parent-gen same-index chunk is a delta → capture must store whole,
        // never a delta-of-delta (depth-2 unproducible through capture).
        MutateByte(_volPath, 200, 0x02);
        var gen3 = store.Image(new FileBlockSource(_volPath), "gen3",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        Assert.Null(gen3.Chunks[0].BaseDigestHex);
        Assert.Equal(0, gen3.Chunks[0].DeltaCodec);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(ExportTo(3, store)));
        Assert.True(store.Verify(3));
    }

    [Fact]
    public void Delta_ForcedDepth2_FailsLoudNamingChunk()
    {
        WriteChunks(_volPath, TextChunk(), TextChunk(30));
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "gen1");
        MutateByte(_volPath, 100, 0x01);
        var gen2 = store.Image(new FileBlockSource(_volPath), "gen2",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        var deltaDigest = gen2.Chunks[0].DigestHex;
        Assert.NotNull(gen2.Chunks[0].BaseDigestHex);

        // Hand-craft the forbidden state: base points at a delta blob.
        var forged = new List<ManifestEntry>(gen2.Chunks);
        forged[0] = new ManifestEntry(deltaDigest, gen2.Chunks[0].Size, BaseDigestHex: deltaDigest, DeltaCodec: FakeDeltaCodec.Id);
        var gen3 = ManifestStore.Build(3, "forged", gen2.VolumeLength, 1, 3,
            ChunkId.KeyId(ChunkId.EnsureKey(_storeRoot)), gen2.Hmac, forged, parentGeneration: 2);
        var manDir = Path.Combine(_storeRoot, "manifests");
        ManifestStore.Save(manDir, gen3, ChunkId.EnsureKey(_storeRoot));

        var ex = Assert.Throws<InvalidDataException>(() => store.ExportToStream(3, Stream.Null));
        Assert.Contains("chunk 0", ex.Message);
        Assert.False(store.Verify(3));
    }

    [Fact]
    public void Delta_GcPinsBase()
    {
        WriteChunks(_volPath, TextChunk(), TextChunk(40));
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "gen1");
        MutateByte(_volPath, 100, 0x01);
        var gen2 = store.Image(new FileBlockSource(_volPath), "gen2",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        Assert.NotNull(gen2.Chunks[0].BaseDigestHex);

        // Drop gen1; the delta in gen2 must pin its base chunks.
        var log = new TestLogger();
        var gc = new GarbageCollector(_storeRoot, _dbPath,
            new CorpusRetentionConfig { KeepLast = 1 }, log);
        var plan = gc.Plan();
        Assert.Contains(plan.DropManifests, p => p.Contains("000001-"));
        gc.Execute(plan);

        Assert.True(store.Verify(2));
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(ExportTo(2, store)));
        Assert.Equal(File.ReadAllBytes(_volPath),
            ReadVolumeReader(_storeRoot, _dbPath, LoadGen(2), new FakeDeltaCodec()));
    }

    [Fact]
    public void Delta_CorruptPatch_FailsLoudNamingChunk()
    {
        WriteChunks(_volPath, TextChunk(), TextChunk(50));
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "gen1");
        MutateByte(_volPath, 100, 0x01);
        var gen2 = store.Image(new FileBlockSource(_volPath), "gen2",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        Assert.NotNull(gen2.Chunks[0].BaseDigestHex);

        // Flip a byte inside the patch region (past the 41B blob header +
        // 32B base digest prefix) of the delta's pack blob.
        var digest = Convert.FromHexString(gen2.Chunks[0].DigestHex);
        ChunkRow row;
        using (var index = new ChunkIndex(_dbPath))
            Assert.True(index.TryGet(digest, out row));
        var packPath = Path.Combine(_storeRoot, "packs", row.Pack);
        var packBytes = File.ReadAllBytes(packPath);
        packBytes[row.PackOffset + PackWriter.BlobHeaderBytes + 32] ^= 0xFF;
        File.WriteAllBytes(packPath, packBytes);

        var ex = Assert.Throws<InvalidDataException>(() => store.ExportToStream(2, Stream.Null));
        Assert.Contains("chunk 0", ex.Message);
        Assert.False(store.Verify(2));
    }

    [Fact]
    public void Delta_OldReader_RefusesLoud()
    {
        WriteChunks(_volPath, TextChunk(), TextChunk(60));
        using (var store = NewStore())
        {
            using (var source = new FileBlockSource(_volPath))
                store.Image(source, "gen1");
            MutateByte(_volPath, 100, 0x01);
            store.Image(new FileBlockSource(_volPath), "gen2",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));
        }
        Assert.NotNull(LoadGen(2).Chunks[0].BaseDigestHex);

        // No codec wired = old reader: every read path refuses loud.
        using var old = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger());
        var ex = Assert.Throws<InvalidDataException>(() => old.ExportToStream(2, Stream.Null));
        Assert.Contains("chunk 0", ex.Message);
        Assert.False(old.Verify(2));
        using (var vol = new SnapshotVolumeReader(_storeRoot, _dbPath, LoadGen(2)))
            Assert.Throws<InvalidDataException>(() => vol.ReadExact(0, 64));
        // Codec framing itself is the refusal: unknown bytes throw.
        Assert.Throws<InvalidDataException>(() => Compression.Decode(new byte[10], FakeDeltaCodec.Id));
    }

    // Post-swap: the REAL codec (ZstdDeltaCodec) drives capture +
    // all four read paths. 1-byte edit on a 1MiB RANDOM chunk must store
    // a delta (~1KiB patch + 32B base prefix, far under the ~1MiB whole)
    // and reconstruct byte-identical with a green hash-checked verify.
    // (On highly-compressible data whole-compressed can beat patch+32B —
    // smaller-wins then stores whole, by design — so the edited chunk is
    // random, like the high-entropy VM images this targets.)
    [Fact]
    public void RealCodec_RoundTrip_PostSwap()
    {
        WriteChunks(_volPath, RandomChunk(7), TextChunk(10), RandomChunk(8));
        using var store = NewStore(codec: new ZstdDeltaCodec());
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "parent");
        var parent = LoadGen(1);

        MutateByte(_volPath, 12345, 0xFF);
        var child = store.Image(new FileBlockSource(_volPath), "child",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(0)));

        var entry = child.Chunks[0];
        Assert.Equal(parent.Chunks[0].DigestHex, entry.BaseDigestHex);
        Assert.Equal(2, entry.DeltaCodec);
        Assert.Null(child.Chunks[1].BaseDigestHex);
        Assert.Null(child.Chunks[2].BaseDigestHex);

        // The stored delta blob (incl. the 32B base prefix) must beat the
        // 1MiB whole chunk — otherwise capture would have stored whole.
        var digest = Convert.FromHexString(entry.DigestHex);
        ChunkRow row;
        using (var index = new ChunkIndex(_dbPath))
            Assert.True(index.TryGet(digest, out row));
        Assert.True(row.StoredBytes < Mib, $"delta blob {row.StoredBytes} should beat the 1MiB whole chunk");

        using (var ms = new MemoryStream())
        {
            store.ExportToStream(2, ms);
            Assert.Equal(File.ReadAllBytes(_volPath), ms.ToArray());
        }
        Assert.True(store.Verify(2));
        Assert.Equal(File.ReadAllBytes(_volPath),
            ReadVolumeReader(_storeRoot, _dbPath, child, new ZstdDeltaCodec()));
    }

    private string ExportTo(int generation, Store store)
    {
        var outPath = Path.Combine(_dir, $"gen{generation}-out.img");
        store.Export(generation, outPath);
        return outPath;
    }
}
