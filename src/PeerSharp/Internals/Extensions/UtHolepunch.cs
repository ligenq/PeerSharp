using System.Buffers.Binary;
using System.Net;
using PeerSharp.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeerSharp.Internals.Extensions;

internal class UtHolepunch : IUtHolepunch, IDisposable
{
    public const string Name = "ut_holepunch";

    private readonly IPeerCommunication _peer;
    private readonly ILogger<UtHolepunch> _logger;

    private AtomicDisposal _disposal = new();

    public UtHolepunch(IPeerCommunication peer) : this(peer, NullLoggerFactory.Instance)
    {
    }

    public UtHolepunch(IPeerCommunication peer, ILoggerFactory loggerFactory)
    {
        _peer = peer;
        _logger = loggerFactory.CreateLogger<UtHolepunch>();
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

        if (_disposal.IsDisposed || data.Length < 2 || data[0] > (byte)MsgId.Error || data[1] > 1)
        {
            return;
        }

        MsgId id = (MsgId)data[0];
        bool ipv6 = data[1] == 1;
        int addrLen = ipv6 ? 16 : 4;

        int endpointLength = 2 + addrLen + 2;
        // Older implementations omit the zero error code on non-error messages.
        if (id == MsgId.Error ? data.Length != endpointLength + 4
            : data.Length != endpointLength && data.Length != endpointLength + 4)
        {
            return;
        }

        var ipSpan = new ReadOnlySpan<byte>(data, 2, addrLen);
        var ip = new IPAddress(ipSpan.ToArray());
        var port = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(data, 2 + addrLen, 2));
        if (port == 0) return;
        var endpoint = new IPEndPoint(ip, port);

        ErrorCode error = ErrorCode.None;
        if (data.Length == endpointLength + 4)
        {
            error = (ErrorCode)BinaryPrimitives.ReadInt32BigEndian(new ReadOnlySpan<byte>(data, 2 + addrLen + 2, 4));
            if (id != MsgId.Error && error != ErrorCode.None) return;
        }

        // Notify listener
        await _peer.Listener.HolepunchMessageReceivedAsync(_peer, id, endpoint, error).ConfigureAwait(false);
    }

    public void Init(ExtensionHandshake handshake)
    {
        if (handshake.MessageIds.ContainsKey(Name))
        {
            RemoteMessageId = handshake.GetEnabledMessageId(Name);
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
        if (_disposal.IsDisposed || !RemoteMessageId.HasValue)
        {
            return;
        }

        bool ipv6 = endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        byte[] addrBytes = endpoint.Address.GetAddressBytes();

        // Length: 1 (ExtId) + 1 (MsgId) + 1 (Type: 0=ipv4, 1=ipv6) + AddrLen + 2 (Port) + [4 Error]
        int packetLen = 1 + 1 + 1 + addrBytes.Length + 2 + 4;

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

        _ = SendSafeAsync(msg); // fire-and-forget: the helper catches and logs send failures.
    }

    private async Task SendSafeAsync(PeerMessage message)
    {
        try { await _peer.SendMessageAsync(message).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Holepunch send failed"); }
    }
}
