namespace Corpus;

public sealed class ChunkerTests
{
    [Fact]
    public void Read_TenBytesInFours_YieldsSizesOffsetsIndexes()
    {
        using var stream = new MemoryStream(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var chunks = Chunker.Read(stream, 4).ToList();
        Assert.Equal(3, chunks.Count);
        Assert.Equal([4, 4, 2], chunks.Select(c => c.Data.Length));
        Assert.Equal([0L, 4L, 8L], chunks.Select(c => c.Offset));
        Assert.Equal([0L, 1L, 2L], chunks.Select(c => c.Index));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, chunks[0].Data);
        Assert.Equal(new byte[] { 8, 9 }, chunks[2].Data);
    }

    [Fact]
    public void Read_EmptyStream_YieldsNothing()
    {
        using var stream = new MemoryStream([]);
        Assert.Empty(Chunker.Read(stream, 4));
    }

    [Fact]
    public void Read_ExactMultiple_YieldsNoShortTail()
    {
        using var stream = new MemoryStream(new byte[8]);
        var chunks = Chunker.Read(stream, 4).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(4, c.Data.Length));
    }
}
