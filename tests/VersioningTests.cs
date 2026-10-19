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
        const int size = 64 * 1024 * 1024;
        var dest = Dst("race.bin");
        File.WriteAllBytes(dest, new byte[size]);

        var versioning = new TimestampVersioning(_verDir, _dstDir, 10);
        using var stop = new ManualResetEventSlim(false);
        using var firstWrite = new ManualResetEventSlim(false);
        // One handle, held open for the whole archive, doing small random
        // writes in a tight loop. The archive only starts after the first
        // write has landed: without this gate a fast disk can finish the
        // whole copy before the writer thread is first scheduled, and the
        // archive (correctly) succeeds. Deterministic on both OSes, for
        // different reasons: on Windows File.OpenRead (Share.Read) can never
        // open a file with a live Write handle, so the archive fails closed
        // with a sharing message; on Linux share locks don't exist, so the
        // stream hash (prefix bytes) always diverges from the post-copy
        // re-read (hundreds of 4 KB mutations land during the copy window).
        // Either way: fail, commit nothing, leave no temp.
        using var fh = new FileStream(dest, FileMode.Open, FileAccess.Write, FileShare.Read);
        var writer = Task.Run(() =>
        {
            var rnd = new Random(1234);
            var buf = new byte[4096];
            var first = true;
            while (!stop.IsSet)
            {
                rnd.NextBytes(buf);
                try
                {
                    lock (fh)
                    {
                        fh.Seek(rnd.Next(size - buf.Length), SeekOrigin.Begin);
                        fh.Write(buf, 0, buf.Length);
                        fh.Flush(true);
                    }
                    if (first) { first = false; firstWrite.Set(); }
                }
                catch { }
            }
        });
        Assert.True(firstWrite.Wait(TimeSpan.FromSeconds(30)), "writer thread never started");
        ActionResult result;
        try { result = await versioning.ArchivePreviousVersionAsync(dest, CancellationToken.None); }
        finally { stop.Set(); await writer; }

        Assert.False(result.Success);
        // Linux pins the validation path; Windows can never reach it (the
        // open itself throws under a live Write handle), so there we pin
        // only the fail-closed properties below.
        if (OperatingSystem.IsLinux())
            Assert.Contains("Version validation failed", result.ErrorMessage ?? "");
        var verSub = Path.Combine(_verDir, "Desktop");
        Assert.Empty(Directory.Exists(verSub) ? Directory.GetFiles(verSub) : Array.Empty<string>());
    }
}
