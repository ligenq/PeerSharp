using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Utilities;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PeerSharp.Internals.Peers;

internal class ProtocolEncryption
{
    private readonly Lock _decryptLock = new();

    // CRITICAL FIX: Add synchronization to prevent race conditions
    // RC4 is stateful and not thread-safe - concurrent encrypt/decrypt calls corrupt state
    private readonly Lock _encryptLock = new();

    public RC4 RC4In { get; } = new RC4();
    public RC4 RC4Out { get; } = new RC4();

    public void Decrypt(byte[] data, int offset, int count)
    {
        lock (_decryptLock)
        {
            RC4In.Decrypt(data, offset, count);
        }
    }

    public void Decrypt(Span<byte> data)
    {
        lock (_decryptLock)
        {
            RC4In.Decrypt(data);
        }
    }

    public void Encrypt(byte[] data, int offset, int count)
    {
        lock (_encryptLock)
        {
            RC4Out.Encrypt(data, offset, count);
        }
    }

    public void Encrypt(Span<byte> data)
    {
        lock (_encryptLock)
        {
            RC4Out.Encrypt(data);
        }
    }
}

internal sealed class ProtocolEncryptionHandshake : IDisposable
{
    // MSE spec: padding is 0-512 bytes, plus protocol overhead and BT handshake (68 bytes)
    // Maximum reasonable buffer: 96 (key) + 512 (pad) + 40 (hashes) + 14 (header) + 512 (pad) + 2 + 68 (IA) + margin
    private const int MaxBufferSize = 16384;

    private const int MaxInitialPayloadLength = 256;

    private const int MaxPaddingLength = 512;

    private readonly DiffieHellman _dh = new DiffieHellman();

    private readonly byte[]? _infoHash;

    private readonly bool _initiator;

    private readonly ITorrentResolver? _resolver;

    private byte[]? _buffer = new byte[8192];

    // MSE spec limit
    // BT handshake is 68 bytes, allow some margin for extensions
    private int _bufferCount = 0;

    private AtomicDisposal _disposal = new();

    private Step _step = Step.Pe1;

    public ProtocolEncryptionHandshake(byte[] infoHash, bool initiator)
    {
        _infoHash = infoHash;
        _initiator = initiator;
        if (initiator)
        {
            _step = Step.Pe2;
        }
    }

    public ProtocolEncryptionHandshake(ITorrentResolver resolver)
    {
        _resolver = resolver;
        _initiator = false;
        _step = Step.Pe1;
    }

    private enum Step
    { Pe1, Pe2, Pe3, Pe4, Established, Error }

    public ProtocolEncryption? Encryption { get; private set; }
    public byte[]? InitialPayload { get; set; } // BT Handshake
    public bool IsComplete => _step == Step.Established;
    public bool IsError => _step == Step.Error;
    public byte[]? MatchedInfoHash { get; private set; }
    public byte[]? ReceivedPayload { get; private set; } // BT Handshake from remote
    public byte[] TrailingData
    {
        get
        {
            if (_buffer == null || _bufferCount == 0)
            {
                return [];
            }

            byte[] trailing = new byte[_bufferCount];
            Array.Copy(_buffer, 0, trailing, 0, _bufferCount);
            return trailing;
        }
    }

    public byte[] HandleIncoming(byte[] data)
    {
        ThrowIfDisposed();

        // Prevent memory exhaustion DOS by limiting buffer growth
        int requiredSize = _bufferCount + data.Length;
        if (requiredSize > MaxBufferSize)
        {
            _step = Step.Error;
            throw new InvalidDataException($"Encryption handshake buffer exceeded maximum size ({MaxBufferSize} bytes)");
        }

        if (_buffer!.Length < requiredSize)
        {
            var newSize = Math.Min(Math.Max(_buffer.Length * 2, requiredSize), MaxBufferSize);
            var newBuf = new byte[newSize];
            Array.Copy(_buffer, 0, newBuf, 0, _bufferCount);
            // Clear old buffer to prevent sensitive encryption data from leaking
            Array.Clear(_buffer);
            _buffer = newBuf;
        }

        Array.Copy(data, 0, _buffer!, _bufferCount, data.Length);
        _bufferCount += data.Length;

        if (_initiator)
        {
            if (_step == Step.Pe2)
            {
                if (_bufferCount < 96)
                {
                    return [];
                }

                byte[] keyB = new byte[96];
                Array.Copy(_buffer!, 0, keyB, 0, 96);
                _dh.ComputeSharedSecret(keyB);

                Encryption = new ProtocolEncryption();
                InitRC4(true, _infoHash!);

                ConsumeBuffer(96);

                _step = Step.Pe4;

                var pe3Msg = CreatePe3Message();

                // CRITICAL FIX: Try to process Pe4 immediately from remaining buffer
                // We might have received Pe2 + Pe4 in the same packet/read.
                // If we don't process it now, we'll return to ReadAsync and deadlock
                // because the data is already in _buffer.
                ProcessPe4();

                return pe3Msg;
            }

            if (_step == Step.Pe4)
            {
                return ProcessPe4();
            }
        }
        else // Listener
        {
            if (_step == Step.Pe1)
            {
                if (_bufferCount < 96)
                {
                    return [];
                }

                byte[] keyA = new byte[96];
                Array.Copy(_buffer!, 0, keyA, 0, 96);
                _dh.ComputeSharedSecret(keyA);

                Encryption = new ProtocolEncryption();
                // InitRC4 depends on InfoHash, which we don't know yet if we are the receiver (multi-torrent)
                // We will initialize it in ProcessPe3 after matching the SKEY
                if (_infoHash != null)
                {
                    InitRC4(false, _infoHash);
                }

                ConsumeBuffer(96);

                _step = Step.Pe3;

                // Respond with KeyB + PadB
                return CreateKeyMessage();
            }

            if (_step == Step.Pe3)
            {
                return ProcessPe3();
            }
        }

        return [];
    }

    public byte[] Initiate()
    {
        ThrowIfDisposed();
        return CreateKeyMessage();
    }

    public void Dispose()
    {
        if (_disposal.MarkDisposed())
        {
            var buffer = _buffer;
            _buffer = null;
            if (buffer != null)
            {
                // Clear buffer to prevent sensitive encryption data from leaking
                Array.Clear(buffer);
            }
            GC.SuppressFinalize(this);
        }
    }

    private static byte[] Sha1(params byte[][] data)
    {
        int totalLen = 0;
        foreach (var d in data)
        {
            totalLen += d.Length;
        }

        byte[] buffer = new byte[totalLen];
        int pos = 0;
        foreach (var d in data)
        {
            Array.Copy(d, 0, buffer, pos, d.Length);
            pos += d.Length;
        }
        return SHA1.HashData(buffer);
    }

    private static byte[] Sha1(ReadOnlySpan<byte> data1, ReadOnlySpan<byte> data2)
    {
        byte[] buffer = new byte[data1.Length + data2.Length];
        data1.CopyTo(buffer);
        data2.CopyTo(buffer.AsSpan(data1.Length));
        return SHA1.HashData(buffer);
    }

    private void ConsumeBuffer(int count)
    {
        if (count == 0)
        {
            return;
        }

        if (count >= _bufferCount)
        {
            _bufferCount = 0;
        }
        else
        {
            Array.Copy(_buffer!, count, _buffer!, 0, _bufferCount - count);
            _bufferCount -= count;
        }
    }

    private byte[] CreateKeyMessage()
    {
        var key = _dh.GetPublicKeyBytes();
        var pad = new byte[Random.Shared.Next(512)];
        Random.Shared.NextBytes(pad);

        var msg = new byte[key.Length + pad.Length];
        key.CopyTo(msg, 0);
        pad.CopyTo(msg, key.Length);
        return msg;
    }

    private byte[] CreatePe3Message()
    {
        // HASH('req1', S)
        byte[] req1Hash = Sha1("req1"u8.ToArray(), _dh.GetSharedSecretBytes());

        // HASH('req2', SKEY) xor HASH('req3', S)
        // Note: _infoHash is guaranteed non-null here because CreatePe3Message is only called
        // by initiators, who are always constructed with the (byte[] infoHash, bool initiator)
        // constructor which requires a non-null infoHash.
        byte[] req2Hash = Sha1("req2"u8.ToArray(), _infoHash!);
        byte[] req3Hash = Sha1("req3"u8.ToArray(), _dh.GetSharedSecretBytes());

        // XOR req2 and req3 to create verification
        Span<byte> verification = stackalloc byte[20];
        for (int i = 0; i < 20; i++)
        {
            verification[i] = (byte)(req2Hash[i] ^ req3Hash[i]);
        }

        // ENCRYPT(VC, crypto_provide, len(PadC), PadC, len(IA), IA)
        var ia = InitialPayload ?? [];
        int padLen = Random.Shared.Next(MaxPaddingLength + 1);

        // Pre-calculate sizes: VC(8) + provide(4) + padLen(2) + pad + iaLen(2) + ia
        int encryptedSize = 8 + 4 + 2 + padLen + 2 + ia.Length;
        // Total message: req1Hash(20) + verification(20) + encrypted
        int totalSize = 20 + 20 + encryptedSize;

        byte[] msg = new byte[totalSize];
        int offset = 0;

        // Copy req1Hash
        req1Hash.AsSpan().CopyTo(msg.AsSpan(offset));
        offset += 20;

        // Copy verification
        verification.CopyTo(msg.AsSpan(offset));
        offset += 20;

        // Build encrypted part in-place
        int encStart = offset;
        // VC = 8 zero bytes (already zeroed)
        offset += 8;

        // crypto_provide = 0x02 (RC4) in big-endian
        msg[offset + 3] = 2;
        offset += 4;

        // len(PadC) in big-endian
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(offset), (ushort)padLen);
        offset += 2;

        // PadC (random padding)
        if (padLen > 0)
        {
            Random.Shared.NextBytes(msg.AsSpan(offset, padLen));
            offset += padLen;
        }

        // len(IA) in big-endian
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(offset), (ushort)ia.Length);
        offset += 2;

        // IA (Initial Application payload)
        ia.AsSpan().CopyTo(msg.AsSpan(offset));

        // Encrypt the encrypted portion
        Encryption!.Encrypt(msg, encStart, encryptedSize);

        return msg;
    }

    private void InitRC4(bool initiator, byte[] infoHash)
    {
        byte[] keyA = Sha1("keyA"u8.ToArray(), _dh.GetSharedSecretBytes(), infoHash);
        byte[] keyB = Sha1("keyB"u8.ToArray(), _dh.GetSharedSecretBytes(), infoHash);

        if (initiator)
        {
            Encryption!.RC4Out.Init(keyA);
            Encryption.RC4In.Init(keyB);
        }
        else
        {
            Encryption!.RC4Out.Init(keyB);
            Encryption.RC4In.Init(keyA);
        }

        Encryption.RC4Out.Skip(1024);
        Encryption.RC4In.Skip(1024);
    }

    private byte[] ProcessPe3()
    {
        // Find Hash("req1", S)
        byte[] req1Hash = Sha1("req1"u8, _dh.GetSharedSecretBytes());

        int foundAt = -1;
        for (int i = 0; i <= _bufferCount - 20; i++)
        {
            if (_buffer!.AsSpan(i, 20).SequenceEqual(req1Hash))
            {
                foundAt = i;
                break;
            }
        }

        if (foundAt == -1)
        {
            return []; // Need more data?
        }

        // Check if we have enough data for synchronization and the first encrypted block
        // req1Hash (20) + verification (20) + VC (8) + crypto_provide (4) + len(PadC) (2) = 54
        if (_bufferCount < foundAt + 54)
        {
            return [];
        }

        // Found synchronization
        // Read verification (req2Hash ^ req3Hash)
        byte[] hash2 = new byte[20];
        Array.Copy(_buffer!, foundAt + 20, hash2, 0, 20);

        byte[] req3Hash = Sha1("req3"u8, _dh.GetSharedSecretBytes());
        byte[]? matchedHash = _infoHash;

        if (matchedHash == null && _resolver != null)
        {
            foreach (var torrent in _resolver.GetTorrents())
            {
                var candidate = torrent.Hash.ToArray();
                var candidateReq2 = Sha1("req2"u8, candidate);

                bool match = true;
                for (int i = 0; i < 20; i++)
                {
                    if ((hash2[i] ^ req3Hash[i]) != candidateReq2[i])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    matchedHash = candidate;
                    break;
                }
            }
        }

        if (matchedHash == null)
        {
            _step = Step.Error;
            return [];
        }

        // Initialize RC4 if we just identified the hash (Receiver mode)
        if (_infoHash == null)
        {
            InitRC4(false, matchedHash);
        }
        else
        {
            // Verify against known hash if we had one
            var expectedReq2 = Sha1("req2"u8, matchedHash);
            for (int i = 0; i < 20; i++)
            {
                if ((hash2[i] ^ req3Hash[i]) != expectedReq2[i])
                {
                    _step = Step.Error;
                    return [];
                }
            }
        }

        MatchedInfoHash = matchedHash;

        // Buffer contains: [PadA...][Hash1][Hash2][Encrypted...]
        int encStart = foundAt + 40;

        // Save RC4 state for potential rollback
        var rc4Snapshot = Encryption!.RC4In.Clone();

        // 1. Decrypt VC(8) + crypto_provide(4) + len(PadC)(2) = 14 bytes
        byte[] header = new byte[14];
        Array.Copy(_buffer!, encStart, header, 0, 14);
        Encryption.RC4In.Decrypt(header);

        // Check VC (8 bytes of 0)
        for (int i = 0; i < 8; i++)
        {
            if (header[i] != 0)
            {
                _step = Step.Error;
                return [];
            }
        }

        int padLen = (header[12] << 8) | header[13];
        if (padLen > MaxPaddingLength)
        {
            _step = Step.Error;
            throw new InvalidDataException($"Padding length {padLen} exceeds MSE spec limit of {MaxPaddingLength}");
        }

        // 2. Ensure we have PadC + len(IA)(2)
        if (_bufferCount < encStart + 14 + padLen + 2)
        {
            Encryption.RC4In.Restore(rc4Snapshot);
            return [];
        }

        // Decrypt PadC (skip it)
        if (padLen > 0)
        {
            Encryption.RC4In.Skip(padLen);
        }

        // 3. Decrypt len(IA)
        byte[] iaLenBytes = new byte[2];
        Array.Copy(_buffer!, encStart + 14 + padLen, iaLenBytes, 0, 2);
        Encryption.RC4In.Decrypt(iaLenBytes);
        int iaLen = (iaLenBytes[0] << 8) | iaLenBytes[1];

        if (iaLen > MaxInitialPayloadLength)
        {
            _step = Step.Error;
            throw new InvalidDataException($"Initial payload length {iaLen} exceeds limit of {MaxInitialPayloadLength}");
        }

        // 4. Ensure full IA present
        if (_bufferCount < encStart + 14 + padLen + 2 + iaLen)
        {
            Encryption.RC4In.Restore(rc4Snapshot);
            return [];
        }

        // Decrypt IA
        ReceivedPayload = new byte[iaLen];
        Array.Copy(_buffer!, encStart + 14 + padLen + 2, ReceivedPayload, 0, iaLen);
        Encryption.RC4In.Decrypt(ReceivedPayload);

        _step = Step.Established;

        // Consume used data from buffer
        ConsumeBuffer(encStart + 14 + padLen + 2 + iaLen);

        // Create Pe4 Response: ENCRYPT(VC, crypto_select, len(padD), padD)
        int padDLen = Random.Shared.Next(MaxPaddingLength + 1);
        int respSize = 8 + 4 + 2 + padDLen;
        byte[] respBytes = new byte[respSize];

        // crypto_select = 0x02 (RC4)
        respBytes[11] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(respBytes.AsSpan(12), (ushort)padDLen);
        if (padDLen > 0)
        {
            Random.Shared.NextBytes(respBytes.AsSpan(14, padDLen));
        }

        Encryption.RC4Out.Encrypt(respBytes);

        return respBytes;
    }

    private byte[] ProcessPe4()
    {
        // Initiator expects Pe4: ENCRYPT(VC, crypto_select, len(padD), padD)
        var rc4Snapshot = Encryption!.RC4In.Clone();

        int foundAt = -1;
        int padLen = 0;
        for (int i = 0; i <= _bufferCount - 14; i++)
        {
            Encryption.RC4In.Restore(rc4Snapshot);

            byte[] vc = new byte[8];
            Array.Copy(_buffer!, i, vc, 0, 8);
            Encryption.RC4In.Decrypt(vc);

            bool isVc = true;
            for (int k = 0; k < 8; k++)
            {
                if (vc[k] != 0) { isVc = false; break; }
            }

            if (isVc)
            {
                // VC found, now check crypto_select(4) and len(padD)(2)
                byte[] header = new byte[6];
                Array.Copy(_buffer!, i + 8, header, 0, 6);
                Encryption.RC4In.Decrypt(header);

                padLen = (header[4] << 8) | header[5];
                if (padLen <= MaxPaddingLength && _bufferCount >= i + 14 + padLen)
                {
                    foundAt = i;
                    Encryption.RC4In.Skip(padLen);
                    break;
                }
            }
        }

        if (foundAt == -1)
        {
            Encryption.RC4In.Restore(rc4Snapshot);
            return [];
        }

        _step = Step.Established;
        ConsumeBuffer(foundAt + 14 + padLen);
        return [];
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_buffer == null, nameof(ProtocolEncryptionHandshake));
    }
}
