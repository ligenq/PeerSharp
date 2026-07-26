using PeerSharp.Internals.Trackers;
using System.Text;

namespace PeerSharp.Tests.Core.Trackers;

/// <summary>
/// BEP 41: encoding the path and query of a UDP tracker URL as announce options.
/// </summary>
public class UdpTrackerUrlDataTests
{
    private static (byte Type, string Data)[] Decode(byte[] options)
    {
        var parsed = new List<(byte, string)>();
        int i = 0;
        while (i < options.Length)
        {
            byte type = options[i++];
            if (type <= 0x1)
            {
                // EndOfOptions and NOP carry no length byte.
                parsed.Add((type, string.Empty));
                continue;
            }

            byte length = options[i++];
            parsed.Add((type, Encoding.ASCII.GetString(options, i, length)));
            i += length;
        }

        return [.. parsed];
    }

    [Fact]
    public void PathAndQuery_IsEncodedAsAUrlDataOption()
    {
        var options = UdpTrackerUrlData.Encode("udp://tracker.example:2710/announce?passkey=abc123");

        var parsed = Decode(options);
        Assert.Single(parsed);
        Assert.Equal(0x2, parsed[0].Type);
        Assert.Equal("/announce?passkey=abc123", parsed[0].Data);
    }

    [Fact]
    public void PathOnly_IsEncoded()
    {
        // Passkeys live in the path as often as in the query.
        var options = UdpTrackerUrlData.Encode("udp://tracker.example:2710/abc123def456/announce");

        var parsed = Decode(options);
        Assert.Equal("/abc123def456/announce", parsed[0].Data);
    }

    [Theory]
    [InlineData("udp://tracker.example:2710")]
    [InlineData("udp://tracker.example:2710/")]
    public void UrlWithNothingToConvey_ProducesNoOptions(string url)
    {
        // The packet then stays byte-identical to a plain BEP 15 announce, so nothing changes for the
        // trackers this extension cannot help.
        Assert.Empty(UdpTrackerUrlData.Encode(url));
    }

    [Fact]
    public void MalformedUrl_ProducesNoOptions()
    {
        Assert.Empty(UdpTrackerUrlData.Encode("not a url"));
    }

    [Fact]
    public void PercentEncoding_IsPreservedVerbatim()
    {
        // A passkey is opaque. Decoding or re-encoding it would change what the tracker matches on.
        var options = UdpTrackerUrlData.Encode("udp://tracker.example:2710/announce?key=a%2Fb%20c");

        Assert.Equal("/announce?key=a%2Fb%20c", Decode(options)[0].Data);
    }

    [Fact]
    public void DataLongerThan255Bytes_IsSplitAcrossOptions()
    {
        // One length byte cannot address more than 255 bytes, so BEP 41 concatenates repeated options.
        var longPath = "/" + new string('x', 400) + "/announce";
        var options = UdpTrackerUrlData.Encode($"udp://tracker.example:2710{longPath}");

        var parsed = Decode(options);
        Assert.Equal(2, parsed.Length);
        Assert.All(parsed, option => Assert.Equal(0x2, option.Type));
        Assert.Equal(255, parsed[0].Data.Length);
        Assert.Equal(longPath, parsed[0].Data + parsed[1].Data);
    }

    [Fact]
    public void ExactlyOneOptionWorthOfData_IsNotSplit()
    {
        var path = "/" + new string('y', 254);
        var options = UdpTrackerUrlData.Encode($"udp://tracker.example:2710{path}");

        var parsed = Decode(options);
        Assert.Single(parsed);
        Assert.Equal(255, parsed[0].Data.Length);
        Assert.Equal(path, parsed[0].Data);
    }
}
