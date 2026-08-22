namespace TicTack;

public class ValidatorTests : IDisposable
{
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly MockFileAccessor _accessor = new();

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
