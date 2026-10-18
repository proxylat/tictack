using System.Security.Cryptography;

namespace TicTack;

public class ComparerTests : IDisposable
{
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly FileAccessor _accessor = new();

    public ComparerTests()
    {
        _srcDir = Path.Combine(Path.GetTempPath(), "TicTackTest_src_" + Guid.NewGuid());
        _dstDir = Path.Combine(Path.GetTempPath(), "TicTackTest_dst_" + Guid.NewGuid());
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_dstDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_srcDir, true); } catch { }
        try { Directory.Delete(_dstDir, true); } catch { }
    }

    string Src(string name) => Path.Combine(_srcDir, name);
    string Dst(string name) => Path.Combine(_dstDir, name);

    [Fact]
    public void SizeComparer_EqualSizes()
    {
        File.WriteAllText(Src("a.txt"), "hello");
        File.WriteAllText(Dst("a.txt"), "hello");
        Assert.True(new SizeComparer().AreEqual(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public void SizeComparer_DifferentSizes()
    {
        File.WriteAllText(Src("a.txt"), "hello");
        File.WriteAllText(Dst("a.txt"), "hello world");
        Assert.False(new SizeComparer().AreEqual(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public void SizeComparer_MissingSource()
    {
        File.WriteAllText(Dst("a.txt"), "hello");
        Assert.False(new SizeComparer().AreEqual(Src("missing.txt"), Dst("a.txt")));
    }

    [Fact]
    public void DateSizeComparer_Equal()
    {
        File.WriteAllText(Src("a.txt"), "hello");
        File.Copy(Src("a.txt"), Dst("a.txt"), overwrite: true);
        Assert.True(new DateSizeComparer().AreEqual(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public void DateSizeComparer_DifferentDate()
    {
        File.WriteAllText(Src("a.txt"), "hello");
        File.WriteAllText(Dst("a.txt"), "hello");
        File.SetLastWriteTimeUtc(Dst("a.txt"), DateTime.UtcNow.AddHours(-1));
        Assert.False(new DateSizeComparer().AreEqual(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public void DateSizeComparer_UsesSuppliedSourceSnapshot()
    {
        File.WriteAllText(Src("a.txt"), "hello");
        File.Copy(Src("a.txt"), Dst("a.txt"), overwrite: true);
        Assert.True(FileSnapshot.TryRead(Src("a.txt"), out var snapshot));
        File.SetLastWriteTimeUtc(Src("a.txt"), snapshot.LastWriteTimeUtc.AddHours(1));

        Assert.True(new DateSizeComparer().AreEqual(Src("a.txt"), Dst("a.txt"), snapshot));
        Assert.False(new DateSizeComparer().AreEqual(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public void HashComparer_Equal()
    {
        var content = "hello world content for hashing";
        File.WriteAllText(Src("a.txt"), content);
        File.WriteAllText(Dst("a.txt"), content);
        Assert.True(new HashComparer(_accessor).AreEqual(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public void HashComparer_Different()
    {
        File.WriteAllText(Src("a.txt"), "content a");
        File.WriteAllText(Dst("a.txt"), "content b");
        Assert.False(new HashComparer(_accessor).AreEqual(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public void HashComparer_MissingDest()
    {
        File.WriteAllText(Src("a.txt"), "content");
        Assert.False(new HashComparer(_accessor).AreEqual(Src("a.txt"), Dst("missing.txt")));
    }

    [Fact]
    public void FullComparer_Equal()
    {
        var content = "full comparer test";
        File.WriteAllText(Src("a.txt"), content);
        File.Copy(Src("a.txt"), Dst("a.txt"), overwrite: true);
        Assert.True(new FullComparer(_accessor).AreEqual(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public void FullComparer_DifferentDate()
    {
        File.WriteAllText(Src("a.txt"), "same content");
        File.WriteAllText(Dst("a.txt"), "same content");
        File.SetLastWriteTimeUtc(Dst("a.txt"), DateTime.UtcNow.AddDays(-1));
        Assert.False(new FullComparer(_accessor).AreEqual(Src("a.txt"), Dst("a.txt")));
    }

    [Theory]
    [InlineData(VerificationLevel.Size, typeof(SizeComparer))]
    [InlineData(VerificationLevel.DateAndSize, typeof(DateSizeComparer))]
    [InlineData(VerificationLevel.Hash, typeof(HashComparer))]
    [InlineData(VerificationLevel.Full, typeof(FullComparer))]
    public void Factory_ReturnsCorrectType(VerificationLevel level, Type expectedType)
    {
        var comparer = ComparerFactory.Create(level, _accessor);
        Assert.IsType(expectedType, comparer);
    }

    [Fact]
    public void Factory_ThrowsOnUnknownLevel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ComparerFactory.Create((VerificationLevel)999));
    }

    // Fix 2 guard: the pooled incremental hash must equal a fresh
    // one-shot SHA-256 digest at every size class (empty, sub-chunk,
    // exact chunk, chunk +/- 1, multi-chunk), via both open paths.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(81919)]
    [InlineData(81920)]
    [InlineData(81921)]
    [InlineData(200000)]
    public void FileHasher_PooledHash_MatchesFreshSha256(int size)
    {
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(Src("h.bin"), bytes);
        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));

        Assert.Equal(expected, FileHasher.ComputeHex(Src("h.bin"), null));
        Assert.Equal(expected, FileHasher.ComputeHex(Src("h.bin"), _accessor));
        using (var stream = File.OpenRead(Src("h.bin")))
            Assert.Equal(expected, FileHasher.ComputeHex(stream));
    }

    // Fix 1 guard: hash-during-copy returns the content digest and
    // transfers bytes exactly.
    [Fact]
    public void FileHasher_CopyAndHash_CopiesBytesAndMatchesDigest()
    {
        var bytes = new byte[200000];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(Src("c.bin"), bytes);

        string hash;
        using (var input = File.OpenRead(Src("c.bin")))
        using (var output = File.Create(Dst("c.bin")))
            hash = FileHasher.CopyAndHash(input, output);

        Assert.Equal(bytes, File.ReadAllBytes(Dst("c.bin")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), hash);
    }
}
