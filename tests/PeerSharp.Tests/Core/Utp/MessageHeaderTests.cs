using PeerSharp.Internals.Utp;

namespace PeerSharp.Tests.Core.Utp;

public class MessageHeaderTests
{
    [Fact]
    public void Version_ExtractedFromLowerNibble()
    {
        // TypeVer = (type << 4) | version
        var header = new MessageHeader { TypeVer = 0x11 }; // ST_FIN=1, version=1

        Assert.Equal((byte)1, header.Version);
    }

    [Fact]
    public void Type_ExtractedFromUpperNibble()
    {
        // ST_DATA=0, version=1 → TypeVer=0x01
        Assert.Equal(MessageType.ST_DATA, new MessageHeader { TypeVer = 0x01 }.Type);
        // ST_FIN=1, version=1 → TypeVer=0x11
        Assert.Equal(MessageType.ST_FIN, new MessageHeader { TypeVer = 0x11 }.Type);
        // ST_STATE=2, version=1 → TypeVer=0x21
        Assert.Equal(MessageType.ST_STATE, new MessageHeader { TypeVer = 0x21 }.Type);
        // ST_RESET=3, version=1 → TypeVer=0x31
        Assert.Equal(MessageType.ST_RESET, new MessageHeader { TypeVer = 0x31 }.Type);
        // ST_SYN=4, version=1 → TypeVer=0x41
        Assert.Equal(MessageType.ST_SYN, new MessageHeader { TypeVer = 0x41 }.Type);
    }
}
