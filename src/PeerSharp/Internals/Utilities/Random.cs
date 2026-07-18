using System.Security.Cryptography;

namespace PeerSharp.Internals.Utilities;

internal static class RandomUtils
{
    public static void Data(Span<byte> buffer)
    {
        RandomNumberGenerator.Fill(buffer);
    }

    public static void Data(byte[] buffer)
    {
        RandomNumberGenerator.Fill(buffer);
    }

    public static uint Number()
    {
        Span<byte> buf = stackalloc byte[4];
        RandomNumberGenerator.Fill(buf);
        return BitConverter.ToUInt32(buf);
    }

    public static ulong Number64()
    {
        Span<byte> buf = stackalloc byte[8];
        RandomNumberGenerator.Fill(buf);
        return BitConverter.ToUInt64(buf);
    }
}
