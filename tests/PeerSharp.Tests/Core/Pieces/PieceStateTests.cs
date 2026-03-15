using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Pieces;

public class PieceStateTests
{
    [Fact]
    public void TryAddBlock_AddsAndTracksContributor()
    {
        var piece = new PieceState(0, 2);
        var block = new Block(16);
        var ctx = CreatePeerContext();
        var peer = ctx.Peer;

        try
        {
            bool added = piece.TryAddBlock(0, block, peer);

            Assert.True(added);
            Assert.True(piece.Blocks[0]);
            Assert.NotNull(piece.BlockData[0]);
            Assert.Equal(1, piece.ReceivedCount);
            Assert.Contains(peer, piece.Contributors);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public void TryAddBlock_RejectsWhenWriting()
    {
        var piece = new PieceState(0, 1);
        piece.SetReceivedCountForInit(1);
        Assert.True(piece.TryCompleteAndSetWriting());

        var block = new Block(16);
        var ctx = CreatePeerContext();
        var peer = ctx.Peer;

        try
        {
            bool added = piece.TryAddBlock(0, block, peer);

            Assert.False(added);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public void GetReceivedBytes_RespectsPieceEnd()
    {
        var piece = new PieceState(0, 2);
        var ctx = CreatePeerContext();
        var peer = ctx.Peer;

        try
        {
            piece.TryAddBlock(0, new Block(ProtocolConstants.BlockSize), peer);

            long received = piece.GetReceivedBytes(0, 20000, 20000, 0, 20000);
            Assert.Equal(ProtocolConstants.BlockSize, received);

            piece.TryAddBlock(1, new Block(4000), peer);

            received = piece.GetReceivedBytes(0, 20000, 20000, 0, 20000);
            Assert.Equal(20000, received);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public void Reset_DisposesBlocks()
    {
        var piece = new PieceState(0, 1);
        var block = new Block(16);
        var ctx = CreatePeerContext();
        var peer = ctx.Peer;

        try
        {
            piece.TryAddBlock(0, block, peer);
            piece.Reset();

            Assert.Throws<ObjectDisposedException>(() => _ = block.Buffer);
            Assert.Equal(0, piece.ReceivedCount);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    private static (PeerCommunication Peer, Torrent Torrent, string Path) CreatePeerContext()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize, Offset = 0 });

        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);
        return (peer, torrent, path);
    }

    private static void Cleanup((PeerCommunication Peer, Torrent Torrent, string Path) ctx)
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
        return Path.Combine(Path.GetTempPath(), "MtTorrentTests_PieceState", Guid.NewGuid().ToString("N"));
    }

    private sealed class TestPeerListener : IPeerListener
    {
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, System.Net.IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<System.Net.IPEndPoint> added, List<byte> addedFlags, List<System.Net.IPEndPoint> dropped) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}






