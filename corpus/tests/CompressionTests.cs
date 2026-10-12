namespace Corpus;

public sealed class CompressionTests
{
    [Fact]
    public void Encode_Zeros_UsesZstdAndRoundTrips()
    {
        var zeros = new byte[64 * 1024];
        var (payload, codec) = Compression.Encode(zeros, 3);
        Assert.Equal(Compression.CodecZstd, codec);
        Assert.Equal(zeros, Compression.Decode(payload, codec));
    }

    [Fact]
    public void Encode_Text_RoundTrips()
    {
        var text = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(System.Linq.Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 1000)));
        var (payload, codec) = Compression.Encode(text, 3);
        Assert.Equal(Compression.CodecZstd, codec);
        Assert.Equal(text, Compression.Decode(payload, codec));
    }

    [Fact]
    public void Encode_RandomBytes_RoundTrips()
    {
        var random = new byte[4096];
        Random.Shared.NextBytes(random);
        var (payload, codec) = Compression.Encode(random, 3);
        Assert.Equal(random, Compression.Decode(payload, codec));
    }

    [Fact]
    public void Encode_4MiBRandom_StoresRaw()
    {
        var random = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(random);
        var (payload, codec) = Compression.Encode(random, 3);
        Assert.Equal(Compression.CodecRaw, codec);
        Assert.Same(random, payload);
    }

    [Fact]
    public void Decode_UnknownCodec_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => Compression.Decode(new byte[] { 1 }, 99));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    public void Encode_Levels1And22_RoundTrip(int level)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(System.Linq.Enumerable.Repeat("level check 0123456789 ", 2000)));
        var (payload, codec) = Compression.Encode(text, level);
        Assert.Equal(text, Compression.Decode(payload, codec));
    }
}
