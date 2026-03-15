using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Extensions;
using PeerSharp.Messages;
using System.Reflection;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

public class PeerManagerPexTests
{
    private class MockUtPex : IUtPex
    {
        public int? LocalMessageId { get; set; }
        public int? RemoteMessageId { get; set; }
        public List<List<(IPEndPoint, byte)>> Updates { get; } = new();

        public void Init(ExtensionHandshake handshake) { }
        public void SetLocalMessageId(int id) => LocalMessageId = id;
        public Task HandleMessageAsync(byte[] data) => Task.CompletedTask;
        public void SendPex(List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) { }
        public void Update(IEnumerable<(IPEndPoint Ep, byte Flags)> peers) => Updates.Add(new List<(IPEndPoint, byte)>(peers));
    }

    private class MockPeer : PeerCommunication
    {
        public MockUtPex Pex { get; } = new();
        public override IUtPex UtPex => Pex;

        public MockPeer(Torrent torrent) : base(torrent, new MockListener(), TimeProvider.System) { }
    }

    private class MockListener : IPeerListener
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

    [Fact]
    public void BroadcastPex_SendsPeersToConnectedClients()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var manager = new PeerManager(torrent, null!, null!, TimeProvider.System, null!);

        // Setup 2 peers
        var p1 = new MockPeer(torrent);
        p1.SetRemoteEndPoint(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1000));

        var p2 = new MockPeer(torrent);
        p2.SetRemoteEndPoint(new IPEndPoint(IPAddress.Parse("2.2.2.2"), 2000));

        // Add to connected
        var dict = typeof(PeerManager).GetField("_connectedPeers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager) as System.Collections.Concurrent.ConcurrentDictionary<PeerCommunication, byte>;
        dict!.TryAdd(p1, 0);
        dict.TryAdd(p2, 0);

        // Act
        typeof(PeerManager).GetMethod("BroadcastPex", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, null);

        // Assert
        // P1 should receive P2
        Assert.Single(p1.Pex.Updates);
        Assert.Contains(p1.Pex.Updates[0], x => x.Item1.Equals(p2.RemoteEndPoint));

        // P2 should receive P1
        Assert.Single(p2.Pex.Updates);
        Assert.Contains(p2.Pex.Updates[0], x => x.Item1.Equals(p1.RemoteEndPoint));
    }

    [Fact]
    public void BroadcastPex_SkipsPrivateTorrents()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.InfoFile.Info.IsPrivate = true; // Private!

        var manager = new PeerManager(torrent, null!, null!, TimeProvider.System, null!);
        var p1 = new MockPeer(torrent); p1.SetRemoteEndPoint(new IPEndPoint(IPAddress.Loopback, 1));
        var dict = typeof(PeerManager).GetField("_connectedPeers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager) as System.Collections.Concurrent.ConcurrentDictionary<PeerCommunication, byte>;
        dict!.TryAdd(p1, 0);

        typeof(PeerManager).GetMethod("BroadcastPex", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, null);

        Assert.Empty(p1.Pex.Updates);
    }
}

// Extension to set private field for test
internal static class PeerExtensions
{
    public static void SetRemoteEndPoint(this PeerCommunication peer, IPEndPoint ep)
    {
        peer.GetType().GetProperty("RemoteEndPoint")!.SetValue(peer, ep);
    }
}
