using PeerSharp.Internals.Utilities;
using System.Text;

namespace PeerSharp.Tests.Core.Utilities;

public class ClientIdentificationTests
{
    [Fact]
    public void GetClientName_AzureusStyle_IdentifiesCorrectly()
    {
        // -TR2940- -> Transmission 2.9.4.0
        byte[] peerId = Encoding.ASCII.GetBytes("-TR2940-123456789012");
        string name = ClientIdentification.GetClientName(peerId);
        Assert.Equal("Transmission 2.9.4.0", name);
    }

    [Fact]
    public void GetClientName_UTorrent_IdentifiesCorrectly()
    {
        // -UT3550- -> uTorrent 3.5.5.0
        byte[] peerId = Encoding.ASCII.GetBytes("-UT3550-xxxxxxxxxxxx");
        string name = ClientIdentification.GetClientName(peerId);
        Assert.Equal("uTorrent 3.5.5.0", name);
    }

    [Fact]
    public void GetClientName_ShadowStyle_IdentifiesCorrectly()
    {
        // S588--- -> Shadow 5.8.8
        byte[] peerId = Encoding.ASCII.GetBytes("S588---xxxxxxxxxxxxx");
        string name = ClientIdentification.GetClientName(peerId);
        Assert.Equal("Shadow 5.8.8", name);
    }

    [Fact]
    public void GetClientName_Unknown_ReturnsUnknown()
    {
        byte[] peerId = new byte[20];
        string name = ClientIdentification.GetClientName(peerId);
        Assert.Equal("Unknown Client", name);
    }

    [Fact]
    public void GetClientName_ShortId_ReturnsUnknown()
    {
        byte[] peerId = new byte[10];
        string name = ClientIdentification.GetClientName(peerId);
        Assert.Equal("Unknown", name);
    }
}





