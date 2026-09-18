namespace TicTack;

public class FilterTests
{
    [Theory]
    [InlineData("*.txt", "readme.txt", false)]
    [InlineData("*.txt", "readme.md", true)]
    [InlineData("*.tmp", "file.tmp", false)]
    [InlineData("", "anything.txt", true)]
    [InlineData("?est.txt", "test.txt", false)]
    [InlineData("?est.txt", "best.txt", false)]
    [InlineData("?est.txt", "atest.txt", true)]
    [InlineData("data.*", "data.csv", false)]
    [InlineData("data.*", "data.txt", false)]
    [InlineData("data.*", "dataset.txt", true)]
    [InlineData("*backup*", "mybackupfile.txt", false)]
    [InlineData("*backup*", "backup.zip", false)]
    [InlineData("a?c", "abc", false)]
    [InlineData("a?c", "ac", true)]
    [InlineData("*backup*", "plainfile.txt", true)]
    [InlineData("*backup*", "nobackuphere.txt", false)]
    public void PatternFilter_WildcardMatching(string pattern, string filename, bool expectedShouldProcess)
    {
        var filter = new PatternFilter(new[] { pattern });
        var path = Path.Combine(Path.GetTempPath(), filename);
        Assert.Equal(expectedShouldProcess, filter.ShouldProcess(path));
    }

    [Fact]
    public void PatternFilter_NoPatterns_ProcessesAll()
    {
        var filter = new PatternFilter(Array.Empty<string>());
        Assert.True(filter.ShouldProcess(@"C:\any\file.txt"));
    }

    [Fact]
    public void PatternFilter_CaseSensitivity_IsOsAware()
    {
        var filter = new PatternFilter(new[] { "*.txt" });
        var path = Path.Combine(Path.GetTempPath(), "REPORT.TXT");
        // Windows matches case-insensitively (blocked); Linux is exact (processed).
        Assert.Equal(!OperatingSystem.IsWindows(), filter.ShouldProcess(path));
    }

    [Fact]
    public void PatternFilter_NullPatterns_ProcessesAll()
    {
        var filter = new PatternFilter(null!);
        Assert.True(filter.ShouldProcess(@"C:\any\file.exe"));
    }

    [Theory]
    [InlineData("temp/*", @"C:\Users\x\Desktop\temp\file.txt", false)]
    [InlineData("temp/*", @"C:\Users\x\Desktop\temp\sub\file.txt", false)]
    [InlineData("temp/*", @"C:\Users\x\Desktop\other\file.txt", true)]
    [InlineData(@"build\*", @"C:\proj\build\out.dll", false)]
    [InlineData("obj/*", @"C:\proj\obj\debug\f.dll", false)]
    [InlineData("temp/*", @"C:\Users\x\Desktop\tempish\file.txt", true)]
    [InlineData("C:/proj/build/*", @"C:\proj\build\out.dll", false)]
    [InlineData("D:/other/*", @"C:\proj\build\out.dll", true)]
    public void PatternFilter_DirectoryPatterns(string pattern, string path, bool expectedShouldProcess)
    {
        var filter = new PatternFilter(new[] { pattern });
        Assert.Equal(expectedShouldProcess, filter.ShouldProcess(path));
    }

    [Fact]
    public void PatternFilter_MultiplePatterns_BlocksAny()
    {
        var filter = new PatternFilter(new[] { "*.tmp", "*.bak", "*.old" });
        Assert.False(filter.ShouldProcess(@"C:\dir\file.tmp"));
        Assert.False(filter.ShouldProcess(@"C:\dir\file.bak"));
        Assert.False(filter.ShouldProcess(@"C:\dir\file.old"));
        Assert.True(filter.ShouldProcess(@"C:\dir\file.txt"));
    }

    [Fact]
    public void SizeFilter_BlocksOverLimit_AllowsUnder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_filter_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var small = Path.Combine(dir, "small.bin");
            File.WriteAllBytes(small, new byte[5000]);
            var large = Path.Combine(dir, "large.bin");
            File.WriteAllBytes(large, new byte[2 * 1024 * 1024]);

            // 0 MB means no limit.
            Assert.True(new SizeFilter(0).ShouldProcess(small));

            var oneMb = new SizeFilter(1);
            Assert.True(oneMb.ShouldProcess(small));
            Assert.False(oneMb.ShouldProcess(large));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SizeFilter_AllowsSmallFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_filter_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "small.txt");
            File.WriteAllText(path, "hello");
            var filter = new SizeFilter(10); // 10 MB
            Assert.True(filter.ShouldProcess(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SizeFilter_ZeroMax_ProcessesAll()
    {
        var filter = new SizeFilter(0);
        Assert.True(filter.ShouldProcess(@"C:\any\file.txt"));
    }

    [Fact]
    public void CompositeFilter_PassesWhenAllPass()
    {
        var filter = new CompositeFilter(new IFileFilter[]
        {
            new PatternFilter(Array.Empty<string>()),
            new SizeFilter(0)
        });
        Assert.True(filter.ShouldProcess(@"C:\dir\file.txt"));
    }

    [Fact]
    public void CompositeFilter_FailsOnFirstReject()
    {
        var filter = new CompositeFilter(new IFileFilter[]
        {
            new PatternFilter(new[] { "*.txt" }),
            new SizeFilter(0)
        });
        Assert.False(filter.ShouldProcess(@"C:\dir\file.txt"));
    }

    [Fact]
    public void CompositeFilter_EmptyFilters_ProcessesAll()
    {
        var filter = new CompositeFilter(null!);
        Assert.True(filter.ShouldProcess(@"C:\any\file.txt"));
    }

    [Fact]
    public void PathPrefixFilter_ExcludesSubtreeOnly()
    {
        var filter = new PathPrefixFilter("/home/user/Sync");
        Assert.False(filter.ShouldProcess("/home/user/Sync/file.txt"));
        // Sibling sharing the prefix must still be processed.
        Assert.True(filter.ShouldProcess("/home/user/Sync2/file.txt"));
    }

    [Fact]
    public void PathPrefixFilter_CaseSensitivity_IsOsAware()
    {
        var filter = new PathPrefixFilter("/home/user/Sync");
        var path = "/home/user/sync/file.txt";
        Assert.Equal(!OperatingSystem.IsWindows(), filter.ShouldProcess(path));
    }
}
