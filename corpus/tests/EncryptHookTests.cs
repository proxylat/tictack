namespace Corpus;

public sealed class EncryptHookTests : IDisposable
{
    private readonly string _dir;

    public EncryptHookTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_hook_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] Digest(byte fill)
    {
        var d = new byte[32];
        for (var i = 0; i < 32; i++) d[i] = fill;
        return d;
    }

    private string WriteScript(string body)
    {
        var path = Path.Combine(_dir, Guid.NewGuid() + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        return path;
    }

    private static string HookCmd(string script) => "sh \"" + script + "\" \"{input}\" \"{output}\"";

    [Fact]
    public void EmptyHook_SealedPack_ReadsBack_CPK1()
    {
        var packsDir = Path.Combine(_dir, "packs");
        var payload = new byte[] { 9, 8, 7, 6, 5 };
        string pack;
        long offset;
        using (var writer = new PackWriter(packsDir))
            (pack, offset) = writer.Add(Digest(1), payload, payload.Length, 0);
        var bytes = File.ReadAllBytes(Path.Combine(packsDir, pack));
        Assert.Equal(new byte[] { (byte)'C', (byte)'P', (byte)'K', (byte)'1' }, bytes[^4..]);
        Assert.Equal(payload, PackReader.ReadPayload(Path.Combine(packsDir, pack), offset, payload.Length, out _));
    }

    [Fact]
    public void Hook_Success_ReplacesSealedPack()
    {
        if (!OperatingSystem.IsLinux()) return;
        var script = WriteScript("cat \"$1\" > \"$2\"\nprintf HOOKED >> \"$2\"\n");
        var packsDir = Path.Combine(_dir, "packs");
        var payload = new byte[] { 1, 2, 3 };
        string pack;
        using (var writer = new PackWriter(packsDir, encryptHook: HookCmd(script)))
            (pack, _) = writer.Add(Digest(2), payload, payload.Length, 0);
        var bytes = File.ReadAllBytes(Path.Combine(packsDir, pack));
        Assert.Equal("HOOKED"u8.ToArray(), bytes[^6..]);
        Assert.Empty(Directory.GetFiles(packsDir, "*.enc.tmp"));
    }

    [Fact]
    public void Hook_RunsPerPack_NotPerChunk()
    {
        if (!OperatingSystem.IsLinux()) return;
        var count = Path.Combine(_dir, "runs.log");
        var script = WriteScript("cat \"$1\" > \"$2\"\necho x >> \"" + count + "\"\n");
        var packsDir = Path.Combine(_dir, "packs");
        using (var writer = new PackWriter(packsDir, maxPackBytes: 120, encryptHook: HookCmd(script)))
        {
            // 3 blobs of 51B: [51+51] then rotation → 2 packs.
            for (byte i = 0; i < 3; i++)
                writer.Add(Digest(i), new byte[10], 10, 0);
            writer.Complete();
        }
        Assert.Equal(2, Directory.GetFiles(packsDir, "*.pack").Length);
        Assert.Equal(2, File.ReadAllLines(count).Length);
    }

    [Fact]
    public void Hook_Substitution_ReceivesInputAndOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        var marker = Path.Combine(_dir, "args.txt");
        var script = WriteScript("echo \"$1|$2\" > \"" + marker + "\"\ncat \"$1\" > \"$2\"\n");
        var packsDir = Path.Combine(_dir, "packs");
        string pack;
        using (var writer = new PackWriter(packsDir, encryptHook: HookCmd(script)))
            (pack, _) = writer.Add(Digest(3), new byte[] { 7 }, 1, 0);
        var sealedPath = Path.Combine(packsDir, pack);
        Assert.Equal(sealedPath + "|" + sealedPath + PackHook.OutputSuffix, File.ReadAllText(marker).Trim());
    }

    [Fact]
    public void Hook_NonZeroExit_ThrowsLoud_RemovesPlaintext()
    {
        if (!OperatingSystem.IsLinux()) return;
        var script = WriteScript("exit 3\n");
        var packsDir = Path.Combine(_dir, "packs");
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            using var writer = new PackWriter(packsDir, encryptHook: HookCmd(script));
            writer.Add(Digest(4), new byte[] { 1 }, 1, 0);
            writer.Complete();
        });
        Assert.Contains("exited 3", ex.Message);
        Assert.Empty(Directory.GetFiles(packsDir, "*.pack"));
        Assert.Empty(Directory.GetFiles(packsDir, "*.tmp"));
    }

    [Fact]
    public void Hook_NoOutput_ThrowsLoud()
    {
        if (!OperatingSystem.IsLinux()) return;
        var script = WriteScript("exit 0\n");
        var packsDir = Path.Combine(_dir, "packs");
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            using var writer = new PackWriter(packsDir, encryptHook: HookCmd(script));
            writer.Add(Digest(5), new byte[] { 2 }, 2, 0);
            writer.Complete();
        });
        Assert.Contains("no output", ex.Message);
        Assert.Empty(Directory.GetFiles(packsDir, "*.pack"));
    }

    [Fact]
    public void Config_Encrypt_ParsesAndDefaultsEmpty()
    {
        var withHook = Path.Combine(_dir, "hook.yaml");
        File.WriteAllText(withHook, "store_path: /data/store\nencrypt: 'age -r KEY -o \"{output}\" \"{input}\"'\n");
        var cfg = CorpusConfig.Load(withHook);
        Assert.NotNull(cfg);
        Assert.Equal("age -r KEY -o \"{output}\" \"{input}\"", cfg!.Encrypt);
        Assert.True(CorpusConfig.Validate(cfg, new TestLogger()));

        var minimal = Path.Combine(_dir, "minimal.yaml");
        File.WriteAllText(minimal, "store_path: /data/store\n");
        Assert.Equal(string.Empty, CorpusConfig.Load(minimal)!.Encrypt);
    }
}
