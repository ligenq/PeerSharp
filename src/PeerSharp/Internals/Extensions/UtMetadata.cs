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
        if (handshake.MessageIds.ContainsKey(Name))
        {
            RemoteMessageId = handshake.GetEnabledMessageId(Name);
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

        SendMessage(dict, data, RemoteMessageId.Value);
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

        SendMessage(dict, null, RemoteMessageId.Value);
    }

    /// <summary>
    /// Asks a peer for one piece of the metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sent under the id the peer advertised for ut_metadata, and only that one. BEP 10 numbers
    /// extensions per receiver: the id in an outgoing extended message is the one the peer chose for
    /// itself, and this client's own numbering means nothing on the wire.
    /// </para>
    /// <para>
    /// This used to send the request a second time under the local id as well, as a fallback "for
    /// peers that ignore our extension mapping". There is no such peer, and the copy was addressed to
    /// whichever different extension the receiver had put at that number: PeerSharp offers
    /// ut_metadata as 1 and libtorrent offers it as 2, so every request also arrived at libtorrent's
    /// extension 1 as nonsense. libtorrent offers an extended message to each of its extensions in
    /// turn and disconnects with invalid_message when none of them claims it
    /// (bt_peer_connection.cpp, on_extended), so the copy was at best ignored and at worst fatal.
    /// </para>
    /// <para>
    /// It went unnoticed because two PeerSharp instances agree: both put ut_metadata at 1, the
    /// duplicate is suppressed when the ids match, and nothing was ever sent twice.
    /// </para>
    /// <para>
    /// Removing it is correct on its own terms and is not yet known to be sufficient: a metadata
    /// fetch from libtorrent still ends with the peer closing the connection, for a reason that build
    /// cannot be asked for because it is compiled without logging.
    /// </para>
    /// </remarks>
    public void SendRequest(int piece)
    {
        if (!RemoteMessageId.HasValue)
        {
            // A peer that did not advertise ut_metadata has no id to address, and there is no id we
            // may invent for it.
            return;
        }

        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber((int)MessageType.Request);
        dict.Dict["piece"] = new BNumber(piece);

        SendMessage(dict, null, RemoteMessageId.Value);
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
