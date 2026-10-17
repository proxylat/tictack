using TicTack;
using Xunit;

namespace Corpus;

// FAT32 pre-flight tests: OverFat32Limit scans the staged media tree for
// files no FAT32 stick can hold. Sparse (SetLength-only) files stand in
// for multi-GB WIMs — Length reports full size with zero bytes written.
public sealed class Fat32PreflightTests : IDisposable
{
    private readonly string _dir;

    public Fat32PreflightTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_fat32_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void OverFat32Limit_FlagsHugeFileIgnoresSmall()
    {
        var huge = Path.Combine(_dir, "install.wim");
        using (var fs = new FileStream(huge, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.SetLength(MediaBuilder.Fat32MaxFileBytes + 1);
        var small = Path.Combine(_dir, "boot.wim");
        File.WriteAllText(small, "tiny");
        var over = MediaBuilder.OverFat32Limit(_dir);
        Assert.Single(over);
        Assert.Equal(huge, over[0].Path);
        Assert.Equal(MediaBuilder.Fat32MaxFileBytes + 1, over[0].Size);
    }

    [Fact]
    public void OverFat32Limit_BoundaryAndMissingDir()
    {
        var edge = Path.Combine(_dir, "edge.wim");
        using (var fs = new FileStream(edge, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.SetLength(MediaBuilder.Fat32MaxFileBytes);
        Assert.Empty(MediaBuilder.OverFat32Limit(_dir)); // exactly at the limit still fits
        Assert.Empty(MediaBuilder.OverFat32Limit(Path.Combine(_dir, "no-such-dir")));
    }

    [Fact]
    public void OverFat32Limit_RecursesSubdirs()
    {
        var sub = Path.Combine(_dir, "sources");
        Directory.CreateDirectory(sub);
        var deep = Path.Combine(sub, "install.wim");
        using (var fs = new FileStream(deep, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.SetLength(MediaBuilder.Fat32MaxFileBytes + 100);
        var over = MediaBuilder.OverFat32Limit(_dir);
        Assert.Single(over);
        Assert.Equal(deep, over[0].Path);
    }
}
