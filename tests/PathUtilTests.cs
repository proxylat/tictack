namespace TicTack;

public class PathUtilTests
{
    [Fact]
    public void EnsureExtended_ReturnsInputOnCurrentPlatform()
    {
        var path = Path.Combine(Path.GetTempPath(), "some", "file.txt");
        if (OperatingSystem.IsWindows()) return; // Windows branch pinned below
        Assert.Equal(path, PathUtil.EnsureExtended(path));
    }

    [Fact]
    public void WindowsBranch_ShortPath_Unchanged()
    {
        Assert.Equal(@"C:\a.txt", PathUtil.EnsureExtended(@"C:\a.txt", true));
    }

    [Fact]
    public void WindowsBranch_Boundary240_NoPrefix()
    {
        var path = @"C:\" + new string('x', 237); // exactly 240 chars
        Assert.Equal(240, path.Length);
        Assert.Equal(path, PathUtil.EnsureExtended(path, true));
    }

    [Fact]
    public void WindowsBranch_LongPath_GetsPrefix()
    {
        var path = @"C:\" + new string('x', 238); // 241 chars
        Assert.Equal(@"\\?\" + path, PathUtil.EnsureExtended(path, true));
    }

    [Fact]
    public void WindowsBranch_AlreadyPrefixed_Untouched()
    {
        var path = @"\\?\" + @"C:\" + new string('x', 300);
        Assert.Equal(path, PathUtil.EnsureExtended(path, true));
    }

    [Fact]
    public void NonWindowsBranch_LongPath_Untouched()
    {
        var path = "/data/" + new string('x', 300);
        Assert.Equal(path, PathUtil.EnsureExtended(path, false));
    }
}
