using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Extensions;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core;

public class PieceStateTests
{
    private readonly Torrent _torrent;
    private readonly PeerCommunication _peer;

    public PieceStateTests()
    {
        _torrent = TorrentTestUtility.CreateMinimal();
        _peer = new PeerCommunication(_torrent, new MockPeerListener(), TimeProvider.System);
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

    [Fact]
    public void TryAddBlock_Success()
    {
        var state = new PieceState(0, 2);
        var block = new Block(1024);

        bool result = state.TryAddBlock(0, block, _peer);

        Assert.True(result);
        Assert.Equal(1, state.ReceivedCount);
        Assert.True(state.Blocks[0]);
        Assert.Equal(block, state.BlockData[0]);
        Assert.Contains(_peer, state.Contributors);
    }

    [Fact]
    public void TryAddBlock_AlreadyExists_ReturnsFalse()
    {
        var state = new PieceState(0, 2);
        var block1 = new Block(1024);
        var block2 = new Block(1024);

        state.TryAddBlock(0, block1, _peer);
        bool result = state.TryAddBlock(0, block2, _peer);

        Assert.False(result);
        Assert.Equal(1, state.ReceivedCount);
        Assert.Equal(block1, state.BlockData[0]);
    }

    [Fact]
    public void TryCompleteAndSetWriting_Works()
    {
        var state = new PieceState(0, 1);

        Assert.False(state.TryCompleteAndSetWriting()); // Not complete

        state.TryAddBlock(0, new Block(1024), _peer);

        Assert.True(state.TryCompleteAndSetWriting());
        Assert.True(state.IsWriting);

        Assert.False(state.TryCompleteAndSetWriting()); // Already writing
    }

    [Fact]
    public void Reset_ClearsStateAndDisposesBlocks()
    {
        var state = new PieceState(0, 1);
        var block = new Block(1024);
        state.TryAddBlock(0, block, _peer);
        state.TryCompleteAndSetWriting();

        state.Reset();

        Assert.Equal(0, state.ReceivedCount);
        Assert.False(state.IsWriting);
        Assert.Null(state.BlockData[0]);
        Assert.False(state.Blocks[0]);
        Assert.Empty(state.Contributors);
        Assert.Throws<ObjectDisposedException>(() => block.Buffer);
    }
}






