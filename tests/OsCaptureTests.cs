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
}
