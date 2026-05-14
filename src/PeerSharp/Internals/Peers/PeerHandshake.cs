using System.Text;

namespace PeerSharp.Internals.Peers;

internal enum PeerHandshakeValidationError
{
    None,
    TooShort,
    InvalidProtocolLength,
    InvalidProtocolString,
    InfoHashMismatch
}

internal readonly record struct PeerHandshakeResult(
    byte[] PeerId,
    bool SupportsExtensions,
    bool SupportsFastExtension,
    bool SupportsV2,
    PeerHandshakeValidationError Error);

internal static class PeerHandshake
{
    private const int HandshakeLength = 68;
    private const int ProtocolLength = 19;
    private const string ProtocolName = "BitTorrent protocol";

    public static byte[] Create(TorrentFileInfo info, byte[] peerId)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(peerId);
        if (peerId.Length < InfoHash.V1Length)
        {
            throw new ArgumentException("Peer ID must be at least 20 bytes.", nameof(peerId));
        }

        byte[] handshake = new byte[HandshakeLength];
        handshake[0] = ProtocolLength;
        Encoding.ASCII.GetBytes(ProtocolName).CopyTo(handshake, 1);

        handshake[25] |= 0x10; // BEP-10 extension protocol
        handshake[27] |= 0x01; // BEP-5 DHT
        handshake[27] |= 0x04; // BEP-6 fast extension
        if (info.IsV2)
        {
            handshake[27] |= 0x10; // BEP-52 v2 protocol
        }

        if (info.IsV1)
        {
            info.Hash.CopyTo(handshake, 28);
        }
        else if (info.IsV2)
        {
            info.HashV2.Span[..InfoHash.V1Length].CopyTo(handshake.AsSpan(28));
        }

        peerId.AsSpan(0, InfoHash.V1Length).CopyTo(handshake.AsSpan(48));
        return handshake;
    }

    public static bool TryParse(byte[] handshake, TorrentFileInfo info, out PeerHandshakeResult result)
    {
        ArgumentNullException.ThrowIfNull(handshake);
        ArgumentNullException.ThrowIfNull(info);

        if (handshake.Length < HandshakeLength)
        {
            result = new PeerHandshakeResult([], false, false, false, PeerHandshakeValidationError.TooShort);
            return false;
        }

        if (handshake[0] != ProtocolLength)
        {
            result = new PeerHandshakeResult([], false, false, false, PeerHandshakeValidationError.InvalidProtocolLength);
            return false;
        }

        if (!handshake.AsSpan(1, ProtocolLength).SequenceEqual(ProtocolNameBytes))
        {
            result = new PeerHandshakeResult([], false, false, false, PeerHandshakeValidationError.InvalidProtocolString);
            return false;
        }

        var receivedInfoHash = handshake.AsSpan(28, InfoHash.V1Length);
        bool hashMatches = info.IsV1 && receivedInfoHash.SequenceEqual(info.Hash.Span);
        if (!hashMatches && info.IsV2)
        {
            hashMatches = receivedInfoHash.SequenceEqual(info.HashV2.Span[..InfoHash.V1Length]);
        }

        if (!hashMatches)
        {
            result = new PeerHandshakeResult([], false, false, false, PeerHandshakeValidationError.InfoHashMismatch);
            return false;
        }

        result = new PeerHandshakeResult(
            handshake.AsSpan(48, InfoHash.V1Length).ToArray(),
            SupportsExtensions: (handshake[25] & 0x10) != 0,
            SupportsFastExtension: (handshake[27] & 0x04) != 0,
            SupportsV2: (handshake[27] & 0x10) != 0,
            Error: PeerHandshakeValidationError.None);
        return true;
    }

    private static ReadOnlySpan<byte> ProtocolNameBytes => "BitTorrent protocol"u8;
}
