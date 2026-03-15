using System.Net;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Core.Network;

public class IpBlocklistTests
{
    [Fact]
    public void IsBlocked_ReturnsFalseWhenDisabled()
    {
        var blocklist = new IpBlocklist { Enabled = false };
        blocklist.AddRange(IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.1.1.10"));

        Assert.False(blocklist.IsBlocked(IPAddress.Parse("1.1.1.5")));
    }

    [Fact]
    public void IsBlocked_IPv4_SingleIp()
    {
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddRange(IPAddress.Parse("1.2.3.4"), IPAddress.Parse("1.2.3.4"));

        Assert.True(blocklist.IsBlocked(IPAddress.Parse("1.2.3.4")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("1.2.3.5")));
    }

    [Fact]
    public void IsBlocked_IPv4_Range()
    {
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddRange(IPAddress.Parse("192.168.1.100"), IPAddress.Parse("192.168.1.200"));

        Assert.True(blocklist.IsBlocked(IPAddress.Parse("192.168.1.100")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("192.168.1.150")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("192.168.1.200")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("192.168.1.99")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("192.168.1.201")));
    }

    [Fact]
    public void IsBlocked_IPv6_Range()
    {
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddRange(IPAddress.Parse("2001:db8::1"), IPAddress.Parse("2001:db8::10"));

        Assert.True(blocklist.IsBlocked(IPAddress.Parse("2001:db8::5")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("2001:db8::11")));
    }

    [Fact]
    public void AddCidr_Works()
    {
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddCidr("10.0.0.0/24");

        Assert.True(blocklist.IsBlocked(IPAddress.Parse("10.0.0.1")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("10.0.0.255")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("10.0.1.0")));
    }

    [Fact]
    public void LoadFromStream_SupportsMultipleFormats()
    {
        var content = string.Join("\n", new[]
        {
            "# Comment",
            "// Another comment",
            "Simple Block:1.1.1.1-1.1.1.10",
            "10.0.0.0/8",
            "192.168.1.50"
        });

        var blocklist = new IpBlocklist();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        blocklist.LoadFromStream(stream);

        Assert.True(blocklist.Enabled);
        Assert.True(blocklist.IsBlocked("1.1.1.5"));
        Assert.True(blocklist.IsBlocked("10.255.255.255"));
        Assert.True(blocklist.IsBlocked("192.168.1.50"));
        Assert.False(blocklist.IsBlocked("1.1.1.11"));
        Assert.False(blocklist.IsBlocked("11.0.0.1"));
    }

    [Fact]
    public void Clear_RemovesAllRangesAndDisables()
    {
        var blocklist = new IpBlocklist();
        blocklist.Enabled = true;
        blocklist.AddCidr("1.1.1.0/24");
        Assert.True(blocklist.IsBlocked("1.1.1.1"));

        blocklist.Clear();
        Assert.False(blocklist.Enabled);
        Assert.False(blocklist.IsBlocked("1.1.1.1"));
        Assert.Equal(0, blocklist.RangeCount);
    }
}





