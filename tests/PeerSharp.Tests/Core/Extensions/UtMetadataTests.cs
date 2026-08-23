using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.BEncoding;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Extensions;

public class UtMetadataTests
{
    private class MockPeerCommunication : IPeerCommunication
    {
        public byte[] PeerId { get; set; } = new byte[20];
        public IPEndPoint? RemoteEndPoint { get; set; }
        public bool RemoteSupportsExtensions { get; set; }
        public bool RemoteIsUploadOnly { get; set; }
        public PiecesProgress PeerPieces { get; set; } = new(16);
        public ExtensionHandshake? RemoteExtensions { get; set; }
        public IUtMetadata UtMetadata => throw new NotImplementedException();
        public IUtPex UtPex => throw new NotImplementedException();
        public IPeerListener Listener { get; set; } = null!;
        public IUtHashPiece? UtHashPiece => throw new NotImplementedException();
        public IUtHolepunch UtHolepunch => throw new NotImplementedException();
        public List<PeerMessage> SentMessages { get; } = [];
        public Task SetInterestedAsync(bool interested) => Task.CompletedTask;
        public Task SendMessageAsync(PeerMessage msg)
        {
            SentMessages.Add(msg);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void SendRequest_ValidPiece_SendsCorrectMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_metadata"] = 3;
        utMetadata.Init(handshake);

        // Act
        utMetadata.SendRequest(5);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        Assert.Equal(3, msg.Data[0]);

        var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
        Assert.Equal(0, (int?)dict.GetLong("msg_type"));
        Assert.Equal(5, (int?)dict.GetLong("piece"));
    }

    [Fact]
    public void SendData_ValidPiece_SendsCorrectMessageWithPayload()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_metadata"] = 3;
        utMetadata.Init(handshake);
        var data = new byte[] { 0xAA, 0xBB, 0xCC };

        // Act
        utMetadata.SendData(5, data, 1000);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        Assert.Equal(3, msg.Data[0]);

        var result = BencodeParser.ParseWithConsumed(msg.Data.AsSpan(1).ToArray());
        var dict = Assert.IsType<BDict>(result.Node);
        Assert.Equal(1, (int?)dict.GetLong("msg_type"));
        Assert.Equal(5, (int?)dict.GetLong("piece"));
        Assert.Equal(1000, (int?)dict.GetLong("total_size"));

        var payload = msg.Data.AsSpan(1 + result.Consumed).ToArray();
        Assert.Equal(data, payload);
    }

    [Fact]
    public void SendReject_ValidPiece_SendsCorrectMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_metadata"] = 3;
        utMetadata.Init(handshake);

        // Act
        utMetadata.SendReject(5);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
        Assert.Equal(2, (int?)dict.GetLong("msg_type"));
        Assert.Equal(5, (int?)dict.GetLong("piece"));
    }

    [Fact]
    public void SendRequest_WithoutInit_DoesNotSendMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        // Note: Init is NOT called

        // Act
        utMetadata.SendRequest(5);

        // Assert - should not send anything
        Assert.Empty(mockPeer.SentMessages);
    }

    [Fact]
    public void SendRequest_WithLocalIdOnly_SendsNothing()
    {
        // BEP 10 numbers extensions per receiver: the id in an outgoing extended message is the one
        // the peer chose for itself. A peer that never advertised ut_metadata has given us no id to
        // address, and this client's own numbering means nothing on the wire - addressing a message
        // with it delivers to whatever extension that peer happens to have put at that number.
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        utMetadata.SetLocalMessageId(9);

        utMetadata.SendRequest(3);

        Assert.Empty(mockPeer.SentMessages);
    }

    [Fact]
    public void SendRequest_SendsExactlyOnce_UnderThePeersId()
    {
        // It used to send a second copy under the local id whenever the two differed, as a fallback
        // "for peers that ignore our extension mapping". PeerSharp offers ut_metadata as 1 and
        // libtorrent offers it as 2, so every request also arrived at libtorrent's extension 1 as
        // nonsense - and libtorrent disconnects when none of its extensions claims an extended
        // message. Two PeerSharp instances agreed on 1, so the duplicate was never sent between them
        // and nothing here noticed.
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        utMetadata.SetLocalMessageId(1);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_metadata"] = 2;
        handshake.MetadataSize = 1024;
        utMetadata.Init(handshake);

        utMetadata.SendRequest(0);

        var msg = Assert.Single(mockPeer.SentMessages);
        Assert.Equal(2, msg.Data[0]);
    }

    [Fact]
    public void SendData_WithoutInit_DoesNotSendMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        // Note: Init is NOT called

        // Act
        utMetadata.SendData(5, [0xAA], 100);

        // Assert - should not send anything
        Assert.Empty(mockPeer.SentMessages);
    }

    [Fact]
    public void SendReject_WithoutInit_DoesNotSendMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        // Note: Init is NOT called

        // Act
        utMetadata.SendReject(5);

        // Assert - should not send anything
        Assert.Empty(mockPeer.SentMessages);
    }

    [Fact]
    public void Init_WithoutMetadataSupport_DoesNotSetMessageId()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        var handshake = new ExtensionHandshake();
        // No ut_metadata in handshake

        // Act
        utMetadata.Init(handshake);
        utMetadata.SendRequest(0);

        // Assert - should not send anything since no message ID
        Assert.Empty(mockPeer.SentMessages);
    }

    [Fact]
    public void SendRequest_PieceZero_SendsCorrectPieceNumber()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_metadata"] = 3;
        utMetadata.Init(handshake);

        // Act
        utMetadata.SendRequest(0);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
        Assert.Equal(0, (int?)dict.GetLong("piece"));
    }

    [Fact]
    public void SendData_EmptyPayload_SendsMessageWithEmptyData()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_metadata"] = 3;
        utMetadata.Init(handshake);

        // Act
        utMetadata.SendData(0, [], 0);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        var result = BencodeParser.ParseWithConsumed(msg.Data.AsSpan(1).ToArray());
        var dict = Assert.IsType<BDict>(result.Node);
        Assert.Equal(1, (int?)dict.GetLong("msg_type"));
        Assert.Equal(0, (int?)dict.GetLong("total_size"));

        // Payload should be empty (nothing after the dict)
        var payload = msg.Data.AsSpan(1 + result.Consumed).ToArray();
        Assert.Empty(payload);
    }

    [Fact]
    public void SendData_LargePayload_SendsCorrectly()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_metadata"] = 3;
        utMetadata.Init(handshake);

        // Create a 16KB payload (one piece)
        var data = new byte[16 * 1024];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 256);
        }

        // Act
        utMetadata.SendData(0, data, data.Length);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        var result = BencodeParser.ParseWithConsumed(msg.Data.AsSpan(1).ToArray());
        var dict = Assert.IsType<BDict>(result.Node);
        Assert.Equal(data.Length, (int?)dict.GetLong("total_size"));

        var payload = msg.Data.AsSpan(1 + result.Consumed).ToArray();
        Assert.Equal(data, payload);
    }

    [Fact]
    public void SendRequest_MultipleRequests_SendsAllMessages()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_metadata"] = 3;
        utMetadata.Init(handshake);

        // Act
        utMetadata.SendRequest(0);
        utMetadata.SendRequest(1);
        utMetadata.SendRequest(2);

        // Assert
        Assert.Equal(3, mockPeer.SentMessages.Count);

        for (int i = 0; i < 3; i++)
        {
            var msg = mockPeer.SentMessages[i];
            var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
            Assert.Equal(0, (int?)dict.GetLong("msg_type")); // Request
            Assert.Equal(i, (int?)dict.GetLong("piece"));
        }
    }

    [Fact]
    public void Init_SetsMessageIdFromHandshake()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utMetadata = new UtMetadata(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_metadata"] = 42;

        // Act
        utMetadata.Init(handshake);
        utMetadata.SendRequest(0);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        Assert.Equal(42, msg.Data[0]); // Extension ID should be 42
    }
}





