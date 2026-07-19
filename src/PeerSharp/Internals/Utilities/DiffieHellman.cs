using System.Numerics;

namespace PeerSharp.Internals.Utilities;

internal class DiffieHellman
{
    private static readonly BigInteger G = 2;

    private static readonly byte[] PrimeBytes =
    [
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xC9,0x0F,0xDA,0xA2,0x21,0x68,0xC2,0x34,
        0xC4,0xC6,0x62,0x8B,0x80,0xDC,0x1C,0xD1,
        0x29,0x02,0x4E,0x08,0x8A,0x67,0xCC,0x74,
        0x02,0x0B,0xBE,0xA6,0x3B,0x13,0x9B,0x22,
        0x51,0x4A,0x08,0x79,0x8E,0x34,0x04,0xDD,
        0xEF,0x95,0x19,0xB3,0xCD,0x3A,0x43,0x1B,
        0x30,0x2B,0x0A,0x6D,0xF2,0x5F,0x14,0x37,
        0x4F,0xE1,0x35,0x6D,0x6D,0x51,0xC2,0x45,
        0xE4,0x85,0xB5,0x76,0x62,0x5E,0x7E,0xC6,
        0xF4,0x4C,0x42,0xE9,0xA6,0x3A,0x36,0x21,
        0x00,0x00,0x00,0x00,0x00,0x09,0x05,0x63
    ];

    private static readonly BigInteger P = FromBigEndian(PrimeBytes);

    private readonly BigInteger _privateKey;

    public DiffieHellman()
    {
        byte[] priv = new byte[96];
        System.Security.Cryptography.RandomNumberGenerator.Fill(priv);
        // Force positive
        priv[0] &= 0x7F;

        _privateKey = FromBigEndian(priv);
        PublicKey = BigInteger.ModPow(G, _privateKey, P);
    }

    public BigInteger PublicKey { get; private set; }
    public BigInteger SharedSecret { get; private set; }

    public void ComputeSharedSecret(byte[] remotePubKeyBytes)
    {
        var remoteKey = FromBigEndian(remotePubKeyBytes);
        SharedSecret = BigInteger.ModPow(remoteKey, _privateKey, P);
    }

    public byte[] GetPublicKeyBytes()
    {
        return ToBigEndian(PublicKey, 96);
    }

    public byte[] GetSharedSecretBytes()
    {
        return ToBigEndian(SharedSecret, 96);
    }

    private static BigInteger FromBigEndian(byte[] bytes)
    {
        // Prepend 00 to force positive for BigInteger (LE)
        byte[] le = new byte[bytes.Length + 1];
        for (int i = 0; i < bytes.Length; i++)
        {
            le[bytes.Length - 1 - i] = bytes[i]; // bytes[0] (MSB) goes to le[len]?? No.
        }
        // bytes[0] is MSB. le[len-1] (last byte) should be MSB.
        // le[len] is sign (0).

        // Example: BE [01, 02]. 01 is MSB.
        // LE: [02, 01, 00].
        // i=0: le[1] = bytes[0] (01).
        // i=1: le[0] = bytes[1] (02).
        // le[2] = 0.

        return new BigInteger(le);
    }

    private static byte[] ToBigEndian(BigInteger val, int size)
    {
        byte[] bytes = val.ToByteArray(); // LE

        int len = bytes.Length;
        if (len > size && bytes[len - 1] == 0)
        {
            len--;
        }

        byte[] ret = new byte[size];
        int copyLen = Math.Min(len, size);

        for (int i = 0; i < copyLen; i++)
        {
            ret[size - 1 - i] = bytes[i];
        }
        return ret;
    }
}
