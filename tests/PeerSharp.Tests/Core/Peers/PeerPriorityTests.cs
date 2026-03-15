using PeerSharp.Internals.Peers;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

public class PeerPriorityTests
{
    [Fact]
    public void Calculate_SameInput_ReturnsSameResult()
    {
        // Arrange
        var ourIp = IPAddress.Parse("1.2.3.4");
        var peerIp = IPAddress.Parse("5.6.7.8");
        var infoHash = new byte[20];
        infoHash[0] = 0xAA;

        // Act
        uint p1 = PeerPriority.Calculate(ourIp, peerIp, infoHash);
        uint p2 = PeerPriority.Calculate(ourIp, peerIp, infoHash);

        // Assert
        Assert.Equal(p1, p2);
    }

    [Fact]
    public void Calculate_IPv4Masking_IgnoresLastTwoBytes()
    {
        // Arrange
        var ourIp = IPAddress.Parse("1.2.3.4");
        var peerIp1 = IPAddress.Parse("5.6.7.8");
        var peerIp2 = IPAddress.Parse("5.6.10.20"); // Same /16 prefix
        var infoHash = new byte[20];

        // Act
        uint p1 = PeerPriority.Calculate(ourIp, peerIp1, infoHash);
        uint p2 = PeerPriority.Calculate(ourIp, peerIp2, infoHash);

        // Assert
        Assert.Equal(p1, p2);
    }

    [Fact]
    public void Calculate_IPv4DifferentPrefix_ReturnsDifferentResult()
    {
        // Arrange
        var ourIp = IPAddress.Parse("1.2.3.4");
        var peerIp1 = IPAddress.Parse("5.6.7.8");
        var peerIp2 = IPAddress.Parse("5.7.7.8"); // Different /16 prefix
        var infoHash = new byte[20];

        // Act
        uint p1 = PeerPriority.Calculate(ourIp, peerIp1, infoHash);
        uint p2 = PeerPriority.Calculate(ourIp, peerIp2, infoHash);

        // Assert
        Assert.NotEqual(p1, p2);
    }

    [Fact]
    public void Calculate_IPv6Masking_IgnoresBeyond48Bits()
    {
        // Arrange
        var ourIp = IPAddress.Parse("2001:db8:1::1");
        var peerIp1 = IPAddress.Parse("2001:db8:2::1");
        var peerIp2 = IPAddress.Parse("2001:db8:2::2"); // Same /48 prefix
        var infoHash = new byte[20];

        // Act
        uint p1 = PeerPriority.Calculate(ourIp, peerIp1, infoHash);
        uint p2 = PeerPriority.Calculate(ourIp, peerIp2, infoHash);

        // Assert
        Assert.Equal(p1, p2);
    }

    [Fact]
    public void Calculate_DifferentInfoHash_ReturnsDifferentResult()
    {
        // Arrange
        var ourIp = IPAddress.Parse("1.2.3.4");
        var peerIp = IPAddress.Parse("5.6.7.8");
        var infoHash1 = new byte[20];
        var infoHash2 = new byte[20];
        infoHash2[0] = 1;

        // Act
        uint p1 = PeerPriority.Calculate(ourIp, peerIp, infoHash1);
        uint p2 = PeerPriority.Calculate(ourIp, peerIp, infoHash2);

        // Assert
        Assert.NotEqual(p1, p2);
    }
}





