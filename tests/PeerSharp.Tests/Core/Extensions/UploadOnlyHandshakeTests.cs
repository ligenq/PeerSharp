using PeerSharp.BEncoding;
using PeerSharp.Internals.Extensions;

namespace PeerSharp.Tests.Core.Extensions;

/// <summary>
/// BEP 21: the <c>upload_only</c> handshake key, by which a seed or partial seed says it will not be
/// downloading anything.
/// </summary>
public class UploadOnlyHandshakeTests
{
    private static BDict RoundTrip(ExtensionHandshake handshake)
    {
        return (BDict)BencodeParser.Parse(BencodeWriter.Write(handshake.ToBencode()));
    }

    [Fact]
    public void WhenSet_TheKeyIsEmittedWithValueOne()
    {
        var dict = RoundTrip(new ExtensionHandshake { IsUploadOnly = true });

        Assert.Equal(1, dict.GetLong("upload_only"));
    }

    [Fact]
    public void WhenNotSet_TheKeyIsAbsent()
    {
        // A peer that is still downloading has nothing to say here, and an explicit 0 would be noise.
        var dict = RoundTrip(new ExtensionHandshake());

        Assert.Null(dict.Get("upload_only"));
    }

    [Fact]
    public void Parse_ReadsTheFlag()
    {
        var dict = new BDict();
        dict.Dict["upload_only"] = new BNumber(1);

        Assert.True(ExtensionHandshake.Parse(dict).IsUploadOnly);
    }

    [Fact]
    public void Parse_AbsentKeyMeansFalse()
    {
        Assert.False(ExtensionHandshake.Parse(new BDict()).IsUploadOnly);
    }

    [Fact]
    public void Parse_ExplicitZeroMeansFalse()
    {
        var dict = new BDict();
        dict.Dict["upload_only"] = new BNumber(0);

        Assert.False(ExtensionHandshake.Parse(dict).IsUploadOnly);
    }

    [Fact]
    public void Parse_UnexpectedNonZeroIsTreatedAsSet()
    {
        // The spec says 1. The intent of any other non-zero value is unambiguous, and this is a hint
        // rather than a security boundary, so it is honoured instead of discarded.
        var dict = new BDict();
        dict.Dict["upload_only"] = new BNumber(2);

        Assert.True(ExtensionHandshake.Parse(dict).IsUploadOnly);
    }

    [Fact]
    public void RoundTrip_PreservesTheFlagAlongsideOtherKeys()
    {
        var handshake = new ExtensionHandshake
        {
            Client = "PeerSharp",
            IsUploadOnly = true,
            MetadataSize = 1234,
            MessageIds = { ["ut_metadata"] = 1 }
        };

        var parsed = ExtensionHandshake.Parse(RoundTrip(handshake));

        Assert.True(parsed.IsUploadOnly);
        Assert.Equal(1234, parsed.MetadataSize);
        Assert.Equal(1, parsed.MessageIds["ut_metadata"]);
    }
}
