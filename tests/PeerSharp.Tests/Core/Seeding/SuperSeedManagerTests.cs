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





