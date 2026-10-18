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
        File.WriteAllText(Dst("a.txt"), "keep");

        var versioning = new NoVersioning();
        await versioning.ArchivePreviousVersionAsync(Dst("a.txt"), CancellationToken.None);

        Assert.Equal("keep", File.ReadAllText(Dst("a.txt")));
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
        // Production prunes to exactly the cap after every archive.
        Assert.Equal(5, versions.Length);
    }

    [Fact]
    public async Task TimestampVersioning_PrunesNamesWithGlobChars()
    {
        // '[' used to be a char class in the prune glob, so these names were
        // never pruned; prefix/suffix matching fixes it.
        var dest = Dst("a[1].txt");
        File.WriteAllText(dest, "v0");
        var versioning = new TimestampVersioning(_verDir, _dstDir, 3);

        for (int i = 0; i < 8; i++)
        {
            File.WriteAllText(dest, "v" + i);
            await versioning.ArchivePreviousVersionAsync(dest, CancellationToken.None);
        }

        var files = Directory.GetFiles(Path.Combine(_verDir, "Desktop"))
            .Where(f => Path.GetFileName(f).StartsWith("a[1]_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, files.Length);
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

    // Fix 1 guard: a dest mutated mid-archive must fail validation (the
    // copy-stream hash no longer matches the post-copy dest re-read), and
    // the failed archive must commit nothing and leave no temp behind.
    [Fact]
    public async Task TimestampVersioning_DetectsMidCopyModification()
    {
        const int size = 24 * 1024 * 1024;
        var dest = Dst("race.bin");
        File.WriteAllBytes(dest, new byte[size]);

        var versioning = new TimestampVersioning(_verDir, _dstDir, 10);
        using var stop = new ManualResetEventSlim(false);
        // Continuous whole-file rewrites, each state distinct: any copy
        // window overlapping a write streams bytes the final re-read cannot
        // match. Interrupted writes are expected on Windows (share
        // violation against the open copy stream) and retried.
        var writer = Task.Run(() =>
        {
            var buf = new byte[size];
            var i = 1;
            while (!stop.IsSet)
            {
                Array.Fill(buf, (byte)i);
                try { File.WriteAllBytes(dest, buf); } catch { }
                if (++i > 250) i = 1;
            }
        });
        ActionResult result;
        try { result = await versioning.ArchivePreviousVersionAsync(dest, CancellationToken.None); }
        finally { stop.Set(); await writer; }

        Assert.False(result.Success);
        Assert.Contains("Version validation failed", result.ErrorMessage ?? "");
        var verSub = Path.Combine(_verDir, "Desktop");
        Assert.Empty(Directory.Exists(verSub) ? Directory.GetFiles(verSub) : Array.Empty<string>());
    }
}
