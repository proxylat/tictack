namespace Corpus;

public sealed class VssTests
{
    [Fact]
    public void Create_OffWindows_ThrowsPlatformNotSupported()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<PlatformNotSupportedException>(() => VssSnapshot.Create("C:", new TestLogger()));
    }

    [Fact]
    public void Create_BadDrive_ThrowsArgumentException()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<ArgumentException>(() => VssSnapshot.Create(@"C:\Windows", new TestLogger()));
    }

    [Fact]
    public void Snapshot_Lifecycle_CreatesAndDeletes()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.GetEnvironmentVariable("TICTACK_VSS_TESTS") != "1") return;
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        using var s = VssSnapshot.Create(drive, new TestLogger());
        Assert.StartsWith(@"\\?\GLOBALROOT\Device\", s.DeviceObject);
    }

    [Fact]
    public void Snapshot_SequentialReads_MatchLive()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.GetEnvironmentVariable("TICTACK_VSS_TESTS") != "1") return;
        // Needs elevation like the lifecycle test (Create enforces it). No
        // fixture volume: any drive works, the quirk is read-pattern-based.
        // Sequential same-handle shadow reads desync past ~8MB and serve
        // deterministic garbage (32MB over a 100MB volume), so a 16MB probe
        // through the production chunker straddles it: pre-fix this diffs
        // ~8MB from live, post-fix only live skew (KBs over seconds).
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        const int probeBytes = 16 * 1024 * 1024;
        byte[] snap;
        using (var src = new VssBlockSource(drive, new TestLogger()))
        using (var s = src.OpenRead())
        {
            using var ms = new MemoryStream();
            foreach (var chunk in Chunker.Read(s, 1024 * 1024))
            {
                if (ms.Length >= probeBytes) break;
                var want = (int)Math.Min(chunk.Data.Length, probeBytes - ms.Length);
                ms.Write(chunk.Data, 0, want);
            }
            snap = ms.ToArray();
        }
        var live = new byte[probeBytes];
        using (var fs = new FileStream(@"\\.\" + drive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var read = 0;
            while (read < probeBytes)
            {
                var n = fs.Read(live, read, probeBytes - read);
                if (n <= 0) break;
                read += n;
            }
            if (read != probeBytes) Array.Resize(ref live, read);
        }
        var len = Math.Min(snap.Length, live.Length);
        long diff = Math.Abs((long)snap.Length - live.Length);
        for (var i = 0; i < len; i++)
        {
            if (snap[i] != live[i]) diff++;
        }
        Assert.True(diff < 1024 * 1024, $"snapshot diverges {diff} bytes from live in the first {probeBytes}");
    }
}
