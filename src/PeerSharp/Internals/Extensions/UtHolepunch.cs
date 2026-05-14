using System.Buffers.Binary;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Extensions;

internal class UtHolepunch : IUtHolepunch, IDisposable
{
    public const string Name = "ut_holepunch";

    private readonly IPeerCommunication _peer;

    private AtomicDisposal _disposal = new();

    public UtHolepunch(IPeerCommunication peer)
    {
        _peer = peer;
    }

    internal enum ErrorCode
    {
        None = 0,
        NoSuchPeer = 1,
        NotConnected = 2,
        NoSupport = 3,
        NoSelf = 4
    }

    internal enum MsgId
    {
        Rendezvous = 0,
        Connect = 1,
        Error = 2
    }

    public int? LocalMessageId { get; private set; }
    public int? RemoteMessageId { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async Task HandleMessageAsync(byte[] data)
    {
        // Data starts after the ExtId (which was consumed by PeerCommunication)
        // Format: [MsgId][Type][Addr...][Port][Error?]

        if (data.Length < 2)
        {
            return;
        }

        MsgId id = (MsgId)data[0];
        bool ipv6 = data[1] == 1;
        int addrLen = ipv6 ? 16 : 4;

        if (data.Length < 2 + addrLen + 2)
        {
            return;
        }

        var ipSpan = new ReadOnlySpan<byte>(data, 2, addrLen);
        var ip = new IPAddress(ipSpan.ToArray());
        var port = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(data, 2 + addrLen, 2));
        var endpoint = new IPEndPoint(ip, port);

        ErrorCode error = ErrorCode.None;
        if (id == MsgId.Error && data.Length >= 2 + addrLen + 2 + 4)
        {
            error = (ErrorCode)BinaryPrimitives.ReadInt32BigEndian(new ReadOnlySpan<byte>(data, 2 + addrLen + 2, 4));
        }

        // Notify listener
        await _peer.Listener.HolepunchMessageReceivedAsync(_peer, id, endpoint, error).ConfigureAwait(false);
    }

    public void Init(ExtensionHandshake handshake)
    {
        if (handshake.MessageIds.TryGetValue(Name, out int id))
        {
            RemoteMessageId = id;
        }
    }

    public void SetLocalMessageId(int id)
    {
        LocalMessageId = id;
    }

    public void SendConnect(IPEndPoint endpoint)
    {
        Send(MsgId.Connect, endpoint, ErrorCode.None);
    }

    public void SendError(IPEndPoint endpoint, ErrorCode error)
    {
        Send(MsgId.Error, endpoint, error);
    }

    public void SendRendezvous(IPEndPoint endpoint)
    {
        Send(MsgId.Rendezvous, endpoint, ErrorCode.None);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            // No disposable resources currently
        }
    }

    private void Send(MsgId id, IPEndPoint endpoint, ErrorCode error)
    {
        if (!RemoteMessageId.HasValue)
        {
            return;
        }

        bool ipv6 = endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        byte[] addrBytes = endpoint.Address.GetAddressBytes();

        // Length: 1 (ExtId) + 1 (MsgId) + 1 (Type: 0=ipv4, 1=ipv6) + AddrLen + 2 (Port) + [4 Error]
        int packetLen = 1 + 1 + 1 + addrBytes.Length + 2;
        if (id == MsgId.Error)
        {
            packetLen += 4;
        }

        var msg = new PeerMessage(MessageId.Extended)
        {
            Data = new byte[packetLen]
        };

        var span = msg.Data.AsSpan();
        span[0] = (byte)RemoteMessageId.Value;
        span[1] = (byte)id;
        span[2] = (byte)(ipv6 ? 1 : 0);

        addrBytes.CopyTo(span[3..]);
        BinaryPrimitives.WriteUInt16BigEndian(span[(3 + addrBytes.Length)..], (ushort)endpoint.Port);

        if (id == MsgId.Error)
        {
            BinaryPrimitives.WriteInt32BigEndian(span[(3 + addrBytes.Length + 2)..], (int)error);
        }

        _ = _peer.SendMessageAsync(msg);
    }
}
