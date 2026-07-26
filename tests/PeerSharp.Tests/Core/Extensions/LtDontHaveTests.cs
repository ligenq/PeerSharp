using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Buffers.Binary;
using System.Net;

namespace PeerSharp.Tests.Core.Extensions;

/// <summary>
/// BEP 54: <c>lt_donthave</c>, the retraction the base protocol lacks.
/// </summary>
public class LtDontHaveTests
{
    private sealed class MockPeerCommunication : IPeerCommunication
    {
        public List<PeerMessage> SentMessages { get; } = [];
        public IPeerListener Listener => null!;
        public byte[] PeerId { get; } = new byte[20];
        public IPEndPoint? RemoteEndPoint { get; } = new(IPAddress.Loopback, 6881);
        public ExtensionHandshake? RemoteExtensions => null;
        public bool RemoteSupportsExtensions => true;
        public bool RemoteIsUploadOnly { get; set; }
        public PiecesProgress PeerPieces { get; } = new(8);
        public IUtHashPiece? UtHashPiece => null;
        public IUtHolepunch UtHolepunch => null!;
        public IUtMetadata UtMetadata => null!;
        public IUtPex UtPex => null!;

        public Task SendMessageAsync(PeerMessage msg)
        {
            SentMessages.Add(msg);
            return Task.CompletedTask;
        }

        public Task SetInterestedAsync(bool interested) => Task.CompletedTask;
    }

    private static byte[] Body(int pieceIndex)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(body, pieceIndex);
        return body;
    }

    [Fact]
    public void HandleMessage_ClearsThePieceFromTheBitfield()
    {
        var peer = new MockPeerCommunication();
        peer.PeerPieces.AddPiece(3);
        var extension = new LtDontHave(peer);

        int? retracted = extension.HandleMessage(Body(3));

        Assert.Equal(3, retracted);
        Assert.False(peer.PeerPieces.HasPiece(3));
    }

    [Fact]
    public void HandleMessage_LeavesOtherPiecesAlone()
    {
        var peer = new MockPeerCommunication();
        peer.PeerPieces.AddPiece(2);
        peer.PeerPieces.AddPiece(3);
        var extension = new LtDontHave(peer);

        extension.HandleMessage(Body(3));

        Assert.True(peer.PeerPieces.HasPiece(2));
        Assert.Equal(1, peer.PeerPieces.ReceivedCount);
    }

    [Fact]
    public void HandleMessage_AfterHaveAll_StillRetractsTheSinglePiece()
    {
        // The interesting case: a peer that claimed everything then loses one piece. The "has all"
        // shortcut has to be expanded, or the retraction is silently ignored and we keep asking.
        var peer = new MockPeerCommunication();
        peer.PeerPieces.SetHaveAll();
        var extension = new LtDontHave(peer);

        extension.HandleMessage(Body(5));

        Assert.False(peer.PeerPieces.HasPiece(5));
        Assert.True(peer.PeerPieces.HasPiece(4));
        Assert.False(peer.PeerPieces.IsFull);
        Assert.Equal(7, peer.PeerPieces.ReceivedCount);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9999)]
    [InlineData(-1)]
    public void HandleMessage_OutOfRangeIndex_IsRejected(int index)
    {
        var peer = new MockPeerCommunication();
        peer.PeerPieces.SetHaveAll();
        var extension = new LtDontHave(peer);

        Assert.Null(extension.HandleMessage(Body(index)));
        Assert.True(peer.PeerPieces.IsFull);
    }

    [Fact]
    public void HandleMessage_TruncatedPayload_IsRejected()
    {
        var peer = new MockPeerCommunication();
        peer.PeerPieces.AddPiece(1);
        var extension = new LtDontHave(peer);

        Assert.Null(extension.HandleMessage([0, 0, 1]));
        Assert.True(peer.PeerPieces.HasPiece(1));
    }

    [Fact]
    public async Task SendAsync_WritesTheRemoteIdAndPieceIndex()
    {
        var peer = new MockPeerCommunication();
        var extension = new LtDontHave(peer);
        extension.Init(new ExtensionHandshake { MessageIds = { [LtDontHave.Name] = 7 } });

        await extension.SendAsync(260);

        var msg = Assert.Single(peer.SentMessages);
        Assert.Equal(MessageId.Extended, msg.Id);
        Assert.Equal(5, msg.Data.Length);
        Assert.Equal(7, msg.Data[0]);
        Assert.Equal(260, BinaryPrimitives.ReadInt32BigEndian(msg.Data.AsSpan(1)));
    }

    [Fact]
    public async Task SendAsync_WithoutRemoteSupport_SendsNothing()
    {
        var peer = new MockPeerCommunication();
        var extension = new LtDontHave(peer);

        await extension.SendAsync(1);

        Assert.Empty(peer.SentMessages);
    }

    [Fact]
    public void Init_WithoutTheExtension_LeavesRemoteIdUnset()
    {
        var peer = new MockPeerCommunication();
        var extension = new LtDontHave(peer);

        extension.Init(new ExtensionHandshake { MessageIds = { ["ut_pex"] = 2 } });

        Assert.Null(extension.RemoteMessageId);
    }
}
