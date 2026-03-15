using System.Buffers;
using System.Buffers.Binary;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Peers;

/// <summary>
/// Stateless BitTorrent peer protocol message encoder/decoder.
/// </summary>
internal static class PeerProtocol
{
    public const int MaxMessageSize = 2 * 1024 * 1024; // 2MB limit

    public static int GetMessageLength(PeerMessage msg)
    {
        return 4 + GetPayloadLength(msg);
    }

    /// <summary>
    /// Attempts to decode a message from the buffer.
    /// On success, advances the buffer past the consumed bytes.
    /// </summary>
    public static bool TryDecodeMessage(ref ReadOnlySequence<byte> buffer, out PeerMessage? message, out int bytesConsumed)
    {
        message = null;
        bytesConsumed = 0;

        if (buffer.Length < 4)
        {
            return false;
        }

        Span<byte> lenBytes = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(lenBytes);
        int length = BinaryPrimitives.ReadInt32BigEndian(lenBytes);

        if (length < 0)
        {
            throw new InvalidDataException($"Invalid negative message length: {length}");
        }

        if (length > MaxMessageSize)
        {
            throw new InvalidDataException($"Message size {length} exceeds limit {MaxMessageSize}");
        }

        if (buffer.Length < 4 + length)
        {
            return false;
        }

        bytesConsumed = 4 + length;

        if (length == 0)
        {
            // KeepAlive - message remains null
            buffer = buffer.Slice(bytesConsumed);
            return true;
        }

        var messageBuffer = buffer.Slice(4, length);
        byte id = messageBuffer.FirstSpan[0];
        message = new PeerMessage((MessageId)id);

        switch (message.Id)
        {
            case MessageId.Have:
                if (length < 5)
                {
                    throw new InvalidDataException($"Have message too short: {length} < 5");
                }
                message.HavePieceIndex = ReadInt(messageBuffer, 1);
                break;

            case MessageId.Suggest:
            case MessageId.AllowedFast:
                if (length < 5)
                {
                    throw new InvalidDataException($"{message.Id} message too short: {length} < 5");
                }
                message.PieceIndex = ReadInt(messageBuffer, 1);
                break;

            case MessageId.Bitfield:
                message.Data = messageBuffer.Slice(1).ToArray();
                break;

            case MessageId.Piece:
                if (length < 9)
                {
                    throw new InvalidDataException($"Piece message too short: {length} < 9");
                }
                message.PieceIndex = ReadInt(messageBuffer, 1);
                message.BlockOffset = ReadInt(messageBuffer, 5);
                var payload = messageBuffer.Slice(9);
                var block = new Block(message.PieceIndex, message.BlockOffset, (int)payload.Length);
                payload.CopyTo(block.Buffer);
                message.PooledBlock = block;
                break;

            case MessageId.Request:
            case MessageId.Cancel:
            case MessageId.Reject:
                if (length < 13)
                {
                    throw new InvalidDataException($"{message.Id} message too short: {length} < 13");
                }
                message.PieceIndex = ReadInt(messageBuffer, 1);
                message.BlockOffset = ReadInt(messageBuffer, 5);
                message.BlockLength = ReadInt(messageBuffer, 9);
                break;

            case MessageId.Extended:
                // Extended message payload starts after the message ID byte
                message.Data = messageBuffer.Slice(1).ToArray();
                break;

            case MessageId.Port:
                // BEP 5: Port message contains 2-byte port number for DHT
                if (length < 3)
                {
                    throw new InvalidDataException($"Port message too short: {length} < 3");
                }
                message.Port = ReadUShort(messageBuffer, 1);
                break;
        }

        buffer = buffer.Slice(bytesConsumed);
        return true;
    }

    public static int WriteMessage(PeerMessage msg, Span<byte> destination)
    {
        int payloadLen = GetPayloadLength(msg);
        int totalLen = 4 + payloadLen;

        if (destination.Length < totalLen)
        {
            throw new ArgumentException($"Destination buffer too small: need {totalLen}, have {destination.Length}");
        }

        BinaryPrimitives.WriteInt32BigEndian(destination, payloadLen);

        if (payloadLen == 0)
        {
            return 4; // KeepAlive
        }

        destination[4] = (byte)msg.Id;

        switch (msg.Id)
        {
            case MessageId.Have:
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(5), msg.HavePieceIndex);
                break;

            case MessageId.Suggest:
            case MessageId.AllowedFast:
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(5), msg.PieceIndex);
                break;

            case MessageId.Request:
            case MessageId.Cancel:
            case MessageId.Reject:
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(5), msg.PieceIndex);
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(9), msg.BlockOffset);
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(13), msg.BlockLength);
                break;

            case MessageId.Piece:
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(5), msg.PieceIndex);
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(9), msg.BlockOffset);
                if (msg.PooledBlock != null)
                {
                    msg.PooledBlock.Data.Span.CopyTo(destination.Slice(13));
                }
                else
                {
                    msg.Data?.CopyTo(destination.Slice(13));
                }
                break;

            case MessageId.Bitfield:
            case MessageId.Extended:
                msg.Data?.CopyTo(destination.Slice(5));
                break;

            case MessageId.Port:
                // BEP 5: Port message contains 2-byte port number for DHT
                BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(5), msg.Port);
                break;
        }

        return totalLen;
    }

    private static int GetPayloadLength(PeerMessage msg)
    {
        if (msg.Id == MessageId.KeepAlive)
        {
            return 0;
        }

        return msg.Id switch
        {
            MessageId.Choke or MessageId.Unchoke or
            MessageId.Interested or MessageId.NotInterested or
            MessageId.HaveAll or MessageId.HaveNone => 1,

            MessageId.Have or MessageId.Suggest or MessageId.AllowedFast => 5,

            // BEP 5: Port message is 1 byte ID + 2 bytes port = 3 bytes payload
            MessageId.Port => 3,

            MessageId.Request or MessageId.Cancel or MessageId.Reject => 13,

            MessageId.Bitfield => 1 + (msg.Data?.Length ?? 0),

            MessageId.Piece => 9 + (msg.PooledBlock?.Length ?? msg.Data?.Length ?? 0),

            MessageId.Extended => 1 + (msg.Data?.Length ?? 0),

            _ => 1 // Unknown message types: just the ID byte
        };
    }

    private static int ReadInt(ReadOnlySequence<byte> seq, long offset)
    {
        Span<byte> tmp = stackalloc byte[4];
        seq.Slice(offset, 4).CopyTo(tmp);
        return BinaryPrimitives.ReadInt32BigEndian(tmp);
    }

    private static ushort ReadUShort(ReadOnlySequence<byte> seq, long offset)
    {
        Span<byte> tmp = stackalloc byte[2];
        seq.Slice(offset, 2).CopyTo(tmp);
        return BinaryPrimitives.ReadUInt16BigEndian(tmp);
    }
}

