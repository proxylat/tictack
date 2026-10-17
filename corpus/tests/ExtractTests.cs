namespace Corpus;

// Seeded round trip: a hand-built NTFS volume (boot sector + $MFT with USA
// fixup, resident + fragmented + sparse files, ADS, EFS, reparse, attrlist,
// compressed, junction, EFS dir) is imaged through the real Store, then
// extracted via SelectiveExtract and compared byte-for-byte.
public sealed class ExtractTests : IDisposable
{
    private const int Bps = 512;
    private const int Bpc = 4096;
    private const int RecLen = 1024;
    private const long MftLcn = 4;
    private const int MftSlots = 20;
    private const int BigSize = 8292; // 2 clusters + 100 bytes, middle cluster sparse

    private static readonly DateTime Created = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Written = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private static readonly DateTime Accessed = new(2024, 9, 10, 11, 12, 13, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;
    private readonly string _outDir;
    private readonly byte[] _bigContent;
    private readonly byte[] _volBytes;

    public ExtractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_extract_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        _outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_dir);
        (_volBytes, _bigContent) = BuildVolume();
        File.WriteAllBytes(_volPath, _volBytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Store NewStore(TestLogger? log = null) =>
        new(_storeRoot, _dbPath, 1, 3, log ?? new TestLogger());

    private void ImageSeed()
    {
        using var store = NewStore();
        using var source = new FileBlockSource(_volPath);
        store.Image(source, "seed");
    }

    private TestLogger Extract(params string[] paths) =>
        ExtractTo(_outDir, paths);

    private TestLogger ExtractTo(string outDir, params string[] paths)
    {
        var log = new TestLogger();
        SelectiveExtract.Extract(_storeRoot, _dbPath, 1, paths, outDir, log);
        return log;
    }

    [Fact]
    public void Extract_SingleResidentFile_ByteIdenticalAndTimestamps()
    {
        ImageSeed();
        var log = Extract("hello.txt");

        var path = Path.Combine(_outDir, "hello.txt");
        Assert.Equal(Written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(Accessed, File.GetLastAccessTimeUtc(path)); // before the content read: reading bumps atime
        Assert.Equal("hello world"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Contains(log.Messages, m => m.StartsWith("INF:extract: snapshot 1:"));
    }

    [Fact]
    public void Extract_Directory_RecursiveWithSparseRun()
    {
        ImageSeed();
        Extract("docs");

        Assert.Equal(_bigContent, File.ReadAllBytes(Path.Combine(_outDir, "docs", "big.bin")));
        Assert.Equal(0, new FileInfo(Path.Combine(_outDir, "docs", "empty.bin")).Length);
        Assert.Equal(Written, Directory.GetLastWriteTimeUtc(Path.Combine(_outDir, "docs")));
    }

    [Fact]
    public void Extract_Root_RestoresAllExceptSpecial()
    {
        ImageSeed();
        Extract("");

        Assert.True(File.Exists(Path.Combine(_outDir, "hello.txt")));
        Assert.True(File.Exists(Path.Combine(_outDir, "docs", "big.bin")));
        Assert.True(File.Exists(Path.Combine(_outDir, "doc.txt"))); // main stream restores; ADS warns
        Assert.True(Directory.Exists(Path.Combine(_outDir, "encdir"))); // EFS dir restores; policy warns
        Assert.False(File.Exists(Path.Combine(_outDir, "secret.txt"))); // EFS skipped
        Assert.False(File.Exists(Path.Combine(_outDir, "link.lnk"))); // reparse skipped
        Assert.False(File.Exists(Path.Combine(_outDir, "huge.dat"))); // attrlist skipped
        Assert.False(File.Exists(Path.Combine(_outDir, "comp.bin"))); // compressed skipped
        Assert.False(Directory.Exists(Path.Combine(_outDir, "junct"))); // junction skipped, not descended
    }

    [Fact]
    public void Extract_SpecialFiles_EmitWrnNamingPath()
    {
        ImageSeed();
        var log = Extract("");

        Assert.Contains(log.Messages, m => m.Contains("doc.txt:Zone.Identifier") && m.StartsWith("WRN:"));
        Assert.Contains(log.Messages, m => m.Contains("secret.txt") && m.Contains("EFS") && m.StartsWith("WRN:"));
        Assert.Contains(log.Messages, m => m.Contains("link.lnk") && m.Contains("reparse") && m.StartsWith("WRN:"));
        Assert.Contains(log.Messages, m => m.Contains("huge.dat") && m.Contains("attribute list") && m.StartsWith("WRN:"));
        Assert.Contains(log.Messages, m => m.Contains("comp.bin") && m.Contains("compressed") && m.StartsWith("WRN:"));
        Assert.Contains(log.Messages, m => m.Contains("junct") && m.Contains("reparse") && m.StartsWith("WRN:"));
        Assert.Contains(log.Messages, m => m.Contains("encdir") && m.Contains("EFS") && m.StartsWith("WRN:"));
        Assert.Contains(log.Messages, m => m.Contains("ACLs not restored") && m.StartsWith("WRN:"));
    }

    [Fact]
    public void Extract_Paths_CaseAndSeparatorInsensitive()
    {
        ImageSeed();
        var log = Extract("DOCS/BIG.BIN");

        Assert.Equal(_bigContent, File.ReadAllBytes(Path.Combine(_outDir, "docs", "big.bin")));
        Assert.DoesNotContain(log.Messages, m => m.StartsWith("ERR:"));
    }

    [Fact]
    public void Extract_OverlappingPaths_ExtractsOnce()
    {
        ImageSeed();
        var log = new TestLogger();
        var summary = SelectiveExtract.Extract(_storeRoot, _dbPath, 1, new[] { "docs", "docs\\big.bin" }, _outDir, log);

        Assert.Equal(2, summary.Files);
        Assert.Equal(1, summary.Directories);
        Assert.Equal(_bigContent, File.ReadAllBytes(Path.Combine(_outDir, "docs", "big.bin")));
    }

    [Fact]
    public void Extract_MissingPath_ThrowsNamingPath()
    {
        ImageSeed();
        var ex = Assert.Throws<ExtractPathNotFoundException>(() =>
            SelectiveExtract.Extract(_storeRoot, _dbPath, 1, new[] { "nope\\missing.txt" }, _outDir, new TestLogger()));
        Assert.Contains("nope\\missing.txt", ex.Message);
        Assert.Equal("nope\\missing.txt", ex.RequestedPath);
    }

    [Fact]
    public void Extract_DotDotPath_Refuses()
    {
        ImageSeed();
        var ex = Assert.Throws<ExtractRefusedException>(() =>
            SelectiveExtract.Extract(_storeRoot, _dbPath, 1, new[] { "docs\\..\\hello.txt" }, _outDir, new TestLogger()));
        Assert.Contains("..", ex.Message);
    }

    [Fact]
    public void Extract_EmptyPaths_Refuses()
    {
        var ex = Assert.Throws<ExtractRefusedException>(() =>
            SelectiveExtract.Extract(_storeRoot, _dbPath, 1, Array.Empty<string>(), _outDir, new TestLogger()));
        Assert.Contains("--paths", ex.Message);
    }

    [Fact]
    public void Extract_BadGeneration_Refuses()
    {
        var ex = Assert.Throws<ExtractRefusedException>(() =>
            SelectiveExtract.Extract(_storeRoot, _dbPath, 0, new[] { "hello.txt" }, _outDir, new TestLogger()));
        Assert.Contains("--snapshot", ex.Message);
    }

    [Fact]
    public void Extract_OutIsFile_Refuses()
    {
        File.WriteAllText(_outDir, "x");
        var ex = Assert.Throws<ExtractRefusedException>(() =>
            SelectiveExtract.Extract(_storeRoot, _dbPath, 1, new[] { "hello.txt" }, _outDir, new TestLogger()));
        Assert.Contains("--out", ex.Message);
    }

    [Fact]
    public void Extract_MissingGeneration_ThrowsLikeExport()
    {
        ImageSeed();
        Assert.Throws<InvalidDataException>(() =>
            SelectiveExtract.Extract(_storeRoot, _dbPath, 99, new[] { "hello.txt" }, _outDir, new TestLogger()));
    }

    [Fact]
    public void Extract_NonNtfsVolume_Corrupt()
    {
        File.WriteAllBytes(_volPath, new byte[4096]);
        ImageSeed();
        var ex = Assert.Throws<ExtractCorruptException>(() =>
            SelectiveExtract.Extract(_storeRoot, _dbPath, 1, new[] { "hello.txt" }, _outDir, new TestLogger()));
        Assert.Contains("snapshot 1", ex.Message);
        Assert.Equal(1, ex.Generation);
    }

    [Fact]
    public void Extract_LandingReadThrough_BeforeOptimize()
    {
        using (var store = NewStore())
        {
            using var source = new FileBlockSource(_volPath);
            store.ImageFast(source, "fast");
        }
        Extract("hello.txt");
        Assert.Equal("hello world"u8.ToArray(), File.ReadAllBytes(Path.Combine(_outDir, "hello.txt")));
    }

    // Volume layout (12 clusters): 0 boot, 1-3 zero, 4-8 $MFT (20 slots),
    // 9 big.bin run 1, 10 big.bin run 2 (100 bytes used), 11 compressed file.
    private (byte[] VolBytes, byte[] FileBytes) BuildVolume()
    {
        var big = new byte[BigSize];
        new Random(1234).NextBytes(big);
        Array.Clear(big, Bpc, Bpc); // VCN 1 is the sparse run: reads back as zeros
        var compCluster = new byte[Bpc];
        new Random(99).NextBytes(compCluster);

        var mft = new byte[MftSlots * RecLen];
        void Put(long rec, byte[] bytes) => bytes.CopyTo(mft, (int)rec * RecLen);
        Put(0, Record(1, 1, SiAttr(), FnAttr(5, 5, "$MFT"),
            DataNonResidentAttr(MftRecordBuilder.EncodeRuns((5, MftLcn)), MftSlots * RecLen)));
        Put(5, Record(5, 3, SiAttr(), FnAttr(5, 5, ".")));
        Put(6, Record(6, 3, SiAttr(), FnAttr(5, 5, "docs")));
        Put(7, Record(7, 1, SiAttr(), FnAttr(5, 5, "hello.txt"), DataResidentAttr("hello world"u8.ToArray())));
        Put(8, Record(8, 1, SiAttr(), FnAttr(6, 6, "big.bin"),
            DataNonResidentAttr(MftRecordBuilder.EncodeRuns((1, 9), (1, null), (1, 1)), BigSize)));
        Put(9, Record(9, 1, SiAttr(), FnAttr(5, 5, "doc.txt"),
            DataResidentAttr("main"u8.ToArray()), DataResidentAttr("ads"u8.ToArray(), "Zone.Identifier")));
        Put(10, Record(10, 1, SiAttr(0x4000), FnAttr(5, 5, "secret.txt"),
            DataResidentAttr("x"u8.ToArray()), ResidentAttr(0x100, Array.Empty<byte>())));
        Put(11, Record(11, 1, SiAttr(), FnAttr(5, 5, "link.lnk"),
            DataResidentAttr("x"u8.ToArray()), ResidentAttr(0xC0, Array.Empty<byte>())));
        Put(12, Record(12, 1, SiAttr(), FnAttr(5, 5, "huge.dat"),
            DataResidentAttr("x"u8.ToArray()), ResidentAttr(0x20, Array.Empty<byte>())));
        Put(13, Record(13, 1, SiAttr(), FnAttr(5, 5, "comp.bin"),
            DataNonResidentAttr(MftRecordBuilder.EncodeRuns((1, 11)), Bpc, compUnit: 4)));
        Put(14, Record(14, 3, SiAttr(), FnAttr(5, 5, "junct"), ResidentAttr(0xC0, Array.Empty<byte>())));
        Put(15, Record(15, 1, SiAttr(), FnAttr(6, 6, "empty.bin"), DataResidentAttr(Array.Empty<byte>())));
        Put(16, Record(16, 3, SiAttr(0x4000), FnAttr(5, 5, "encdir")));
        // Slots 1-4, 17-19 stay zeroed (dead slots filter by magic/in-use).

        var vol = new byte[12 * Bpc];
        var boot = new byte[Bps];
        "NTFS    "u8.CopyTo(boot.AsSpan(3));
        BitConverter.GetBytes((ushort)Bps).CopyTo(boot, 0x0B);
        boot[0x0D] = Bpc / Bps;
        BitConverter.GetBytes((ulong)(vol.Length / Bps)).CopyTo(boot, 0x28);
        BitConverter.GetBytes((ulong)MftLcn).CopyTo(boot, 0x30);
        boot[0x40] = unchecked((byte)(sbyte)-10); // 2^10 = 1024-byte records
        boot[0x1FE] = 0x55;
        boot[0x1FF] = 0xAA;
        boot.CopyTo(vol, 0);
        mft.CopyTo(vol, (int)MftLcn * Bpc);
        big.AsSpan(0, Bpc).CopyTo(vol.AsSpan(9 * Bpc));
        big.AsSpan(2 * Bpc, BigSize - 2 * Bpc).CopyTo(vol.AsSpan(10 * Bpc));
        compCluster.CopyTo(vol, 11 * Bpc);
        return (vol, big);
    }

    // FILE record with forward-applied update-sequence fixup (USN in the
    // sector trailers, originals in the USA): the parser must restore them.
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

    private static byte[] SiAttr(uint flags = 0)
    {
        var val = new byte[72];
        BitConverter.GetBytes(Created.ToFileTimeUtc()).CopyTo(val, 0);
        BitConverter.GetBytes(Written.ToFileTimeUtc()).CopyTo(val, 8);
        BitConverter.GetBytes(Written.ToFileTimeUtc()).CopyTo(val, 16);
        BitConverter.GetBytes(Accessed.ToFileTimeUtc()).CopyTo(val, 24);
        BitConverter.GetBytes(flags).CopyTo(val, 32);
        return ResidentAttr(0x10, val);
    }

    private static byte[] FnAttr(ulong parent, ushort parentSeq, string name, int ns = 3)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        var val = new byte[66 + nameBytes.Length];
        BitConverter.GetBytes(parent | ((ulong)parentSeq << 48)).CopyTo(val, 0);
        BitConverter.GetBytes(Created.ToFileTimeUtc()).CopyTo(val, 8);
        BitConverter.GetBytes(Written.ToFileTimeUtc()).CopyTo(val, 16);
        BitConverter.GetBytes(Written.ToFileTimeUtc()).CopyTo(val, 24);
        BitConverter.GetBytes(Accessed.ToFileTimeUtc()).CopyTo(val, 32);
        val[64] = (byte)name.Length;
        val[65] = (byte)ns;
        nameBytes.CopyTo(val, 66);
        return ResidentAttr(0x30, val);
    }

    private static byte[] DataResidentAttr(byte[] content, string? name = null) =>
        ResidentAttr(0x80, content, name);

    private static byte[] DataNonResidentAttr(byte[] runs, long realSize, ushort compUnit = 0, string? name = null)
    {
        var nameBytes = name == null ? Array.Empty<byte>() : System.Text.Encoding.Unicode.GetBytes(name);
        var runOff = 64 + nameBytes.Length;
        var attr = new byte[runOff + runs.Length];
        BitConverter.GetBytes(0x80u).CopyTo(attr, 0);
        attr[8] = 1;
        attr[9] = (byte)(name == null ? 0 : name.Length);
        BitConverter.GetBytes((ushort)64).CopyTo(attr, 10);
        BitConverter.GetBytes((ushort)runOff).CopyTo(attr, 32);
        BitConverter.GetBytes(compUnit).CopyTo(attr, 34);
        BitConverter.GetBytes((ulong)((realSize + Bpc - 1) / Bpc * Bpc)).CopyTo(attr, 40);
        BitConverter.GetBytes((ulong)realSize).CopyTo(attr, 48);
        BitConverter.GetBytes((ulong)realSize).CopyTo(attr, 56);
        nameBytes.CopyTo(attr, 64);
        runs.CopyTo(attr, runOff);
        return Seal(attr);
    }

    private static byte[] ResidentAttr(uint type, byte[] value, string? name = null)
    {
        var nameBytes = name == null ? Array.Empty<byte>() : System.Text.Encoding.Unicode.GetBytes(name);
        var valOff = 24 + nameBytes.Length;
        var attr = new byte[valOff + value.Length];
        BitConverter.GetBytes(type).CopyTo(attr, 0);
        attr[8] = 0;
        attr[9] = (byte)(name == null ? 0 : name.Length);
        BitConverter.GetBytes((ushort)24).CopyTo(attr, 10);
        BitConverter.GetBytes((uint)value.Length).CopyTo(attr, 16);
        BitConverter.GetBytes((ushort)valOff).CopyTo(attr, 20);
        nameBytes.CopyTo(attr, 24);
        value.CopyTo(attr, valOff);
        return Seal(attr);
    }

    // NTFS aligns attributes to 8 bytes; the length field includes padding.
    private static byte[] Seal(byte[] attr)
    {
        var pad = (8 - attr.Length % 8) % 8;
        var sealed_ = new byte[attr.Length + pad];
        Buffer.BlockCopy(attr, 0, sealed_, 0, attr.Length);
        BitConverter.GetBytes((uint)sealed_.Length).CopyTo(sealed_, 4);
        return sealed_;
    }
}
