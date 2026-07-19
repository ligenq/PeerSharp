using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

internal sealed class NullPeerListener : IPeerListener
{
    public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
    public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
    public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
    public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
    public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
    public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
    public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
    public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
}

internal class PolicyTestPeer : PeerCommunication
{
    public PolicyTestPeer(Torrent torrent, TimeProvider? timeProvider = null)
        : base(torrent, new NullPeerListener(), timeProvider ?? TimeProvider.System)
    {
    }

    public void SetSpeed(int speed) => SetSmoothedDownloadSpeedForTesting(speed);
    public void SetInterested(bool interested) => SetPeerInterestedForTesting(interested);
}
