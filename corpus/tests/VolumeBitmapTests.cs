namespace Corpus;

public sealed class VolumeBitmapTests
{
    [Fact]
    public void ParseRuns_AllZero_NoRuns()
    {
        Assert.Empty(VolumeBitmap.ParseRuns(0, new byte[8], 64));
    }

    [Fact]
    public void ParseRuns_LsbFirst_AndGaps()
    {
        // 0xFF: LCN 0-7 set; 0x00: gap; 0x0F: LCN 16-19 set.
        var runs = VolumeBitmap.ParseRuns(0, new byte[] { 0xFF, 0x00, 0x0F }, 24);
        Assert.Equal([(0L, 8L), (16L, 4L)], runs);
    }

    [Fact]
    public void ParseRuns_StartLcnOffset_Respected()
    {
        var runs = VolumeBitmap.ParseRuns(100, new byte[] { 0x03 }, 8);
        Assert.Equal([(100L, 2L)], runs);
    }

    [Fact]
    public void ParseRuns_ValidBits_ClampsTrailingBits()
    {
        var runs = VolumeBitmap.ParseRuns(0, new byte[] { 0xFF }, 3);
        Assert.Equal([(0L, 3L)], runs);
    }

    [Fact]
    public void AppendRuns_ContiguousAcrossWindows_Merges()
    {
        var runs = VolumeBitmap.ParseRuns(0, new byte[] { 0xFF }, 8);
        VolumeBitmap.AppendRuns(runs, 8, new byte[] { 0x01, 0x00 }, 0, 2, 16);
        Assert.Equal([(0L, 9L)], runs);
    }

    [Fact]
    public void AppendRuns_GapAtBoundary_StaysSplit()
    {
        var runs = VolumeBitmap.ParseRuns(0, new byte[] { 0xFF }, 8);
        VolumeBitmap.AppendRuns(runs, 8, new byte[] { 0x02 }, 0, 1, 8);
        Assert.Equal([(0L, 8L), (9L, 1L)], runs);
    }
}
