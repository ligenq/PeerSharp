using PeerSharp.Internals;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class PeerHandshakeTests
{
    [Fact]
    public void Create_V1Handshake_UsesV1HashAndAdvertisesBaseCapabilities()
    {
        var info = CreateInfo(TorrentVersion.V1);
        byte[] peerId = Enumerable.Range(0, 20).Select(i => (byte)(50 + i)).ToArray();

        byte[] handshake = PeerHandshake.Create(info, peerId);

        Assert.Equal(68, handshake.Length);
        Assert.Equal(19, handshake[0]);
        Assert.Equal("BitTorrent protocol"u8.ToArray(), handshake.AsSpan(1, 19).ToArray());
        Assert.True((handshake[25] & 0x10) != 0);
        Assert.True((handshake[27] & 0x04) != 0);
        Assert.False((handshake[27] & 0x10) != 0);
        Assert.Equal(info.Hash.ToArray(), handshake.AsSpan(28, 20).ToArray());
        Assert.Equal(peerId, handshake.AsSpan(48, 20).ToArray());
    }

    [Fact]
    public void Create_V2OnlyHandshake_UsesTruncatedV2HashAndAdvertisesV2()
    {
        var info = CreateInfo(TorrentVersion.V2);
        byte[] peerId = Enumerable.Range(0, 20).Select(i => (byte)(80 + i)).ToArray();

        byte[] handshake = PeerHandshake.Create(info, peerId);

        Assert.True((handshake[27] & 0x10) != 0);
        Assert.Equal(info.HashV2.Span[..20].ToArray(), handshake.AsSpan(28, 20).ToArray());
    }

    [Fact]
    public void Create_HybridHandshake_PrefersV1HashForCompatibility()
    {
        var info = CreateInfo(TorrentVersion.Hybrid);
        byte[] peerId = Enumerable.Range(0, 20).Select(i => (byte)(100 + i)).ToArray();

        byte[] handshake = PeerHandshake.Create(info, peerId);

        Assert.True((handshake[27] & 0x10) != 0);
        Assert.Equal(info.Hash.ToArray(), handshake.AsSpan(28, 20).ToArray());
    }

    [Fact]
    public void Create_SetsDhtBit_InReservedByte7()
    {
        var info = CreateInfo(TorrentVersion.V1);
        byte[] handshake = PeerHandshake.Create(info, new byte[20]);
        Assert.True((handshake[27] & 0x01) != 0);
    }

    [Fact]
    public void TryParse_ValidHandshake_ReturnsCapabilitiesAndPeerId()
    {
        var info = CreateInfo(TorrentVersion.Hybrid);
        byte[] peerId = Enumerable.Range(0, 20).Select(i => (byte)(120 + i)).ToArray();
        byte[] handshake = PeerHandshake.Create(info, peerId);

        bool ok = PeerHandshake.TryParse(handshake, info, out var result);

        Assert.True(ok);
        Assert.Equal(PeerHandshakeValidationError.None, result.Error);
        Assert.True(result.SupportsExtensions);
        Assert.True(result.SupportsFastExtension);
        Assert.True(result.SupportsV2);
        Assert.Equal(peerId, result.PeerId);
    }

    [Fact]
    public void TryParse_V2OnlyAcceptsTruncatedV2Hash()
    {
        var info = CreateInfo(TorrentVersion.V2);
        byte[] handshake = PeerHandshake.Create(info, new byte[20]);

        bool ok = PeerHandshake.TryParse(handshake, info, out var result);

        Assert.True(ok);
        Assert.True(result.SupportsV2);
    }

    [Theory]
    [InlineData(0, (int)PeerHandshakeValidationError.InvalidProtocolLength)]
    [InlineData(1, (int)PeerHandshakeValidationError.InvalidProtocolString)]
    [InlineData(28, (int)PeerHandshakeValidationError.InfoHashMismatch)]
    public void TryParse_InvalidHandshake_ReturnsReason(int byteToCorrupt, int expected)
    {
        var info = CreateInfo(TorrentVersion.V1);
        byte[] handshake = PeerHandshake.Create(info, new byte[20]);
        handshake[byteToCorrupt] ^= 0xFF;

        bool ok = PeerHandshake.TryParse(handshake, info, out var result);

        Assert.False(ok);
        Assert.Equal((PeerHandshakeValidationError)expected, result.Error);
    }

    [Fact]
    public void TryParse_TooShort_ReturnsReason()
    {
        bool ok = PeerHandshake.TryParse(new byte[67], CreateInfo(TorrentVersion.V1), out var result);

        Assert.False(ok);
        Assert.Equal(PeerHandshakeValidationError.TooShort, result.Error);
    }

    private static Internals.TorrentFileInfo CreateInfo(TorrentVersion version)
    {
        return new Internals.TorrentFileInfo
        {
            Version = version,
            Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray()),
            HashV2 = new InfoHash(Enumerable.Range(0, 32).Select(i => (byte)(200 - i)).ToArray()),
            PieceSize = ProtocolConstants.BlockSize,
            FullSize = ProtocolConstants.BlockSize
        };
    }
}
