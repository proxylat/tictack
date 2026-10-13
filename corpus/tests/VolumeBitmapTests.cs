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

    [Theory]
    // ok, bitmapSize, ret -> returned bits. MORE_DATA (ok=false) ignores
    // BitmapSize (total remaining) and counts the filled prefix instead.
    [InlineData(true, 8000L, 1016, 8000L)]    // success: BitmapSize rules
    [InlineData(true, 0L, 16, 0L)]            // success, empty: caller breaks
    [InlineData(true, 9000L, 1016, 8000L)]    // success: clamped to capacity
    [InlineData(false, 16532991L, 1024, 8064L)]  // MORE_DATA 1KB prefix
    [InlineData(false, 16532991L, 131088, 1048576L)] // MORE_DATA 128KB window
    public void WindowBits_MoreData_CountsPrefix(bool ok, long bitmapSize, int ret, long expected)
    {
        Assert.Equal(expected, VolumeBitmap.WindowBits(ok, bitmapSize, ret));
    }

    [Fact]
    public void WriteRuns_GoldenText()
    {
        // Text shape is the harness contract (--bitmap-dump consumers parse
        // it in PowerShell): header comment + "startLcns count" lines.
        var path = Path.Combine(Path.GetTempPath(), "CorpusTest_runs_" + Guid.NewGuid() + ".txt");
        try
        {
            VolumeBitmap.WriteRuns(path, 4096, 100, new List<(long, long)> { (0, 256), (512, 128) });
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.Equal("# bpc=4096 totalClusters=100 runs=2\n0 256\n512 128\n", text);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
