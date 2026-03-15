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





