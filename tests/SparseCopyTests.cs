using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TicTack.Tests;

public sealed class SparseCopyTests : IDisposable
{
    private readonly string _dir;

    public SparseCopyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tictack-sparse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopyAction_PreservesSparseHoles_WithoutDiskInflation(bool computeHash)
    {
        const long logicalSize = 32L * 1024 * 1024;
        var srcDir = Path.Combine(_dir, "src");
        var dstDir = Path.Combine(_dir, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        var src = Path.Combine(srcDir, "vm-disk.img");
        var dst = Path.Combine(dstDir, "vm-disk.img");

        var head = new byte[65536];
        var tail = new byte[65536];
        new Random(42).NextBytes(head);
        new Random(99).NextBytes(tail);

        using (var fs = new FileStream(src, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (OperatingSystem.IsWindows())
                SetSparse(fs);
            fs.Write(head, 0, head.Length);
            fs.Seek(logicalSize - tail.Length, SeekOrigin.Begin);
            fs.Write(tail, 0, tail.Length);
            fs.SetLength(logicalSize);
        }

        var copy = new CopyAction(new FileAccessor(), fullDurability: false)
        {
            ComputeSourceHash = computeHash
        };

        var snap = new FileSnapshot(logicalSize, File.GetLastWriteTimeUtc(src).Ticks, FileAttributes.SparseFile);
        var ev = new FileChangedEventArgs(ChangeType.Created, src);
        var res = await copy.ExecuteAsync(new FileActionArgs(ev, srcDir, dstDir, snap), CancellationToken.None);

        Assert.True(res.Success, res.ErrorMessage);
        Assert.True(File.Exists(dst));
        Assert.Equal(logicalSize, new FileInfo(dst).Length);

        var expectedHash = Sha256File(src);
        Assert.Equal(expectedHash, Sha256File(dst));
        if (computeHash)
            Assert.Equal(expectedHash, res.SourceHash);

        var allocBytes = GetAllocatedBytes(dst);
        Assert.True(allocBytes >= 0 && allocBytes < 2 * 1024 * 1024,
            $"Expected sparse destination < 2 MiB on disk, got {allocBytes} bytes");
    }

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static long GetAllocatedBytes(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            uint low = GetCompressedFileSizeW(path, out uint high);
            return ((long)high << 32) | low;
        }
        var psi = new ProcessStartInfo("stat", $"-c %b \"{path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi)!;
        var outText = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return long.Parse(outText, System.Globalization.CultureInfo.InvariantCulture) * 512L;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    private static void SetSparse(FileStream fs)
    {
        const uint FSCTL_SET_SPARSE = 0x000900C4;
        DeviceIoControl(fs.SafeFileHandle.DangerousGetHandle(), FSCTL_SET_SPARSE,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }
}
