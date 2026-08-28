using CsCheck;
using PeerSharp.Internals.Utp;

namespace PeerSharp.Tests.Core.Utp;

/// <summary>
/// The SACK bitmask, over every gap pattern rather than a few chosen ones.
/// </summary>
/// <remarks>
/// <para>
/// This is what tells the sender which packets after the acknowledged one actually arrived, so
/// everything it does not report is retransmitted. Reading a bit at the wrong position does not fail
/// loudly - it resends data the peer already has, or worse, marks as received something that never
/// came and leaves a hole nobody fills.
/// </para>
/// <para>
/// The bytes come from a datagram, and the interesting part is the shape of the gaps between set
/// bits, which is exactly what is tedious to enumerate by hand and free to generate.
/// </para>
/// </remarks>
public class UtpSackParserPropertyTests
{
    /// <summary>BEP 29 allows 1 to 32 bytes of bitmask.</summary>
    private static readonly Gen<byte[]> Bitmask = Gen.Byte.Array[1, 32];

    [Fact]
    public void EverySetBitComesBackAndNothingElseDoes()
    {
        Gen.Select(Bitmask, Gen.UShort).Sample((mask, ackNr) =>
        {
            var expected = new List<ushort>();
            for (int i = 0; i < mask.Length * 8; i++)
            {
                if ((mask[i / 8] & (1 << (i % 8))) != 0)
                {
                    expected.Add((ushort)(ackNr + 2 + i));
                }
            }

            var ranges = UtpSackParser.Parse(mask, 0, mask.Length, ackNr);

            Assert.Equal(expected.Count == 0, ranges is null);
            Assert.Equal(expected, Expand(ranges, mask.Length * 8));
        }, iter: 20_000);
    }

    [Fact]
    public void RangesAreContiguousRunsAndNeverTouch()
    {
        // Two ranges reported back to back would mean a gap was invented where the bits were
        // continuous, which costs a retransmission of something already delivered.
        Gen.Select(Bitmask, Gen.UShort).Sample((mask, ackNr) =>
        {
            var ranges = UtpSackParser.Parse(mask, 0, mask.Length, ackNr);
            if (ranges is null)
            {
                return;
            }

            for (int i = 1; i < ranges.Count; i++)
            {
                // Ranges are produced in bit order, so each starts at least two beyond the previous
                // end - one for the gap that separated them.
                ushort previousEnd = ranges[i - 1].End;
                ushort start = ranges[i].Start;
                Assert.True(
                    (ushort)(start - previousEnd) >= 2,
                    $"ranges {previousEnd} and {start} were adjacent and should have been one");
            }
        }, iter: 20_000);
    }

    [Fact]
    public void ARunSpanningTheSequenceWrapStaysOneRange()
    {
        // Sequence numbers are 16-bit and wrap. A run crossing the boundary is still one run, and is
        // reported as a range whose start is numerically greater than its end - which is why every
        // consumer of these has to compare sequence numbers modularly rather than with <.
        var ranges = UtpSackParser.Parse([0xFF], 0, 1, ackNr: 65530);

        Assert.NotNull(ranges);
        var range = Assert.Single(ranges);
        // ack_nr + 2 + 7 is 65539, which is 3 once the 16-bit counter wraps.
        Assert.Equal(65532, range.Start);
        Assert.Equal(3, range.End);
        Assert.True(range.Start > range.End, "the wrap should leave start above end");
    }

    [Fact]
    public void OffsetAndLengthSelectTheBitmaskAndNothingAroundIt()
    {
        Gen.Select(Bitmask, Gen.Byte.Array[1, 8], Gen.Byte.Array[1, 8], Gen.UShort)
            .Sample((mask, before, after, ackNr) =>
            {
                byte[] framed = [.. before, .. mask, .. after];

                Assert.Equal(
                    UtpSackParser.Parse(mask, 0, mask.Length, ackNr),
                    UtpSackParser.Parse(framed, before.Length, mask.Length, ackNr));
            }, iter: 10_000);
    }

    [Fact]
    public void AnOutOfRangeWindowIsRefused()
    {
        // Length and offset come from the packet, so they are a stranger's numbers.
        Gen.Select(Gen.Byte.Array[0, 8], Gen.Int[-4, 12], Gen.Int[-4, 12]).Sample((data, offset, length) =>
        {
            bool valid = offset >= 0 && length >= 0 && offset <= data.Length - length;
            if (valid)
            {
                UtpSackParser.Parse(data, offset, length, 0);
                return;
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => UtpSackParser.Parse(data, offset, length, 0));
        }, iter: 10_000);
    }

    /// <summary>
    /// Walks each range from start to end with plain 16-bit increment, which recovers the sequence
    /// numbers without borrowing the modular comparison the engine uses elsewhere.
    /// </summary>
    private static List<ushort> Expand(List<(ushort Start, ushort End)>? ranges, int bitCount)
    {
        var all = new List<ushort>();
        if (ranges is null)
        {
            return all;
        }

        foreach (var (start, end) in ranges)
        {
            ushort seq = start;
            for (int guard = 0; guard <= bitCount; guard++)
            {
                all.Add(seq);
                if (seq == end)
                {
                    break;
                }

                seq++;
            }
        }

        return all;
    }
}
