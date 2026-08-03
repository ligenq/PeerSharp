using System.Net;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// The snub flag's lifecycle.
///
/// <para>
/// A peer is snubbed when it lets a request expire unanswered, and unsnubbed the moment it sends
/// anything. It is not a punishment - the peer keeps its slot and its other requests. All it changes
/// is which end of the rarest-first ordering the picker offers it, so a stalled peer does not end up
/// holding the one copy of a piece nobody else has.
/// </para>
/// </summary>
public class PeerSnubbingTests
{
    private class MockPeerListener : IPeerListener
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

    private static PeerCommunication CreatePeer()
    {
        var torrent = TorrentTestUtility.CreateMinimal(new TorrentFileMetadata());
        return new PeerCommunication(torrent, new MockPeerListener(), TimeProvider.System);
    }

    [Fact]
    public void APeerStartsUnsnubbed()
    {
        Assert.False(CreatePeer().IsSnubbed);
    }

    [Fact]
    public void AnExpiredRequestSnubsThePeer()
    {
        var peer = CreatePeer();

        peer.MarkSnubbed();

        Assert.True(peer.IsSnubbed);
    }

    [Fact]
    public void AnyDataAtAllClearsTheSnub()
    {
        // The peer answered, so whatever stalled it has passed. One block is enough - waiting for a
        // whole piece would leave a recovered peer steered away from rare pieces for far too long.
        var peer = CreatePeer();
        peer.MarkSnubbed();

        peer.AddDownloaded(1);

        Assert.False(peer.IsSnubbed);
    }

    [Fact]
    public void SnubbingIsIdempotent()
    {
        var peer = CreatePeer();

        peer.MarkSnubbed();
        peer.MarkSnubbed();
        peer.AddDownloaded(16384);

        Assert.False(peer.IsSnubbed);
    }

    [Fact]
    public void UploadingToAPeerDoesNotClearItsSnub()
    {
        // The flag is about what the peer sends us. What we send it says nothing about whether it has
        // recovered, and clearing on upload would unsnub a peer that is still sending us nothing.
        var peer = CreatePeer();
        peer.MarkSnubbed();

        peer.AddUploaded(16384);

        Assert.True(peer.IsSnubbed);
    }
}
