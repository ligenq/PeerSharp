using PeerSharp.Internals.Utilities;
using System.Net;

namespace PeerSharp.Tests.Core.Utilities;

public class NetworkUtilsTests
{
    [Fact]
    public void IpToUInt128_IPv4_ConvertsCorrectly()
    {
        var ip = IPAddress.Parse("192.168.1.1");
        var val = NetworkUtils.IpToUInt128(ip);

        // (192 << 24) | (168 << 16) | (1 << 8) | 1
        const uint expected = 0xC0A80101;
        Assert.Equal((UInt128)expected, val);
    }

    [Fact]
    public void IpToUInt128_IPv6_ConvertsCorrectly()
    {
        var ip = IPAddress.Parse("2001:db8::1");
        var val = NetworkUtils.IpToUInt128(ip);

        var bytes = ip.GetAddressBytes();
        // Manual verification of bytes
        Assert.Equal(0x20, bytes[0]);
        Assert.Equal(0x01, bytes[1]);
        Assert.Equal(0x0d, bytes[2]);
        Assert.Equal(0xb8, bytes[3]);
        Assert.Equal(0x01, bytes[15]);
    }

    [Fact]
    public void TryParseCidr_IPv4_CalculatesRange()
    {
        bool result = NetworkUtils.TryParseCidr("192.168.1.0/24", out var start, out var end);

        Assert.True(result);
        Assert.Equal((UInt128)0xC0A80100, start);
        Assert.Equal((UInt128)0xC0A801FF, end);
    }

    [Fact]
    public void TryParseCidr_SingleIp_ReturnsSameStartEnd()
    {
        bool result = NetworkUtils.TryParseCidr("1.2.3.4/32", out var start, out var end);
        Assert.True(result);
        Assert.Equal(start, end);
        Assert.Equal((UInt128)0x01020304, start);
    }

    [Fact]
    public void TryParseCidr_InvalidFormat_ReturnsFalse()
    {
        Assert.False(NetworkUtils.TryParseCidr("1.2.3.4", out _, out _));
        Assert.False(NetworkUtils.TryParseCidr("invalid/24", out _, out _));
        Assert.False(NetworkUtils.TryParseCidr("1.2.3.4/33", out _, out _));
    }

    [Theory]
    [InlineData("203.0.113.9", 6881, true)]
    [InlineData("192.168.1.20", 6881, true)]
    [InlineData("127.0.0.1", 6881, true)]
    [InlineData("2001:db8::1", 6881, true)]
    [InlineData("::ffff:203.0.113.9", 6881, true)]
    [InlineData("203.0.113.9", 0, false)]
    [InlineData("0.0.0.0", 6881, false)]
    [InlineData("0.1.2.3", 6881, false)]
    [InlineData("224.0.0.251", 5353, false)]
    [InlineData("253.253.253.253", 6881, false)]
    [InlineData("255.255.255.255", 6881, false)]
    [InlineData("::", 6881, false)]
    [InlineData("ff02::1", 6881, false)]
    [InlineData("::ffff:0.0.0.0", 6881, false)]
    public void IsDeliverableUnicast_RefusesWhatASocketWouldRefuse(string address, int port, bool expected)
    {
        // Peers and DHT nodes hand out all of these; the socket answers the refused ones with an exception.
        Assert.Equal(expected, NetworkUtils.IsDeliverableUnicast(new IPEndPoint(IPAddress.Parse(address), port)));
    }
}





