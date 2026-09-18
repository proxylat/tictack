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

    [Fact]
    public void WindowsBranch_UncLongPath_GetsUncPrefix()
    {
        var path = @"\\server\share\" + new string('x', 260);
        Assert.Equal(@"\\?\UNC\server\share\" + new string('x', 260), PathUtil.EnsureExtended(path, true));
    }

    [Fact]
    public void WindowsBranch_LongRelativePath_Untouched()
    {
        var path = new string('x', 300) + @"\file.txt";
        Assert.Equal(path, PathUtil.EnsureExtended(path, true));
    }

    [Fact]
    public void Relative_TrimsRootPrefix()
    {
        Assert.Equal("sub/file.txt", PathUtil.Relative("/a/b/sub/file.txt", "/a/b").Replace('\\', '/'));
    }

    [Fact]
    public void Relative_ExactRoot_ReturnsEmpty()
    {
        Assert.Equal("", PathUtil.Relative("/a/b", "/a/b"));
    }

    [Fact]
    public void Relative_BoundaryMismatch_ReturnsPathUnchanged()
    {
        // /a/src must not swallow /a/src2.
        Assert.Equal("/a/src2/f.txt", PathUtil.Relative("/a/src2/f.txt", "/a/src"));
    }

    [Fact]
    public void Relative_TrailingSeparatorRoot_StillTrims()
    {
        Assert.Equal("f.txt", PathUtil.Relative("/a/b/f.txt", "/a/b/"));
    }
}
