namespace TicTack;

public sealed class OsCaptureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tictack-oscap-" + Guid.NewGuid().ToString("N"));

    public OsCaptureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    sealed class MemoryAccessor : IFileAccessor
    {
        public int Opens;
        private readonly byte[] _bytes;
        public MemoryAccessor(byte[] bytes) => _bytes = bytes;
        public Stream OpenRead(string path) { Opens++; return new MemoryStream(_bytes, false); }
    }

    [Theory]
    [InlineData("Boot/BCD", true)]
    [InlineData(@"EFI\Microsoft\Boot\BCD", true)]
    [InlineData("bootmgr", true)]
    [InlineData("BOOTMGR", true)]
    [InlineData("Windows/System32/winload.exe", true)]
    [InlineData("Windows/System32/winresume.efi", true)]
    [InlineData("boot/vmlinuz-6.8", true)]
    [InlineData("boot/grub/grub.cfg", true)]
    [InlineData("Users/a.txt", false)]
    [InlineData("Windows/System32/kernel32.dll", false)]
    [InlineData("", false)]
    public void IsBootFile_Matrix(string? rel, bool expected)
    {
        Assert.Equal(expected, OsCapture.IsBootFile(rel));
    }

    [Fact]
    public void IsBootFile_Null_IsFalse()
    {
        Assert.False(OsCapture.IsBootFile(null));
    }

    [Fact]
    public async Task Capture_Restore_RoundTrip()
    {
        var src = Path.Combine(_dir, "live", "winload.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        var bytes = new byte[4096];
        new Random(11).NextBytes(bytes);
        await File.WriteAllBytesAsync(src, bytes);

        var cap = Path.Combine(_dir, "capture", "Windows", "System32", "winload.exe");
        await OsCapture.CaptureAsync(new FileAccessor(), src, cap, CancellationToken.None);
        var capped = await File.ReadAllBytesAsync(cap);
        Assert.True(bytes.SequenceEqual(capped));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(cap)!, "*.tictack.tmp"));

        var dst = Path.Combine(_dir, "restored", "winload.exe");
        await OsCapture.RestoreAsync(cap, dst, CancellationToken.None);
        var restored = await File.ReadAllBytesAsync(dst);
        Assert.True(bytes.SequenceEqual(restored));
    }

    [Fact]
    public async Task Capture_UsesCallerAccessor()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("snapshot-bytes");
        var accessor = new MemoryAccessor(bytes);
        var cap = Path.Combine(_dir, "cap", "bcd");
        await OsCapture.CaptureAsync(accessor, @"C:\Boot\BCD", cap, CancellationToken.None);
        Assert.Equal(1, accessor.Opens);
        Assert.Equal("snapshot-bytes", await File.ReadAllTextAsync(cap));
    }

    static void SeedTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Boot"));
        Directory.CreateDirectory(Path.Combine(root, "Users"));
        File.WriteAllText(Path.Combine(root, "Boot", "BCD"), "bcd-bytes");
        File.WriteAllText(Path.Combine(root, "Users", "a.txt"), "user-bytes");
    }

    [Fact]
    public async Task CaptureTree_CopiesWholeTree()
    {
        var live = Path.Combine(_dir, "live");
        var cap = Path.Combine(_dir, "cap");
        SeedTree(live);
        var log = new RecordingLogger();

        var (captured, failed) = await OsCaptureRunner.CaptureTreeAsync(
            new FileAccessor(), live, cap, log, CancellationToken.None);

        Assert.Equal((2, 0), (captured, failed));
        Assert.Equal("bcd-bytes", File.ReadAllText(Path.Combine(cap, "Boot", "BCD")));
        Assert.Equal("user-bytes", File.ReadAllText(Path.Combine(cap, "Users", "a.txt")));
        Assert.Empty(Directory.GetFiles(cap, "*.tictack.tmp", SearchOption.AllDirectories));
        Assert.Contains(log.Messages, m => m.Contains("1 boot-critical"));
    }

    [Fact]
    public async Task CaptureTree_UsesCallerAccessor()
    {
        var live = Path.Combine(_dir, "live");
        var cap = Path.Combine(_dir, "cap");
        SeedTree(live);
        var accessor = new MemoryAccessor(System.Text.Encoding.UTF8.GetBytes("snap"));

        var (captured, failed) = await OsCaptureRunner.CaptureTreeAsync(
            accessor, live, cap, new RecordingLogger(), CancellationToken.None);

        Assert.Equal((2, 0), (captured, failed));
        Assert.Equal(2, accessor.Opens);
    }

    [Fact]
    public async Task RestoreTree_RoundTrip_LogsBootloaderHint()
    {
        var live = Path.Combine(_dir, "live");
        var cap = Path.Combine(_dir, "cap");
        var back = Path.Combine(_dir, "back");
        SeedTree(live);
        var log = new RecordingLogger();
        await OsCaptureRunner.CaptureTreeAsync(new FileAccessor(), live, cap, log, CancellationToken.None);

        var (restored, failed) = await OsCaptureRunner.RestoreTreeAsync(cap, back, log, CancellationToken.None);

        Assert.Equal((2, 0), (restored, failed));
        Assert.Equal("bcd-bytes", File.ReadAllText(Path.Combine(back, "Boot", "BCD")));
        var hint = OperatingSystem.IsWindows() ? "bcdboot" : "GRUB";
        Assert.Contains(log.Messages, m => m.Contains(hint));
    }

    static TicTackConfig OsCfg() => new() { Sources = new List<SourceConfig>() };

    [Fact]
    public async Task RunOsAsync_MissingArgs_ReturnsOne()
    {
        Assert.Equal(1, await Program.RunOsAsync(OsCfg(), new RecordingLogger(), false, null, null));
        Assert.Equal(1, await Program.RunOsAsync(OsCfg(), new RecordingLogger(), false,
            Path.Combine(_dir, "nope"), Path.Combine(_dir, "cap")));
    }

    [Fact]
    public async Task RunOsAsync_DestInsideSource_Refuses()
    {
        var live = Path.Combine(_dir, "live");
        SeedTree(live);
        var log = new RecordingLogger();

        var rc = await Program.RunOsAsync(OsCfg(), log, false, live, Path.Combine(live, "cap"));

        Assert.Equal(1, rc);
        Assert.Contains(log.Messages, m => m.Contains("must not be inside"));
    }

    [Fact]
    public async Task RunOsAsync_Capture_Restore_RoundTrip()
    {
        var live = Path.Combine(_dir, "live");
        var cap = Path.Combine(_dir, "cap");
        var back = Path.Combine(_dir, "back");
        SeedTree(live);
        var log = new RecordingLogger();

        Assert.Equal(0, await Program.RunOsAsync(OsCfg(), log, false, live, cap));
        Assert.Equal(0, await Program.RunOsAsync(OsCfg(), log, true, cap, back));
        Assert.Equal("user-bytes", File.ReadAllText(Path.Combine(back, "Users", "a.txt")));
    }

    [Fact]
    public void CreateOsAccessor_DirectByDefault()
    {
        Assert.IsType<FileAccessor>(Program.CreateOsAccessor(OsCfg(), new RecordingLogger()));
    }
}
