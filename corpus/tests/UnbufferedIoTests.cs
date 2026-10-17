namespace Corpus;

// §12.12: byte-identical output buffered vs unbuffered (structural: same
// bytes, different path), misalignment handling pinned, opt-out honored.
public sealed class UnbufferedIoTests
{
    private static string SeedFile(long size, int seed)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ubio-{Guid.NewGuid():N}.bin");
        var rng = new Random(seed);
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buf = new byte[65536];
        var left = size;
        while (left > 0)
        {
            rng.NextBytes(buf);
            var take = (int)Math.Min(buf.Length, left);
            fs.Write(buf, 0, take);
            left -= take;
        }
        return path;
    }

    private static byte[] ReadAll(Stream s, int bufSize)
    {
        using var ms = new MemoryStream();
        var buf = new byte[bufSize];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    private static byte[] ReadFully(string path, bool useUnbuffered, int bufSize)
    {
        using var s = ExtentStreams.OpenExtentRead(path, useUnbuffered);
        return ReadAll(s, bufSize);
    }

    [Fact]
    public void ExtentRead_SeededData_BufferedEqualsUnbuffered()
    {
        var path = SeedFile(2 * 1024 * 1024 + 12345, seed: 0x12C);
        try
        {
            var expected = File.ReadAllBytes(path);
            // Odd buffer sizes: sector straddles + non-multiple tails.
            Assert.Equal(expected, ReadFully(path, useUnbuffered: false, bufSize: 1000));
            Assert.Equal(expected, ReadFully(path, useUnbuffered: true, bufSize: 1000));
            Assert.Equal(expected, ReadFully(path, useUnbuffered: true, bufSize: 65536));
            Assert.Equal(expected, ReadFully(path, useUnbuffered: true, bufSize: 7));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtentRead_ChunkedNonSectorGrid_BufferedEqualsUnbuffered()
    {
        var path = SeedFile(1024 * 1024 + 777, seed: 99);
        try
        {
            static List<byte[]> Chunks(string p, bool unbuf)
            {
                using var s = ExtentStreams.OpenExtentRead(p, unbuf);
                return Chunker.Read(s, 100_000).Select(c => c.Data).ToList();
            }
            var a = Chunks(path, false);
            var b = Chunks(path, true);
            Assert.Equal(a.Count, b.Count);
            Assert.All(a.Zip(b), pair => Assert.Equal(pair.First, pair.Second));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtentRead_OptOut_ReturnsPlainFileStream()
    {
        var path = SeedFile(4096 + 13, seed: 7);
        try
        {
            using (var s = ExtentStreams.OpenExtentRead(path, useUnbuffered: false))
                Assert.IsType<FileStream>(s);
            using (var src = new FileBlockSource(path, useUnbuffered: false))
            using (var s = src.OpenRead())
                Assert.IsType<FileStream>(s);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileBlockSource_DefaultOn_ReadsExactBytes()
    {
        var path = SeedFile(300_000, seed: 42);
        try
        {
            using var src = new FileBlockSource(path);
            Assert.Equal(File.ReadAllBytes(path), ReadAll(src.OpenRead(), 8192));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AlignedWrapper_MisalignedReads_ExactBytes()
    {
        const int size = 3 * 1024 * 1024 + 511;
        var path = SeedFile(size, seed: 0xBEEF);
        try
        {
            var expected = File.ReadAllBytes(path);
            using var s = ExtentStreams.OpenAlignedBuffered(path);
            Assert.Equal(size, s.Length);
            var rng = new Random(1234);
            // Pinned misalignments: sector edges ±1 plus fuzz.
            var probes = new List<(long Off, int Count)>
            {
                (0, 1), (1, 1), (4095, 2), (4096, 4096), (4097, 3),
                (1024 * 1024 - 1, 3), (1024 * 1024, 1), (size - 1, 1), (size - 511, 511),
            };
            var off = 0L;
            while (off < size)
            {
                var want = Math.Min(rng.Next(1, 20000), size - off);
                probes.Add((off, (int)want));
                off += want;
            }
            foreach (var (o, c) in probes)
            {
                s.Seek(o, SeekOrigin.Begin);
                Assert.Equal(o, s.Position);
                var buf = new byte[c];
                var got = 0;
                while (got < c)
                {
                    var n = s.Read(buf, got, c - got);
                    Assert.True(n > 0);
                    got += n;
                }
                Assert.Equal(expected.AsSpan((int)o, c).ToArray(), buf);
            }
            // Past EOF reads zero; seek-back rereads exactly.
            Assert.Equal(0, s.Read(new byte[16], 0, 16));
            s.Seek(4097, SeekOrigin.Begin);
            var tail = new byte[3];
            Assert.Equal(3, s.Read(tail, 0, 3));
            Assert.Equal(expected.AsSpan(4097, 3).ToArray(), tail);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AlignedWrapper_EmptyFile_ReadsZero()
    {
        var path = SeedFile(0, seed: 1);
        try
        {
            using var s = ExtentStreams.OpenAlignedBuffered(path);
            Assert.Equal(0, s.Length);
            Assert.Equal(0, s.Read(new byte[8], 0, 8));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AlignedWrapper_UnderVssReadStream_ExactBytes()
    {
        var path = SeedFile(100_000, seed: 5);
        try
        {
            var expected = File.ReadAllBytes(path);
            using var raw = ExtentStreams.OpenAlignedBuffered(path);
            var done = 0;
            using var s = new VssReadStream(raw, expected.Length, () => done++, 4096);
            Assert.Equal(expected, ReadAll(s, 777));
            Assert.Equal(1, done);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
