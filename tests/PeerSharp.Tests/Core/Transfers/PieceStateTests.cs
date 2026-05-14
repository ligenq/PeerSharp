using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Extensions;
using PeerSharp.Messages;
using Microsoft.Extensions.Time.Testing;
using System.Net;

namespace PeerSharp.Tests.Core.Transfers;

public class PieceStateTests
{
    private static PeerCommunication MakePeer()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        return new PeerCommunication(torrent, new MockPeerListener(), new FakeTimeProvider());
    }

    private class MockPeerListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }

    // ── TryAddBlock ────────────────────────────────────────────────────────────

    [Fact]
    public void TryAddBlock_AddsSuccessfully()
    {
        var state = new PieceState(0, blocksCount: 2);
        var peer = MakePeer();
        var block = new Block(0, 0, 16384);

        bool added = state.TryAddBlock(0, block, peer);

        Assert.True(added);
        Assert.Equal(1, state.ReceivedCount);
        Assert.True(state.Blocks[0]);
        Assert.Contains(peer, state.Contributors);
    }

    [Fact]
    public void TryAddBlock_DuplicateBlock_ReturnsFalse()
    {
        var state = new PieceState(0, blocksCount: 2);
        var peer = MakePeer();
        var block1 = new Block(0, 0, 16384);
        var block2 = new Block(0, 0, 16384);

        state.TryAddBlock(0, block1, peer);
        bool added = state.TryAddBlock(0, block2, peer);

        Assert.False(added);
        Assert.Equal(1, state.ReceivedCount);
    }

    [Fact]
    public void TryAddBlock_IndexTooLarge_ReturnsFalse()
    {
        var state = new PieceState(0, blocksCount: 2);
        var peer = MakePeer();
        var block = new Block(0, 0, 16384);

        bool added = state.TryAddBlock(5, block, peer);

        Assert.False(added);
        Assert.Equal(0, state.ReceivedCount);
    }

    [Fact]
    public void TryAddBlock_WhileWriting_ReturnsFalse()
    {
        var state = new PieceState(0, blocksCount: 1);
        var peer = MakePeer();

        var block1 = new Block(0, 0, 16384);
        state.TryAddBlock(0, block1, peer);
        state.TryCompleteAndSetWriting(); // now IsWriting = true

        var block2 = new Block(0, 0, 16384);
        bool added = state.TryAddBlock(0, block2, peer);

        Assert.False(added);
    }

    // ── TryAddBlockFromWebSeed ─────────────────────────────────────────────────

    [Fact]
    public void TryAddBlockFromWebSeed_AddsWithNoContributor()
    {
        var state = new PieceState(0, blocksCount: 2);
        var block = new Block(0, 0, 16384);

        bool added = state.TryAddBlockFromWebSeed(0, block);

        Assert.True(added);
        Assert.Equal(1, state.ReceivedCount);
        Assert.Empty(state.Contributors);
    }

    [Fact]
    public void TryAddBlockFromWebSeed_DuplicateBlock_ReturnsFalse()
    {
        var state = new PieceState(0, blocksCount: 2);
        state.TryAddBlockFromWebSeed(0, new Block(0, 0, 16384));
        bool added = state.TryAddBlockFromWebSeed(0, new Block(0, 0, 16384));

        Assert.False(added);
    }

    // ── TryCompleteAndSetWriting ───────────────────────────────────────────────

    [Fact]
    public void TryCompleteAndSetWriting_NotComplete_ReturnsFalse()
    {
        var state = new PieceState(0, blocksCount: 2);
        var peer = MakePeer();
        state.TryAddBlock(0, new Block(0, 0, 16384), peer);

        bool claimed = state.TryCompleteAndSetWriting();

        Assert.False(claimed);
        Assert.False(state.IsWriting);
    }

    [Fact]
    public void TryCompleteAndSetWriting_Complete_ReturnsTrueAndSetsWriting()
    {
        var state = new PieceState(0, blocksCount: 2);
        var peer = MakePeer();
        state.TryAddBlock(0, new Block(0, 0, 16384), peer);
        state.TryAddBlock(1, new Block(0, 16384, 16384), peer);

        bool claimed = state.TryCompleteAndSetWriting();

        Assert.True(claimed);
        Assert.True(state.IsWriting);
    }

    [Fact]
    public void TryCompleteAndSetWriting_AlreadyWriting_ReturnsFalse()
    {
        var state = new PieceState(0, blocksCount: 1);
        var peer = MakePeer();
        state.TryAddBlock(0, new Block(0, 0, 16384), peer);
        state.TryCompleteAndSetWriting();

        bool claimed = state.TryCompleteAndSetWriting();

        Assert.False(claimed);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsAllState()
    {
        var state = new PieceState(0, blocksCount: 2);
        var peer = MakePeer();
        state.TryAddBlock(0, new Block(0, 0, 16384), peer);
        state.TryAddBlock(1, new Block(0, 16384, 16384), peer);
        state.TryCompleteAndSetWriting();

        state.Reset();

        Assert.Equal(0, state.ReceivedCount);
        Assert.False(state.IsWriting);
        Assert.False(state.Blocks[0]);
        Assert.False(state.Blocks[1]);
        Assert.Empty(state.Contributors);
    }

    [Fact]
    public void Reset_AfterReset_CanAddBlocksAgain()
    {
        var state = new PieceState(0, blocksCount: 1);
        var peer = MakePeer();
        state.TryAddBlock(0, new Block(0, 0, 16384), peer);
        state.Reset();

        bool added = state.TryAddBlock(0, new Block(0, 0, 16384), peer);

        Assert.True(added);
        Assert.Equal(1, state.ReceivedCount);
    }

    // ── GetReceivedBytes ──────────────────────────────────────────────────────

    [Fact]
    public void GetReceivedBytes_NoBlocks_ReturnsZero()
    {
        var state = new PieceState(0, blocksCount: 2);
        long bytes = state.GetReceivedBytes(0, 32768, 32768, 0, 32768);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void GetReceivedBytes_OneBlockInRange_ReturnsBlockSize()
    {
        var state = new PieceState(0, blocksCount: 2);
        var peer = MakePeer();
        state.TryAddBlock(0, new Block(0, 0, 16384), peer); // block 0: bytes 0-16383

        long bytes = state.GetReceivedBytes(
            pieceStartOffset: 0,
            pieceSize: 32768,
            torrentFullSize: 32768,
            rangeStart: 0,
            rangeSize: 32768);

        Assert.Equal(16384, bytes);
    }

    [Fact]
    public void GetReceivedBytes_RangeDoesNotOverlapBlock_ReturnsZero()
    {
        var state = new PieceState(0, blocksCount: 2);
        var peer = MakePeer();
        state.TryAddBlock(0, new Block(0, 0, 16384), peer); // block 0: bytes 0-16383

        // Ask for range 16384-32768 (block 1's range)
        long bytes = state.GetReceivedBytes(
            pieceStartOffset: 0,
            pieceSize: 32768,
            torrentFullSize: 32768,
            rangeStart: 16384,
            rangeSize: 16384);

        Assert.Equal(0, bytes);
    }
}

public class TransferStatsTests
{
    [Fact]
    public void AddDownloaded_AccumulatesAtomically()
    {
        var stats = new PeerSharp.Internals.TransferStats();
        stats.AddDownloaded(1000);
        stats.AddDownloaded(500);
        Assert.Equal(1500, stats.Downloaded);
    }

    [Fact]
    public void AddUploaded_AccumulatesAtomically()
    {
        var stats = new PeerSharp.Internals.TransferStats();
        stats.AddUploaded(2048);
        Assert.Equal(2048, stats.Uploaded);
    }

    [Fact]
    public void InitialValues_AreZero()
    {
        var stats = new PeerSharp.Internals.TransferStats();
        Assert.Equal(0, stats.Downloaded);
        Assert.Equal(0, stats.Uploaded);
    }
}
