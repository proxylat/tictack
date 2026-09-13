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
        File.WriteAllText(tmpFile, "recoverable data");

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = _srcDir, Destination = _dstDir });

        PowerGuard.Cleanup(cfg, _log);

        var recovered = Path.Combine(_dstDir, "recovered.txt");
        Assert.True(File.Exists(recovered));
        Assert.False(File.Exists(tmpFile));
        Assert.Equal("recoverable data", File.ReadAllText(recovered));
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
        Directory.CreateDirectory(dst2);

        var tmp1 = Path.Combine(_dstDir, "f1.txt.tictack.tmp");
        var tmp2 = Path.Combine(dst2, "f2.txt.tictack.tmp");
        File.WriteAllText(tmp1, "data1");
        File.WriteAllText(tmp2, "data2");

        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = _srcDir, Destination = _dstDir });
        var src2 = Path.Combine(Path.GetDirectoryName(_srcDir)!, "src2");
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

        Assert.True(File.Exists(Path.Combine(versionDir, "v1.txt")));
        Assert.False(File.Exists(tmp));
    }
}
