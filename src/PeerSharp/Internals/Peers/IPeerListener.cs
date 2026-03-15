using PeerSharp.Internals.Extensions;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Peers;

internal interface IPeerListener
{
    Task ConnectionClosedAsync(IPeerCommunication peer, int code);

    // Extended Protocol hooks
    Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake);

    Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data);

    Task HandshakeFinishedAsync(IPeerCommunication peer);

    Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, System.Net.IPEndPoint endpoint, UtHolepunch.ErrorCode error);

    Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg);

    Task PexReceivedAsync(IPeerCommunication peer, List<System.Net.IPEndPoint> added, List<byte> addedFlags, List<System.Net.IPEndPoint> dropped);

    /// <summary>
    /// BEP 5: Called when a peer sends a Port message advertising their DHT UDP port.
    /// </summary>
    Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort);
}
