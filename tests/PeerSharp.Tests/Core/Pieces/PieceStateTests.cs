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

    /// <summary>
    /// Reset clears the contributor list, and the hash-failure handler used to call it before reading
    /// that list - so the loop that strikes the peers responsible always ran over an empty set. Nothing
    /// was ever struck and nothing was ever dropped for bad data. This pins the ordering the handler
    /// depends on: whoever supplied a piece has to be read out before the piece is reset.
    /// </summary>
    [Fact]
    public void Reset_ClearsContributors_SoTheyMustBeReadFirst()
    {
        var piece = new PieceState(0, 2);
        var ctx = CreatePeerContext();

        try
        {
            piece.TryAddBlock(0, new Block(16), ctx.Peer);
            Assert.Contains(ctx.Peer, piece.Contributors);

            var supplied = piece.Contributors.ToArray();
            piece.Reset();

            Assert.Empty(piece.Contributors);
            Assert.Contains(ctx.Peer, supplied);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public void WebSeedContribution_IsTrackedUntilReset()
    {
        var piece = new PieceState(0, 1);

        Assert.True(piece.TryAddBlockFromWebSeed(0, new Block(16)));
        Assert.True(piece.HasWebSeedContributor);

        piece.Reset();

        Assert.False(piece.HasWebSeedContributor);
    }

    /// <summary>
    /// A piece is asked of anyone until it fails, then of one peer at a time - which is what turns the
    /// next failure into an answer rather than a suspicion shared between everyone who contributed.
    /// </summary>
    [Fact]
    public void RetryClaim_RestrictsToOnePeerOnceThePieceHasFailed()
    {
        var piece = new PieceState(0, 2);
        var first = CreatePeerContext();
        var second = CreatePeerContext();
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(30);

        try
        {
            // Before any failure the piece is open to everyone, which is how it downloads quickly.
            Assert.True(piece.TryClaimForRetry(first.Peer, now, timeout));
            Assert.True(piece.TryClaimForRetry(second.Peer, now, timeout));

            piece.RecordHashFailure();
            piece.Reset();

            Assert.True(piece.TryClaimForRetry(first.Peer, now, timeout));
            Assert.False(piece.TryClaimForRetry(second.Peer, now, timeout));

            // Still the same peer a moment later - the claim is not per request.
            Assert.True(piece.TryClaimForRetry(first.Peer, now.AddSeconds(5), timeout));

            // But it expires, so a peer that goes quiet costs one interval rather than the piece.
            Assert.True(piece.TryClaimForRetry(second.Peer, now.AddSeconds(31), timeout));
            Assert.False(piece.TryClaimForRetry(first.Peer, now.AddSeconds(31), timeout));

            // The count survives Reset; it is what says this piece is being retried at all.
            Assert.Equal(1, piece.HashFailures);

            // A reservation held by a connection that has gone away must not outlive it. The peer most
            // likely to be holding one is the peer just dropped for supplying the bad data.
            Assert.True(piece.TryClaimForRetry(first.Peer, now.AddSeconds(62), timeout));
            piece.ReleaseRetryClaim(second.Peer);
            Assert.False(piece.TryClaimForRetry(second.Peer, now.AddSeconds(63), timeout));

            piece.ReleaseRetryClaim(first.Peer);
            Assert.True(piece.TryClaimForRetry(second.Peer, now.AddSeconds(64), timeout));
        }
        finally
        {
            Cleanup(first);
            Cleanup(second);
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






