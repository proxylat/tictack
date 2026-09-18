namespace TicTack;

public class ExecutorTests : IDisposable
{
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly FileAccessor _accessor = new();

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
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
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
    public async Task RenameAction_DirCollision_WithoutStrategy_PreservesOldTree()
    {
        Directory.CreateDirectory(Dst("old"));
        File.WriteAllText(Path.Combine(Dst("old"), "keep.txt"), "x");
        Directory.CreateDirectory(Dst("new"));

        var action = new RenameAction();
        var e = new FileChangedEventArgs(ChangeType.Renamed, Src("new"), Src("old"));
        var result = await action.ExecuteAsync(new FileActionArgs(e, _srcDir, _dstDir), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(Dst("old")));
        Assert.True(File.Exists(Path.Combine(Dst("old"), "keep.txt")));
    }

    [Fact]
    public async Task RenameAction_DirCollision_WithMirror_DeletesOldTreeAndMoves()
    {
        Directory.CreateDirectory(Dst("old"));
        File.WriteAllText(Path.Combine(Dst("old"), "gone.txt"), "x");
        Directory.CreateDirectory(Dst("new"));

        var deletion = new RecordingDeletion(new MirrorDeletion());
        var action = new RenameAction(deletion, new RecordingLogger());
        var e = new FileChangedEventArgs(ChangeType.Renamed, Src("new"), Src("old"));
        var result = await action.ExecuteAsync(new FileActionArgs(e, _srcDir, _dstDir), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(Dst("old")));
        Assert.Single(deletion.Calls);
    }

    [Fact]
    public async Task RenameAction_DirCollision_WithArchive_PreservesContentInArchive()
    {
        Directory.CreateDirectory(Dst("old"));
        File.WriteAllText(Path.Combine(Dst("old"), "keep.txt"), "archive me");
        Directory.CreateDirectory(Dst("new"));

        var action = new RenameAction(new ArchiveDeletion(Path.Combine(_dstDir, ".archive"), _dstDir), new RecordingLogger());
        var e = new FileChangedEventArgs(ChangeType.Renamed, Src("new"), Src("old"));
        var result = await action.ExecuteAsync(new FileActionArgs(e, _srcDir, _dstDir), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_dstDir, ".archive", "old", "keep.txt")));
    }

    [Fact]
    public async Task RenameAction_FileCollision_WithoutStrategy_PreservesOldFile()
    {
        File.WriteAllText(Dst("old.txt"), "old");
        File.WriteAllText(Dst("new.txt"), "new");

        var action = new RenameAction();
        var e = new FileChangedEventArgs(ChangeType.Renamed, Src("new.txt"), Src("old.txt"));
        var result = await action.ExecuteAsync(new FileActionArgs(e, _srcDir, _dstDir), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("old", File.ReadAllText(Dst("old.txt")));
        Assert.Equal("new", File.ReadAllText(Dst("new.txt")));
    }

    private sealed class StubDeletion : IDeletionStrategy
    {
        private readonly ActionResult _result;
        public StubDeletion(ActionResult result) => _result = result;
        public Task<ActionResult> HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    [Fact]
    public async Task RenameAction_DirCollision_WhenStrategyFails_PreservesOldTree()
    {
        Directory.CreateDirectory(Dst("old"));
        File.WriteAllText(Path.Combine(Dst("old"), "keep.txt"), "x");
        Directory.CreateDirectory(Dst("new"));

        var action = new RenameAction(new StubDeletion(ActionResult.Fail("nope")), new RecordingLogger());
        var e = new FileChangedEventArgs(ChangeType.Renamed, Src("new"), Src("old"));
        var result = await action.ExecuteAsync(new FileActionArgs(e, _srcDir, _dstDir), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(File.Exists(Path.Combine(Dst("old"), "keep.txt")));
    }

    [Fact]
    public async Task RenameAction_DirCollision_WhenStrategyKeepsTree_MoveFailsSafely()
    {
        Directory.CreateDirectory(Dst("old"));
        File.WriteAllText(Path.Combine(Dst("old"), "keep.txt"), "x");
        Directory.CreateDirectory(Dst("new"));

        var action = new RenameAction(new StubDeletion(ActionResult.Ok()), new RecordingLogger());
        var e = new FileChangedEventArgs(ChangeType.Renamed, Src("new"), Src("old"));
        var result = await action.ExecuteAsync(new FileActionArgs(e, _srcDir, _dstDir), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(File.Exists(Path.Combine(Dst("old"), "keep.txt")));
    }

}
