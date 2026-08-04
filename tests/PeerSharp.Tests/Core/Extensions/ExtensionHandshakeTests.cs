using PeerSharp.Internals.Extensions;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Transfers;
using PeerSharp.Internals;
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
        Assert.Null(handshake.RequestQueueDepth);
    }

    [Fact]
    public void Reqq_RoundTrips()
    {
        var handshake = new ExtensionHandshake { RequestQueueDepth = 250 };

        var parsed = ExtensionHandshake.Parse(handshake.ToBencode());

        Assert.Equal(250, parsed.RequestQueueDepth);
    }

    [Fact]
    public void Reqq_IsOmittedWhenUnset()
    {
        // A peer that has nothing to say must not say zero: clients read a missing key as "assume your
        // own default" and an explicit zero as "accepts nothing".
        var dict = new ExtensionHandshake().ToBencode();

        Assert.False(dict.Dict.ContainsKey("reqq"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reqq_NonPositiveIsIgnored(long value)
    {
        // No client means "I accept no requests", and believing one that said so would stall us.
        var dict = new BDict();
        dict.Dict["reqq"] = new BNumber(value);

        Assert.Null(ExtensionHandshake.Parse(dict).RequestQueueDepth);
    }

    [Fact]
    public void ListenPort_RoundTrips()
    {
        var handshake = new ExtensionHandshake { ListenPort = 6881 };

        Assert.Equal(6881, ExtensionHandshake.Parse(handshake.ToBencode()).ListenPort);
    }

    [Fact]
    public void ListenPort_ZeroRoundTripsAsNotListening()
    {
        var handshake = new ExtensionHandshake { ListenPort = 0 };

        Assert.Equal(0, ExtensionHandshake.Parse(handshake.ToBencode()).ListenPort);
    }

    [Fact]
    public void ListenPort_IsOmittedWhenUnset()
    {
        Assert.False(new ExtensionHandshake().ToBencode().Dict.ContainsKey("p"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(1L + int.MaxValue)]
    public void ListenPort_OutOfRangeIsIgnored(long value)
    {
        // These cannot be ports at all. Accepting one would have us record an endpoint we can never
        // connect to; zero is tested separately because it is the valid "not listening" signal.
        var dict = new BDict();
        dict.Dict["p"] = new BNumber(value);

        Assert.Null(ExtensionHandshake.Parse(dict).ListenPort);
    }

    [Fact]
    public void AdvertisedReqq_MatchesWhatTheUploadQueueWillAccept()
    {
        // The whole point of advertising is that the number is true. If these ever diverge we are
        // telling peers to send more than we will take, which is worse than saying nothing.
        Assert.Equal(
            ProtocolConstants.MaxOutstandingRequestsPerPeer,
            UploadQueueManager.MaxQueueDepthPerPeer);
    }
}


