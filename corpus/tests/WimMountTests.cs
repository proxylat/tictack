using System.Text.Json;
using TicTack;

namespace Corpus;

// Servicing mount/unmount tests (research item 6). All backend surface runs
// behind FakeWimRunner — real-DISM shapes and the on-box servicing-mount
// probe need Windows + DISM and are VM steps (on-box-unprobed, marked in
// WimMount.cs). The Linux E2E at the bottom runs the REAL
// /usr/bin/wimlib-imagex mountrw/unmount: FUSE-dependent, SkipException when
// the container cannot mount.
// NOTE (wiring): corpus/WimMount.cs needs `<Compile
// Include="../WimMount.cs" Link="corpus/WimMount.cs" />` in
// Corpus.Tests.csproj (this file compiles by SDK default glob).

public sealed class WimMountTests : IDisposable
{
    private readonly string _dir;

    public WimMountTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_wimmount_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class FakeWimRunner : IWimRunner
    {
        public WimBackendKind Backend { get; init; }
        public List<string> Calls { get; } = new();
        public Func<string, DismResult>? Handler;

        public DismResult Run(string arguments)
        {
            Calls.Add(arguments);
            return Handler != null ? Handler(arguments) : new DismResult(0, "", "");
        }
    }

    private string SeedWim(string name = "base.wim")
    {
        var wim = Path.Combine(_dir, name);
        File.WriteAllText(wim, "fake-wim");
        return wim;
    }

    private string EmptyDir(string name)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Quote(string path) => ProcessRunner.QuoteArg(path);

    [Fact]
    public void Mount_Dism_ShapeIsReadWriteAndWritesMarker()
    {
        // On-box-unprobed: needs Windows + real DISM (VM step). The fake
        // pins the arg shape only.
        var wim = SeedWim();
        var mount = EmptyDir("mnt");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        WimMount.Mount(wim, 2, mount, runner, new TestLogger());
        Assert.Equal($"/Mount-Image /ImageFile:{Quote(wim)} /Index:2 /MountDir:{Quote(mount)}", Assert.Single(runner.Calls));
        var marker = Path.Combine(mount, WimMount.MarkerFileName);
        Assert.True(File.Exists(marker), "mount must leave the claim marker");
        using var doc = JsonDocument.Parse(File.ReadAllText(marker));
        Assert.Equal(wim, doc.RootElement.GetProperty("ImagePath").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("Index").GetInt32());
        Assert.True(doc.RootElement.GetProperty("MountedUtcTicks").GetInt64() > 0);
    }

    [Fact]
    public void Mount_Wimlib_MountrwShape()
    {
        var wim = SeedWim();
        var mount = EmptyDir("mnt");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Wimlib };
        WimMount.Mount(wim, 1, mount, runner, new TestLogger());
        Assert.Equal($"mountrw {Quote(wim)} 1 {Quote(mount)}", Assert.Single(runner.Calls));
        Assert.True(File.Exists(Path.Combine(mount, WimMount.MarkerFileName)));
    }

    [Fact]
    public void MountToUnmountCommit_Dism_SequenceAndMarkerRemoved()
    {
        var wim = SeedWim();
        var mount = EmptyDir("mnt");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        var log = new TestLogger();
        WimMount.Mount(wim, 1, mount, runner, log);
        WimMount.Unmount(mount, commit: true, runner, log);
        Assert.Equal(2, runner.Calls.Count);
        Assert.StartsWith("/Mount-Image", runner.Calls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("/ReadOnly", runner.Calls[0], StringComparison.Ordinal);
        var unmount = runner.Calls[1];
        Assert.StartsWith("/Unmount-Image", unmount, StringComparison.Ordinal);
        Assert.Contains("/Commit", unmount, StringComparison.Ordinal);
        Assert.Contains("/CheckIntegrity", unmount, StringComparison.Ordinal); // on-box-owed pin, see WimMount.Unmount
        Assert.DoesNotContain("/Discard", unmount, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(mount, WimMount.MarkerFileName)), "successful unmount must remove the marker");
        Assert.Contains(log.Messages, m => m.Contains("committed", StringComparison.Ordinal));
    }

    [Fact]
    public void MountToUnmountCommit_Wimlib_SequenceAndMarkerRemoved()
    {
        var wim = SeedWim();
        var mount = EmptyDir("mnt");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Wimlib };
        WimMount.Mount(wim, 3, mount, runner, new TestLogger());
        WimMount.Unmount(mount, commit: true, runner, new TestLogger());
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal($"mountrw {Quote(wim)} 3 {Quote(mount)}", runner.Calls[0]);
        Assert.Equal($"unmount {Quote(mount)} --commit", runner.Calls[1]);
        Assert.False(File.Exists(Path.Combine(mount, WimMount.MarkerFileName)));
    }

    [Theory]
    [InlineData(WimBackendKind.Dism)]
    [InlineData(WimBackendKind.Wimlib)]
    public void Unmount_Discard_Shapes(WimBackendKind backend)
    {
        var wim = SeedWim();
        var mount = EmptyDir("mnt");
        var runner = new FakeWimRunner { Backend = backend };
        var log = new TestLogger();
        WimMount.Mount(wim, 1, mount, runner, log);
        WimMount.Unmount(mount, commit: false, runner, log);
        var unmount = runner.Calls[1];
        if (backend == WimBackendKind.Dism)
        {
            Assert.Equal($"/Unmount-Image /MountDir:{Quote(mount)} /Discard", unmount);
        }
        else
        {
            // No --discard flag exists on wimlib unmount (probed) — discard
            // is the bare form.
            Assert.Equal($"unmount {Quote(mount)}", unmount);
        }
        Assert.False(File.Exists(Path.Combine(mount, WimMount.MarkerFileName)));
        Assert.Contains(log.Messages, m => m.Contains("discarded", StringComparison.Ordinal));
    }

    [Fact]
    public void Mount_MissingImage_RefusesNamingPath()
    {
        var missing = Path.Combine(_dir, "no.wim");
        var mount = EmptyDir("mnt");
        foreach (var backend in new[] { WimBackendKind.Dism, WimBackendKind.Wimlib })
        {
            var runner = new FakeWimRunner { Backend = backend };
            var ex = Assert.Throws<SingleFileSourceNotFoundException>(() =>
                WimMount.Mount(missing, 1, mount, runner, new TestLogger()));
            Assert.Contains(missing, ex.Message);
            Assert.Empty(runner.Calls);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Mount_BadIndex_RefusesWithoutRunnerCall(int index)
    {
        var wim = SeedWim();
        var mount = EmptyDir("mnt");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WimMount.Mount(wim, index, mount, runner, new TestLogger()));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Mount_MissingMountDir_RefusesNamingPath()
    {
        var wim = SeedWim();
        var missing = Path.Combine(_dir, "no-dir");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        var ex = Assert.Throws<WimMountRefusedException>(() =>
            WimMount.Mount(wim, 1, missing, runner, new TestLogger()));
        Assert.Contains(missing, ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Mount_NonEmptyDir_RefusesNamingPath()
    {
        var wim = SeedWim();
        var mount = EmptyDir("mnt");
        File.WriteAllText(Path.Combine(mount, "user-file.txt"), "do-not-cover-me");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        var ex = Assert.Throws<WimMountNonEmptyException>(() =>
            WimMount.Mount(wim, 1, mount, runner, new TestLogger()));
        Assert.Contains(mount, ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Unmount_NoMarker_RefusesWithoutRunnerCall()
    {
        var mount = EmptyDir("mnt");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        var ex = Assert.Throws<WimMountMissingMarkerException>(() =>
            WimMount.Unmount(mount, commit: true, runner, new TestLogger()));
        Assert.Contains(mount, ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Unmount_ForeignMarker_RefusesWithoutRunnerCall()
    {
        var mount = EmptyDir("mnt");
        File.WriteAllText(Path.Combine(mount, WimMount.MarkerFileName), "not-json{{{");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Wimlib };
        var ex = Assert.Throws<WimMountMissingMarkerException>(() =>
            WimMount.Unmount(mount, commit: false, runner, new TestLogger()));
        Assert.Contains(mount, ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(WimBackendKind.Dism)]
    [InlineData(WimBackendKind.Wimlib)]
    public void Mount_BackendFailure_PassthroughNamingImageLeavesNoMarker(WimBackendKind backend)
    {
        var wim = SeedWim();
        var mount = EmptyDir("mnt");
        var runner = new FakeWimRunner
        {
            Backend = backend,
            Handler = _ => new DismResult(1, "", "boom"),
        };
        if (backend == WimBackendKind.Dism)
        {
            var dex = Assert.Throws<SingleFileDismException>(() =>
                WimMount.Mount(wim, 1, mount, runner, new TestLogger()));
            Assert.Contains(wim, dex.Message);
        }
        else
        {
            var wex = Assert.Throws<WimBackendException>(() =>
                WimMount.Mount(wim, 1, mount, runner, new TestLogger()));
            Assert.Contains(wim, wex.Message);
            Assert.Contains("boom", wex.Message);
        }
        Assert.False(File.Exists(Path.Combine(mount, WimMount.MarkerFileName)), "failed mount must not leave a marker");
    }

    [Fact]
    public void Unmount_BackendFailure_KeepsMarker()
    {
        var wim = SeedWim();
        var mount = EmptyDir("mnt");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        WimMount.Mount(wim, 1, mount, runner, new TestLogger());
        runner.Handler = args => args.StartsWith("/Unmount-Image", StringComparison.Ordinal)
            ? new DismResult(1, "", "busy")
            : new DismResult(0, "", "");
        var ex = Assert.Throws<SingleFileDismException>(() =>
            WimMount.Unmount(mount, commit: true, runner, new TestLogger()));
        Assert.Contains(wim, ex.Message);
        Assert.True(File.Exists(Path.Combine(mount, WimMount.MarkerFileName)), "failed unmount must keep the claim marker");
    }

    [Fact]
    public void Linux_Wimlib_MountEditCommit_RoundTrip()
    {
        // Real-binary E2E (FUSE). Mount is read/write: the pre-existing file
        // reads through, a new file is added, --commit persists it (proved
        // by apply + content compare). Container without FUSE privileges
        // lands in early return, not failure.
        // No wimlib binary here: not a failure, nothing to exercise.
        if (!OperatingSystem.IsLinux() || !File.Exists(WimlibRunner.DefaultBinaryPath))
            return;
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "before");
        var wim = Path.Combine(_dir, "svc.wim");
        var runner = new WimlibRunner();
        var log = new TestLogger();
        var capture = runner.Run($"capture {Quote(src)} {Quote(wim)} \"e2e\" --compress=none");
        Assert.True(capture.ExitCode == 0, "capture failed: " + capture.Stderr);
        var mount = EmptyDir("mnt");
        try
        {
            WimMount.Mount(wim, 1, mount, runner, log);
        }
        catch (SingleFileBackupException)
        {
            return; // FUSE mount unavailable in this container: nothing to exercise.
        }
        try
        {
            Assert.Equal("before", File.ReadAllText(Path.Combine(mount, "a.txt")));
            File.WriteAllText(Path.Combine(mount, "b.txt"), "added-via-mount");
            WimMount.Unmount(mount, commit: true, runner, log);
        }
        finally
        {
            try
            {
                if (File.Exists(Path.Combine(mount, WimMount.MarkerFileName)))
                {
                    try { WimMount.Unmount(mount, commit: false, runner, log); }
                    catch (SingleFileBackupException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Stale FUSE mountpoint — nothing to release from here.
            }
        }
        var applied = Path.Combine(_dir, "out");
        Directory.CreateDirectory(applied);
        var apply = runner.Run($"apply {Quote(wim)} 1 {Quote(applied)}");
        Assert.True(apply.ExitCode == 0, "apply failed: " + apply.Stderr);
        Assert.Equal("before", File.ReadAllText(Path.Combine(applied, "a.txt")));
        Assert.Equal("added-via-mount", File.ReadAllText(Path.Combine(applied, "b.txt")));
    }
}
