using System.Buffers.Binary;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// An independent implementation of the responder half of MSE (Message Stream Encryption), written
/// from the specification and cross-checked against Transmission's <c>peer-mse.cc</c> and
/// <c>handshake.cc</c>.
///
/// <para>
/// Independence is the entire point. PeerSharp's existing encryption tests run its encryptor against
/// its own decryptor, which agree by construction and would agree just as happily on a wrong keystream,
/// a mis-ordered handshake field or the wrong number of discarded bytes. Every real connection in the
/// wild is encrypted while almost every local test is plaintext, so a conformance fault here would be
/// invisible locally and near-total in production. This shares no code with the implementation under
/// test.
/// </para>
///
/// <para>
/// Protocol, with A the initiator and B this class:
/// <code>
/// A-&gt;B: Ya, PadA
/// B-&gt;A: Yb, PadB
/// A-&gt;B: HASH('req1', S), HASH('req2', SKEY) xor HASH('req3', S),
///       ENCRYPT(VC, crypto_provide, len(PadC), PadC, len(IA)), ENCRYPT(IA)
/// B-&gt;A: ENCRYPT(VC, crypto_select, len(PadD), PadD), ENCRYPT(payload)
/// </code>
/// </para>
/// </summary>
internal sealed class MseResponder
{
    /// <summary>The 768-bit MSE prime. Same value Transmission carries in peer-mse.cc.</summary>
    private const string PrimeHex =
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A63A36210000000000090563";

    private const int KeySize = 96;
    private const int PrivateKeySize = 20;

    /// <summary>MSE discards the first 1024 bytes of each RC4 keystream.</summary>
    private const int Discard = 1024;

    /// <summary>Verification constant: eight zero bytes.</summary>
    private static readonly byte[] Vc = new byte[8];

    private const uint CryptoPlaintext = 0x01;
    private const uint CryptoRc4 = 0x02;

    private readonly byte[] _infoHash;
    private readonly BigInteger _prime;
    private readonly BigInteger _privateKey;

    private Rc4? _incoming;
    private Rc4? _outgoing;

    public MseResponder(byte[] infoHash)
    {
        _infoHash = infoHash;

        // BigInteger parses big-endian when given a leading zero byte to force a positive value.
        _prime = new BigInteger(Convert.FromHexString("00" + PrimeHex), isUnsigned: true, isBigEndian: true);

        byte[] priv = RandomNumberGenerator.GetBytes(PrivateKeySize);
        _privateKey = new BigInteger(priv, isUnsigned: true, isBigEndian: true);
    }

    /// <summary>What the initiator asked for and sent in its first encrypted block.</summary>
    public uint CryptoProvide { get; private set; }

    /// <summary>The initial payload the initiator attached, which carries its BitTorrent handshake.</summary>
    public byte[] InitialPayload { get; private set; } = [];

    /// <summary>
    /// Performs the responder side of the handshake. Throws when the initiator deviates from the spec,
    /// which is the failure this exists to detect.
    /// </summary>
    public async Task AcceptAsync(NetworkStream stream, CancellationToken ct = default)
    {
        // 1. Ya. PadA follows and its length is unknown, so it is skipped later by resynchronising on
        //    HASH('req1', S).
        byte[] ya = new byte[KeySize];
        await ReadExactlyAsync(stream, ya, KeySize, ct);

        // 2. Our public key. No PadB, which is legal - the initiator must resynchronise regardless.
        var publicKey = BigInteger.ModPow(2, _privateKey, _prime);
        await stream.WriteAsync(ToFixedBigEndian(publicKey, KeySize), ct);
        await stream.FlushAsync(ct);

        // 3. Shared secret.
        var peerPublic = new BigInteger(ya, isUnsigned: true, isBigEndian: true);
        byte[] secret = ToFixedBigEndian(BigInteger.ModPow(peerPublic, _privateKey, _prime), KeySize);

        // 4. Skip PadA by finding HASH('req1', S).
        byte[] req1 = Sha1("req1"u8.ToArray(), secret);
        await ResyncAsync(stream, req1, maxSkip: 512 + KeySize, ct);

        // 5. HASH('req2', SKEY) xor HASH('req3', S) identifies which torrent, and proves the initiator
        //    derived the same secret we did.
        byte[] req2Xor3 = new byte[20];
        await ReadExactlyAsync(stream, req2Xor3, 20, ct);

        byte[] req2 = Sha1("req2"u8.ToArray(), _infoHash);
        byte[] req3 = Sha1("req3"u8.ToArray(), secret);
        for (int i = 0; i < 20; i++)
        {
            if ((byte)(req2[i] ^ req3[i]) != req2Xor3[i])
            {
                throw new InvalidOperationException(
                    "HASH('req2', SKEY) xor HASH('req3', S) did not match. The initiator derived a different " +
                    "shared secret or hashed the info hash differently.");
            }
        }

        // 6. Keys. Note the direction: the initiator encrypts with keyA, so we decrypt with it.
        _incoming = new Rc4(Sha1("keyA"u8.ToArray(), secret, _infoHash));
        _outgoing = new Rc4(Sha1("keyB"u8.ToArray(), secret, _infoHash));
        _incoming.Discard(Discard);
        _outgoing.Discard(Discard);

        // 7. ENCRYPT(VC, crypto_provide, len(PadC), PadC, len(IA))
        byte[] vc = new byte[8];
        await ReadDecryptedAsync(stream, vc, 8, ct);
        if (!vc.SequenceEqual(Vc))
        {
            throw new InvalidOperationException(
                $"VC decrypted to {Convert.ToHexString(vc)} rather than eight zero bytes. The RC4 keystream is " +
                "wrong - wrong key derivation, or the wrong number of discarded bytes.");
        }

        byte[] four = new byte[4];
        await ReadDecryptedAsync(stream, four, 4, ct);
        CryptoProvide = BinaryPrimitives.ReadUInt32BigEndian(four);

        if ((CryptoProvide & (CryptoPlaintext | CryptoRc4)) == 0)
        {
            throw new InvalidOperationException(
                $"crypto_provide was 0x{CryptoProvide:X8}, offering neither plaintext nor RC4.");
        }

        byte[] two = new byte[2];
        await ReadDecryptedAsync(stream, two, 2, ct);
        int padCLength = BinaryPrimitives.ReadUInt16BigEndian(two);
        if (padCLength > 512)
        {
            throw new InvalidOperationException($"len(PadC) was {padCLength}; the spec allows at most 512.");
        }

        if (padCLength > 0)
        {
            await ReadDecryptedAsync(stream, new byte[padCLength], padCLength, ct);
        }

        await ReadDecryptedAsync(stream, two, 2, ct);
        int iaLength = BinaryPrimitives.ReadUInt16BigEndian(two);
        if (iaLength is < 0 or > 8192)
        {
            throw new InvalidOperationException($"len(IA) was {iaLength}, which is not a plausible handshake length.");
        }

        InitialPayload = new byte[iaLength];
        if (iaLength > 0)
        {
            await ReadDecryptedAsync(stream, InitialPayload, iaLength, ct);
        }

        // 8. ENCRYPT(VC, crypto_select, len(PadD), PadD). RC4 is selected, so the stream stays encrypted.
        var response = new byte[8 + 4 + 2];
        Vc.CopyTo(response, 0);
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(8), CryptoRc4);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(12), 0);

        byte[] encrypted = new byte[response.Length];
        _outgoing.Process(response, encrypted);
        await stream.WriteAsync(encrypted, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Decrypts payload bytes arriving after the handshake.</summary>
    public async Task ReadPayloadAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct = default)
    {
        await ReadDecryptedAsync(stream, buffer, count, ct);
    }

    /// <summary>Encrypts and sends payload bytes.</summary>
    public async Task WritePayloadAsync(NetworkStream stream, byte[] plain, CancellationToken ct = default)
    {
        byte[] encrypted = new byte[plain.Length];
        _outgoing!.Process(plain, encrypted);
        await stream.WriteAsync(encrypted, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Consumes bytes one at a time until the needle is found, which is how the responder skips PadA of
    /// unknown length.
    /// </summary>
    private static async Task ResyncAsync(NetworkStream stream, byte[] needle, int maxSkip, CancellationToken ct)
    {
        var window = new List<byte>(needle.Length);
        byte[] one = new byte[1];

        for (int skipped = 0; skipped <= maxSkip + needle.Length; skipped++)
        {
            await ReadExactlyAsync(stream, one, 1, ct);
            window.Add(one[0]);

            if (window.Count > needle.Length)
            {
                window.RemoveAt(0);
            }

            if (window.Count == needle.Length && window.SequenceEqual(needle))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "HASH('req1', S) never appeared. Either PadA exceeded 512 bytes or the initiator computed a " +
            "different shared secret.");
    }

    private async Task ReadDecryptedAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        byte[] cipher = new byte[count];
        await ReadExactlyAsync(stream, cipher, count, ct);
        _incoming!.Process(cipher, buffer.AsSpan(0, count));
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0)
            {
                throw new IOException($"Peer closed the connection after {read} of {count} expected bytes.");
            }

            read += n;
        }
    }

    private static byte[] ToFixedBigEndian(BigInteger value, int length)
    {
        byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == length)
        {
            return raw;
        }

        // MSE keys are always sent as exactly 96 bytes, zero padded on the left.
        byte[] padded = new byte[length];
        raw.CopyTo(padded, length - raw.Length);
        return padded;
    }

    private static byte[] Sha1(params byte[][] parts)
    {
        using var sha = SHA1.Create();
        foreach (var part in parts)
        {
            sha.TransformBlock(part, 0, part.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash!;
    }

    /// <summary>Plain RC4, written here so the test shares no cipher code with the implementation.</summary>
    private sealed class Rc4
    {
        private readonly byte[] _s = new byte[256];
        private int _i;
        private int _j;

        public Rc4(byte[] key)
        {
            for (int i = 0; i < 256; i++)
            {
                _s[i] = (byte)i;
            }

            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + _s[i] + key[i % key.Length]) & 0xFF;
                (_s[i], _s[j]) = (_s[j], _s[i]);
            }
        }

        public void Discard(int count)
        {
            for (int n = 0; n < count; n++)
            {
                NextByte();
            }
        }

        public void Process(ReadOnlySpan<byte> input, Span<byte> output)
        {
            for (int n = 0; n < input.Length; n++)
            {
                output[n] = (byte)(input[n] ^ NextByte());
            }
        }

        private byte NextByte()
        {
            _i = (_i + 1) & 0xFF;
            _j = (_j + _s[_i]) & 0xFF;
            (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
            return _s[(_s[_i] + _s[_j]) & 0xFF];
        }
    }
}
