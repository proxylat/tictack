namespace Corpus;

public sealed class VssReadStreamTests
{
    private const int Seg = 1024;

    private static byte[] Pattern(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        var buf = new byte[300];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    [Fact]
    public void Read_SpansSegments_ExactBytes()
    {
        var data = Pattern(3 * Seg + 117);
        using var inner = new MemoryStream(data, false);
        var done = 0;
        using var s = new VssReadStream(inner, data.Length, () => done++, Seg);
        Assert.Equal(data, ReadAll(s));
        Assert.Equal(1, done);
    }

    [Fact]
    public void Read_SmallUnalignedReads_ExactBytes()
    {
        var data = Pattern(2 * Seg + 5);
        using var inner = new MemoryStream(data, false);
        using var s = new VssReadStream(inner, data.Length, () => { }, Seg);
        var rng = new Random(11);
        var off = 0;
        var buf = new byte[5000];
        while (off < data.Length)
        {
            var want = Math.Min(rng.Next(1, 5000), data.Length - off);
            var got = 0;
            while (got < want)
            {
                var n = s.Read(buf, got, want - got);
                Assert.True(n > 0);
                got += n;
            }
            Assert.Equal(data.AsSpan(off, want).ToArray(), buf.AsSpan(0, want).ToArray());
            off += got;
        }
    }

    [Fact]
    public void Seek_BackwardRefills_ExactBytes()
    {
        var data = Pattern(2 * Seg + 9);
        using var inner = new MemoryStream(data, false);
        using var s = new VssReadStream(inner, data.Length, () => { }, Seg);
        var tail = new byte[100];
        s.Seek(data.Length - 100, SeekOrigin.Begin);
        Assert.Equal(100, s.Read(tail, 0, 100));
        Assert.Equal(data.AsSpan(data.Length - 100, 100).ToArray(), tail);
        var head = new byte[Seg + 3];
        s.Seek(0, SeekOrigin.Begin);
        var got = 0;
        while (got < head.Length) got += s.Read(head, got, head.Length - got);
        Assert.Equal(data.AsSpan(0, head.Length).ToArray(), head);
    }

    [Fact]
    public void Read_PastLength_ReturnsZero()
    {
        var data = Pattern(Seg + 1);
        using var inner = new MemoryStream(data, false);
        using var s = new VssReadStream(inner, data.Length, () => { }, Seg);
        Assert.Equal(data, ReadAll(s));
        Assert.Equal(0, s.Read(new byte[10], 0, 10));
    }

    [Fact]
    public void Read_ShortDevice_Throws()
    {
        var data = Pattern(Seg + 1);
        using var inner = new MemoryStream(data, false);
        using var s = new VssReadStream(inner, data.Length + 500, () => { }, Seg);
        Assert.Throws<IOException>(() => ReadAll(s));
    }
}
