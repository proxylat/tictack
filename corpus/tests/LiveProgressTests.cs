using Xunit;

namespace Corpus.Tests;

public sealed class LiveProgressTests
{
    [Fact]
    public void FromConfig_Null_Defaults()
    {
        var opts = LiveProgressOptions.FromConfig(null);
        Assert.Equal("auto", opts.Mode);
        Assert.True(opts.ShowName);
        Assert.False(opts.ShowPath);
        Assert.True(opts.Enabled);
    }

    [Theory]
    [InlineData("on", "on")]
    [InlineData("OFF", "off")]
    [InlineData("bogus", "auto")]
    [InlineData("", "auto")]
    public void FromConfig_ModeParsing(string raw, string expected)
    {
        var cfg = new CorpusConfig { Logging = new CorpusLoggingConfig { Progress = raw } };
        Assert.Equal(expected, LiveProgressOptions.FromConfig(cfg.Logging).Mode);
    }

    [Fact]
    public void Build_Lookup_HitAndMiss()
    {
        var index = ExtentNameIndex.Build(4096, new[]
        {
            ("a.txt", (IReadOnlyList<(long, long)>)new[] { (10L, 2L) }),
        });
        Assert.Equal("a.txt", index.Lookup(10 * 4096));
        Assert.Equal("a.txt", index.Lookup(12 * 4096 - 1));
        Assert.Null(index.Lookup(9 * 4096));
        Assert.Null(index.Lookup(12 * 4096));
    }

    [Fact]
    public void Build_Overlap_FirstWins()
    {
        var index = ExtentNameIndex.Build(512, new[]
        {
            ("first.txt", (IReadOnlyList<(long, long)>)new[] { (0L, 10L) }),
            ("second.txt", (IReadOnlyList<(long, long)>)new[] { (0L, 10L) }),
        });
        Assert.Equal("first.txt", index.Lookup(100));
    }

    [Fact]
    public void Build_Empty_Null()
    {
        var index = ExtentNameIndex.Build(4096, Array.Empty<(string, IReadOnlyList<(long, long)>)>());
        Assert.Null(index.Lookup(0));
    }

    [Fact]
    public void Render_Shape()
    {
        var line = LiveProgress.Render("image", 42 * (1L << 30), 100 * (1L << 30), 10.0, "current.dat");
        Assert.Contains("image [", line);
        Assert.Contains("42%", line);
        Assert.Contains("current.dat", line);
        Assert.Contains("ETA", line);
    }

    [Fact]
    public void TruncateLeft_ShortUnchanged_LongEllipsized()
    {
        Assert.Equal(@"C:\a.dat", LiveProgress.TruncateLeft(@"C:\a.dat", 60));
        var longPath = @"C:\very\long\" + new string('x', 100) + ".dat";
        var cut = LiveProgress.TruncateLeft(longPath, 60);
        Assert.Equal(60, cut.Length);
        Assert.StartsWith("...", cut);
        Assert.EndsWith(".dat", cut);
    }

    [Fact]
    public void Render_NoName_StatsOnly()
    {
        var line = LiveProgress.Render("restore", 5 * (1L << 20), 10 * (1L << 20), 2.0, null);
        Assert.Contains("restore [", line);
        Assert.Contains("50%", line);
        Assert.DoesNotContain("@offset", line);
    }

    [Fact]
    public void Human_Units()
    {
        Assert.Equal("1.0 GB", LiveProgress.Human(1L << 30));
        Assert.Equal("41.2 MB", LiveProgress.Human((long)(41.2 * (1L << 20))));
        Assert.Equal("512 B", LiveProgress.Human(512));
    }

    [Fact]
    public void Create_OffsetOnly_NeverTouchesVolume()
    {
        // Offset-only display needs no names: the volume must not even be
        // opened, let alone walked. Guards the silent-MFT-scan regression.
        var log = new TestLogger();
        var opts = new LiveProgressOptions("on", false, false);
        bool called = false;
        Func<IMftVolume?> volume = () => { called = true; return null; };
        var progress = LiveProgress.Create(log, opts, "test", 100, volume);
        Assert.NotNull(progress);
        progress!.Report(10);
        Assert.False(called);
    }
}
