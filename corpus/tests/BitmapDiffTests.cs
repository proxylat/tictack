namespace Corpus;

// Bitmap hole-flip net: parent hole map vs current allocated runs.
public sealed class BitmapDiffTests
{
    private const int ChunkBytes = 4096;
    private const long Bpc = 4096;

    private static List<ManifestEntry> Chunks(params bool[] holes)
    {
        var chunks = new List<ManifestEntry>();
        foreach (var h in holes)
            chunks.Add(h ? ManifestEntry.Hole(ChunkBytes) : new ManifestEntry(new string('a', 64), ChunkBytes));
        return chunks;
    }

    [Fact]
    public void NoFlip_Empty()
    {
        var flips = VolumeBitmap.HoleFlipChunks(Chunks(false, true), [(0, 1)], Bpc, 2L * ChunkBytes, ChunkBytes);
        Assert.NotNull(flips);
        Assert.Empty(flips!);
    }

    [Fact]
    public void EitherDirection_Flips()
    {
        // Chunk 0 data→hole (freed), chunk 2 hole→data (reused).
        var flips = VolumeBitmap.HoleFlipChunks(Chunks(false, false, true),
            [(1, 2)], Bpc, 3L * ChunkBytes, ChunkBytes);
        Assert.Equal([0L, 2L], flips);
    }

    [Fact]
    public void GridMismatch_Null()
    {
        Assert.Null(VolumeBitmap.HoleFlipChunks(Chunks(false), [(0, 2)], Bpc, 2L * ChunkBytes, ChunkBytes));
    }

    [Fact]
    public void EmptyRuns_AllHoles()
    {
        var flips = VolumeBitmap.HoleFlipChunks(Chunks(false, false), [], Bpc, 2L * ChunkBytes, ChunkBytes);
        Assert.Equal([0L, 1L], flips);
    }
}
