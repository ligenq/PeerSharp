using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Extensions;

public class UtHolepunchTests
{
    private class MockPeerListener : IPeerListener
    {
        public UtHolepunch.MsgId? LastHolepunchId { get; private set; }
        public IPEndPoint? LastHolepunchEndpoint { get; private set; }
        public UtHolepunch.ErrorCode? LastHolepunchError { get; private set; }

        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error)
        {
            LastHolepunchId = id;
            LastHolepunchEndpoint = endpoint;
            LastHolepunchError = error;
            return Task.CompletedTask;
        }
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }

    private class MockPeerCommunication : IPeerCommunication
    {
        public byte[] PeerId { get; set; } = new byte[20];
        public IPEndPoint? RemoteEndPoint { get; set; }
        public bool RemoteSupportsExtensions { get; set; }
        public ExtensionHandshake? RemoteExtensions { get; set; }
        public IUtMetadata UtMetadata => throw new NotImplementedException();
        public IUtPex UtPex => throw new NotImplementedException();
        public IPeerListener Listener { get; set; } = null!;
        public IUtHashPiece? UtHashPiece => throw new NotImplementedException();
        public IUtHolepunch UtHolepunch => throw new NotImplementedException();
        public List<PeerMessage> SentMessages { get; } = new();
        public Task SetInterestedAsync(bool interested) => Task.CompletedTask;
        public Task SendMessageAsync(PeerMessage msg)
        {
            SentMessages.Add(msg);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void SendConnect_IPv4_SendsCorrectBinaryMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utHolepunch = new UtHolepunch(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_holepunch"] = 4;
        utHolepunch.Init(handshake);
        var endpoint = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1234);

        // Act
        utHolepunch.SendConnect(endpoint);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        Assert.Equal(4, msg.Data[0]); // Ext ID
        Assert.Equal((byte)UtHolepunch.MsgId.Connect, msg.Data[1]);
        Assert.Equal(0, msg.Data[2]); // IPv4
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, msg.Data.AsSpan(3, 4).ToArray());
        Assert.Equal(1234, (msg.Data[7] << 8) | msg.Data[8]);
    }

    [Fact]
    public async Task HandleMessage_ValidIPv4Connect_NotifiesListener()
    {
        // Arrange
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utHolepunch = new UtHolepunch(mockPeer);

        // Data: [MsgId=1][Type=0][IP=1,2,3,4][Port=0x04,0xD2] (1234)
        var data = new byte[] { 1, 0, 1, 2, 3, 4, 0x04, 0xD2 };

        // Act
        await utHolepunch.HandleMessageAsync(data);

        // Assert
        Assert.Equal(UtHolepunch.MsgId.Connect, listener.LastHolepunchId);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), listener.LastHolepunchEndpoint?.Address);
        Assert.Equal(1234, listener.LastHolepunchEndpoint?.Port);
        Assert.Equal(UtHolepunch.ErrorCode.None, listener.LastHolepunchError);
    }

    [Fact]
    public void SendConnect_IPv6_SendsCorrectBinaryMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utHolepunch = new UtHolepunch(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_holepunch"] = 4;
        utHolepunch.Init(handshake);
        var endpoint = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6881);

        // Act
        utHolepunch.SendConnect(endpoint);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        Assert.Equal(4, msg.Data[0]); // Ext ID
        Assert.Equal((byte)UtHolepunch.MsgId.Connect, msg.Data[1]);
        Assert.Equal(1, msg.Data[2]); // IPv6
        // IPv6 address is 16 bytes starting at index 3
        var expectedIp = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        Assert.Equal(expectedIp, msg.Data.AsSpan(3, 16).ToArray());
        // Port at index 19 (3 + 16)
        Assert.Equal(6881, (msg.Data[19] << 8) | msg.Data[20]);
    }

    [Fact]
    public void SendRendezvous_IPv4_SendsCorrectBinaryMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utHolepunch = new UtHolepunch(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_holepunch"] = 5;
        utHolepunch.Init(handshake);
        var endpoint = new IPEndPoint(IPAddress.Parse("192.168.1.1"), 51413);

        // Act
        utHolepunch.SendRendezvous(endpoint);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        Assert.Equal(5, msg.Data[0]); // Ext ID
        Assert.Equal((byte)UtHolepunch.MsgId.Rendezvous, msg.Data[1]);
        Assert.Equal(0, msg.Data[2]); // IPv4
        Assert.Equal(new byte[] { 192, 168, 1, 1 }, msg.Data.AsSpan(3, 4).ToArray());
        Assert.Equal(51413, (msg.Data[7] << 8) | msg.Data[8]);
    }

    [Fact]
    public void SendError_IPv4_SendsCorrectBinaryMessageWithErrorCode()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utHolepunch = new UtHolepunch(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_holepunch"] = 4;
        utHolepunch.Init(handshake);
        var endpoint = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 8080);

        // Act
        utHolepunch.SendError(endpoint, UtHolepunch.ErrorCode.NoSuchPeer);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        Assert.Equal(4, msg.Data[0]); // Ext ID
        Assert.Equal((byte)UtHolepunch.MsgId.Error, msg.Data[1]);
        Assert.Equal(0, msg.Data[2]); // IPv4
        Assert.Equal(new byte[] { 10, 0, 0, 1 }, msg.Data.AsSpan(3, 4).ToArray());
        Assert.Equal(8080, (msg.Data[7] << 8) | msg.Data[8]);
        // Error code is 4 bytes big-endian starting at index 9
        int errorCode = (msg.Data[9] << 24) | (msg.Data[10] << 16) | (msg.Data[11] << 8) | msg.Data[12];
        Assert.Equal((int)UtHolepunch.ErrorCode.NoSuchPeer, errorCode);
    }

    [Fact]
    public async Task HandleMessage_ValidIPv6Connect_NotifiesListener()
    {
        // Arrange
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utHolepunch = new UtHolepunch(mockPeer);

        // Data: [MsgId=1][Type=1][IPv6=16bytes][Port=0x1A,0xE1] (6881)
        var ipv6Bytes = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        var data = new byte[2 + 16 + 2];
        data[0] = 1; // Connect
        data[1] = 1; // IPv6
        ipv6Bytes.CopyTo(data, 2);
        data[18] = 0x1A; // Port high byte
        data[19] = 0xE1; // Port low byte (6881)

        // Act
        await utHolepunch.HandleMessageAsync(data);

        // Assert
        Assert.Equal(UtHolepunch.MsgId.Connect, listener.LastHolepunchId);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), listener.LastHolepunchEndpoint?.Address);
        Assert.Equal(6881, listener.LastHolepunchEndpoint?.Port);
    }

    [Fact]
    public async Task HandleMessage_ValidRendezvous_NotifiesListener()
    {
        // Arrange
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utHolepunch = new UtHolepunch(mockPeer);

        // Data: [MsgId=0][Type=0][IP=192,168,1,100][Port=0xC8,0xD5] (51413)
        var data = new byte[] { 0, 0, 192, 168, 1, 100, 0xC8, 0xD5 };

        // Act
        await utHolepunch.HandleMessageAsync(data);

        // Assert
        Assert.Equal(UtHolepunch.MsgId.Rendezvous, listener.LastHolepunchId);
        Assert.Equal(IPAddress.Parse("192.168.1.100"), listener.LastHolepunchEndpoint?.Address);
        Assert.Equal(51413, listener.LastHolepunchEndpoint?.Port);
    }

    [Fact]
    public async Task HandleMessage_ValidError_NotifiesListenerWithErrorCode()
    {
        // Arrange
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utHolepunch = new UtHolepunch(mockPeer);

        // Data: [MsgId=2][Type=0][IP=1,2,3,4][Port=0x04,0xD2][Error=0,0,0,3] (NoSupport)
        var data = new byte[] { 2, 0, 1, 2, 3, 4, 0x04, 0xD2, 0, 0, 0, 3 };

        // Act
        await utHolepunch.HandleMessageAsync(data);

        // Assert
        Assert.Equal(UtHolepunch.MsgId.Error, listener.LastHolepunchId);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), listener.LastHolepunchEndpoint?.Address);
        Assert.Equal(1234, listener.LastHolepunchEndpoint?.Port);
        Assert.Equal(UtHolepunch.ErrorCode.NoSupport, listener.LastHolepunchError);
    }

    [Fact]
    public async Task HandleMessage_TooShortData_DoesNotNotifyListener()
    {
        // Arrange
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utHolepunch = new UtHolepunch(mockPeer);

        // Data too short (only 1 byte)
        var data = new byte[] { 1 };

        // Act
        await utHolepunch.HandleMessageAsync(data);

        // Assert - should not crash and should not notify
        Assert.Null(listener.LastHolepunchId);
    }

    [Fact]
    public async Task HandleMessage_TruncatedIPv4_DoesNotNotifyListener()
    {
        // Arrange
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utHolepunch = new UtHolepunch(mockPeer);

        // Data: [MsgId=1][Type=0][IP=1,2,3] - missing last byte of IP and port
        var data = new byte[] { 1, 0, 1, 2, 3 };

        // Act
        await utHolepunch.HandleMessageAsync(data);

        // Assert - should not crash and should not notify
        Assert.Null(listener.LastHolepunchId);
    }

    [Fact]
    public async Task HandleMessage_TruncatedIPv6_DoesNotNotifyListener()
    {
        // Arrange
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utHolepunch = new UtHolepunch(mockPeer);

        // Data: [MsgId=1][Type=1][IPv6=8bytes] - truncated IPv6
        var data = new byte[] { 1, 1, 0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0 };

        // Act
        await utHolepunch.HandleMessageAsync(data);

        // Assert - should not crash and should not notify
        Assert.Null(listener.LastHolepunchId);
    }

    [Fact]
    public void Send_WithoutInit_DoesNotSendMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utHolepunch = new UtHolepunch(mockPeer);
        // Note: Init is NOT called, so MessageId is null
        var endpoint = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1234);

        // Act
        utHolepunch.SendConnect(endpoint);

        // Assert - should not send anything
        Assert.Empty(mockPeer.SentMessages);
    }

    [Fact]
    public void Init_WithoutHolepunchSupport_DoesNotSetMessageId()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utHolepunch = new UtHolepunch(mockPeer);
        var handshake = new ExtensionHandshake();
        // No ut_holepunch in message IDs

        // Act
        utHolepunch.Init(handshake);
        utHolepunch.SendConnect(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1234));

        // Assert - should not send anything since no message ID
        Assert.Empty(mockPeer.SentMessages);
    }

    [Fact]
    public async Task HandleMessage_ErrorWithTruncatedErrorCode_StillNotifiesWithNoneError()
    {
        // Arrange
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utHolepunch = new UtHolepunch(mockPeer);

        // Data: [MsgId=2][Type=0][IP=1,2,3,4][Port=0x04,0xD2] - no error code bytes
        var data = new byte[] { 2, 0, 1, 2, 3, 4, 0x04, 0xD2 };

        // Act
        await utHolepunch.HandleMessageAsync(data);

        // Assert - should still notify with ErrorCode.None since error bytes are missing
        Assert.Equal(UtHolepunch.MsgId.Error, listener.LastHolepunchId);
        Assert.Equal(UtHolepunch.ErrorCode.None, listener.LastHolepunchError);
    }

    [Fact]
    public void SendError_AllErrorCodes_SendCorrectly()
    {
        var errorCodes = new[]
        {
            UtHolepunch.ErrorCode.None,
            UtHolepunch.ErrorCode.NoSuchPeer,
            UtHolepunch.ErrorCode.NotConnected,
            UtHolepunch.ErrorCode.NoSupport,
            UtHolepunch.ErrorCode.NoSelf
        };

        foreach (var errorCode in errorCodes)
        {
            // Arrange
            var mockPeer = new MockPeerCommunication();
            var utHolepunch = new UtHolepunch(mockPeer);
            var handshake = new ExtensionHandshake();
            handshake.MessageIds["ut_holepunch"] = 4;
            utHolepunch.Init(handshake);
            var endpoint = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1234);

            // Act
            utHolepunch.SendError(endpoint, errorCode);

            // Assert
            var msg = mockPeer.SentMessages[0];
            int sentErrorCode = (msg.Data[9] << 24) | (msg.Data[10] << 16) | (msg.Data[11] << 8) | msg.Data[12];
            Assert.Equal((int)errorCode, sentErrorCode);
        }
    }
}





