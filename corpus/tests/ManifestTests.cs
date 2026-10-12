namespace Corpus;

public sealed class ManifestTests : IDisposable
{
    private readonly string _dir;
    private readonly byte[] _key;

    public ManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_man_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _key = new byte[32];
        Random.Shared.NextBytes(_key);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static SnapshotManifest Sample(int gen, string name) => ManifestStore.Build(
        gen, name, 123456789L, 4, 3, "key-1", gen > 1 ? "prevhmac" : "",
        new List<ManifestEntry> { new(new string('a', 64), 4194304), new(new string('b', 64), 100) });

    [Fact]
    public void Save_Load_RoundTripsAllFields()
    {
        var m = Sample(1, "vol-c");
        var path = ManifestStore.Save(_dir, m, _key);
        Assert.Equal(Path.Combine(_dir, "000001-vol-c.json"), path);
        var loaded = ManifestStore.Load(path, _key);
        Assert.Equal(m.Generation, loaded.Generation);
        Assert.Equal(m.Name, loaded.Name);
        Assert.Equal(m.CreatedUtc, loaded.CreatedUtc);
        Assert.Equal(m.VolumeLength, loaded.VolumeLength);
        Assert.Equal(m.ChunkMib, loaded.ChunkMib);
        Assert.Equal(m.CompressionLevel, loaded.CompressionLevel);
        Assert.Equal(m.KeyId, loaded.KeyId);
        Assert.Equal(m.PrevDigest, loaded.PrevDigest);
        Assert.Equal(m.Hmac, loaded.Hmac);
        Assert.Equal(m.Chunks, loaded.Chunks);
    }

    [Fact]
    public void Load_TamperedFile_ThrowsInvalidData()
    {
        var path = ManifestStore.Save(_dir, Sample(1, "tamperme"), _key);
        var json = File.ReadAllText(path);
        var i = json.IndexOf("tamperme", StringComparison.Ordinal);
        var tampered = json[..i] + "X" + json[(i + 1)..];
        File.WriteAllText(path, tampered);
        Assert.Throws<InvalidDataException>(() => ManifestStore.Load(path, _key));
    }

    [Fact]
    public void Load_WrongKey_ThrowsInvalidData()
    {
        var path = ManifestStore.Save(_dir, Sample(1, "vol"), _key);
        var wrong = new byte[32];
        Random.Shared.NextBytes(wrong);
        Assert.Throws<InvalidDataException>(() => ManifestStore.Load(path, wrong));
    }

    [Fact]
    public void NextGeneration_EmptyThenTwoSaves()
    {
        Assert.Equal(1, ManifestStore.NextGeneration(_dir));
        ManifestStore.Save(_dir, Sample(1, "a"), _key);
        ManifestStore.Save(_dir, Sample(2, "b"), _key);
        Assert.Equal(3, ManifestStore.NextGeneration(_dir));
    }

    [Fact]
    public void List_ReturnsSortedEntries()
    {
        ManifestStore.Save(_dir, Sample(2, "second"), _key);
        ManifestStore.Save(_dir, Sample(1, "first"), _key);
        var list = ManifestStore.List(_dir);
        Assert.Equal([(1, "first"), (2, "second")], list);
    }
}
