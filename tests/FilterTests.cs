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
    public void SizeFilter_BlocksLargeFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_filter_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "large.bin");
            File.WriteAllBytes(path, new byte[5000]);
            var filter = new SizeFilter(0); // 0 MB means no limit via ShouldProcess logic
            Assert.True(filter.ShouldProcess(path));

            var blockingFilter = new SizeFilter(1); // 1 MB limit, 5000 bytes is less, so passes
            Assert.True(blockingFilter.ShouldProcess(path));

            // Test with actual blocking: make a tiny limit (0.001 MB = ~1048 bytes)
            // We need to test the internal _maxBytes logic - write a file > limit
            // SizeFilter takes MB, so 1 MB limit allows 5000 bytes. To block, file must be > limit.
            var path2 = Path.Combine(dir, "large2.bin");
            File.WriteAllBytes(path2, new byte[2 * 1024 * 1024]); // 2 MB
            Assert.False(blockingFilter.ShouldProcess(path2));
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
}
