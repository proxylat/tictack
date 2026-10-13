using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DiscUtils.Vhdx;

namespace Corpus;

// File-backed ISparseBlockSource: Length + OpenRead over a seeded file,
// fixed 4 KiB clusters, caller-scripted allocated runs.
internal sealed class FakeSparseSource : ISparseBlockSource
{
    private readonly string _path;
    private readonly IReadOnlyList<(long Start, long Count)> _runs;
    private bool _disposed;

    public FakeSparseSource(string path, IReadOnlyList<(long Start, long Count)> runs)
    {
        _path = path;
        _runs = runs;
    }

    public long Length => new FileInfo(_path).Length;

    public long BytesPerCluster => 4096;

    public IReadOnlyList<(long Start, long Count)> GetAllocatedRuns() => _runs;

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

public sealed class SparseImagingTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private const int Seg = 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public SparseImagingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_sparse_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Store NewStore() => new(_storeRoot, _dbPath, 1, 3, new TestLogger());

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

    private sealed class CountingStream : Stream
    {
        private readonly MemoryStream _inner;
        public int ReadCalls;

        public CountingStream(byte[] data) => _inner = new MemoryStream(data, false);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            return _inner.Read(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Manifest_HoleEntry_RoundTrips()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var manDir = Path.Combine(_dir, "man1");
        var chunks = new List<ManifestEntry>
        {
            ManifestEntry.Hole(Mib),
            new(new string('a', 64), Mib),
            ManifestEntry.Hole(100),
        };
        var m = ManifestStore.Build(1, "sparse", (2L * Mib) + 100, 1, 3, "key-1", "", chunks);
        var path = ManifestStore.Save(manDir, m, key);
        var loaded = ManifestStore.Load(path, key);
        Assert.Equal(3, loaded.Chunks.Count);
        Assert.True(loaded.Chunks[0].IsHole);
        Assert.Equal("", loaded.Chunks[0].DigestHex);
        Assert.Equal((long)Mib, loaded.Chunks[0].Size);
        Assert.False(loaded.Chunks[1].IsHole);
        Assert.True(loaded.Chunks[2].IsHole);
        Assert.Equal(100L, loaded.Chunks[2].Size);
        Assert.Equal(m.Chunks, loaded.Chunks);
        Assert.Equal(m.Hmac, loaded.Hmac);
    }

    [Fact]
    public void Manifest_DenseJson_OmitsIsHole()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var manDir = Path.Combine(_dir, "man2");
        var digest = new string('b', 64);
        var m = ManifestStore.Build(1, "dense", Mib, 1, 3, "key-1", "",
            new List<ManifestEntry> { new(digest, Mib) });
        var json = File.ReadAllText(ManifestStore.Save(manDir, m, key));
        Assert.DoesNotContain("\"IsHole\"", json);
        Assert.Contains(digest, json);
    }

    [Fact]
    public void Manifest_OldDenseJson_LoadsDense()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var manDir = Path.Combine(_dir, "man3");
        Directory.CreateDirectory(manDir);
        var digest = new string('c', 64);
        // Hand-written pre-sparse shape: no IsHole member anywhere, members
        // in SnapshotManifest declaration order so STJ re-serializes byte-identically.
        var body = $$"""{"Generation":1,"Name":"old","CreatedUtc":"2026-01-01T00:00:00.0000000Z","VolumeLength":1048576,"ChunkMib":1,"CompressionLevel":3,"KeyId":"key-1","PrevDigest":"","Chunks":[{"DigestHex":"{{digest}}","Size":1048576}],"Hmac":""}""";
        using var hmac = new HMACSHA256(key);
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var fileJson = body.Replace("\"Hmac\":\"\"", "\"Hmac\":\"" + hex + "\"", StringComparison.Ordinal);
        Assert.DoesNotContain("IsHole", fileJson);
        var path = Path.Combine(manDir, "000001-old.json");
        File.WriteAllText(path, fileJson);
        var loaded = ManifestStore.Load(path, key);
        Assert.Single(loaded.Chunks);
        Assert.False(loaded.Chunks[0].IsHole);
        Assert.Equal(digest, loaded.Chunks[0].DigestHex);
        Assert.Equal(1048576L, loaded.Chunks[0].Size);
    }

    [Fact]
    public void Manifest_DenseJson_ByteStableGolden()
    {
        // The HMAC covers exact serialization bytes, so pin them: member
        // order/set/spacing must never drift. Clock + HMAC vary by design;
        // their presence/shape is asserted, values normalized away.
        var key = new byte[32]; // fixed: determinism is the point
        var manDir = Path.Combine(_dir, "man4");
        var digest = new string('d', 64);
        var m = ManifestStore.Build(1, "dense", Mib, 1, 3, "key-1", "",
            new List<ManifestEntry> { new(digest, Mib) });
        var json = File.ReadAllText(ManifestStore.Save(manDir, m, key));
        var created = ExtractJsonString(json, "CreatedUtc");
        var hmac = ExtractJsonString(json, "Hmac");
        Assert.True(DateTime.TryParse(created, null, DateTimeStyles.RoundtripKind, out _));
        Assert.Matches("^[0-9a-f]{64}$", hmac);
        var normalized = json.Replace(created, "TS", StringComparison.Ordinal)
            .Replace(hmac, "HMAC", StringComparison.Ordinal);
        var expected = $$"""{"Generation":1,"Name":"dense","CreatedUtc":"TS","VolumeLength":1048576,"ChunkMib":1,"CompressionLevel":3,"KeyId":"key-1","PrevDigest":"","Chunks":[{"DigestHex":"{{digest}}","Size":1048576}],"Hmac":"HMAC"}""";
        Assert.Equal(expected, normalized);
    }

    private static string ExtractJsonString(string json, string member)
    {
        var key = "\"" + member + "\":\"";
        var start = json.IndexOf(key, StringComparison.Ordinal) + key.Length;
        Assert.InRange(start, key.Length, json.Length);
        var end = json.IndexOf('"', start);
        Assert.InRange(end, start + 1, json.Length);
        return json.Substring(start, end - start);
    }

    [Fact]
    public void BuildHoleMap_PartialChunk_CapturesWhole()
    {
        const int chunkBytes = 1024 * 1024;
        const long bpc = 4096;
        var volumeLength = (4L * chunkBytes) + 100;
        // One run inside chunk 1 ending exactly at chunk 2's start (boundary:
        // run end == chunk start still leaves chunk 2 a hole); one cluster in
        // the 100-byte tail chunk.
        var runs = new (long Start, long Count)[] { (300, 212), (1024, 1) };
        Assert.Equal(new[] { true, false, true, true, false },
            Chunker.BuildHoleMap(runs, bpc, volumeLength, chunkBytes));
        Assert.Equal(new[] { true, true },
            Chunker.BuildHoleMap(Array.Empty<(long, long)>(), bpc, 2L * chunkBytes, chunkBytes));
    }

    [Fact]
    public void Chunker_ReadSparse_SkipsHoleBytes()
    {
        using var stream = new CountingStream(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        using var e = Chunker.ReadSparse(stream, 4, i => i == 1).GetEnumerator();
        Assert.True(e.MoveNext());
        Assert.False(e.Current.IsHole);
        Assert.Equal(0L, e.Current.Index);
        Assert.Equal(0L, e.Current.Offset);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, e.Current.Data);
        var readsBeforeHole = stream.ReadCalls;
        Assert.True(e.MoveNext());
        Assert.True(e.Current.IsHole);
        Assert.Equal(1L, e.Current.Index);
        Assert.Equal(4L, e.Current.Offset);
        Assert.Empty(e.Current.Data);
        Assert.Equal(readsBeforeHole, stream.ReadCalls);
        Assert.True(e.MoveNext());
        Assert.False(e.Current.IsHole);
        Assert.Equal(2L, e.Current.Index);
        Assert.Equal(8L, e.Current.Offset);
        Assert.Equal(new byte[] { 8, 9 }, e.Current.Data);
        Assert.False(e.MoveNext());
    }

    [Fact]
    public void Image_SparseSource_EmitsHoles()
    {
        var vol = Path.Combine(_dir, "vol6.img");
        var random = RandomChunk(42);
        WriteChunks(vol, new byte[Mib], random, new byte[Mib], random, TextChunk());
        using var store = NewStore();
        using var sparse = new FakeSparseSource(vol, new (long Start, long Count)[] { (256, 256), (768, 512) });
        var m = store.Image(sparse, "sparse");
        Assert.Equal(5, m.Chunks.Count);
        Assert.Equal(5L * Mib, m.VolumeLength);
        Assert.True(m.Chunks[0].IsHole);
        Assert.False(m.Chunks[1].IsHole);
        Assert.True(m.Chunks[2].IsHole);
        Assert.False(m.Chunks[3].IsHole);
        Assert.False(m.Chunks[4].IsHole);
        Assert.Equal("", m.Chunks[0].DigestHex);
        Assert.Equal("", m.Chunks[2].DigestHex);
        Assert.All(m.Chunks, c => Assert.Equal((long)Mib, c.Size));
        Assert.Equal(5L * Mib, m.Chunks.Sum(c => c.Size));
        using var denseSource = new FileBlockSource(vol);
        var d = store.Image(denseSource, "dense");
        foreach (var i in new[] { 1, 3, 4 })
            Assert.Equal(d.Chunks[i].DigestHex, m.Chunks[i].DigestHex);
    }

    [Fact]
    public void Image_MidChunkRun_CapturesWholeChunk()
    {
        var vol = Path.Combine(_dir, "vol6b.img");
        WriteChunks(vol, RandomChunk(1), RandomChunk(2), new byte[Mib]);
        using var store = NewStore();
        // Single cluster at 300 sits mid-chunk-1 (clusters 256-511):
        // whole-chunk rule captures all of chunk 1, holes chunk 2.
        using var sparse = new FakeSparseSource(vol, new (long Start, long Count)[] { (0, 256), (300, 1) });
        var m = store.Image(sparse, "sparse");
        Assert.Equal(3, m.Chunks.Count);
        Assert.False(m.Chunks[0].IsHole);
        Assert.False(m.Chunks[1].IsHole);
        Assert.True(m.Chunks[2].IsHole);
        using var denseSource = new FileBlockSource(vol);
        var d = store.Image(denseSource, "dense");
        Assert.Equal(d.Chunks[1].DigestHex, m.Chunks[1].DigestHex);
        var outPath = Path.Combine(_dir, "export6b.img");
        store.Export(1, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void Image_HoleNeverEntersIndex()
    {
        var vol = Path.Combine(_dir, "vol7.img");
        var random = RandomChunk(7);
        WriteChunks(vol, random, new byte[Mib], random);
        using var store = NewStore();
        using var sparse = new FakeSparseSource(vol, new (long Start, long Count)[] { (0, 256), (512, 256) });
        var m = store.Image(sparse, "sparse");
        Assert.Equal(3, m.Chunks.Count);
        Assert.True(m.Chunks[1].IsHole);
        Assert.Equal(m.Chunks[0].DigestHex, m.Chunks[2].DigestHex);
        Assert.NotEqual("", m.Chunks[0].DigestHex);
        using var idx = new ChunkIndex(_dbPath);
        Assert.False(idx.TryGet(Array.Empty<byte>(), out _));
        Assert.Equal(1L, idx.Count());
    }

    [Fact]
    public void Export_Sparse_RoundTrips()
    {
        var vol = Path.Combine(_dir, "vol8.img");
        WriteChunks(vol, RandomChunk(42), new byte[Mib], TextChunk());
        using var store = NewStore();
        using var sparse = new FakeSparseSource(vol, new (long Start, long Count)[] { (0, 256), (512, 256) });
        var m = store.Image(sparse, "sparse");
        Assert.True(m.Chunks[1].IsHole);
        var outPath = Path.Combine(_dir, "export8.img");
        store.Export(1, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void Verify_CorruptHole_Fails()
    {
        var vol = Path.Combine(_dir, "vol9.img");
        WriteChunks(vol, RandomChunk(42), new byte[Mib], TextChunk());
        using var store = NewStore();
        using var sparse = new FakeSparseSource(vol, new (long Start, long Count)[] { (0, 256), (512, 256) });
        var m = store.Image(sparse, "sparse");
        Assert.True(m.Chunks[1].IsHole);
        var key = ChunkId.EnsureKey(_storeRoot);
        var manDir = Path.Combine(_storeRoot, "manifests");
        m.Chunks[1] = m.Chunks[1] with { DigestHex = new string('d', 64) };
        ManifestStore.Save(manDir, m, key);
        Assert.False(store.Verify(1));
        m.Chunks[1] = m.Chunks[1] with { DigestHex = "", Size = (long)Mib + 1 };
        ManifestStore.Save(manDir, m, key);
        Assert.False(store.Verify(1));
    }

    [Fact]
    public void ExportVhdx_Sparse_HolesReadZero()
    {
        var vol = Path.Combine(_dir, "vol10.img");
        WriteChunks(vol, RandomChunk(42), new byte[Mib], TextChunk());
        using var store = NewStore();
        using var sparse = new FakeSparseSource(vol, new (long Start, long Count)[] { (0, 256), (512, 256) });
        var m = store.Image(sparse, "sparse");
        var vhdxPath = Path.Combine(_dir, "sparse10.vhdx");
        store.ExportVhdx(1, vhdxPath);
        var rawPath = Path.Combine(_dir, "dense10.img");
        store.Export(1, rawPath);
        var expect = File.ReadAllBytes(rawPath);
        using var disk = new Disk(vhdxPath, FileAccess.Read);
        var back = new byte[m.VolumeLength];
        disk.Content.Seek(0, SeekOrigin.Begin);
        var read = 0;
        while (read < back.Length)
        {
            var n = disk.Content.Read(back, read, back.Length - read);
            Assert.True(n > 0, "VHDX content ended early at " + read);
            read += n;
        }
        Assert.Equal(expect, back);
    }

    [Fact]
    public void VssReadStream_SeekPastEnd_FiresCompletion()
    {
        var data = new byte[(3 * Seg) + 117];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 31) + 7);
        using var inner = new MemoryStream(data, false);
        var done = 0;
        using var s = new VssReadStream(inner, data.Length, () => done++, Seg);
        var buf = new byte[5000];
        var got = new List<byte>();
        var n = s.Read(buf, 0, Seg);
        got.AddRange(buf.AsSpan(0, n).ToArray());
        s.Seek(2 * Seg, SeekOrigin.Begin);
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
            got.AddRange(buf.AsSpan(0, n).ToArray());
        Assert.Equal(1, done);
        Assert.True(got.Count < data.Length);
        var expect = data.AsSpan(0, Seg).ToArray().Concat(data.AsSpan(2 * Seg).ToArray()).ToArray();
        Assert.Equal(expect, got.ToArray());

        // Abort path: dispose mid-range never fires.
        using var inner2 = new MemoryStream(data, false);
        var done2 = 0;
        var s2 = new VssReadStream(inner2, data.Length, () => done2++, Seg);
        Assert.Equal(10, s2.Read(new byte[10], 0, 10));
        s2.Dispose();
        Assert.Equal(0, done2);
    }

    [Fact]
    public void Image_SparseEqualsDense_Export()
    {
        var vol = Path.Combine(_dir, "vol12.img");
        WriteChunks(vol, RandomChunk(42), RandomChunk(43), TextChunk());
        using var store = NewStore();
        using (var sparse = new FakeSparseSource(vol, new (long Start, long Count)[] { (0, 768) }))
            store.Image(sparse, "sparse");
        using (var dense = new FileBlockSource(vol))
            store.Image(dense, "dense");
        var sparseOut = Path.Combine(_dir, "sparse12.img");
        var denseOut = Path.Combine(_dir, "dense12.img");
        store.Export(1, sparseOut);
        store.Export(2, denseOut);
        Assert.Equal(File.ReadAllBytes(denseOut), File.ReadAllBytes(sparseOut));
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(sparseOut));
    }
}
