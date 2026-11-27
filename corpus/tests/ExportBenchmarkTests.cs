using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Corpus.Tests;

[Trait("Category", "Benchmark")]
public sealed class ExportBenchmarkTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public ExportBenchmarkTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "corpus-exp-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] MakeRealisticVolume(int totalBytes)
    {
        var data = new byte[totalBytes];
        var rng = new Random(1337);
        var chunk = 64 * 1024;
        var textPattern = Encoding.ASCII.GetBytes("Windows\\System32\\ntoskrnl.exe - Corpus benchmark compressible payload block 0123456789ABCDEF\n");
        for (var off = 0; off < totalBytes; off += chunk)
        {
            var len = Math.Min(chunk, totalBytes - off);
            var mode = (off / chunk) % 4;
            if (mode == 0)
            {
                // 25% sparse zeros
                continue;
            }
            if (mode is 1 or 2)
            {
                // 50% realistic compressible data (binaries/text)
                for (var i = 0; i < len; i++)
                    data[off + i] = textPattern[(i + (off >> 12)) % textPattern.Length];
            }
            else
            {
                // 25% incompressible (media/compressed archives)
                rng.NextBytes(data.AsSpan(off, len));
            }
        }
        return data;
    }

    [Fact]
    public void BenchmarkAllExportPipelines()
    {
        const int sizeMb = 128;
        const int totalBytes = sizeMb * 1024 * 1024;
        var rawPath = Path.Combine(_dir, "vol.raw");
        File.WriteAllBytes(rawPath, MakeRealisticVolume(totalBytes));
        var log = new TestLogger();

        // 1. Direct OVF (VMDK + OVF + MF)
        var ovfDir = Path.Combine(_dir, "ovf-direct");
        var sw = Stopwatch.StartNew();
        using (var src = new FileBlockSource(rawPath))
        {
            OvfExporter.ExportOvf(Path.Combine(ovfDir, "disk.ovf"), "disk", src.Length,
                s => OvfExporter.StreamBlockSource(src, s, 0, null));
        }
        sw.Stop();
        _out.WriteLine($"[BENCH] Direct ExportOvf ({sizeMb} MiB): {sw.Elapsed.TotalMilliseconds:F0} ms ({sizeMb / sw.Elapsed.TotalSeconds:F1} MiB/s)");

        // 2. Direct OVA (VMDK + OVF + MF + TAR)
        var ovaPath = Path.Combine(_dir, "disk.ova");
        sw.Restart();
        using (var src = new FileBlockSource(rawPath))
        {
            OvfExporter.ExportOva(ovaPath, "disk", src.Length,
                s => OvfExporter.StreamBlockSource(src, s, 0, null));
        }
        sw.Stop();
        _out.WriteLine($"[BENCH] Direct ExportOva ({sizeMb} MiB): {sw.Elapsed.TotalMilliseconds:F0} ms ({sizeMb / sw.Elapsed.TotalSeconds:F1} MiB/s)");

        // 3. Build a chunk store once, then benchmark Store.Export (raw), Store.ExportVhdx, Store.ExportOvf
        var storeDir = Path.Combine(_dir, "store");
        var dbPath = Path.Combine(_dir, "chunks.db");
        using (var store = new Store(storeDir, dbPath, 4, 16, log))
        {
            using (var src = new FileBlockSource(rawPath))
                store.Image(src, "bench");

            var outRaw = Path.Combine(_dir, "store-export.raw");
            sw.Restart();
            store.Export(1, outRaw);
            sw.Stop();
            _out.WriteLine($"[BENCH] Store Export raw ({sizeMb} MiB): {sw.Elapsed.TotalMilliseconds:F0} ms ({sizeMb / sw.Elapsed.TotalSeconds:F1} MiB/s)");

            var outVhdx = Path.Combine(_dir, "store-export.vhdx");
            sw.Restart();
            store.ExportVhdx(1, outVhdx);
            sw.Stop();
            _out.WriteLine($"[BENCH] Store ExportVhdx ({sizeMb} MiB): {sw.Elapsed.TotalMilliseconds:F0} ms ({sizeMb / sw.Elapsed.TotalSeconds:F1} MiB/s)");

            var outStoreOvf = Path.Combine(_dir, "store-ovf", "disk.ovf");
            sw.Restart();
            store.ExportOvf(1, outStoreOvf);
            sw.Stop();
            _out.WriteLine($"[BENCH] Store ExportOvf ({sizeMb} MiB): {sw.Elapsed.TotalMilliseconds:F0} ms ({sizeMb / sw.Elapsed.TotalSeconds:F1} MiB/s)");
        }

        // 4. --zstd sidecar compression
        var zstdInput = Path.Combine(_dir, "sidecar.bin");
        File.Copy(rawPath, zstdInput);
        sw.Restart();
        SingleFileBackup.CompressSidecar(zstdInput, log, level: 3);
        sw.Stop();
        _out.WriteLine($"[BENCH] CompressSidecar --zstd ({sizeMb} MiB): {sw.Elapsed.TotalMilliseconds:F0} ms ({sizeMb / sw.Elapsed.TotalSeconds:F1} MiB/s)");
    }
}
