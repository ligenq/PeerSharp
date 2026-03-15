using PeerSharp.BEncoding;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Extensions;

internal class UtMetadata : IUtMetadata
{
    public const string Name = "ut_metadata";
    public const int PieceSize = 16 * 1024;

    private readonly IPeerCommunication _peer;

    public UtMetadata(IPeerCommunication peer)
    {
        _peer = peer;
    }

    internal enum MessageType
    {
        Request = 0,
        Data = 1,
        Reject = 2
    }

    public int? LocalMessageId { get; private set; }
    public int? RemoteMessageId { get; private set; }

    public void Init(ExtensionHandshake handshake)
    {
        if (handshake.MessageIds.TryGetValue(Name, out int id))
        {
            RemoteMessageId = id;
        }
    }

    public void SendData(int piece, byte[] data, int totalSize)
    {
        if (!RemoteMessageId.HasValue)
        {
            return;
        }

        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber((int)MessageType.Data);
        dict.Dict["piece"] = new BNumber(piece);
        dict.Dict["total_size"] = new BNumber(totalSize);

        SendMessage(dict, data, RemoteMessageId!.Value);
    }

    public void SendReject(int piece)
    {
        if (!RemoteMessageId.HasValue)
        {
            return;
        }

        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber((int)MessageType.Reject);
        dict.Dict["piece"] = new BNumber(piece);

        SendMessage(dict, null, RemoteMessageId!.Value);
    }

    public void SendRequest(int piece)
    {
        if (!RemoteMessageId.HasValue && !LocalMessageId.HasValue)
        {
            return;
        }

        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber((int)MessageType.Request);
        dict.Dict["piece"] = new BNumber(piece);

        if (RemoteMessageId.HasValue)
        {
            SendMessage(dict, null, RemoteMessageId.Value);
        }
        if (LocalMessageId.HasValue && RemoteMessageId != LocalMessageId)
        {
            // Compatibility fallback for peers that ignore our extension mapping.
            SendMessage(dict, null, LocalMessageId.Value);
        }
    }

    public void SetLocalMessageId(int id)
    {
        LocalMessageId = id;
    }

    private void SendMessage(BDict dict, byte[]? payload, int messageId)
    {
        // We need to serialize BDict manually to bytes
        // Then prepend to payload
        // Then send as Extended Message (ID 20) + ExtMsgID
        using var result = BencodeWriter.WriteToResult(dict);

        var msg = new PeerMessage(MessageId.Extended);
        // Payload = [ExtMsgId][Dict][Data]

        int len = 1 + result.Memory.Length + (payload?.Length ?? 0);
        msg.Data = new byte[len];
        msg.Data[0] = (byte)messageId;
        result.Memory.Span.CopyTo(msg.Data.AsSpan(1));
        if (payload != null)
        {
            Array.Copy(payload, 0, msg.Data, 1 + result.Memory.Length, payload.Length);
        }

        _ = _peer.SendMessageAsync(msg);
    }
}
