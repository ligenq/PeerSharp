using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

public class DhtSecurityTests
{
    [Fact]
    public void GenerateSecureNodeId_ValidatesAgainstIp()
    {
        var ip = IPAddress.Parse("123.45.67.89");
        byte[] nodeId = DhtSecurity.GenerateSecureNodeId(ip);

        Assert.Equal(20, nodeId.Length);
        Assert.True(DhtSecurity.ValidateNodeId(nodeId, ip));
    }

    [Fact]
    public void ValidateNodeId_WrongIp_ReturnsFalse()
    {
        var ip1 = IPAddress.Parse("123.45.67.89");
        var ip2 = IPAddress.Parse("123.45.67.90");

        byte[] nodeId = DhtSecurity.GenerateSecureNodeId(ip1);

        Assert.False(DhtSecurity.ValidateNodeId(nodeId, ip2));
    }

    [Fact]
    public void ShouldValidate_PrivateIps_ReturnsFalse()
    {
        Assert.False(DhtSecurity.ShouldValidate(IPAddress.Parse("127.0.0.1")));
        Assert.False(DhtSecurity.ShouldValidate(IPAddress.Parse("10.0.0.1")));
        Assert.False(DhtSecurity.ShouldValidate(IPAddress.Parse("192.168.1.1")));
        Assert.False(DhtSecurity.ShouldValidate(IPAddress.Parse("172.16.0.1")));
        Assert.False(DhtSecurity.ShouldValidate(IPAddress.Parse("169.254.1.1")));
    }

    [Fact]
    public void ShouldValidate_PublicIps_ReturnsTrue()
    {
        Assert.True(DhtSecurity.ShouldValidate(IPAddress.Parse("8.8.8.8")));
        Assert.True(DhtSecurity.ShouldValidate(IPAddress.Parse("123.45.67.89")));
    }

    // BEP 42 reference test vectors: these node IDs are declared valid for the given IPs
    // in the specification, so a conformant validator must accept them. The last byte of
    // each ID is the `r` value used to generate it.
    [Theory]
    [InlineData("124.31.75.21", "5fbfbff10c5d6a4ec8a88e4c6ab4c28b95eee401")]
    [InlineData("21.75.31.124", "5a3ce9c14e7a08645677bbd1cfe7d8f956d53256")]
    [InlineData("65.23.51.170", "a5d43220bc8f112a3d426c84764f8c2a1150e616")]
    [InlineData("84.124.73.14", "1b0321dd1bb1fe518101ceef99462b947a01ff41")]
    [InlineData("43.213.53.83", "e56f6cbf5b7c4be0237986d5243b87aa6d51305a")]
    public void ValidateNodeId_Bep42ReferenceVectors_ReturnsTrue(string ip, string nodeIdHex)
    {
        byte[] nodeId = Convert.FromHexString(nodeIdHex);

        Assert.True(DhtSecurity.ValidateNodeId(nodeId, IPAddress.Parse(ip)));
    }

    [Fact]
    public void ValidateNodeId_TamperedTopBits_ReturnsFalse()
    {
        byte[] nodeId = Convert.FromHexString("5fbfbff10c5d6a4ec8a88e4c6ab4c28b95eee401");
        nodeId[0] ^= 0xff; // corrupt the CRC-derived prefix

        Assert.False(DhtSecurity.ValidateNodeId(nodeId, IPAddress.Parse("124.31.75.21")));
    }

    [Fact]
    public void GenerateRandomNodeId_IsRandom()
    {
        byte[] id1 = DhtSecurity.GenerateRandomNodeId();
        byte[] id2 = DhtSecurity.GenerateRandomNodeId();

        Assert.Equal(20, id1.Length);
        Assert.Equal(20, id2.Length);
        Assert.NotEqual(id1, id2);
    }
}





