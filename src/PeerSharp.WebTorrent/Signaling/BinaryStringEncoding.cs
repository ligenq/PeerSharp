using System.Text;

namespace PeerSharp.WebTorrent.Signaling;

internal static class BinaryStringEncoding
{
    public static string Encode(ReadOnlyMemory<byte> data)
    {
        return Encoding.Latin1.GetString(data.Span);
    }

    public static string Encode(byte[] data)
    {
        return Encoding.Latin1.GetString(data);
    }
}
