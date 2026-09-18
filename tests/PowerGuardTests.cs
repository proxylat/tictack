namespace TicTack;

public class PowerGuardTests : IDisposable
{
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly string _verDir;
    private readonly RecordingLogger _log = new();

    public PowerGuardTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "TicTackTest_pg_" + Guid.NewGuid());
        _srcDir = Path.Combine(baseDir, "src");
        _dstDir = Path.Combine(baseDir, "dst");
        _verDir = Path.Combine(baseDir, "ver");
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_dstDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_srcDir)!, true); } catch { }
    }

    [Fact]
    public void Cleanup_RecoversOrphanedTemp()
    {
        var tmpFile = Path.Combine(_dstDir, "recovered.txt.tictack.tmp");
        var sourceFile = Path.Combine(_srcDir, "recovered.txt");
        File.WriteAllText(sourceFile, "recoverable data");
        File.Copy(sourceFile, tmpFile);

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = _srcDir, Destination = _dstDir });

        PowerGuard.Cleanup(cfg, _log);

        var recovered = Path.Combine(_dstDir, "recovered.txt");
        Assert.True(File.Exists(recovered));
        Assert.False(File.Exists(tmpFile));
        Assert.Equal("recoverable data", File.ReadAllText(recovered));
    }

    [Fact]
    public void Cleanup_DeletesUnvalidatedTemp()
    {
        var tmpFile = Path.Combine(_dstDir, "partial.txt.tictack.tmp");
        File.WriteAllText(Path.Combine(_srcDir, "partial.txt"), "complete data");
        File.WriteAllText(tmpFile, "partial");

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = _srcDir, Destination = _dstDir });

        PowerGuard.Cleanup(cfg, _log);

        Assert.False(File.Exists(Path.Combine(_dstDir, "partial.txt")));
        Assert.False(File.Exists(tmpFile));
    }

    [Fact]
    public void Cleanup_DeletesOrphanedTemp_WhenDestExists()
    {
        var destFile = Path.Combine(_dstDir, "exists.txt");
        var tmpFile = Path.Combine(_dstDir, "exists.txt.tictack.tmp");
        File.WriteAllText(destFile, "original");
        File.WriteAllText(tmpFile, "stale temp");

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = _srcDir, Destination = _dstDir });

        PowerGuard.Cleanup(cfg, _log);

        Assert.True(File.Exists(destFile));
        Assert.False(File.Exists(tmpFile));
        Assert.Equal("original", File.ReadAllText(destFile));
    }

    [Fact]
    public void Cleanup_ProcessesAllSources()
    {
        var dst2 = Path.Combine(Path.GetDirectoryName(_dstDir)!, "dst2");
        var src2 = Path.Combine(Path.GetDirectoryName(_srcDir)!, "src2");
        Directory.CreateDirectory(dst2);
        Directory.CreateDirectory(src2);

        var tmp1 = Path.Combine(_dstDir, "f1.txt.tictack.tmp");
        var tmp2 = Path.Combine(dst2, "f2.txt.tictack.tmp");
        File.WriteAllText(Path.Combine(_srcDir, "f1.txt"), "data1");
        File.WriteAllText(Path.Combine(src2, "f2.txt"), "data2");
        File.Copy(Path.Combine(_srcDir, "f1.txt"), tmp1);
        File.Copy(Path.Combine(src2, "f2.txt"), tmp2);

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = _srcDir, Destination = _dstDir });
        cfg.Sources.Add(new SourceConfig { Path = src2, Destination = dst2 });

        PowerGuard.Cleanup(cfg, _log);

        Assert.True(File.Exists(Path.Combine(_dstDir, "f1.txt")));
        Assert.True(File.Exists(Path.Combine(dst2, "f2.txt")));

        try { Directory.Delete(dst2, true); } catch { }
    }

    [Fact]
    public void Cleanup_NullConfig_DoesNotThrow()
    {
        PowerGuard.Cleanup(null, _log);
    }

    [Fact]
    public void Cleanup_NullSources_DoesNotThrow()
    {
        var cfg = new TicTackConfig();
        cfg.Sources = null!;
        PowerGuard.Cleanup(cfg, _log);
    }

    [Fact]
    public void Cleanup_HandlesVersionDir()
    {
        var versionDir = Path.Combine(Path.GetDirectoryName(_srcDir)!, "versions");
        Directory.CreateDirectory(versionDir);
        var tmp = Path.Combine(versionDir, "v1.txt.tictack.tmp");
        File.WriteAllText(tmp, "version data");

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig
        {
            Path = _srcDir,
            Destination = _dstDir,
            Sync = new SyncConfig
            {
                Versioning = new VersioningConfig { Path = versionDir }
            }
        });

        PowerGuard.Cleanup(cfg, _log);

        Assert.False(File.Exists(Path.Combine(versionDir, "v1.txt")));
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void Cleanup_DeletesTemp_WhenSameLengthButDifferentContent()
    {
        var tmpFile = Path.Combine(_dstDir, "same.txt.tictack.tmp");
        File.WriteAllText(Path.Combine(_srcDir, "same.txt"), "AAAAAAAA");
        File.WriteAllText(tmpFile, "BBBBBBBB");

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = _srcDir, Destination = _dstDir });

        PowerGuard.Cleanup(cfg, _log);

        Assert.False(File.Exists(Path.Combine(_dstDir, "same.txt")));
        Assert.False(File.Exists(tmpFile));
    }

    [Fact]
    public void Cleanup_RecoversNestedTemp()
    {
        var sub = Path.Combine(_dstDir, "sub");
        var srcSub = Path.Combine(_srcDir, "sub");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(srcSub);
        File.WriteAllText(Path.Combine(srcSub, "x.txt"), "nested data");
        File.Copy(Path.Combine(srcSub, "x.txt"), Path.Combine(sub, "x.txt.tictack.tmp"));

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = _srcDir, Destination = _dstDir });

        PowerGuard.Cleanup(cfg, _log);

        Assert.Equal("nested data", File.ReadAllText(Path.Combine(sub, "x.txt")));
        Assert.False(File.Exists(Path.Combine(sub, "x.txt.tictack.tmp")));
    }

    [Fact]
    public void Cleanup_DeletesTemp_WhenSourceMissing()
    {
        var tmpFile = Path.Combine(_dstDir, "orphan.txt.tictack.tmp");
        File.WriteAllText(tmpFile, "orphan");

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = _srcDir, Destination = _dstDir });

        PowerGuard.Cleanup(cfg, _log);

        Assert.False(File.Exists(Path.Combine(_dstDir, "orphan.txt")));
        Assert.False(File.Exists(tmpFile));
    }

    [Fact]
    public void Cleanup_HandlesDeletionDir()
    {
        var delDir = Path.Combine(Path.GetDirectoryName(_srcDir)!, "deleted");
        Directory.CreateDirectory(delDir);
        var tmp = Path.Combine(delDir, "d1.txt.tictack.tmp");
        File.WriteAllText(tmp, "deleted data");

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig
        {
            Path = _srcDir,
            Destination = _dstDir,
            Sync = new SyncConfig
            {
                Deletion = new DeletionConfig { Path = delDir }
            }
        });

        PowerGuard.Cleanup(cfg, _log);

        Assert.False(File.Exists(Path.Combine(delDir, "d1.txt")));
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void Cleanup_SkipsUnauthorizedDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        if (Environment.UserName == "root") return; // chmod is meaningless for root
        var locked = Path.Combine(_dstDir, "locked");
        Directory.CreateDirectory(locked);
        var tmp = Path.Combine(locked, "x.txt.tictack.tmp");
        File.WriteAllText(tmp, "data");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var cfg = new TicTackConfig();
            cfg.Sources.Add(new SourceConfig { Path = _srcDir, Destination = _dstDir });

            // Must not throw; the locked dir is skipped silently.
            PowerGuard.Cleanup(cfg, _log);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        // Checked after permissions are restored: File.Exists cannot traverse
        // a 000 directory, so asserting inside the try would always fail.
        Assert.True(File.Exists(tmp));
    }
}
