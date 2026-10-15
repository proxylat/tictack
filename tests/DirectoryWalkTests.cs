namespace TicTack;

// Item 11a: MEASURE directory-walk cost on big trees before building any fast
// path. Linux-safe temp dirs; generous bounds (never flaky) — the numbers in
// test output are the artifact, not the assertions.
[Trait("Category", "Perf")]
public sealed class DirectoryWalkTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public DirectoryWalkTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public void DirectoryWalk_BigTree_Timed()
    {
        var root = Path.Combine(Path.GetTempPath(), "TicTackWalk_" + Guid.NewGuid().ToString("N"));
        const int dirs = 50;
        const int perDir = 200;
        var expected = dirs * perDir;
        try
        {
            var payload = new byte[64];
            for (int d = 0; d < dirs; d++)
            {
                var sub = Path.Combine(root, $"d{d:D2}");
                Directory.CreateDirectory(sub);
                for (int i = 0; i < perDir; i++)
                    File.WriteAllBytes(Path.Combine(sub, $"f{i:D5}.bin"), payload);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var enumerated = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();
            sw.Stop();
            var enumerateMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            var statted = 0;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (FileSnapshot.TryRead(f, out _)) statted++;
            }
            sw.Stop();
            var statMs = sw.Elapsed.TotalMilliseconds;

            _output.WriteLine($"DirectoryWalk: files={enumerated} enumerate={enumerateMs:F0}ms enumerate+stat={statMs:F0}ms");
            Assert.Equal(expected, enumerated);
            Assert.Equal(expected, statted);
            Assert.True(enumerateMs < 60_000, $"enumerate took {enumerateMs:F0}ms");
            Assert.True(statMs < 60_000, $"enumerate+stat took {statMs:F0}ms");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
