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
    public void GetClientName_NullId_ReturnsUnknown()
    {
        string name = ClientIdentification.GetClientName(null!);
        Assert.Equal("Unknown", name);
    }

    [Fact]
    public void GetClientName_ShortId_ReturnsUnknown()
    {
        byte[] peerId = new byte[10];
        string name = ClientIdentification.GetClientName(peerId);
        Assert.Equal("Unknown", name);
    }

    [Theory]
    [InlineData("AZ", "Vuze")]
    [InlineData("qB", "qBittorrent")]
    [InlineData("LT", "libtorrent")]
    [InlineData("lt", "libTorrent")]
    [InlineData("PS", "PeerSharp")]
    [InlineData("ZZ", "Unknown (ZZ)")]
    public void GetClientName_AzureusStyle_MapsKnownAndUnknownCodes(string code, string expectedClient)
    {
        byte[] peerId = Encoding.ASCII.GetBytes($"-{code}1234-abcdefghijkl");

        string name = ClientIdentification.GetClientName(peerId);

        Assert.Equal($"{expectedClient} 1.2.3.4", name);
    }

    [Theory]
    [MemberData(nameof(AzureusStyleClientCodes))]
    public void GetClientName_AzureusStyle_MapsAllKnownCodes(string code, string expectedClient)
    {
        byte[] peerId = Encoding.ASCII.GetBytes($"-{code}4321-abcdefghijkl");

        string name = ClientIdentification.GetClientName(peerId);

        Assert.Equal($"{expectedClient} 4.3.2.1", name);
    }

    [Theory]
    [InlineData('A', "ABC")]
    [InlineData('O', "Osprey")]
    [InlineData('Q', "BTQueue")]
    [InlineData('R', "Tribler")]
    [InlineData('T', "BitTornado")]
    [InlineData('U', "UPnP BitTorrent")]
    [InlineData('Z', "Unknown (Z)")]
    public void GetClientName_ShadowStyle_MapsKnownAndUnknownCodes(char code, string expectedClient)
    {
        byte[] peerId = Encoding.ASCII.GetBytes($"{code}123---abcdefghijklm");

        string name = ClientIdentification.GetClientName(peerId);

        Assert.Equal($"{expectedClient} 1.2.3", name);
    }

    [Theory]
    [InlineData("S12A---abcdefghijklm")]
    [InlineData("1123---abcdefghijklm")]
    [InlineData("-TR1234_abcdefghijkl")]
    public void GetClientName_MalformedRecognizedStyles_ReturnUnknownClient(string peerIdText)
    {
        byte[] peerId = Encoding.ASCII.GetBytes(peerIdText);

        string name = ClientIdentification.GetClientName(peerId);

        Assert.Equal("Unknown Client", name);
    }

    public static IEnumerable<object[]> AzureusStyleClientCodes()
    {
        yield return ["AG", "Ares"];
        yield return ["AR", "Arctic"];
        yield return ["AT", "Artemis"];
        yield return ["AV", "Avicora"];
        yield return ["BB", "BitBuddy"];
        yield return ["BC", "BitComet"];
        yield return ["BF", "Bitflu"];
        yield return ["BG", "BTG"];
        yield return ["BR", "BitRocket"];
        yield return ["BS", "BTSlave"];
        yield return ["BT", "Mainline"];
        yield return ["BW", "BitWombat"];
        yield return ["BX", "BittorrentX"];
        yield return ["CD", "Enhanced CTorrent"];
        yield return ["CT", "CTorrent"];
        yield return ["DE", "DelugeBT"];
        yield return ["DP", "Propagate Data Client"];
        yield return ["EB", "EBT"];
        yield return ["ES", "electric sheep"];
        yield return ["FT", "FoxTorrent"];
        yield return ["FW", "FrostWire"];
        yield return ["FX", "Freebox BitTorrent"];
        yield return ["GS", "GSTorrent"];
        yield return ["HL", "Halite"];
        yield return ["HM", "Hamachi"];
        yield return ["HN", "Hydranode"];
        yield return ["KG", "KGet"];
        yield return ["KT", "KTorrent"];
        yield return ["LC", "LeechCraft"];
        yield return ["LH", "LH-ABC"];
        yield return ["LP", "LPD"];
        yield return ["LW", "LimeWire"];
        yield return ["MO", "MonoTorrent"];
        yield return ["MP", "MooPolice"];
        yield return ["MR", "Miro"];
        yield return ["MT", "MtTorrent"];
        yield return ["MY", "MyTorrent"];
        yield return ["NS", "Netlyzer BT"];
        yield return ["NT", "Nullsoft NTTorrent"];
        yield return ["OT", "OmegaTorrent"];
        yield return ["PD", "Pando"];
        yield return ["PT", "PHPBT"];
        yield return ["QD", "QuantumTorrent"];
        yield return ["QT", "Qt4 Torrent"];
        yield return ["RT", "Retriever"];
        yield return ["RZ", "RezTorrent"];
        yield return ["S~", "Shareaza"];
        yield return ["SB", "Swiftbit"];
        yield return ["SS", "SwarmScope"];
        yield return ["ST", "SymTorrent"];
        yield return ["tn", "Torrent.dot.net"];
        yield return ["TS", "Torrentstorm"];
        yield return ["TT", "TuoTu"];
        yield return ["UL", "uLeecher!"];
        yield return ["UM", "uTorrent for Mac"];
        yield return ["VG", "Vagaa"];
        yield return ["WT", "BitLet"];
        yield return ["WY", "FireTorrent"];
        yield return ["XL", "Xunlei"];
        yield return ["XT", "Xanadu"];
        yield return ["XX", "Xtorrent"];
        yield return ["ZT", "ZipTorrent"];
    }
}





