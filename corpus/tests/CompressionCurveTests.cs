using System.Diagnostics;

namespace Corpus;

// Item 9: zstd level ratio/speed curve on realistic block payloads.
// Dataset (Linux-safe, seeded, 1 MiB each; production chunk is 4 MiB — ratio scales):
//   zeros  = all-zero (sparse/zero guest ranges)
//   text   = repeated sentence (OS text/logs)
//   mixed  = half text + half Random(43) (typical block)
//   random = Random(42) full (encrypted/compressed guest data, incompressible)
// Asserts correctness only (round-trip + no-growth); timings are output, not asserted.
public sealed class CompressionCurveTests
{
    private const int Mib = 1024 * 1024;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public CompressionCurveTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ZstdLevels_RatioSpeedCurve_RoundTrips()
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
        var payloads = new (string Name, byte[] Data)[]
        {
            ("zeros", zeros), ("text", text), ("mixed", mixed), ("random", random),
        };
        var levels = new[] { 1, 3, 6, 9, 12, 19 };

        Compression.Encode(new byte[4096], 3); // warmup (JIT + zstd tables)
        _output.WriteLine("level,payload,plain,stored,ratio_pct,encode_ms,decode_ms,codec");
        foreach (var (name, plain) in payloads)
        {
            foreach (var level in levels)
            {
                var sw = Stopwatch.StartNew();
                var (payload, codec) = Compression.Encode(plain, level);
                sw.Stop();
                var encodeMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var decoded = Compression.Decode(payload, codec);
                sw.Stop();
                Assert.Equal(plain, decoded);
                Assert.True(payload.Length <= plain.Length, $"{name} L{level} grew: {payload.Length} > {plain.Length}");
                var ratio = 100.0 * payload.Length / plain.Length;
                _output.WriteLine($"{level},{name},{plain.Length},{payload.Length},{ratio:F2},{encodeMs:F1},{sw.Elapsed.TotalMilliseconds:F1},{codec}");
            }
        }
    }
}
