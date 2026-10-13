using System.Security.Cryptography;

namespace Corpus;

public sealed class CryptoTests : IDisposable
{
    private readonly string _dir;

    public CryptoTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_crypto_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Crypto_RoundTrip()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var cipher = ChunkCrypto.Encrypt(plain, key, nonce);
        Assert.Equal(plain.Length + ChunkCrypto.TagBytes, cipher.Length);
        Assert.Equal(plain, ChunkCrypto.Decrypt(cipher, key, nonce));

        var wrongKey = RandomNumberGenerator.GetBytes(32);
        Assert.ThrowsAny<CryptographicException>(() => ChunkCrypto.Decrypt(cipher, wrongKey, nonce));

        var tampered = (byte[])cipher.Clone();
        tampered[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => ChunkCrypto.Decrypt(tampered, key, nonce));
    }

    [Fact]
    public void Crypto_KeyIdShape()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var id = ChunkCrypto.KeyId(key);
        Assert.Equal(8, id.Length);
        Assert.Matches("^[0-9a-f]{8}$", id);
        Assert.Equal(id, ChunkCrypto.KeyId(key));
        Assert.NotEqual(id, ChunkCrypto.KeyId(RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void KeyStore_GenerateAndResolve()
    {
        var (keyId, path) = KeyStore.Generate(_dir);
        Assert.True(File.Exists(path));
        Assert.Equal(32, new FileInfo(path).Length);
        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        var keys = new KeyStore(_dir);
        Assert.True(keys.HasCurrent);
        Assert.Equal(keyId, keys.CurrentKeyId);
        Assert.Equal(File.ReadAllBytes(path), keys.CurrentKey);
        Assert.True(keys.TryGet(keyId, out var resolved));
        Assert.Equal(File.ReadAllBytes(path), resolved);
        Assert.False(keys.TryGet("deadbeef", out _));
    }

    [Fact]
    public void KeyStore_RotateRetainsOld()
    {
        var (first, firstPath) = KeyStore.Generate(_dir);
        var (second, _) = KeyStore.Generate(_dir);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(firstPath));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_dir, "keys"), "*.key").Length);

        var keys = new KeyStore(_dir);
        Assert.Equal(second, keys.CurrentKeyId);
        Assert.True(keys.TryGet(first, out _));
        Assert.True(keys.TryGet(second, out _));
    }

    [Fact]
    public void KeyStore_CorruptKeyFile_Throws()
    {
        var keysDir = Path.Combine(_dir, "keys");
        Directory.CreateDirectory(keysDir);
        File.WriteAllBytes(Path.Combine(keysDir, "abc12345.key"), new byte[31]);
        var ex = Assert.Throws<InvalidDataException>(() => new KeyStore(_dir));
        Assert.Contains("abc12345.key", ex.Message);
    }
}
