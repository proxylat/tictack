namespace Corpus;

public sealed class MissingGenerationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public MissingGenerationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_missinggen_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Export_MissingGeneration_ThrowsWithManifestsDir()
    {
        using var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger());
        var ex = Assert.Throws<InvalidDataException>(() => store.Export(1, Path.Combine(_dir, "out.img")));
        Assert.Contains("generation 1", ex.Message);
        Assert.Contains("manifests", ex.Message);
    }

    [Fact]
    public void ExportVhdx_MissingGeneration_ThrowsWithManifestsDir()
    {
        using var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger());
        var ex = Assert.Throws<InvalidDataException>(() => store.ExportVhdx(1, Path.Combine(_dir, "out.vhdx")));
        Assert.Contains("generation 1", ex.Message);
        Assert.Contains("manifests", ex.Message);
    }
}
