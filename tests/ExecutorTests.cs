namespace TicTack;

public class ExecutorTests : IDisposable
{
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly MockFileAccessor _accessor = new();

    public ExecutorTests()
    {
        _srcDir = Path.Combine(Path.GetTempPath(), "TicTackTest_exec_" + Guid.NewGuid());
        _dstDir = Path.Combine(Path.GetTempPath(), "TicTackTest_exec_dst_" + Guid.NewGuid());
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_dstDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_srcDir, true); } catch { }
        try { Directory.Delete(_dstDir, true); } catch { }
    }

    string Src(string name) => Path.Combine(_srcDir, name);
    string Dst(string name) => Path.Combine(_dstDir, name);

    FileActionArgs MakeArgs(string srcPath, string relFile) =>
        new(new FileChangedEventArgs(ChangeType.Created, srcPath), _srcDir, _dstDir);

    [Fact]
    public async Task CopyAction_CopiesFileCorrectly()
    {
        var content = "hello world copy test";
        File.WriteAllText(Src("a.txt"), content);

        var action = new CopyAction(_accessor);
        var args = MakeArgs(Src("a.txt"), "a.txt");
        var result = await action.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Dst("a.txt")));
        Assert.Equal(content, File.ReadAllText(Dst("a.txt")));
    }

    [Fact]
    public async Task CopyAction_UsesTempThenRename()
    {
        File.WriteAllText(Src("a.txt"), "temp rename test");

        var action = new CopyAction(_accessor);
        var args = MakeArgs(Src("a.txt"), "a.txt");
        await action.ExecuteAsync(args, CancellationToken.None);

        Assert.True(File.Exists(Dst("a.txt")));
        Assert.False(File.Exists(Dst("a.txt.tictack.tmp")));
    }

    [Fact]
    public async Task CopyAction_MissingSource_ReturnsFail()
    {
        var action = new CopyAction(_accessor);
        var args = MakeArgs(Src("missing.txt"), "missing.txt");
        var result = await action.ExecuteAsync(args, CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task CopyAction_OverwritesExistingFile()
    {
        File.WriteAllText(Src("a.txt"), "new content");
        File.WriteAllText(Dst("a.txt"), "old content");

        var action = new CopyAction(_accessor);
        var args = MakeArgs(Src("a.txt"), "a.txt");
        var result = await action.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("new content", File.ReadAllText(Dst("a.txt")));
    }

    [Fact]
    public async Task CopyAction_CreatesSubdirectories()
    {
        var subDir = Path.Combine(_srcDir, "sub", "deep");
        Directory.CreateDirectory(subDir);
        var srcFile = Path.Combine(subDir, "file.txt");
        File.WriteAllText(srcFile, "nested content");

        var action = new CopyAction(_accessor);
        var args = MakeArgs(srcFile, Path.Combine("sub", "deep", "file.txt"));
        var result = await action.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_dstDir, "sub", "deep", "file.txt")));
    }

    [Fact]
    public async Task CopyAction_PreservesSourceTimestamps()
    {
        var content = "timestamp test";
        File.WriteAllText(Src("a.txt"), content);
        var writeTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Src("a.txt"), writeTime);

        var action = new CopyAction(_accessor);
        var args = MakeArgs(Src("a.txt"), "a.txt");
        await action.ExecuteAsync(args, CancellationToken.None);

        var dstTime = File.GetLastWriteTimeUtc(Dst("a.txt"));
        Assert.Equal(writeTime, dstTime);
    }

    [Fact]
    public async Task DeleteAction_DeletesFile()
    {
        File.WriteAllText(Dst("a.txt"), "delete me");

        var action = new DeleteAction();
        var e = new FileChangedEventArgs(ChangeType.Deleted, Src("a.txt"));
        var args = new FileActionArgs(e, _srcDir, _dstDir);
        var result = await action.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(Dst("a.txt")));
    }

    [Fact]
    public async Task DeleteAction_NoError_OnMissingFile()
    {
        var action = new DeleteAction();
        var e = new FileChangedEventArgs(ChangeType.Deleted, Src("missing.txt"));
        var args = new FileActionArgs(e, _srcDir, _dstDir);
        var result = await action.ExecuteAsync(args, CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task RenameAction_RenamesFile()
    {
        File.WriteAllText(Dst("old.txt"), "rename me");

        var action = new RenameAction();
        var e = new FileChangedEventArgs(ChangeType.Renamed, Src("new.txt"), Src("old.txt"));
        var args = new FileActionArgs(e, _srcDir, _dstDir);
        var result = await action.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(Dst("old.txt")));
        Assert.True(File.Exists(Dst("new.txt")));
    }

    [Fact]
    public async Task RenameAction_CreatesDirIfNeeded()
    {
        File.WriteAllText(Dst("old.txt"), "rename with dir");

        var action = new RenameAction();
        var e = new FileChangedEventArgs(ChangeType.Renamed,
            Path.Combine(_srcDir, "sub", "new.txt"),
            Src("old.txt"));
        var args = new FileActionArgs(e, _srcDir, _dstDir);
        var result = await action.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_dstDir, "sub", "new.txt")));
    }

    [Fact]
    public async Task RenameAction_NoError_WhenOldNotExists()
    {
        var action = new RenameAction();
        var e = new FileChangedEventArgs(ChangeType.Renamed, Src("new.txt"), Src("old.txt"));
        var args = new FileActionArgs(e, _srcDir, _dstDir);
        var result = await action.ExecuteAsync(args, CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task CopyAction_FsyncAfterWrite()
    {
        var content = new string('X', 10000);
        File.WriteAllText(Src("large.txt"), content);

        var action = new CopyAction(_accessor);
        var args = MakeArgs(Src("large.txt"), "large.txt");
        var result = await action.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(content.Length, new FileInfo(Dst("large.txt")).Length);
    }
}
