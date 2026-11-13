using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PeerSharp.Internals.Utp;

internal enum MessageType : byte
{
    ST_DATA = 0,
    ST_FIN = 1,
    ST_STATE = 2,
    ST_RESET = 3,
    ST_SYN = 4
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct MessageHeader
{
    public const byte CurrentVersion = 1;

    public byte TypeVer;
    public byte Extension;
    public ushort ConnectionId;
    public uint TimestampMicroseconds;
    public uint TimestampDifferenceMicroseconds;
    public uint WndSize;
    public ushort SeqNr;
    public ushort AckNr;

    public readonly MessageType Type => (MessageType)(TypeVer >> 4);
    public readonly byte Version => (byte)(TypeVer & 0x0F);
}

internal static class Utils
{
    private static readonly Stopwatch TimestampClock = Stopwatch.StartNew();

    public static int CompareSeq(ushort a, ushort b)
    {
        int down = (a - b) & 0xFFFF;
        int up = (b - a) & 0xFFFF;
        if (up < down)
        {
            return -1;
        }

        if (up > down)
        {
            return 1;
        }

        return 0;
    }

    public static uint TimestampMicro()
    {
        // Through TimeSpan ticks: ElapsedTicks * 1_000_000 overflows a long after about 2.5 hours
        // where Stopwatch.Frequency is 1 GHz. Wrapping to 32 bits is what the protocol expects.
        return unchecked((uint)(TimestampClock.Elapsed.Ticks / TimeSpan.TicksPerMicrosecond));
    }
}
