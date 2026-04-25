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

    [Fact]
    public void Parse_Base32InfoHash_IsAccepted()
    {
        // 20 0xFF bytes encode to 32 '7's in RFC 4648 Base32 (no padding).
        string base32 = new string('7', 32);
        byte[] expected = Enumerable.Repeat((byte)0xFF, InfoHash.V1Length).ToArray();
        string magnet = $"magnet:?xt=urn:btih:{base32}";

        var link = MagnetLink.Parse(magnet);

        Assert.True(link.IsV1);
        Assert.Equal(expected, link.InfoHash.ToArray());
    }

    [Fact]
    public void Parse_ExactSourcesAndPeers()
    {
        string hashHex = new string('a', 40);
        string magnet = $"magnet:?xt=urn:btih:{hashHex}"
            + "&xs=http%3A%2F%2Fxs.example.com%2Ffile"
            + "&x.pe=10.0.0.1:6881"
            + "&x.pe=%5B2001%3Adb8%3A%3A1%5D%3A7000";

        var link = MagnetLink.Parse(magnet);

        Assert.Single(link.ExactSources);
        Assert.Equal("http://xs.example.com/file", link.ExactSources[0]);
        Assert.Equal(2, link.Peers.Count);
        Assert.Contains(link.Peers, p => p.Address.ToString() == "10.0.0.1" && p.Port == 6881);
        Assert.Contains(link.Peers, p => p.Address.ToString() == "2001:db8::1" && p.Port == 7000);
    }

    [Fact]
    public void Parse_InvalidPeerEntries_AreIgnored()
    {
        string hashHex = new string('a', 40);
        // Invalid: no port, bad port, non-numeric port, empty brackets, missing colon
        string magnet = $"magnet:?xt=urn:btih:{hashHex}"
            + "&x.pe=nohostport"
            + "&x.pe=1.2.3.4:99999"
            + "&x.pe=1.2.3.4:abc"
            + "&x.pe=1.2.3.4:"
            + "&x.pe=%5B2001%3A%3A1%5D"
            + "&x.pe=10.0.0.2:6881";

        var link = MagnetLink.Parse(magnet);

        Assert.Single(link.Peers);
        Assert.Equal("10.0.0.2", link.Peers[0].Address.ToString());
    }

    [Fact]
    public void Parse_DuplicateTrackersAndPeers_AreDeduplicated()
    {
        string hashHex = new string('a', 40);
        string magnet = $"magnet:?xt=urn:btih:{hashHex}"
            + "&tr=udp%3A%2F%2Ftracker"
            + "&tr=udp%3A%2F%2Ftracker"
            + "&x.pe=10.0.0.1:6881"
            + "&x.pe=10.0.0.1:6881";

        var link = MagnetLink.Parse(magnet);

        Assert.Single(link.Trackers);
        Assert.Single(link.Peers);
    }

    [Fact]
    public void Parse_MissingInfoHash_Fails()
    {
        string magnet = "magnet:?dn=NoHash&tr=udp://tracker";

        Assert.False(MagnetLink.TryParse(magnet, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("info hash", error);
    }

    [Fact]
    public void Parse_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => MagnetLink.Parse(null!));
    }

    [Fact]
    public void Parse_ThrowsOnInvalidFormat()
    {
        Assert.Throws<FormatException>(() => MagnetLink.Parse("not-a-magnet"));
    }

    [Fact]
    public void Equality_SameHashes_AreEqual()
    {
        string hex = new string('a', 40);
        var a = MagnetLink.Parse($"magnet:?xt=urn:btih:{hex}&dn=A");
        var b = MagnetLink.Parse($"magnet:?xt=urn:btih:{hex}&dn=B&tr=udp://different");

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentHashes_AreNotEqual()
    {
        var a = MagnetLink.Parse($"magnet:?xt=urn:btih:{new string('a', 40)}");
        var b = MagnetLink.Parse($"magnet:?xt=urn:btih:{new string('b', 40)}");

        Assert.False(a.Equals(b));
        Assert.True(a != b);
    }

    [Fact]
    public void ToString_ReturnsOriginalString()
    {
        string magnet = $"magnet:?xt=urn:btih:{new string('a', 40)}&dn=Test";
        var link = MagnetLink.Parse(magnet);

        Assert.Equal(magnet, link.ToString());
    }

    [Fact]
    public void ImplicitConversion_FromString_Works()
    {
        string magnet = $"magnet:?xt=urn:btih:{new string('a', 40)}";

        MagnetLink link = magnet;

        Assert.True(link.IsV1);
    }
}




