using PeerSharp.Internals.Utp;

namespace PeerSharp.Tests.Core.Utp;

public class UtpSackParserTests
{
    [Fact]
    public void Parse_NoReceivedPackets_ReturnsNull()
    {
        var result = UtpSackParser.Parse([0x00, 0x00], 0, 2, ackNr: 100);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_SingleByteBitmask_ReturnsContiguousRanges()
    {
        // Bits 0, 1 and 3 set. With ack_nr 10, those map to seq 12, 13 and 15.
        var result = UtpSackParser.Parse([0b0000_1011], 0, 1, ackNr: 10);

        Assert.NotNull(result);
        Assert.Collection(
            result,
            range => Assert.Equal((12, 13), (range.Start, range.End)),
            range => Assert.Equal((15, 15), (range.Start, range.End)));
    }

    [Fact]
    public void Parse_MultipleBytes_ContinuesSequenceAcrossByteBoundary()
    {
        // First byte: seq 2..9. Second byte bit 0: seq 10. All are contiguous.
        var result = UtpSackParser.Parse([0xFF, 0x01], 0, 2, ackNr: 0);

        Assert.NotNull(result);
        var range = Assert.Single(result);
        Assert.Equal((2, 10), (range.Start, range.End));
    }

    [Fact]
    public void Parse_UsesOffsetAndLength()
    {
        var result = UtpSackParser.Parse([0xFF, 0b0000_0101, 0xFF], 1, 1, ackNr: 20);

        Assert.NotNull(result);
        Assert.Collection(
            result,
            range => Assert.Equal((22, 22), (range.Start, range.End)),
            range => Assert.Equal((24, 24), (range.Start, range.End)));
    }

    [Fact]
    public void Parse_SequenceNumbersWrapAtUShortBoundary()
    {
        var result = UtpSackParser.Parse([0b0000_0011], 0, 1, ackNr: 65534);

        Assert.NotNull(result);
        var range = Assert.Single(result);
        Assert.Equal((0, 1), (range.Start, range.End));
    }

    [Fact]
    public void Parse_InvalidRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UtpSackParser.Parse([0x01], 1, 1, ackNr: 0));
    }
}
