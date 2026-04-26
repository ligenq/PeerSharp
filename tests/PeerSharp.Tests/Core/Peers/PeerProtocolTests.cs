using PeerSharp.Internals.Peers;
using System.Buffers;
using System.Buffers.Binary;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Peers;

public class PeerProtocolTests
{
    [Fact]
    public void TryDecodeMessage_KeepAlive_ReturnsTrueAndNullMessage()
    {
        // Arrange
        byte[] data = new byte[] { 0, 0, 0, 0 };
        var buffer = new ReadOnlySequence<byte>(data);

        // Act
        bool result = PeerProtocol.TryDecodeMessage(ref buffer, out var message, out int bytesConsumed);

        // Assert
        Assert.True(result);
        Assert.Null(message);
        Assert.Equal(4, bytesConsumed);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryDecodeMessage_HaveMessage_DecodesCorrectly()
    {
        // Arrange
        byte[] data = new byte[9];
        BinaryPrimitives.WriteInt32BigEndian(data, 5); // Length
        data[4] = (byte)MessageId.Have;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(5), 1234); // PieceIndex
        var buffer = new ReadOnlySequence<byte>(data);

        // Act
        bool result = PeerProtocol.TryDecodeMessage(ref buffer, out var message, out int bytesConsumed);

        // Assert
        Assert.True(result);
        Assert.NotNull(message);
        Assert.Equal(MessageId.Have, message.Id);
        Assert.Equal(1234, message.HavePieceIndex);
        Assert.Equal(9, bytesConsumed);
    }

    [Fact]
    public void TryDecodeMessage_IncompleteBuffer_ReturnsFalse()
    {
        // Arrange
        byte[] data = new byte[] { 0, 0, 0, 5, (byte)MessageId.Have }; // Missing 4 bytes of payload
        var buffer = new ReadOnlySequence<byte>(data);

        // Act
        bool result = PeerProtocol.TryDecodeMessage(ref buffer, out var message, out int bytesConsumed);

        // Assert
        Assert.False(result);
        Assert.Null(message);
        Assert.Equal(0, bytesConsumed);
    }

    [Fact]
    public void WriteMessage_HaveMessage_EncodesCorrectly()
    {
        // Arrange
        var msg = new PeerMessage(MessageId.Have) { HavePieceIndex = 1234 };
        byte[] destination = new byte[PeerProtocol.GetMessageLength(msg)];

        // Act
        int written = PeerProtocol.WriteMessage(msg, destination);

        // Assert
        Assert.Equal(9, written);
        Assert.Equal(5, BinaryPrimitives.ReadInt32BigEndian(destination));
        Assert.Equal((byte)MessageId.Have, destination[4]);
        Assert.Equal(1234, BinaryPrimitives.ReadInt32BigEndian(destination.AsSpan(5)));
    }

    [Fact]
    public void TryDecodeMessage_PieceMessage_DecodesCorrectly()
    {
        // Arrange
        int payloadLen = 9 + 10; // 9 bytes header + 10 bytes data
        byte[] data = new byte[4 + payloadLen];
        BinaryPrimitives.WriteInt32BigEndian(data, payloadLen);
        data[4] = (byte)MessageId.Piece;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(5), 1); // PieceIndex
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(9), 16384); // BlockOffset
        for (int i = 0; i < 10; i++)
        {
            data[13 + i] = (byte)i;
        }

        var buffer = new ReadOnlySequence<byte>(data);

        // Act
        bool result = PeerProtocol.TryDecodeMessage(ref buffer, out var message, out int bytesConsumed);

        // Assert
        Assert.True(result);
        Assert.NotNull(message);
        Assert.Equal(MessageId.Piece, message.Id);
        Assert.Equal(1, message.PieceIndex);
        Assert.Equal(16384, message.BlockOffset);
        Assert.NotNull(message.PooledBlock);
        Assert.Equal(10, message.PooledBlock.Length);
        Assert.Equal(data.AsSpan(13, 10).ToArray(), message.PooledBlock.Data.ToArray());
    }

    [Fact]
    public void WriteMessage_Bitfield_EncodesCorrectly()
    {
        // Arrange
        byte[] bitfield = new byte[] { 0xFF, 0x00, 0xAA };
        var msg = new PeerMessage(MessageId.Bitfield) { Data = bitfield };
        byte[] destination = new byte[PeerProtocol.GetMessageLength(msg)];

        // Act
        int written = PeerProtocol.WriteMessage(msg, destination);

        // Assert
        Assert.Equal(4 + 1 + 3, written);
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(destination));
        Assert.Equal((byte)MessageId.Bitfield, destination[4]);
        Assert.Equal(bitfield, destination.AsSpan(5).ToArray());
    }

    [Fact]
    public void TryDecodeMessage_PortMessage_DecodesCorrectly()
    {
        // Arrange
        byte[] data = new byte[7];
        BinaryPrimitives.WriteInt32BigEndian(data, 3);
        data[4] = (byte)MessageId.Port;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(5), 6881);
        var buffer = new ReadOnlySequence<byte>(data);

        // Act
        bool result = PeerProtocol.TryDecodeMessage(ref buffer, out var message, out int bytesConsumed);

        // Assert
        Assert.True(result);
        Assert.NotNull(message);
        Assert.Equal(MessageId.Port, message.Id);
        Assert.Equal(6881, message.Port);
    }

    [Fact]
    public void WriteAndDecode_HashRequest_RoundTrips()
    {
        byte[] root = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var msg = new PeerMessage(MessageId.HashRequest)
        {
            HashPiecesRoot = root,
            HashBaseLayer = 4,
            HashIndex = 8,
            HashLength = 16,
            HashProofLayers = 2
        };
        byte[] destination = new byte[PeerProtocol.GetMessageLength(msg)];

        int written = PeerProtocol.WriteMessage(msg, destination);
        var buffer = new ReadOnlySequence<byte>(destination);
        bool decoded = PeerProtocol.TryDecodeMessage(ref buffer, out var parsed, out int consumed);

        Assert.Equal(53, written);
        Assert.True(decoded);
        Assert.Equal(written, consumed);
        Assert.NotNull(parsed);
        Assert.Equal(MessageId.HashRequest, parsed.Id);
        Assert.Equal(root, parsed.HashPiecesRoot);
        Assert.Equal(4, parsed.HashBaseLayer);
        Assert.Equal(8, parsed.HashIndex);
        Assert.Equal(16, parsed.HashLength);
        Assert.Equal(2, parsed.HashProofLayers);
    }

    [Fact]
    public void WriteAndDecode_Hashes_RoundTripsHashPayload()
    {
        byte[] root = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] hashes = Enumerable.Range(0, 64).Select(i => (byte)(255 - i)).ToArray();
        var msg = new PeerMessage(MessageId.Hashes)
        {
            HashPiecesRoot = root,
            HashBaseLayer = 0,
            HashIndex = 0,
            HashLength = 2,
            HashProofLayers = 0,
            Data = hashes
        };
        byte[] destination = new byte[PeerProtocol.GetMessageLength(msg)];

        int written = PeerProtocol.WriteMessage(msg, destination);
        var buffer = new ReadOnlySequence<byte>(destination);
        bool decoded = PeerProtocol.TryDecodeMessage(ref buffer, out var parsed, out _);

        Assert.Equal(117, written);
        Assert.True(decoded);
        Assert.NotNull(parsed);
        Assert.Equal(MessageId.Hashes, parsed.Id);
        Assert.Equal(root, parsed.HashPiecesRoot);
        Assert.Equal(hashes, parsed.Data);
    }
}






