using PeerSharp.Internals.Extensions;
using PeerSharp.BEncoding;
using System.Text;

namespace PeerSharp.Tests.Core.Extensions;

public class ExtensionHandshakeTests
{
    [Fact]
    public void ToBencode_EmptyHandshake_ReturnsMinimalDict()
    {
        // Arrange
        var handshake = new ExtensionHandshake();

        // Act
        var dict = handshake.ToBencode();

        // Assert
        Assert.Single(dict.Dict);
        Assert.True(dict.Dict.ContainsKey("m"));
        var m = Assert.IsType<BDict>(dict.Dict["m"]);
        Assert.Empty(m.Dict);
    }

    [Fact]
    public void ToBencode_FullHandshake_ReturnsPopulatedDict()
    {
        // Arrange
        var handshake = new ExtensionHandshake
        {
            Client = "MtTorrent 1.0",
            MetadataSize = 12345,
            YourIp = [127, 0, 0, 1]
        };
        handshake.MessageIds["ut_metadata"] = 1;
        handshake.MessageIds["ut_pex"] = 2;

        // Act
        var dict = handshake.ToBencode();

        // Assert
        Assert.Equal("MtTorrent 1.0", dict.GetString("v"));
        Assert.Equal(12345, dict.GetLong("metadata_size"));
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, dict.GetBytes("yourip")?.ToArray());

        var m = Assert.IsType<BDict>(dict.Dict["m"]);
        Assert.Equal(1, (int?)m.GetLong("ut_metadata"));
        Assert.Equal(2, (int?)m.GetLong("ut_pex"));
    }

    [Fact]
    public void Parse_PopulatedDict_ReturnsCorrectHandshake()
    {
        // Arrange
        var dict = new BDict();
        var m = new BDict();
        m.Dict["ut_metadata"] = new BNumber(1);
        m.Dict["ut_pex"] = new BNumber(2);
        dict.Dict["m"] = m;
        dict.Dict["v"] = new BString(Encoding.UTF8.GetBytes("MtTorrent 1.0"));
        dict.Dict["metadata_size"] = new BNumber(12345);
        dict.Dict["yourip"] = new BString([127, 0, 0, 1]);

        // Act
        var handshake = ExtensionHandshake.Parse(dict);

        // Assert
        Assert.Equal("MtTorrent 1.0", handshake.Client);
        Assert.Equal(12345, handshake.MetadataSize);
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, handshake.YourIp);
        Assert.Equal(1, handshake.MessageIds["ut_metadata"]);
        Assert.Equal(2, handshake.MessageIds["ut_pex"]);
    }

    [Fact]
    public void Parse_EmptyDict_ReturnsEmptyHandshake()
    {
        // Arrange
        var dict = new BDict();

        // Act
        var handshake = ExtensionHandshake.Parse(dict);

        // Assert
        Assert.Empty(handshake.MessageIds);
        Assert.Equal(string.Empty, handshake.Client);
        Assert.Null(handshake.MetadataSize);
        Assert.Null(handshake.YourIp);
    }
}





