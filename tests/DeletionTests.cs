namespace TicTack;

public class DeletionTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly string _archiveDir;

    public DeletionTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "TicTackTest_del_" + Guid.NewGuid());
        _srcDir = Path.Combine(_baseDir, "src");
        _dstDir = Path.Combine(_baseDir, "sync", "Desktop");
        _archiveDir = Path.Combine(_baseDir, "sync", ".archive");
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_dstDir);
        Directory.CreateDirectory(_archiveDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, true); } catch { }
    }

    string Dst(string name) => Path.Combine(_dstDir, name);
    string Archive(params string[] parts) => Path.Combine(new[] { _archiveDir }.Concat(parts).ToArray());

    [Fact]
    public async Task MirrorDeletion_DeletesDestFile()
    {
        File.WriteAllText(Dst("a.txt"), "delete me");

        var deletion = new MirrorDeletion();
        await deletion.HandleDeletionAsync(Path.Combine(_srcDir, "a.txt"), Dst("a.txt"), CancellationToken.None);

        Assert.False(File.Exists(Dst("a.txt")));
    }

    [Fact]
    public async Task MirrorDeletion_NoError_OnMissingDest()
    {
        var deletion = new MirrorDeletion();
        await deletion.HandleDeletionAsync(Path.Combine(_srcDir, "missing.txt"), Dst("missing.txt"), CancellationToken.None);
    }

    [Fact]
    public async Task MirrorDeletion_DeletesDirectory()
    {
        var dir = Dst("subdir");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "nested.txt"), "nested");

        var deletion = new MirrorDeletion();
        await deletion.HandleDeletionAsync(Path.Combine(_srcDir, "subdir"), dir, CancellationToken.None);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task ArchiveDeletion_MovesToArchive()
    {
        File.WriteAllText(Dst("a.txt"), "archive me");

        var deletion = new ArchiveDeletion(_archiveDir, _dstDir);
        await deletion.HandleDeletionAsync(Path.Combine(_srcDir, "a.txt"), Dst("a.txt"), CancellationToken.None);

        Assert.False(File.Exists(Dst("a.txt")));
        Assert.True(File.Exists(Archive("Desktop", "a.txt")));
        Assert.Equal("archive me", File.ReadAllText(Archive("Desktop", "a.txt")));
    }

    [Fact]
    public async Task ArchiveDeletion_IgnoresSourcePathForm_ArchivesByDestName()
    {
        File.WriteAllText(Dst("a.txt"), "archive no rel");

        var deletion = new ArchiveDeletion(_archiveDir, _dstDir);
        await deletion.HandleDeletionAsync("source/path/a.txt", Dst("a.txt"), CancellationToken.None);

        Assert.False(File.Exists(Dst("a.txt")));
        Assert.True(File.Exists(Archive("Desktop", "a.txt")));
    }

    [Fact]
    public async Task ArchiveDeletion_OutsideSyncRoot_FailsWithoutDeleting()
    {
        // Empty relative path: the destination is under neither the sync root
        // nor the dest base, so archiving must fail closed, never delete.
        var outside = Path.Combine(Path.GetTempPath(), "TicTackTest_del_out_" + Guid.NewGuid());
        Directory.CreateDirectory(outside);
        try
        {
            var victim = Path.Combine(outside, "a.txt");
            File.WriteAllText(victim, "do not lose me");

            var deletion = new ArchiveDeletion(_archiveDir, _dstDir);
            var result = await deletion.HandleDeletionAsync(Path.Combine(_srcDir, "a.txt"), victim, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("do not lose me", File.ReadAllText(victim));
        }
        finally { try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public async Task ArchiveDeletion_OverwritesExistingArchive()
    {
        File.WriteAllText(Dst("a.txt"), "newer version");
        Directory.CreateDirectory(Path.Combine(_archiveDir, "Desktop"));
        File.WriteAllText(Archive("Desktop", "a.txt"), "older version");

        var deletion = new ArchiveDeletion(_archiveDir, _dstDir);
        await deletion.HandleDeletionAsync(Path.Combine(_srcDir, "a.txt"), Dst("a.txt"), CancellationToken.None);

        Assert.Equal("newer version", File.ReadAllText(Archive("Desktop", "a.txt")));
    }

    [Fact]
    public async Task ArchiveDeletion_PreservesSubdirectoryStructure()
    {
        var nested = Path.Combine(_dstDir, "sub", "dir", "nested.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "nested archive");

        var deletion = new ArchiveDeletion(_archiveDir, _dstDir);
        await deletion.HandleDeletionAsync(Path.Combine(_srcDir, "sub", "dir", "nested.txt"),
            nested, CancellationToken.None);

        Assert.False(File.Exists(nested));
        Assert.True(File.Exists(Archive("Desktop", "sub", "dir", "nested.txt")));
    }

    [Fact]
    public async Task ArchiveDeletion_MirrorsDirectoryFullPath()
    {
        var dir = Path.Combine(_dstDir, "Proj");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "nested.txt"), "dir archive");

        var deletion = new ArchiveDeletion(_archiveDir, _dstDir);
        await deletion.HandleDeletionAsync(Path.Combine(_srcDir, "Proj"), dir, CancellationToken.None);

        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(Archive("Desktop", "Proj", "nested.txt")));
    }

    [Fact]
    public void Factory_CreatesMirror_WhenModeMirror()
    {
        var deletion = DeletionStrategyFactory.Create(new DeletionConfig { Mode = "mirror" }, _dstDir);
        Assert.IsType<MirrorDeletion>(deletion);
    }

    [Fact]
    public void Factory_CreatesArchive_WhenModeArchive()
    {
        var deletion = DeletionStrategyFactory.Create(new DeletionConfig { Mode = "archive" }, _dstDir);
        Assert.IsType<ArchiveDeletion>(deletion);
    }

    [Fact]
    public void Factory_CreatesArchive_WithCustomPath()
    {
        var deletion = DeletionStrategyFactory.Create(
            new DeletionConfig { Mode = "archive", Path = _archiveDir }, _dstDir);
        Assert.IsType<ArchiveDeletion>(deletion);
    }

    [Fact]
    public void Factory_CreatesArchive_WhenModeUnknown()
    {
        var deletion = DeletionStrategyFactory.Create(new DeletionConfig { Mode = "unknown" }, _dstDir);
        Assert.IsType<ArchiveDeletion>(deletion);
    }

    [Fact]
    public void Factory_CreatesArchive_WhenNullConfig()
    {
        var deletion = DeletionStrategyFactory.Create(null!, _dstDir);
        Assert.IsType<ArchiveDeletion>(deletion);
    }
}
