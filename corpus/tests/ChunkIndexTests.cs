namespace Corpus;

public sealed class ChunkIndexTests : IDisposable
{
    private readonly string _dir;

    public ChunkIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_idx_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ChunkIndex NewIndex() => new(Path.Combine(_dir, Guid.NewGuid() + ".db"));

    private static byte[] Digest(byte fill)
    {
        var d = new byte[32];
        Array.Fill(d, fill);
        return d;
    }

    [Fact]
    public void UpsertBatch_TryGet_RoundTripsAllFields()
    {
        using var idx = NewIndex();
        var digest = Digest(7);
        idx.UpsertBatch(new List<ChunkRow> { new(digest, 1, 2, "pack-0001", 42) });
        Assert.True(idx.TryGet(digest, out var row));
        Assert.Equal(digest, row.Digest);
        Assert.Equal(1, row.SizeBytes);
        Assert.Equal(2, row.StoredBytes);
        Assert.Equal("pack-0001", row.Pack);
        Assert.Equal(42, row.PackOffset);
    }

    [Fact]
    public void TryGet_MissingDigest_ReturnsFalse()
    {
        using var idx = NewIndex();
        Assert.False(idx.TryGet(Digest(9), out _));
    }

    [Fact]
    public void Count_ReflectsInsertedRows()
    {
        using var idx = NewIndex();
        Assert.Equal(0, idx.Count());
        idx.UpsertBatch(new List<ChunkRow> { new(Digest(1), 1, 1, "p", 0), new(Digest(2), 2, 2, "p", 8) });
        Assert.Equal(2, idx.Count());
    }

    [Fact]
    public void UpsertBatch_DuplicateDigest_Replaces()
    {
        using var idx = NewIndex();
        idx.UpsertBatch(new List<ChunkRow> { new(Digest(3), 1, 1, "old-pack", 0) });
        idx.UpsertBatch(new List<ChunkRow> { new(Digest(3), 5, 6, "new-pack", 100) });
        Assert.Equal(1, idx.Count());
        Assert.True(idx.TryGet(Digest(3), out var row));
        Assert.Equal("new-pack", row.Pack);
        Assert.Equal(5, row.SizeBytes);
        Assert.Equal(100, row.PackOffset);
    }

    [Fact]
    public void SweepUnmarked_DeletesOnlyUnmarked()
    {
        using var idx = NewIndex();
        idx.UpsertBatch(new List<ChunkRow> { new(Digest(4), 1, 1, "p", 0), new(Digest(5), 1, 1, "p", 8) });
        idx.SetAllMarks(0);
        Assert.Equal(2, idx.SweepUnmarked());
        Assert.Equal(0, idx.Count());

        idx.UpsertBatch(new List<ChunkRow> { new(Digest(6), 1, 1, "p", 0) });
        idx.SetAllMarks(1);
        Assert.Equal(0, idx.SweepUnmarked());
        Assert.Equal(1, idx.Count());
    }
}
