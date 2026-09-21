using System.Security.Cryptography;

namespace TicTack;

public class ValidatorTests : IDisposable
{
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly FileAccessor _accessor = new();

    public ValidatorTests()
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
    public async Task SizeValidator_ValidatesCorrectSize()
    {
        File.WriteAllText(Src("a.txt"), "hello");
        File.WriteAllText(Dst("a.txt"), "hello");
        Assert.True(await new SizeValidator().ValidateAsync(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public async Task SizeValidator_DetectsSizeMismatch()
    {
        File.WriteAllText(Src("a.txt"), "hello");
        File.WriteAllText(Dst("a.txt"), "hello world");
        Assert.False(await new SizeValidator().ValidateAsync(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public async Task SizeValidator_ReturnsFalse_OnMissingDest()
    {
        File.WriteAllText(Src("a.txt"), "hello");
        Assert.False(await new SizeValidator().ValidateAsync(Src("a.txt"), Dst("missing.txt")));
    }

    [Fact]
    public async Task HashValidator_ValidatesCorrectHash()
    {
        var content = "hash validation test content";
        File.WriteAllText(Src("a.txt"), content);
        File.WriteAllText(Dst("a.txt"), content);
        Assert.True(await new HashValidator(_accessor).ValidateAsync(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public async Task HashValidator_DetectsDifferentHash()
    {
        File.WriteAllText(Src("a.txt"), "content a");
        File.WriteAllText(Dst("a.txt"), "content b");
        Assert.False(await new HashValidator(_accessor).ValidateAsync(Src("a.txt"), Dst("a.txt")));
    }

    [Fact]
    public async Task HashValidator_ReturnsFalse_OnSizeMismatch()
    {
        File.WriteAllText(Src("a.txt"), "short");
        File.WriteAllText(Dst("a.txt"), "much longer content here");
        Assert.False(await new HashValidator(_accessor).ValidateAsync(Src("a.txt"), Dst("a.txt")));
    }

    static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task HashValidator_AcceptsMatchingSourceHash_WithoutReadingSource()
    {
        File.WriteAllText(Src("a.txt"), "copy-time hash fast path content");
        var expected = Sha256Hex(await File.ReadAllBytesAsync(Src("a.txt")));
        Assert.True(FileSnapshot.TryRead(Src("a.txt"), out var snap));
        File.WriteAllText(Dst("a.txt"), "copy-time hash fast path content");
        File.Delete(Src("a.txt")); // fast path must never open src

        Assert.True(await new HashValidator(_accessor).ValidateWithSourceHashAsync(Dst("a.txt"), expected, snap));
    }

    [Fact]
    public async Task HashValidator_RejectsWrongSourceHash()
    {
        File.WriteAllText(Src("a.txt"), "content a");
        File.WriteAllText(Dst("a.txt"), "content a");
        Assert.True(FileSnapshot.TryRead(Src("a.txt"), out var snap));

        Assert.False(await new HashValidator(_accessor).ValidateWithSourceHashAsync(Dst("a.txt"), new string('0', 64), snap));
    }

    [Fact]
    public async Task HashValidator_SourceHash_ReturnsFalse_OnMissingDest()
    {
        File.WriteAllText(Src("a.txt"), "hello");
        var expected = Sha256Hex(await File.ReadAllBytesAsync(Src("a.txt")));
        Assert.True(FileSnapshot.TryRead(Src("a.txt"), out var snap));

        Assert.False(await new HashValidator(_accessor).ValidateWithSourceHashAsync(Dst("missing.txt"), expected, snap));
    }

    [Fact]
    public async Task HashValidator_SourceHash_ReturnsFalse_OnLengthMismatch()
    {
        File.WriteAllText(Src("a.txt"), "short");
        File.WriteAllText(Dst("a.txt"), "much longer content here");
        var expected = Sha256Hex(await File.ReadAllBytesAsync(Src("a.txt")));
        Assert.True(FileSnapshot.TryRead(Src("a.txt"), out var snap));

        Assert.False(await new HashValidator(_accessor).ValidateWithSourceHashAsync(Dst("a.txt"), expected, snap));
    }

    [Theory]
    [InlineData(VerificationLevel.Size, typeof(SizeValidator))]
    [InlineData(VerificationLevel.DateAndSize, typeof(SizeValidator))]
    [InlineData(VerificationLevel.Hash, typeof(HashValidator))]
    [InlineData(VerificationLevel.Full, typeof(HashValidator))]
    public void Factory_ReturnsCorrectType(VerificationLevel level, Type expectedType)
    {
        var validator = ValidatorFactory.Create(level, _accessor);
        Assert.IsType(expectedType, validator);
    }
}
