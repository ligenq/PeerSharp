using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Seeding;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Seeding;

public class SuperSeedManagerTests
{
    [Fact]
    public async Task AssignPieceToPeerAsync_SendsHave()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = true };
        var peer = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));

        try
        {
            Assert.True(manager.HandlePeerConnected(peer));
            await manager.AssignPieceToPeerAsync(peer);

            Assert.Single(peer.SentMessages);
            var msg = peer.SentMessages[0];
            Assert.Equal(MessageId.Have, msg.Id);
            Assert.InRange(msg.HavePieceIndex, 0, 2);
            Assert.True(manager.ShouldAllowRequest(peer, msg.HavePieceIndex));
            Assert.False(manager.ShouldAllowRequest(peer, (msg.HavePieceIndex + 1) % 3));
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task HandlePeerHaveAsync_DistributesAndReassigns()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = true };
        var peerA = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));
        var peerB = new TestPeer(new IPEndPoint(IPAddress.Loopback, 2));

        try
        {
            manager.HandlePeerConnected(peerA);
            manager.HandlePeerConnected(peerB);

            await manager.AssignPieceToPeerAsync(peerA);
            int firstPiece = peerA.SentMessages[0].HavePieceIndex;

            await manager.HandlePeerHaveAsync(peerB, firstPiece);

            Assert.True(peerA.SentMessages.Count >= 2);
            int secondPiece = peerA.SentMessages[1].HavePieceIndex;
            Assert.NotEqual(firstPiece, secondPiece);

            var stats = manager.GetStats();
            Assert.Equal(3, stats.TotalPieces);
            Assert.Equal(1, stats.DistributedPieces);
            Assert.Equal(2, stats.ActivePeers);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task AssignPieceToPeerAsync_MultiplePeersGetDifferentPieces()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = true };
        var peerA = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));
        var peerB = new TestPeer(new IPEndPoint(IPAddress.Loopback, 2));

        try
        {
            manager.HandlePeerConnected(peerA);
            manager.HandlePeerConnected(peerB);

            await manager.AssignPieceToPeerAsync(peerA);
            await manager.AssignPieceToPeerAsync(peerB);

            Assert.Single(peerA.SentMessages);
            Assert.Single(peerB.SentMessages);
            int pieceA = peerA.SentMessages[0].HavePieceIndex;
            int pieceB = peerB.SentMessages[0].HavePieceIndex;
            Assert.NotEqual(pieceA, pieceB);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task AssignPieceToPeerAsync_WhenDisabled_DoesNothing()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = false };
        var peer = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));

        try
        {
            manager.HandlePeerConnected(peer);
            await manager.AssignPieceToPeerAsync(peer);

            Assert.Empty(peer.SentMessages);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task HandlePeerHaveAsync_OutOfRangePiece_IsIgnored()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = true };
        var peerA = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));
        var peerB = new TestPeer(new IPEndPoint(IPAddress.Loopback, 2));

        try
        {
            manager.HandlePeerConnected(peerA);
            manager.HandlePeerConnected(peerB);
            await manager.AssignPieceToPeerAsync(peerA);
            int initialMessages = peerA.SentMessages.Count;

            // Out of range — should be silently ignored
            await manager.HandlePeerHaveAsync(peerB, -1);
            await manager.HandlePeerHaveAsync(peerB, 999);

            // peerA should not have been reassigned
            Assert.Equal(initialMessages, peerA.SentMessages.Count);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task HandlePeerHaveAsync_WhenDisabled_DoesNothing()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = true };
        var peerA = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));
        var peerB = new TestPeer(new IPEndPoint(IPAddress.Loopback, 2));

        try
        {
            manager.HandlePeerConnected(peerA);
            manager.HandlePeerConnected(peerB);
            await manager.AssignPieceToPeerAsync(peerA);
            int assignedPiece = peerA.SentMessages[0].HavePieceIndex;
            int initialMessages = peerA.SentMessages.Count;

            manager.Enabled = false;
            await manager.HandlePeerHaveAsync(peerB, assignedPiece);

            Assert.Equal(initialMessages, peerA.SentMessages.Count);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task HandlePeerBitfield_TracksSightings()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = true };
        var peerA = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));
        var peerB = new TestPeer(new IPEndPoint(IPAddress.Loopback, 2));

        try
        {
            manager.HandlePeerConnected(peerA);
            manager.HandlePeerConnected(peerB);
            await manager.AssignPieceToPeerAsync(peerA);
            int assignedPiece = peerA.SentMessages[0].HavePieceIndex;

            // peerB reports having the piece peerA was given — counts as distributed
            var pp = new PiecesProgress(3);
            pp.AddPiece(assignedPiece);
            manager.HandlePeerBitfield(peerB, pp);

            var stats = manager.GetStats();
            Assert.Equal(1, stats.DistributedPieces);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task HandlePeerDisconnected_RemovesPeerState()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = true };
        var peer = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));

        try
        {
            manager.HandlePeerConnected(peer);
            await manager.AssignPieceToPeerAsync(peer);
            int assignedPiece = peer.SentMessages[0].HavePieceIndex;

            Assert.True(manager.ShouldAllowRequest(peer, assignedPiece));
            Assert.Equal(1, manager.GetStats().ActivePeers);

            manager.HandlePeerDisconnected(peer);

            Assert.False(manager.ShouldAllowRequest(peer, assignedPiece));
            Assert.Equal(0, manager.GetStats().ActivePeers);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public void ShouldAllowRequest_WhenDisabled_AlwaysTrue()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = false };
        var peer = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));

        try
        {
            Assert.True(manager.ShouldAllowRequest(peer, 0));
            Assert.True(manager.ShouldAllowRequest(peer, 1));
            Assert.True(manager.ShouldAllowRequest(peer, 2));
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public void ShouldAllowRequest_UnknownPeer_ReturnsFalse()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = true };
        var peer = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));

        try
        {
            // Peer never connected — not in _assignedPieces
            Assert.False(manager.ShouldAllowRequest(peer, 0));
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task SelectPieceForPeer_PrefersRarestWhenAllOriginated()
    {
        var ctx = CreateTorrentContext(3);
        var manager = new SuperSeedManager(ctx.Torrent) { Enabled = true };
        var peerA = new TestPeer(new IPEndPoint(IPAddress.Loopback, 1));
        var peerB = new TestPeer(new IPEndPoint(IPAddress.Loopback, 2));
        var peerC = new TestPeer(new IPEndPoint(IPAddress.Loopback, 3));
        var peerD = new TestPeer(new IPEndPoint(IPAddress.Loopback, 4));
        var peerE = new TestPeer(new IPEndPoint(IPAddress.Loopback, 5));

        try
        {
            manager.HandlePeerConnected(peerA);
            manager.HandlePeerConnected(peerB);
            manager.HandlePeerConnected(peerC);
            manager.HandlePeerConnected(peerD);
            manager.HandlePeerConnected(peerE);

            // Originate all 3 pieces so the rarest-first fallback kicks in
            await manager.AssignPieceToPeerAsync(peerA); // piece 0
            await manager.AssignPieceToPeerAsync(peerB); // piece 1
            await manager.AssignPieceToPeerAsync(peerC); // piece 2

            // peerE reports having piece 0 (originally given to peerA) — sightings[0] becomes 1
            var pp = new PiecesProgress(3);
            pp.AddPiece(0);
            manager.HandlePeerBitfield(peerE, pp);

            // peerD asks for a piece — should get piece 1 or 2 (sightings=0), NOT piece 0 (sightings=1)
            await manager.AssignPieceToPeerAsync(peerD);

            Assert.Single(peerD.SentMessages);
            Assert.NotEqual(0, peerD.SentMessages[0].HavePieceIndex);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    private static (Torrent Torrent, string Path) CreateTorrentContext(int pieceCount)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize * pieceCount;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry {
            Path = "file.bin",
            Size = metadata.Info.FullSize,
            Offset = 0
        });

        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        for (int i = 0; i < pieceCount; i++)
        {
            torrent.Pieces.AddPiece(i);
        }
        return (torrent, path);
    }

    private static void Cleanup((Torrent Torrent, string Path) ctx)
    {
        ctx.Torrent.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            if (Directory.Exists(ctx.Path))
            {
                Directory.Delete(ctx.Path, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp artifacts.
        }
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "MtTorrentTests_SuperSeed", Guid.NewGuid().ToString("N"));
    }

    private sealed class TestPeer : IPeerCommunication
    {
        public TestPeer(IPEndPoint endpoint)
        {
            RemoteEndPoint = endpoint;
        }

        public List<PeerMessage> SentMessages { get; } = new();
        public IPeerListener Listener => null!;
        public byte[] PeerId { get; } = new byte[20];
        public IPEndPoint? RemoteEndPoint { get; }
        public ExtensionHandshake? RemoteExtensions => null;
        public bool RemoteSupportsExtensions => false;
        public IUtHashPiece? UtHashPiece => null;
        public IUtHolepunch UtHolepunch { get; } = new NullHolepunch();
        public IUtMetadata UtMetadata { get; } = new NullMetadata();
        public IUtPex UtPex { get; } = new NullPex();

        public Task SendMessageAsync(PeerMessage msg)
        {
            SentMessages.Add(msg);
            return Task.CompletedTask;
        }

        public Task SetInterestedAsync(bool interested)
        {
            return Task.CompletedTask;
        }

        private sealed class NullHolepunch : IUtHolepunch
        {
            public int? LocalMessageId => null;
            public int? RemoteMessageId => null;
            public void Init(ExtensionHandshake handshake) { }
            public void SetLocalMessageId(int id) { }
            public void SendConnect(IPEndPoint endpoint) { }
            public void SendError(IPEndPoint endpoint, UtHolepunch.ErrorCode error) { }
            public void SendRendezvous(IPEndPoint endpoint) { }
        }

        private sealed class NullMetadata : IUtMetadata
        {
            public int? LocalMessageId => null;
            public int? RemoteMessageId => null;
            public void Init(ExtensionHandshake handshake) { }
            public void SetLocalMessageId(int id) { }
            public void SendData(int piece, byte[] data, int totalSize) { }
            public void SendReject(int piece) { }
            public void SendRequest(int piece) { }
        }

        private sealed class NullPex : IUtPex
        {
            public int? LocalMessageId => null;
            public int? RemoteMessageId => null;
            public void Init(ExtensionHandshake handshake) { }
            public void SetLocalMessageId(int id) { }
            public Task HandleMessageAsync(byte[] data) => Task.CompletedTask;
            public void SendPex(List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) { }
            public void Update(IEnumerable<(IPEndPoint Ep, byte Flags)> peers) { }
        }
    }
}





