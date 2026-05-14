namespace PeerSharp.Tests.Api;

public class InfoHashTests
{
    [Fact]
    public void FromHex_RoundTrip_V1()
    {
        byte[] bytes = Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)i).ToArray();
        string hex = Convert.ToHexString(bytes);

        var hash = InfoHash.FromHex(hex);

        Assert.True(hash.IsV1);
        Assert.Equal(bytes, hash.ToArray());
    }

    [Fact]
    public void FromHex_RoundTrip_V2()
    {
        byte[] bytes = Enumerable.Range(0, InfoHash.V2Length).Select(i => (byte)(255 - i)).ToArray();
        string hex = Convert.ToHexString(bytes);

        var hash = InfoHash.FromHex(hex);

        Assert.True(hash.IsV2);
        Assert.Equal(bytes, hash.ToArray());
    }

    [Fact]
    public void TryFromHex_Invalid_ReturnsFalse()
    {
        Assert.False(InfoHash.TryFromHex(null, out _));
        Assert.False(InfoHash.TryFromHex(string.Empty, out _));
        Assert.False(InfoHash.TryFromHex("xyz", out _));
        Assert.False(InfoHash.TryFromHex(new string('a', 10), out _));
    }

    [Fact]
    public void TruncateToV1_ReturnsFirst20Bytes()
    {
        var bytes = Enumerable.Range(0, InfoHash.V2Length).Select(i => (byte)i).ToArray();
        var hash = new InfoHash(bytes);

        var truncated = hash.TruncateToV1();

        Assert.True(truncated.IsV1);
        Assert.Equal(bytes.Take(InfoHash.V1Length).ToArray(), truncated.ToArray());
    }

    [Fact]
    public void TruncateToV1_OnV1_Throws()
    {
        var hash = InfoHash.CreateRandom();

        Assert.Throws<InvalidOperationException>(() => hash.TruncateToV1());
    }

    [Fact]
    public void CopyTo_SmallDestination_Throws()
    {
        var hash = InfoHash.CreateRandom();
        var destination = new byte[hash.Length - 1];

        Assert.Throws<ArgumentException>(() => hash.CopyTo(destination));
    }
}




