namespace TicTack;

// sync.file_access=vss. Pure-logic tests run everywhere; live snapshot
// tests are Windows-gated and probe-and-skip when unelevated (same shape
// as UsnMonitorTests live tests).
public class VssAccessorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tictack-vss-" + Guid.NewGuid().ToString("N"));

    public VssAccessorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void IsLockViolation_SharingAndLockViolations()
    {
        Assert.True(VssFileAccessor.IsLockViolation(new IOException("sharing", unchecked((int)0x80070020))));
        Assert.True(VssFileAccessor.IsLockViolation(new IOException("lock", unchecked((int)0x80070021))));
        Assert.False(VssFileAccessor.IsLockViolation(new IOException("missing", unchecked((int)0x80070002))));
        Assert.False(VssFileAccessor.IsLockViolation(new InvalidOperationException("other")));
    }

    [Fact]
    public void MapToSnapshot_JoinsDeviceAndRelativePath()
    {
        const string device = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1";
        var mapped = VssFileAccessor.MapToSnapshot(device, @"C:\", @"C:\Users\a.txt");
        Assert.Equal(device + @"\Users\a.txt", mapped);

        var mappedTrailing = VssFileAccessor.MapToSnapshot(device + "\\", @"C:\", @"C:\a.txt");
        Assert.Equal(device + @"\a.txt", mappedTrailing);

        Assert.Throws<IOException>(() => VssFileAccessor.MapToSnapshot(device, @"C:\", @"D:\other.txt"));
    }

    [Fact]
    public void SnapshotTtl_IsTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), VssFileAccessor.SnapshotTtl);
    }

    [Fact]
    public void Config_AcceptsDirectAndVssFileAccess()
    {
        foreach (var mode in new[] { "direct", "vss" })
        {
            var cfg = new TicTackConfig();
            cfg.Sources.Add(new SourceConfig { Path = "/tmp/tt-src", Destination = "/tmp/tt-dst" });
            cfg.Sources[0].Sync.FileAccess = mode;
            Assert.True(Config.Validate(cfg, new RecordingLogger()));
        }
    }

    [Fact]
    public void Config_RejectsUnknownFileAccess()
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = "/tmp/tt-src", Destination = "/tmp/tt-dst" });
        cfg.Sources[0].Sync.FileAccess = "bogus";
        Assert.False(Config.Validate(cfg, new RecordingLogger()));
    }

    [Fact]
    public void OpenRead_ThrowsPlatformOnNonWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        using var accessor = new VssFileAccessor();
        Assert.Throws<PlatformNotSupportedException>(() => accessor.OpenRead(Path.Combine(_dir, "x")));
        Assert.Throws<PlatformNotSupportedException>(() => accessor.Probe());
    }

    [Fact]
    public void Live_ExclusiveLockedFile_ReadsThroughSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var probe = new VssFileAccessor();
            probe.Probe();
        }
        catch
        {
            return; // unelevated: VSS not available, nothing to prove
        }

        var path = Path.Combine(_dir, "locked.bin");
        var bytes = new byte[65536];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => File.ReadAllBytes(path)); // premise: direct reads fail

            using var accessor = new VssFileAccessor();
            using var snap = accessor.OpenRead(path);
            using var ms = new MemoryStream();
            snap.CopyTo(ms);
            Assert.True(bytes.SequenceEqual(ms.ToArray()));
        }
    }

    [Fact]
    public void Live_Dispose_ReleasesSnapshotsWithoutThrowing()
    {
        if (!OperatingSystem.IsWindows()) return;
        VssFileAccessor accessor;
        try
        {
            accessor = new VssFileAccessor();
            accessor.Probe();
        }
        catch
        {
            return; // unelevated
        }

        var path = Path.Combine(_dir, "locked2.bin");
        File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes("release-me"));
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var snap = accessor.OpenRead(path);
            Assert.Equal("release-me", new StreamReader(snap).ReadToEnd());
        }
        accessor.Dispose();

        // A fresh accessor can snapshot the same volume again: the disposed
        // one's shadow copies were released, not leaked into the way.
        using var again = new VssFileAccessor();
        using var direct = again.OpenRead(path);
        Assert.Equal("release-me", new StreamReader(direct).ReadToEnd());
    }
}
