namespace PeerSharp.Tests.Api;

public class ProxySettingsTests
{
    [Fact]
    public void Defaults_AreExpected()
    {
        var settings = new ProxySettings();

        Assert.Equal(ProxyType.None, settings.Type);
        Assert.True(settings.ProxyPeers);
        Assert.True(settings.ProxyTrackers);
        Assert.False(settings.ForceProxy);
        Assert.Equal(string.Empty, settings.Host);
        Assert.Equal(string.Empty, settings.Username);
        Assert.Equal(string.Empty, settings.Password);
        Assert.Equal((ushort)0, settings.Port);
    }

    [Fact]
    public void Equals_And_HashCode_RespectAllFields()
    {
        var a = new ProxySettings
        {
            Type = ProxyType.Socks5,
            Host = "proxy",
            Port = 1080,
            Username = "user",
            Password = "pass",
            ProxyPeers = false,
            ProxyTrackers = true,
            ForceProxy = true
        };

        var b = new ProxySettings
        {
            Type = ProxyType.Socks5,
            Host = "proxy",
            Port = 1080,
            Username = "user",
            Password = "pass",
            ProxyPeers = false,
            ProxyTrackers = true,
            ForceProxy = true
        };

        var c = new ProxySettings
        {
            Type = ProxyType.Http,
            Host = "proxy",
            Port = 8080,
            Username = "user",
            Password = "pass",
            ProxyPeers = false,
            ProxyTrackers = true,
            ForceProxy = true
        };

        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals(c));
    }
}




