namespace Corpus;

// Mount-any-snapshot: seeded NTFS volume (boot + $MFT with USA fixup, one
// resident file) imaged twice through the real Store; mounts are compared
// against SelectiveExtract output, writes must fail loud, unmount must
// remove the dir, and the first (old) generation must still mount.
public sealed class MountTests : IDisposable
{
    private const int Bps = 512;
    private const int Bpc = 4096;
    private const int RecLen = 1024;
    private const long MftLcn = 4;
    private const int MftSlots = 8;

    private static readonly DateTime Written = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public MountTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_mount_" + Guid.NewGuid().ToString("N"));
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void WriteVolume(string content)
    {
        File.WriteAllBytes(_volPath, BuildVolume(System.Text.Encoding.UTF8.GetBytes(content)));
    }

    private void Image(string name)
    {
        using var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger());
        using var source = new FileBlockSource(_volPath);
        store.Image(source, name);
    }

    private static Dictionary<string, long> StoreTree(string storeRoot)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(storeRoot, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(storeRoot, f)] = new FileInfo(f).Length;
        return map;
    }

    [Fact]
    public void Mount_ById_FileContentMatchesExtract()
    {
        WriteVolume("hello world");
        Image("seed");
        using var mount = SnapshotMount.Mount(_storeRoot, _dbPath, 1, new TestLogger());
        var extractDir = Path.Combine(_dir, "extract");
        SelectiveExtract.Extract(_storeRoot, _dbPath, 1, [""], extractDir, new TestLogger());

        Assert.StartsWith("file://", mount.AccessUrl, StringComparison.Ordinal);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(extractDir, "hello.txt")),
            File.ReadAllBytes(Path.Combine(mount.MountPath, "hello.txt")));
    }

    [Fact]
    public void Mount_WriteAttempt_FailsAndStoreUntouched()
    {
        WriteVolume("hello world");
        Image("seed");
        var before = StoreTree(_storeRoot);
        using var mount = SnapshotMount.Mount(_storeRoot, _dbPath, 1, new TestLogger());
        var target = Path.Combine(mount.MountPath, "hello.txt");

        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(target, "evil"));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(mount.MountPath, "new.txt"), "evil"));
        Assert.Equal("hello world", File.ReadAllText(target));
        Assert.Equal(before, StoreTree(_storeRoot));
    }

    [Fact]
    public void Mount_Dispose_RemovesMountDir()
    {
        WriteVolume("hello world");
        Image("seed");
        var mount = SnapshotMount.Mount(_storeRoot, _dbPath, 1, new TestLogger());

        Assert.True(Directory.Exists(mount.MountPath));
        var path = mount.MountPath;
        mount.Dispose();

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Mount_OldSnapshot_MountsOldContent()
    {
        WriteVolume("v1 bytes");
        Image("first");
        WriteVolume("v2 bytes........");
        Image("second");

        using var old = SnapshotMount.Mount(_storeRoot, _dbPath, 1, new TestLogger());
        using var cur = SnapshotMount.Mount(_storeRoot, _dbPath, 2, new TestLogger());

        Assert.Equal("v1 bytes", File.ReadAllText(Path.Combine(old.MountPath, "hello.txt")));
        Assert.Equal("v2 bytes........", File.ReadAllText(Path.Combine(cur.MountPath, "hello.txt")));
    }

    [Fact]
    public void Mount_UnknownGeneration_ThrowsAndLeavesNoDir()
    {
        WriteVolume("hello world");
        Image("seed");
        string? leaked = null;
        try
        {
            using var mount = SnapshotMount.Mount(_storeRoot, _dbPath, 99, new TestLogger());
        }
        catch (InvalidDataException)
        {
            leaked = Directory.EnumerateDirectories(Path.GetTempPath(), "corpus-mount-g99-*").FirstOrDefault();
        }

        Assert.Null(leaked);
    }

    // Minimal volume: 6 clusters (0 boot, 1-3 zero, 4-5 $MFT with 8 slots),
    // records 0 ($MFT), 5 (root), 6 (resident hello.txt). Same header/fixup
    // shape as ExtractTests (parser-verified there).
    private static byte[] BuildVolume(byte[] content)
    {
        var mft = new byte[MftSlots * RecLen];
        void Put(long rec, byte[] bytes) => bytes.CopyTo(mft, (int)rec * RecLen);
        Put(0, Record(1, 1, SiAttr(), FnAttr(5, 5, "$MFT"),
            DataNonResidentAttr(MftRecordBuilder.EncodeRuns((2, MftLcn)), MftSlots * RecLen)));
        Put(5, Record(5, 3, SiAttr(), FnAttr(5, 5, ".")));
        Put(6, Record(6, 1, SiAttr(), FnAttr(5, 5, "hello.txt"), DataResidentAttr(content)));

        var vol = new byte[6 * Bpc];
        var boot = new byte[Bps];
        "NTFS    "u8.CopyTo(boot.AsSpan(3));
        BitConverter.GetBytes((ushort)Bps).CopyTo(boot, 0x0B);
        boot[0x0D] = Bpc / Bps;
        BitConverter.GetBytes((ulong)(vol.Length / Bps)).CopyTo(boot, 0x28);
        BitConverter.GetBytes((ulong)MftLcn).CopyTo(boot, 0x30);
        boot[0x40] = unchecked((byte)(sbyte)-10);
        boot[0x1FE] = 0x55;
        boot[0x1FF] = 0xAA;
        boot.CopyTo(vol, 0);
        mft.CopyTo(vol, (int)MftLcn * Bpc);
        return vol;
    }

    private static byte[] Record(ushort seq, ushort flags, params byte[][] attrs)
    {
        var used = 48;
        foreach (var a in attrs)
            used += a.Length;
        used += 4;
        var rec = new byte[RecLen];
        rec[0] = (byte)'F'; rec[1] = (byte)'I'; rec[2] = (byte)'L'; rec[3] = (byte)'E';
        BitConverter.GetBytes((ushort)0x2A).CopyTo(rec, 0x04);
        BitConverter.GetBytes((ushort)3).CopyTo(rec, 0x06);
        BitConverter.GetBytes(seq).CopyTo(rec, 0x10);
        BitConverter.GetBytes((ushort)1).CopyTo(rec, 0x12);
        BitConverter.GetBytes((ushort)48).CopyTo(rec, 0x14);
        BitConverter.GetBytes(flags).CopyTo(rec, 0x16);
        BitConverter.GetBytes((uint)used).CopyTo(rec, 0x18);
        BitConverter.GetBytes((uint)RecLen).CopyTo(rec, 0x1C);
        var off = 48;
        foreach (var a in attrs)
        {
            a.CopyTo(rec, off);
            off += a.Length;
        }
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(rec, off);
        var usn = (ushort)(0xA000 + seq);
        BitConverter.GetBytes(usn).CopyTo(rec, 0x2A);
        for (var s = 0; s < 2; s++)
        {
            var trailer = (s + 1) * Bps - 2;
            rec[0x2A + 2 + s * 2] = rec[trailer];
            rec[0x2A + 3 + s * 2] = rec[trailer + 1];
            rec[trailer] = (byte)(usn & 0xFF);
            rec[trailer + 1] = (byte)(usn >> 8);
        }
        return rec;
    }

    private static byte[] SiAttr()
    {
        var val = new byte[72];
        BitConverter.GetBytes(Written.ToFileTimeUtc()).CopyTo(val, 8);
        BitConverter.GetBytes(Written.ToFileTimeUtc()).CopyTo(val, 16);
        return ResidentAttr(0x10, val);
    }

    private static byte[] FnAttr(ulong parent, ushort parentSeq, string name)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        var val = new byte[66 + nameBytes.Length];
        BitConverter.GetBytes(parent | ((ulong)parentSeq << 48)).CopyTo(val, 0);
        BitConverter.GetBytes(Written.ToFileTimeUtc()).CopyTo(val, 16);
        val[64] = (byte)name.Length;
        val[65] = 3;
        nameBytes.CopyTo(val, 66);
        return ResidentAttr(0x30, val);
    }

    private static byte[] DataResidentAttr(byte[] content) => ResidentAttr(0x80, content);

    private static byte[] DataNonResidentAttr(byte[] runs, long realSize)
    {
        var runOff = 64;
        var attr = new byte[runOff + runs.Length];
        BitConverter.GetBytes(0x80u).CopyTo(attr, 0);
        attr[8] = 1;
        BitConverter.GetBytes((ushort)64).CopyTo(attr, 10);
        BitConverter.GetBytes((ushort)runOff).CopyTo(attr, 32);
        BitConverter.GetBytes((ulong)((realSize + Bpc - 1) / Bpc * Bpc)).CopyTo(attr, 40);
        BitConverter.GetBytes((ulong)realSize).CopyTo(attr, 48);
        BitConverter.GetBytes((ulong)realSize).CopyTo(attr, 56);
        runs.CopyTo(attr, runOff);
        return Seal(attr);
    }

    private static byte[] ResidentAttr(uint type, byte[] value)
    {
        var attr = new byte[24 + value.Length];
        BitConverter.GetBytes(type).CopyTo(attr, 0);
        attr[8] = 0;
        BitConverter.GetBytes((ushort)24).CopyTo(attr, 10);
        BitConverter.GetBytes((uint)value.Length).CopyTo(attr, 16);
        BitConverter.GetBytes((ushort)24).CopyTo(attr, 20);
        value.CopyTo(attr, 24);
        return Seal(attr);
    }

    private static byte[] Seal(byte[] attr)
    {
        var pad = (8 - attr.Length % 8) % 8;
        var sealed_ = new byte[attr.Length + pad];
        Buffer.BlockCopy(attr, 0, sealed_, 0, attr.Length);
        BitConverter.GetBytes((uint)sealed_.Length).CopyTo(sealed_, 4);
        return sealed_;
    }
}
