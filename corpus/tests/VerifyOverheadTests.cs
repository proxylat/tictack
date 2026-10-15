using System.Diagnostics;

namespace Corpus;

// Item 10: full re-read verify overhead vs no-verify capture.
// Volume: 8 MiB, chunkMib 1 → 8 chunks, pattern [zeros|text|random|mixed] x2
// (same seeds/shapes as EndToEndTests + CompressionCurveTests, Linux-safe).
// Asserts correctness only (Verify true); timings are output, not asserted.
public sealed class VerifyOverheadTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public VerifyOverheadTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_verifyovh_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
        SeedVolume();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void SeedVolume()
    {
        var zeros = new byte[Mib];
        var text = new byte[Mib];
        var pattern = "The quick brown fox jumps over the lazy dog. "u8;
        for (var i = 0; i < text.Length; i++)
            text[i] = pattern[i % pattern.Length];
        var random = new byte[Mib];
        new Random(42).NextBytes(random);
        var mixed = new byte[Mib];
        for (var i = 0; i < Mib / 2; i++)
            mixed[i] = pattern[i % pattern.Length];
        var tail = new byte[Mib / 2];
        new Random(43).NextBytes(tail);
        Buffer.BlockCopy(tail, 0, mixed, Mib / 2, tail.Length);
        using var f = new FileStream(_volPath, FileMode.Create, FileAccess.Write);
        for (var r = 0; r < 2; r++)
        {
            f.Write(zeros, 0, Mib);
            f.Write(text, 0, Mib);
            f.Write(random, 0, Mib);
            f.Write(mixed, 0, Mib);
        }
    }

    [Fact]
    public void FullReReadVerify_OverheadVsCapture_ReportsPercent()
    {
        using var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger());
        using var source = new FileBlockSource(_volPath);
        var sw = Stopwatch.StartNew();
        var manifest = store.Image(source, "bench");
        sw.Stop();
        var captureMs = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(8, manifest.Chunks.Count);

        sw.Restart();
        var ok = store.Verify(1);
        sw.Stop();
        var verifyMs = sw.Elapsed.TotalMilliseconds;
        Assert.True(ok);
        var overhead = captureMs > 0 ? 100.0 * verifyMs / captureMs : 0;
        _output.WriteLine($"volume_bytes={manifest.VolumeLength} chunks={manifest.Chunks.Count} chunk_mib=1 zstd=3");
        _output.WriteLine($"capture_ms={captureMs:F1} verify_ms={verifyMs:F1} overhead_pct={overhead:F1}");
    }
}
