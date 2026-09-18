namespace TicTack;

public class VersioningTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _dstDir;
    private readonly string _verDir;

    public VersioningTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "TicTackTest_ver_" + Guid.NewGuid());
        _dstDir = Path.Combine(_baseDir, "sync", "Desktop");
        _verDir = Path.Combine(_baseDir, "sync", ".versions");
        Directory.CreateDirectory(_dstDir);
        Directory.CreateDirectory(_verDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, true); } catch { }
    }

    string Dst(string name) => Path.Combine(_dstDir, name);

    [Fact]
    public async Task NoVersioning_DoesNothing()
    {
        var versioning = new NoVersioning();
        await versioning.ArchivePreviousVersionAsync(Dst("a.txt"), CancellationToken.None);
    }

    [Fact]
    public async Task TimestampVersioning_CreatesVersionFile()
    {
        File.WriteAllText(Dst("a.txt"), "version 1");

        var versioning = new TimestampVersioning(_verDir, _dstDir, 10);
        await versioning.ArchivePreviousVersionAsync(Dst("a.txt"), CancellationToken.None);

        Assert.True(File.Exists(Dst("a.txt")));
        var files = Directory.GetFiles(Path.Combine(_verDir, "Desktop"), "a_*");
        // Name contract: a_{yyyyMMdd_HHmmss_fff}_{32-hex-guid}.txt
        var file = Assert.Single(files);
        Assert.Matches(@"^a_\d{8}_\d{6}_\d{3}_[0-9a-f]{32}\.txt$", Path.GetFileName(file));
        Assert.Equal("version 1", File.ReadAllText(file));
    }

    [Fact]
    public async Task TimestampVersioning_NoVersion_WhenFileNotExists()
    {
        var versioning = new TimestampVersioning(_verDir, _dstDir, 10);
        await versioning.ArchivePreviousVersionAsync(Dst("missing.txt"), CancellationToken.None);
        var dir = Path.Combine(_verDir, "Desktop");
        Assert.Empty(Directory.Exists(dir) ? Directory.GetFiles(dir, "missing_*") : Array.Empty<string>());
    }

    [Fact]
    public async Task TimestampVersioning_EnforcesMaxVersions()
    {
        for (int i = 0; i < 15; i++)
        {
            File.WriteAllText(Dst("a.txt"), "version " + i);
            var versioning = new TimestampVersioning(_verDir, _dstDir, 5);
            await versioning.ArchivePreviousVersionAsync(Dst("a.txt"), CancellationToken.None);
        }

        var versions = Directory.GetFiles(Path.Combine(_verDir, "Desktop"), "a_*");
        Assert.True(versions.Length <= 6);
    }

    [Fact]
    public async Task TimestampVersioning_HandlesMultipleFiles()
    {
        File.WriteAllText(Dst("a.txt"), "a version");
        File.WriteAllText(Dst("b.txt"), "b version");

        var versioning = new TimestampVersioning(_verDir, _dstDir, 10);
        await versioning.ArchivePreviousVersionAsync(Dst("a.txt"), CancellationToken.None);
        await versioning.ArchivePreviousVersionAsync(Dst("b.txt"), CancellationToken.None);

        Assert.Equal(2, Directory.GetFiles(Path.Combine(_verDir, "Desktop")).Length);
    }

    [Fact]
    public void Factory_ReturnsNoVersioning_WhenNullConfig()
    {
        var versioning = VersioningFactory.Create(null!, _dstDir);
        Assert.IsType<NoVersioning>(versioning);
    }

    [Fact]
    public void Factory_ReturnsNoVersioning_WhenPathEmpty()
    {
        var versioning = VersioningFactory.Create(new VersioningConfig { Path = "", MaxVersions = 10 }, _dstDir);
        Assert.IsType<NoVersioning>(versioning);
    }

    [Fact]
    public void Factory_ReturnsTimestampVersioning_WhenPathSet()
    {
        var versioning = VersioningFactory.Create(new VersioningConfig { Path = _verDir, MaxVersions = 5 }, _dstDir);
        Assert.IsType<TimestampVersioning>(versioning);
    }
}
