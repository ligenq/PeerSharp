namespace PeerSharp.Internals.Utp;

internal static class UtpSackParser
{
    /// <summary>
    /// Parses a BEP-29 SACK bitmask into contiguous sequence number ranges.
    /// Bit 0 represents ack_nr + 2, bit 1 represents ack_nr + 3, and so on.
    /// </summary>
    public static List<(ushort Start, ushort End)>? Parse(byte[] data, int offset, int length, ushort ackNr)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var ranges = new List<(ushort Start, ushort End)>();
        ushort? rangeStart = null;
        ushort lastSeq = 0;

        for (int i = 0; i < length; i++)
        {
            byte b = data[offset + i];
            for (int bit = 0; bit < 8; bit++)
            {
                int seqOffset = (i * 8) + bit;
                ushort seq = (ushort)(ackNr + 2 + seqOffset);

                if ((b & (1 << bit)) != 0)
                {
                    rangeStart ??= seq;
                    lastSeq = seq;
                }
                else if (rangeStart != null)
                {
                    ranges.Add((rangeStart.Value, lastSeq));
                    rangeStart = null;
                }
            }
        }

        if (rangeStart != null)
        {
            ranges.Add((rangeStart.Value, lastSeq));
        }

        return ranges.Count > 0 ? ranges : null;
    }
}
