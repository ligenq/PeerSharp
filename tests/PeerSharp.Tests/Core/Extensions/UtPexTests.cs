using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.BEncoding;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Extensions;

public class UtPexTests
{
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
        public List<PeerMessage> SentMessages { get; } = [];
        public Task SetInterestedAsync(bool interested) => Task.CompletedTask;
        public Task SendMessageAsync(PeerMessage msg)
        {
            SentMessages.Add(msg);
            return Task.CompletedTask;
        }
    }

    private class MockPeerListener : IPeerListener
    {
        public List<IPEndPoint> Added { get; } = [];
        public List<byte> AddedFlags { get; } = [];
        public List<IPEndPoint> Dropped { get; } = [];

        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;

        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped)
        {
            Added.AddRange(added);
            AddedFlags.AddRange(addedFlags);
            Dropped.AddRange(dropped);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Update_NewPeers_SendsPexMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utPex = new UtPex(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_pex"] = 1;
        utPex.Init(handshake);

        var peers = new List<(IPEndPoint Ep, byte Flags)>
        {
            (new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1111), 0x01)
        };

        // Act
        utPex.Update(peers);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        Assert.Equal(MessageId.Extended, msg.Id);
        Assert.Equal(1, msg.Data[0]); // Ext ID

        var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
        Assert.True(dict.Dict.ContainsKey("added"));
        Assert.True(dict.Dict.ContainsKey("added.f"));
    }

    [Fact]
    public void Update_NoChanges_DoesNotSendMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utPex = new UtPex(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_pex"] = 1;
        utPex.Init(handshake);

        var peers = new List<(IPEndPoint Ep, byte Flags)>
        {
            (new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1111), 0x01)
        };

        utPex.Update(peers);
        mockPeer.SentMessages.Clear();

        // Act
        utPex.Update(peers);

        // Assert
        Assert.Empty(mockPeer.SentMessages);
    }

    [Fact]
    public void Update_DroppedPeer_SendsPexMessageWithDropped()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utPex = new UtPex(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_pex"] = 1;
        utPex.Init(handshake);

        var ep1 = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1111);
        var peers = new List<(IPEndPoint Ep, byte Flags)> { (ep1, 0x01) };
        utPex.Update(peers);
        mockPeer.SentMessages.Clear();

        // Act
        utPex.Update(new List<(IPEndPoint Ep, byte Flags)>());

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
        Assert.True(dict.Dict.ContainsKey("dropped"));
        Assert.False(dict.Dict.ContainsKey("added"));
    }

    [Fact]
    public async Task HandleMessage_ParsesAddedFlags()
    {
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utPex = new UtPex(mockPeer);

        var ipv4 = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881);
        var ipv6 = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 51413);

        var addedBytes = new List<byte>();
        addedBytes.AddRange(ipv4.Address.GetAddressBytes());
        addedBytes.Add((byte)(ipv4.Port >> 8));
        addedBytes.Add((byte)(ipv4.Port & 0xFF));

        var added6Bytes = new List<byte>();
        added6Bytes.AddRange(ipv6.Address.GetAddressBytes());
        added6Bytes.Add((byte)(ipv6.Port >> 8));
        added6Bytes.Add((byte)(ipv6.Port & 0xFF));

        var dict = new BDict();
        dict.Dict["added"] = new BString(addedBytes.ToArray());
        dict.Dict["added.f"] = new BString(new byte[] { 0x05 });
        dict.Dict["added6"] = new BString(added6Bytes.ToArray());
        dict.Dict["added6.f"] = new BString(new byte[] { 0x02 });

        var payload = BencodeWriter.Write(dict);
        await utPex.HandleMessageAsync(payload);

        Assert.Equal(2, listener.Added.Count);
        Assert.Equal(new[] { ipv4, ipv6 }, listener.Added);
        Assert.Equal(new byte[] { 0x05, 0x02 }, listener.AddedFlags);
        Assert.Empty(listener.Dropped);
    }

    [Fact]
    public void Update_WithoutInit_DoesNotSendMessage()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utPex = new UtPex(mockPeer);
        // Note: Init is NOT called

        var peers = new List<(IPEndPoint Ep, byte Flags)>
        {
            (new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1111), 0x01)
        };

        // Act
        utPex.Update(peers);

        // Assert - should not send anything
        Assert.Empty(mockPeer.SentMessages);
    }

    [Fact]
    public void Update_IPv6Peers_SendsPexMessageWithAdded6()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utPex = new UtPex(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_pex"] = 1;
        utPex.Init(handshake);

        var peers = new List<(IPEndPoint Ep, byte Flags)>
        {
            (new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6881), 0x04) // uTP flag
        };

        // Act
        utPex.Update(peers);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
        Assert.True(dict.Dict.ContainsKey("added6"));
        Assert.True(dict.Dict.ContainsKey("added6.f"));
        Assert.False(dict.Dict.ContainsKey("added")); // No IPv4
    }

    [Fact]
    public void Update_MixedIPv4AndIPv6_SendsBothTypes()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utPex = new UtPex(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_pex"] = 1;
        utPex.Init(handshake);

        var peers = new List<(IPEndPoint Ep, byte Flags)>
        {
            (new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881), 0x01),
            (new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6881), 0x04)
        };

        // Act
        utPex.Update(peers);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
        Assert.True(dict.Dict.ContainsKey("added"));
        Assert.True(dict.Dict.ContainsKey("added6"));
    }

    [Fact]
    public async Task HandleMessage_ParsesDroppedPeers()
    {
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utPex = new UtPex(mockPeer);

        var ipv4 = new IPEndPoint(IPAddress.Parse("192.168.1.1"), 51413);

        var droppedBytes = new List<byte>();
        droppedBytes.AddRange(ipv4.Address.GetAddressBytes());
        droppedBytes.Add((byte)(ipv4.Port >> 8));
        droppedBytes.Add((byte)(ipv4.Port & 0xFF));

        var dict = new BDict();
        dict.Dict["dropped"] = new BString(droppedBytes.ToArray());

        var payload = BencodeWriter.Write(dict);
        await utPex.HandleMessageAsync(payload);

        Assert.Empty(listener.Added);
        Assert.Single(listener.Dropped);
        Assert.Equal(ipv4, listener.Dropped[0]);
    }

    [Fact]
    public async Task HandleMessage_ParsesDropped6Peers()
    {
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utPex = new UtPex(mockPeer);

        var ipv6 = new IPEndPoint(IPAddress.Parse("2001:db8::dead:beef"), 8080);

        var dropped6Bytes = new List<byte>();
        dropped6Bytes.AddRange(ipv6.Address.GetAddressBytes());
        dropped6Bytes.Add((byte)(ipv6.Port >> 8));
        dropped6Bytes.Add((byte)(ipv6.Port & 0xFF));

        var dict = new BDict();
        dict.Dict["dropped6"] = new BString(dropped6Bytes.ToArray());

        var payload = BencodeWriter.Write(dict);
        await utPex.HandleMessageAsync(payload);

        Assert.Empty(listener.Added);
        Assert.Single(listener.Dropped);
        Assert.Equal(ipv6, listener.Dropped[0]);
    }

    [Fact]
    public async Task HandleMessage_MalformedData_DoesNotCrash()
    {
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utPex = new UtPex(mockPeer);

        // Not valid bencode
        var data = new byte[] { 0x00, 0x01, 0x02 };

        // Act - should not throw
        await utPex.HandleMessageAsync(data);

        // Assert
        Assert.Empty(listener.Added);
        Assert.Empty(listener.Dropped);
    }

    [Fact]
    public async Task HandleMessage_EmptyDict_DoesNotNotifyListener()
    {
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utPex = new UtPex(mockPeer);

        var dict = new BDict();
        var payload = BencodeWriter.Write(dict);
        await utPex.HandleMessageAsync(payload);

        // Should not notify when nothing to report
        Assert.Empty(listener.Added);
        Assert.Empty(listener.Dropped);
    }

    [Fact]
    public async Task HandleMessage_TruncatedPeerData_ParsesValidPeersOnly()
    {
        var listener = new MockPeerListener();
        var mockPeer = new MockPeerCommunication { Listener = listener };
        var utPex = new UtPex(mockPeer);

        // One complete IPv4 peer (6 bytes) + 3 extra bytes (incomplete peer)
        var addedBytes = new byte[] { 1, 2, 3, 4, 0x1A, 0xE1, 5, 6, 7 };

        var dict = new BDict();
        dict.Dict["added"] = new BString(addedBytes);
        dict.Dict["added.f"] = new BString(new byte[] { 0x01 });

        var payload = BencodeWriter.Write(dict);
        await utPex.HandleMessageAsync(payload);

        // Should only parse the complete peer
        Assert.Single(listener.Added);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), listener.Added[0].Address);
        Assert.Equal(6881, listener.Added[0].Port);
    }

    [Fact]
    public void Update_DroppedIPv6Peer_SendsDropped6()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utPex = new UtPex(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_pex"] = 1;
        utPex.Init(handshake);

        var ipv6Peer = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6881);
        var peers = new List<(IPEndPoint Ep, byte Flags)> { (ipv6Peer, 0x04) };
        utPex.Update(peers);
        mockPeer.SentMessages.Clear();

        // Act - remove the peer
        utPex.Update(new List<(IPEndPoint Ep, byte Flags)>());

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
        Assert.True(dict.Dict.ContainsKey("dropped6"));
        Assert.False(dict.Dict.ContainsKey("dropped")); // No IPv4
    }

    [Fact]
    public void Update_AllPeerFlags_SendsCorrectFlags()
    {
        // Arrange
        var mockPeer = new MockPeerCommunication();
        var utPex = new UtPex(mockPeer);
        var handshake = new ExtensionHandshake();
        handshake.MessageIds["ut_pex"] = 1;
        utPex.Init(handshake);

        // Test all flag combinations
        byte allFlags = 0x01 | 0x02 | 0x04 | 0x08; // Encryption | Seed | Utp | Holepunch
        var peers = new List<(IPEndPoint Ep, byte Flags)>
        {
            (new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1111), allFlags)
        };

        // Act
        utPex.Update(peers);

        // Assert
        var msg = mockPeer.SentMessages[0];
        var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
        var flags = dict.GetBytes("added.f");
        Assert.NotNull(flags);
        Assert.Equal(allFlags, flags.Value.Span[0]);
    }
}





