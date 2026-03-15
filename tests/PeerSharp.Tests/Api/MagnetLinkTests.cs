namespace PeerSharp.Tests;

public class MagnetLinkTests
{
    [Fact]
    public void Parse_V1Hex_WithTrackersAndName()
    {
        byte[] hashBytes = Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)i).ToArray();
        string hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        string magnet = $"magnet:?xt=urn:btih:{hashHex}&dn=TestName&tr=http%3A%2F%2Ftracker1&tr=udp%3A%2F%2Ftracker2";

        var link = MagnetLink.Parse(magnet);

        Assert.True(link.IsV1);
        Assert.False(link.IsV2);
        Assert.Equal("TestName", link.DisplayName);
        Assert.Equal(hashBytes, link.InfoHash.ToArray());
        Assert.Equal(2, link.Trackers.Count);
        Assert.Contains("http://tracker1", link.Trackers);
        Assert.Contains("udp://tracker2", link.Trackers);
    }

    [Fact]
    public void Parse_V2Multihash()
    {
        byte[] hashBytes = Enumerable.Range(0, InfoHash.V2Length).Select(i => (byte)(255 - i)).ToArray();
        string hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        string magnet = $"magnet:?xt=urn:btmh:1220{hashHex}";

        var link = MagnetLink.Parse(magnet);

        Assert.False(link.IsV1);
        Assert.True(link.IsV2);
        Assert.Equal(hashBytes, link.InfoHashV2.ToArray());
    }

    [Fact]
    public void Parse_Hybrid()
    {
        string v1 = new string('a', 40);
        string v2 = new string('b', 64);
        string magnet = $"magnet:?xt=urn:btih:{v1}&xt=urn:btmh:1220{v2}";

        var link = MagnetLink.Parse(magnet);

        Assert.True(link.IsHybrid);
        Assert.True(link.IsV1);
        Assert.True(link.IsV2);
    }

    [Fact]
    public void TryParse_Invalid_ReturnsFalse()
    {
        Assert.False(MagnetLink.TryParse(null, out _));
        Assert.False(MagnetLink.TryParse(string.Empty, out _));
        Assert.False(MagnetLink.TryParse("http://example", out _));
        Assert.False(MagnetLink.TryParse("magnet:?", out _, out _));
    }
}




