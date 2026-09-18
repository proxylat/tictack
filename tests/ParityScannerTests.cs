namespace TicTack;

[Trait("Category", "Unit")]
public sealed class ParityScannerTests
{
    private const string Src = "/parity-src";
    private const string Dst = "/parity-dst";

    private static ParityScanner Make(
        Func<string, bool> directoryExists,
        IEnumerable<string> sourceFiles,
        IEnumerable<string> destFiles,
        IEnumerable<string> destDirs,
        RecordingDeletion deletion,
        RecordingLogger log,
        IFileFilter? filter = null)
    {
        var cfg = new SourceConfig { Path = Src, Destination = Dst };
        var deleter = new TrackedDeleter(deletion, null, log);
        return new ParityScanner(
            cfg,
            filter,
            directoryExists,
            (p, _) => p == Src ? sourceFiles : destFiles,
            (p, _) => p == Src ? Array.Empty<string>() : destDirs,
            deleter,
            log);
    }

    [Fact]
    public async Task Enforce_SourceMissing_BlocksWithWarn()
    {
        var log = new RecordingLogger();
        var deletion = new RecordingDeletion(new OkDeletion());
        var scanner = Make(p => p != Src, Array.Empty<string>(), new[] { Dst + "/stale.txt" }, Array.Empty<string>(), deletion, log);

        await scanner.EnforceAsync(CancellationToken.None);

        Assert.Contains(log.Messages, m => m.Contains("Source folder missing, parity cleanup blocked"));
        Assert.Empty(deletion.Calls);
    }

    [Fact]
    public async Task Enforce_SourceScanThrows_BlocksWithWarn()
    {
        var log = new RecordingLogger();
        var deletion = new RecordingDeletion(new OkDeletion());
        var cfg = new SourceConfig { Path = Src, Destination = Dst };
        var scanner = new ParityScanner(
            cfg,
            null,
            _ => true,
            (p, _) => p == Src ? throw new UnauthorizedAccessException("denied") : Array.Empty<string>(),
            (_, _) => Array.Empty<string>(),
            new TrackedDeleter(deletion, null, log),
            log);

        await scanner.EnforceAsync(CancellationToken.None);

        Assert.Contains(log.Messages, m => m.Contains("Source scan incomplete, parity cleanup blocked"));
        Assert.Empty(deletion.Calls);
    }

    [Fact]
    public async Task Enforce_DestinationMissing_ReturnsWithoutDeleting()
    {
        var log = new RecordingLogger();
        var deletion = new RecordingDeletion(new OkDeletion());
        var scanner = Make(p => p == Src, new[] { Src + "/keep.txt" }, new[] { Dst + "/stale.txt" }, Array.Empty<string>(), deletion, log);

        await scanner.EnforceAsync(CancellationToken.None);

        Assert.Empty(deletion.Calls);
    }

    [Fact]
    public async Task Enforce_CancelledToken_ReturnsWithoutEnumeratingDestination()
    {
        var log = new RecordingLogger();
        var deletion = new RecordingDeletion(new OkDeletion());
        var destEnumerated = false;
        var cfg = new SourceConfig { Path = Src, Destination = Dst };
        var scanner = new ParityScanner(
            cfg,
            null,
            _ => true,
            (p, _) => p == Src ? new[] { Src + "/keep.txt" } : Array.Empty<string>(),
            (_, _) => { destEnumerated = true; return Array.Empty<string>(); },
            new TrackedDeleter(deletion, null, log),
            log);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await scanner.EnforceAsync(cts.Token);

        Assert.Empty(deletion.Calls);
        Assert.False(destEnumerated, "cancelled scan still enumerated the destination");
    }

    [Fact]
    public async Task Enforce_DestFileWithNoSource_IsDeletedAndTracked()
    {
        var log = new RecordingLogger();
        var deletion = new RecordingDeletion(new OkDeletion());
        var scanner = Make(
            _ => true,
            new[] { Src + "/keep.txt" },
            new[] { Dst + "/keep.txt", Dst + "/stale.txt" },
            Array.Empty<string>(),
            deletion,
            log);

        await scanner.EnforceAsync(CancellationToken.None);

        var call = Assert.Single(deletion.Calls);
        Assert.Null(call.src);
        Assert.Equal(Dst + "/stale.txt", call.dst);
        Assert.Contains(log.Messages, m => m.Contains("Cleanup: archived 1 stale files"));
    }

    [Fact]
    public async Task Enforce_ControlAndArchivePaths_AreSkipped()
    {
        var log = new RecordingLogger();
        var deletion = new RecordingDeletion(new OkDeletion());
        var scanner = Make(
            _ => true,
            Array.Empty<string>(),
            new[]
            {
                Dst + "/.tictack.lock",
                Dst + "/.tictack-deferred.json",
                Dst + "/.archive/old.txt",
                Dst + "/.versions/old.txt",
            },
            Array.Empty<string>(),
            deletion,
            log);

        await scanner.EnforceAsync(CancellationToken.None);

        Assert.Empty(deletion.Calls);
    }

    [Fact]
    public async Task Enforce_StaleDirectory_IsDeletedAndAncestorKept()
    {
        var log = new RecordingLogger();
        var deletion = new RecordingDeletion(new OkDeletion());
        var scanner = Make(
            _ => true,
            new[] { Src + "/sub/keep.txt" },
            new[] { Dst + "/sub/keep.txt" },
            new[] { Dst + "/sub", Dst + "/gone", Dst + "/.archive" },
            deletion,
            log);

        await scanner.EnforceAsync(CancellationToken.None);

        var call = Assert.Single(deletion.Calls);
        Assert.Equal(Dst + "/gone", call.dst);
        Assert.Contains(log.Messages, m => m.Contains("Cleanup: removing stale dir"));
    }

    [Fact]
    public async Task Enforce_DestFileScanThrows_BlocksWithWarn()
    {
        var log = new RecordingLogger();
        var deletion = new RecordingDeletion(new OkDeletion());
        var cfg = new SourceConfig { Path = Src, Destination = Dst };
        var scanner = new ParityScanner(
            cfg,
            null,
            _ => true,
            (p, _) => p == Src ? Array.Empty<string>() : throw new IOException("io"),
            (_, _) => Array.Empty<string>(),
            new TrackedDeleter(deletion, null, log),
            log);

        await scanner.EnforceAsync(CancellationToken.None);

        Assert.Contains(log.Messages, m => m.Contains("Destination scan incomplete, parity cleanup blocked"));
        Assert.Empty(deletion.Calls);
    }

    [Fact]
    public async Task Enforce_DestDirectoryScanThrows_BlocksWithWarn()
    {
        var log = new RecordingLogger();
        var deletion = new RecordingDeletion(new OkDeletion());
        var cfg = new SourceConfig { Path = Src, Destination = Dst };
        var scanner = new ParityScanner(
            cfg,
            null,
            _ => true,
            (_, _) => Array.Empty<string>(),
            (p, _) => p == Src ? Array.Empty<string>() : throw new IOException("io"),
            new TrackedDeleter(deletion, null, log),
            log);

        await scanner.EnforceAsync(CancellationToken.None);

        Assert.Contains(log.Messages, m => m.Contains("Destination directory scan incomplete, parity cleanup blocked"));
        Assert.Empty(deletion.Calls);
    }

    [Fact]
    public async Task Enforce_FilteredSourceFile_IsNotProtectedFromParity()
    {
        var log = new RecordingLogger();
        var deletion = new RecordingDeletion(new OkDeletion());
        var scanner = Make(
            _ => true,
            new[] { Src + "/a.tmp" },
            new[] { Dst + "/a.tmp" },
            Array.Empty<string>(),
            deletion,
            log,
            new ExcludeTmpFilter());

        await scanner.EnforceAsync(CancellationToken.None);

        var call = Assert.Single(deletion.Calls);
        Assert.Equal(Dst + "/a.tmp", call.dst);
    }

    private sealed class OkDeletion : IDeletionStrategy
    {
        public Task<ActionResult> HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct) =>
            Task.FromResult(ActionResult.Ok());
    }

    private sealed class ExcludeTmpFilter : IFileFilter
    {
        public bool ShouldProcess(string fullPath) => !fullPath.EndsWith(".tmp", StringComparison.Ordinal);
    }
}
