using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Text;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using RtcForge;

namespace PeerSharp.WebTorrent.Tests;

public class WebTorrentSessionTests
{
    [Fact]
    public void WebTorrentTrackerUrls_CollectsWebSocketTrackersFromMetadataAndSettings()
    {
        var urls = WebTorrentTrackerUrls.Collect(
            "https://tracker.example/announce",
            ["udp://tracker.example:80/announce", "wss://announce-list.example"],
            [
                ["http://tracker.example/announce", "ws://tier.example"],
                ["wss://announce-list.example"]
            ],
            ["wss://additional.example", "ftp://ignored.example"]);

        Assert.Equal(3, urls.Count);
        Assert.Contains("wss://announce-list.example", urls);
        Assert.Contains("ws://tier.example", urls);
        Assert.Contains("wss://additional.example", urls);
    }

    [Fact]
    public async Task CappedMemoryStream_ThrowsBeforeExceedingLimit()
    {
        using var stream = new CappedMemoryStream(4);

        await stream.WriteAsync(new byte[] { 1, 2 });
        await stream.WriteAsync(new byte[] { 3, 4 });

        await Assert.ThrowsAsync<TrackerMessageTooLargeException>(() => stream.WriteAsync(new byte[] { 5 }.AsMemory()).AsTask());
        Assert.Equal(4, stream.Length);
    }

    [Fact]
    public async Task StartAsync_SendsStartedAnnounceWithOffer()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        string sent = Assert.Single(socket.SentMessages);
        var node = JsonNode.Parse(sent)!;

        Assert.Equal("announce", node["action"]!.GetValue<string>());
        Assert.Equal("started", node["event"]!.GetValue<string>());
        Assert.NotNull(node["offers"]);
        Assert.Single(rtcFactory.Created);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveAnswer_AppliesRemoteDescriptionAndConnectsOfferer()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        string announce = Assert.Single(socket.SentMessages);
        var node = JsonNode.Parse(announce)!;
        string offerId = node["offers"]![0]!["offer_id"]!.GetValue<string>();

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["answer"] = new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        await AssertEventuallyAsync(() => rtcFactory.Created.Single().RemoteDescription?.Type == WebRtcSessionDescriptionType.Answer
            && rtcFactory.Created.Single().ConnectCalls == 1, TimeSpan.FromSeconds(2));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task LocalIceCandidate_IsBufferedUntilRemotePeerIdIsKnown()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var connection = Assert.Single(rtcFactory.Created);
        connection.EmitIceCandidate("candidate:1 1 udp 1 127.0.0.1 5000 typ host");
        Assert.Single(socket.SentMessages);

        string announce = socket.SentMessages[0];
        var node = JsonNode.Parse(announce)!;
        string offerId = node["offers"]![0]!["offer_id"]!.GetValue<string>();

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["answer"] = new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        await AssertEventuallyAsync(() => socket.SentMessages.Count >= 2, TimeSpan.FromSeconds(2));
        var candidateMessage = JsonNode.Parse(socket.SentMessages[1])!;
        Assert.Equal("remote-peer-id", candidateMessage["to_peer_id"]!.GetValue<string>());
        Assert.Equal("candidate:1 1 udp 1 127.0.0.1 5000 typ host", candidateMessage["candidate"]!["candidate"]!.GetValue<string>());
        Assert.Equal("0", candidateMessage["candidate"]!["sdpMid"]!.GetValue<string>());
        Assert.Equal(0, candidateMessage["candidate"]!["sdpMLineIndex"]!.GetValue<int>());

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveCandidate_AppliesRemoteIceCandidate()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        string announce = Assert.Single(socket.SentMessages);
        var node = JsonNode.Parse(announce)!;
        string offerId = node["offers"]![0]!["offer_id"]!.GetValue<string>();

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["answer"] = new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["candidate"] = "candidate:2 1 udp 1 127.0.0.1 5001 typ host"
        }.ToJsonString());

        await AssertEventuallyAsync(() => rtcFactory.Created.Single().RemoteCandidates.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Equal("candidate:2 1 udp 1 127.0.0.1 5001 typ host", rtcFactory.Created.Single().RemoteCandidates[0]);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveCandidateBeforeAnswer_BuffersUntilRemoteDescriptionIsApplied()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        string announce = Assert.Single(socket.SentMessages);
        var node = JsonNode.Parse(announce)!;
        string offerId = node["offers"]![0]!["offer_id"]!.GetValue<string>();

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["candidate"] = "candidate:2 1 udp 1 127.0.0.1 5001 typ host"
        }.ToJsonString());

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["answer"] = new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        await AssertEventuallyAsync(() => rtcFactory.Created.Single().RemoteCandidates.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Equal("candidate:2 1 udp 1 127.0.0.1 5001 typ host", rtcFactory.Created.Single().RemoteCandidates[0]);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ChannelOpen_AttachesWrappedStreamToHost()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var connection = Assert.Single(rtcFactory.Created);
        var channel = Assert.IsType<FakeWebRtcDataChannel>(connection.LastCreatedChannel);

        channel.EmitOpened();

        await AssertEventuallyAsync(() => host.AttachedStreams.Count == 1, TimeSpan.FromSeconds(2));
        Assert.True(host.AttachedStreams[0].Initiator);

        await host.AttachedStreams[0].Stream.WriteAsync(new byte[] { 1, 2, 3 });

        byte[] payload = Assert.Single(channel.SentPayloads);
        Assert.Equal([1, 2, 3], payload);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task AttachedStream_ReadAsyncReceivesDataChannelMessages()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var channel = Assert.IsType<FakeWebRtcDataChannel>(Assert.Single(rtcFactory.Created).LastCreatedChannel);
        channel.EmitOpened();

        await AssertEventuallyAsync(() => host.AttachedStreams.Count == 1, TimeSpan.FromSeconds(2));
        channel.EmitMessage([10, 11, 12, 13]);

        byte[] first = new byte[2];
        byte[] second = new byte[2];
        int firstCount = await host.AttachedStreams[0].Stream.ReadAsync(first);
        int secondCount = await host.AttachedStreams[0].Stream.ReadAsync(second);

        Assert.Equal(2, firstCount);
        Assert.Equal(2, secondCount);
        Assert.Equal([10, 11], first);
        Assert.Equal([12, 13], second);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveOffer_SendsAnswerAndAttachesInboundChannelAsResponder()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory
        {
            AnswerSdp = string.Join("\r\n",
                "v=0",
                "o=- 1 1 IN IP4 127.0.0.1",
                "s=fake",
                "c=IN IP4 0.0.0.0",
                "t=0 0",
                "a=candidate:1 1 udp 2130706431 192.168.1.10 5000 typ host",
                "a=candidate:2 1 tcp 2130706431 192.168.1.10 9 typ host tcptype active",
                "")
        };
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var announce = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = announce["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = "remote-offer-id",
            ["offer"] = new JsonObject
            {
                ["type"] = "offer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        await AssertEventuallyAsync(() => rtcFactory.Created.Count == 2
            && rtcFactory.Created[1].RemoteDescription?.Type == WebRtcSessionDescriptionType.Offer
            && rtcFactory.Created[1].ConnectCalls == 1
            && socket.SentMessages.Count >= 2, TimeSpan.FromSeconds(2));

        var answer = JsonNode.Parse(socket.SentMessages[1])!;
        Assert.Equal("remote-peer-id", answer["to_peer_id"]!.GetValue<string>());
        Assert.Equal("remote-offer-id", answer["offer_id"]!.GetValue<string>());
        string answerSdp = answer["answer"]!["sdp"]!.GetValue<string>();
        Assert.Contains("192.168.1.10", answerSdp);
        Assert.DoesNotContain("tcptype active", answerSdp);

        var inboundChannel = new FakeWebRtcDataChannel("bittorrent");
        rtcFactory.Created[1].EmitInboundChannel(inboundChannel);
        inboundChannel.EmitOpened();

        await AssertEventuallyAsync(() => host.AttachedStreams.Count == 1, TimeSpan.FromSeconds(2));
        Assert.False(host.AttachedStreams[0].Initiator);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveAnswerEnvelopeWithOffer_TreatsItAsInboundOffer()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var announce = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        string offerId = announce["offers"]![0]!["offer_id"]!.GetValue<string>();
        var outboundConnection = Assert.Single(rtcFactory.Created);

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = announce["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["answer"] = new JsonObject
            {
                ["type"] = "offer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        await AssertEventuallyAsync(() => rtcFactory.Created.Count == 2
            && outboundConnection.Disposed
            && rtcFactory.Created[1].RemoteDescription?.Type == WebRtcSessionDescriptionType.Offer
            && rtcFactory.Created[1].ConnectCalls == 1
            && socket.SentMessages.Count >= 2, TimeSpan.FromSeconds(2));

        var response = JsonNode.Parse(socket.SentMessages[1])!;
        Assert.Equal("remote-peer-id", response["to_peer_id"]!.GetValue<string>());
        Assert.Equal(offerId, response["offer_id"]!.GetValue<string>());
        Assert.Equal("answer", response["answer"]!["type"]!.GetValue<string>());

        var inboundChannel = new FakeWebRtcDataChannel("bittorrent");
        rtcFactory.Created[1].EmitInboundChannel(inboundChannel);
        inboundChannel.EmitOpened();

        await AssertEventuallyAsync(() => host.AttachedStreams.Count == 1, TimeSpan.FromSeconds(2));
        Assert.False(host.AttachedStreams[0].Initiator);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveOffer_SendsAnswerBeforeTrickledCandidates()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory
        {
            AnswerSdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=fake\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n",
            AnswerIceCandidates =
            [
                "candidate:1 1 udp 1 127.0.0.1 5000 typ host",
                "candidate:2 1 udp 1 127.0.0.1 5001 typ host"
            ]
        };
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var announce = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = announce["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = "remote-offer-id",
            ["offer"] = new JsonObject
            {
                ["type"] = "offer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        await AssertEventuallyAsync(() => socket.SentMessages.Count >= 4, TimeSpan.FromSeconds(2));

        Assert.NotNull(JsonNode.Parse(socket.SentMessages[1])!["answer"]);
        Assert.NotNull(JsonNode.Parse(socket.SentMessages[2])!["candidate"]);
        Assert.NotNull(JsonNode.Parse(socket.SentMessages[3])!["candidate"]);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveCandidateBeforeInboundOffer_BuffersUntilPeerIsCreated()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var announce = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = announce["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = "remote-offer-id",
            ["candidate"] = "candidate:2 1 udp 1 127.0.0.1 5001 typ host"
        }.ToJsonString());

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = announce["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = "remote-offer-id",
            ["offer"] = new JsonObject
            {
                ["type"] = "offer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        await AssertEventuallyAsync(() => rtcFactory.Created.Count == 2
            && rtcFactory.Created[1].RemoteCandidates.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Equal("candidate:2 1 udp 1 127.0.0.1 5001 typ host", rtcFactory.Created[1].RemoteCandidates[0]);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Announce_FiltersUnsupportedIceCandidatesFromOfferSdp()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory
        {
            OfferSdp = string.Join("\r\n",
                "v=0",
                "o=- 1 1 IN IP4 127.0.0.1",
                "s=fake",
                "c=IN IP4 0.0.0.0",
                "t=0 0",
                "a=candidate:1 1 udp 2130706431 192.168.1.10 5000 typ host",
                "a=candidate:2 1 udp 2130706431 fe80::1 5001 typ host",
                "a=end-of-candidates",
                "")
        };
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        string announce = Assert.Single(socket.SentMessages);
        string sdp = JsonNode.Parse(announce)!["offers"]![0]!["offer"]!["sdp"]!.GetValue<string>();
        Assert.Contains("192.168.1.10", sdp);
        Assert.DoesNotContain("fe80::1", sdp);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveOffer_PreservesMdnsHostnameCandidates()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var announce = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        string offerSdp = string.Join("\r\n",
            "v=0",
            "o=- 1 1 IN IP4 127.0.0.1",
            "s=remote",
            "c=IN IP4 0.0.0.0",
            "t=0 0",
            "a=candidate:0 1 UDP 2122187007 c405ac37-a4d5-4366-80e7-f3c242d6294a.local 56700 typ host",
            "a=candidate:3 1 UDP 2122252543 8640816a-6a01-4fe6-8357-3f2866085cf7.local 56701 typ host",
            "a=candidate:6 1 TCP 2105458943 c405ac37-a4d5-4366-80e7-f3c242d6294a.local 9 typ host tcptype active",
            "a=end-of-candidates",
            "");
        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = announce["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = "remote-offer-id",
            ["offer"] = new JsonObject { ["type"] = "offer", ["sdp"] = offerSdp }
        }.ToJsonString());

        await AssertEventuallyAsync(() => rtcFactory.Created.Count == 2
            && rtcFactory.Created[1].RemoteDescription?.Type == WebRtcSessionDescriptionType.Offer, TimeSpan.FromSeconds(2));

        string appliedSdp = rtcFactory.Created[1].RemoteDescription!.Sdp;
        Assert.Contains("c405ac37-a4d5-4366-80e7-f3c242d6294a.local", appliedSdp);
        Assert.Contains("8640816a-6a01-4fe6-8357-3f2866085cf7.local", appliedSdp);
        Assert.DoesNotContain("tcptype active", appliedSdp);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveCandidate_ForwardsMdnsHostnameCandidate()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        string announce = Assert.Single(socket.SentMessages);
        var node = JsonNode.Parse(announce)!;
        string offerId = node["offers"]![0]!["offer_id"]!.GetValue<string>();

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["answer"] = new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["candidate"] = "candidate:1 1 udp 2122252543 8640816a-6a01-4fe6-8357-3f2866085cf7.local 56701 typ host"
        }.ToJsonString());

        await AssertEventuallyAsync(() => rtcFactory.Created.Single().RemoteCandidates.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Contains(".local", rtcFactory.Created.Single().RemoteCandidates[0]);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveCandidate_DropsIpv6LiteralCandidate()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        string announce = Assert.Single(socket.SentMessages);
        var node = JsonNode.Parse(announce)!;
        string offerId = node["offers"]![0]!["offer_id"]!.GetValue<string>();

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["answer"] = new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["candidate"] = "candidate:1 1 udp 1 fe80::1 5001 typ host"
        }.ToJsonString());

        // Give the session a chance to process the message
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(rtcFactory.Created.Single().RemoteCandidates);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Announce_IncludesSessionRelativeDataUploadedAndDownloaded()
    {
        // Host starts with non-zero lifetime totals; session baseline should capture those at
        // StartAsync so subsequent announces carry per-session deltas (BEP-3 semantics), not
        // the host's monotonic totals.
        var host = new FakePeerTransportHost { DataUploaded = 4321, DataDownloaded = 8765 };
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var initial = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        Assert.Equal(0, initial["uploaded"]!.GetValue<long>());
        Assert.Equal(0, initial["downloaded"]!.GetValue<long>());

        host.DataUploaded = 4321 + 100;
        host.DataDownloaded = 8765 + 250;

        await session.DisposeAsync();

        var stopped = JsonNode.Parse(socket.SentMessages.Last())!;
        Assert.Equal("stopped", stopped["event"]!.GetValue<string>());
        Assert.Equal(100, stopped["uploaded"]!.GetValue<long>());
        Assert.Equal(250, stopped["downloaded"]!.GetValue<long>());
    }

    [Fact]
    public async Task Finished_SendsCompletedEventOncePerTracker()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();
        host.Finished = true;

        await AssertEventuallyAsync(
            () => socket.SentMessages.Any(m => JsonNode.Parse(m)!["event"]?.GetValue<string>() == "completed"),
            TimeSpan.FromSeconds(2));

        int completedCount = socket.SentMessages.Count(m => JsonNode.Parse(m)!["event"]?.GetValue<string>() == "completed");
        Assert.Equal(1, completedCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task GetTrackerHealth_ReportsConnectedForInitialTracker()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var health = Assert.Single(session.GetTrackerHealth());
        Assert.True(health.IsConnected);
        Assert.Equal("wss://tracker.example", health.Url);
        Assert.Null(health.LastError);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task GetDiagnostics_ReportsTrackerAndPendingCounts()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var diagnostics = session.GetDiagnostics();
        Assert.Equal(1, diagnostics.TrackerCount);
        Assert.Equal(1, diagnostics.ConnectedTrackers);
        Assert.Equal(0, diagnostics.ReconnectingTrackers);
        Assert.Equal(1, diagnostics.PendingPeerCount);
        Assert.Equal(0, diagnostics.EarlyCandidateOfferCount);
        Assert.False(diagnostics.TorrentFinished);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task TrackerSocketDropped_ReconnectsAndResumesAnnounces()
    {
        var timeProvider = new FakeTimeProvider();
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket1 = new FakeWebSocketConnection();
        var socket2 = new FakeWebSocketConnection();
        var options = new WebTorrentSessionOptions { OffersPerTracker = 1, TimeProvider = timeProvider };
        var session = new WebTorrentSession(host, host, options, rtcFactory, new FakeWebSocketConnectionFactory(socket1, socket2));

        await session.StartAsync();

        Assert.Single(socket1.SentMessages);
        Assert.True(Assert.Single(session.GetTrackerHealth()).IsConnected);

        // Close the first socket's receive channel to drop the connection and force the
        // receive loop's tail path to schedule a reconnect.
        socket1.CloseIncoming();

        await AssertEventuallyAsync(
            () =>
            {
                var health = session.GetTrackerHealth()[0];
                return !health.IsConnected && health.ConsecutiveFailures > 0;
            },
            TimeSpan.FromSeconds(2));

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        await AssertEventuallyAsync(
            () => session.GetTrackerHealth()[0].IsConnected && socket2.SentMessages.Count >= 1,
            TimeSpan.FromSeconds(5));

        var reannounce = JsonNode.Parse(socket2.SentMessages[0])!;
        Assert.Equal("started", reannounce["event"]!.GetValue<string>());

        await session.DisposeAsync();
    }

    [Fact]
    public async Task InitialTransientDnsFailure_RetriesAndConnectsWhenTrackerBecomesReachable()
    {
        var timeProvider = new FakeTimeProvider();
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket1 = new FakeWebSocketConnection { ConnectException = new SocketException((int)SocketError.TryAgain) };
        var socket2 = new FakeWebSocketConnection();
        var session = new WebTorrentSession(
            host,
            host,
            new WebTorrentSessionOptions { OffersPerTracker = 1, TimeProvider = timeProvider },
            rtcFactory,
            new FakeWebSocketConnectionFactory(socket1, socket2));

        await session.StartAsync();

        var initialHealth = Assert.Single(session.GetTrackerHealth());
        Assert.False(initialHealth.IsConnected);
        Assert.Equal($"socket error ({SocketError.TryAgain})", initialHealth.LastError);

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        await AssertEventuallyAsync(
            () => session.GetTrackerHealth()[0].IsConnected && socket2.SentMessages.Count == 1,
            TimeSpan.FromSeconds(5));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task InitialConnectTimeout_SkipsSlowTrackerAndConnectsNextTracker()
    {
        var timeProvider = new FakeTimeProvider();
        var host = new FakePeerTransportHost("wss://slow.example", "wss://fast.example");
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowConnectRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket1 = new FakeWebSocketConnection
        {
            ConnectStep = ct =>
            {
                connectStarted.SetResult();
                return slowConnectRelease.Task.WaitAsync(ct);
            }
        };
        var socket2 = new FakeWebSocketConnection();
        var session = new WebTorrentSession(
            host,
            host,
            new WebTorrentSessionOptions
            {
                OffersPerTracker = 1,
                TimeProvider = timeProvider,
                TrackerConnectTimeout = TimeSpan.FromSeconds(1)
            },
            rtcFactory,
            new FakeWebSocketConnectionFactory(socket1, socket2));

        var startTask = session.StartAsync();
        await connectStarted.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await startTask;

        var health = session.GetTrackerHealth();
        Assert.Equal(2, health.Count);
        Assert.False(health[0].IsConnected);
        Assert.Equal("tracker connect timed out", health[0].LastError);
        Assert.True(health[1].IsConnected);
        Assert.Single(socket2.SentMessages);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_IgnoresLateMessagesFromOldReceiveLoop()
    {
        var timeProvider = new FakeTimeProvider();
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var oldReadRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket1 = new FakeWebSocketConnection { ThrowOnSendCallNumber = 2 };
        socket1.EnqueueReceiveStep(ct => oldReadRelease.Task.WaitAsync(ct));
        var socket2 = new FakeWebSocketConnection();
        var session = new WebTorrentSession(
            host,
            host,
            new WebTorrentSessionOptions { OffersPerTracker = 1, TimeProvider = timeProvider },
            rtcFactory,
            new FakeWebSocketConnectionFactory(socket1, socket2));

        await session.StartAsync();

        timeProvider.Advance(TimeSpan.FromSeconds(121));

        await AssertEventuallyAsync(
            () =>
            {
                var health = session.GetTrackerHealth()[0];
                return !health.IsConnected && health.ConsecutiveFailures > 0;
            },
            TimeSpan.FromSeconds(2));

        Assert.Empty(socket2.SentMessages);

        string infoHash = EncodeLatin1(host.Hash.ToArray());
        oldReadRelease.SetResult(new JsonObject
        {
            ["info_hash"] = infoHash,
            ["peer_id"] = "late-peer",
            ["offer_id"] = "late-offer",
            ["offer"] = new JsonObject
            {
                ["type"] = "offer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=late\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        await AssertEventuallyAsync(
            () => session.GetTrackerHealth()[0].IsConnected && socket2.SentMessages.Count == 1,
            TimeSpan.FromSeconds(5));

        Assert.Equal(3, rtcFactory.Created.Count);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Completed_IsResentAfterReconnect()
    {
        var timeProvider = new FakeTimeProvider();
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket1 = new FakeWebSocketConnection();
        var socket2 = new FakeWebSocketConnection();
        var session = new WebTorrentSession(
            host,
            host,
            new WebTorrentSessionOptions { OffersPerTracker = 1, TimeProvider = timeProvider },
            rtcFactory,
            new FakeWebSocketConnectionFactory(socket1, socket2));

        await session.StartAsync();

        host.Finished = true;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await AssertEventuallyAsync(
            () => socket1.SentMessages.Any(m => JsonNode.Parse(m)!["event"]?.GetValue<string>() == "completed"),
            TimeSpan.FromSeconds(2));

        socket1.CloseIncoming();

        await AssertEventuallyAsync(
            () =>
            {
                var health = session.GetTrackerHealth()[0];
                return !health.IsConnected && health.ConsecutiveFailures > 0;
            },
            TimeSpan.FromSeconds(2));

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        await AssertEventuallyAsync(
            () => session.GetTrackerHealth()[0].IsConnected && socket2.SentMessages.Count >= 1,
            TimeSpan.FromSeconds(5));

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        await AssertEventuallyAsync(
            () => socket2.SentMessages.Any(m => JsonNode.Parse(m)!["event"]?.GetValue<string>() == "completed"),
            TimeSpan.FromSeconds(2));

        Assert.Equal(1, socket2.SentMessages.Count(m => JsonNode.Parse(m)!["event"]?.GetValue<string>() == "completed"));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task HandleAnswer_CleansUpConnectionWhenSetRemoteDescriptionThrows()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory { ThrowOnSetRemoteDescription = true };
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var node = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        string offerId = node["offers"]![0]!["offer_id"]!.GetValue<string>();

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["answer"] = new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        var connection = rtcFactory.Created.Single();
        await AssertEventuallyAsync(() => connection.Disposed, TimeSpan.FromSeconds(2));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task HandleAnswer_CleansUpConnectionWhenConnectReturnsFalse()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory { ConnectResult = false };
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var node = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        string offerId = node["offers"]![0]!["offer_id"]!.GetValue<string>();

        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = node["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = offerId,
            ["answer"] = new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        var connection = rtcFactory.Created.Single();
        await AssertEventuallyAsync(() => connection.Disposed, TimeSpan.FromSeconds(2));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveOffer_CleansUpConnectionWhenAnswerSendFails()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection { ThrowOnSendCallNumber = 2 };
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var announce = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        socket.EnqueueReceive(new JsonObject
        {
            ["info_hash"] = announce["info_hash"]!.GetValue<string>(),
            ["peer_id"] = "remote-peer-id",
            ["offer_id"] = "remote-offer-id",
            ["offer"] = new JsonObject
            {
                ["type"] = "offer",
                ["sdp"] = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=remote\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"
            }
        }.ToJsonString());

        await AssertEventuallyAsync(() => rtcFactory.Created.Count >= 2, TimeSpan.FromSeconds(2));
        var inboundConnection = rtcFactory.Created[1];
        await AssertEventuallyAsync(() => inboundConnection.Disposed, TimeSpan.FromSeconds(2));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task PendingOffer_ExpiresAndIsRemoved()
    {
        var timeProvider = new FakeTimeProvider();
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(
            host,
            host,
            new WebTorrentSessionOptions { OffersPerTracker = 1, TimeProvider = timeProvider },
            rtcFactory,
            new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        var originalConnection = Assert.Single(rtcFactory.Created);
        timeProvider.Advance(TimeSpan.FromSeconds(31));

        await AssertEventuallyAsync(() => originalConnection.Disposed, TimeSpan.FromSeconds(2));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_SendsStoppedAnnounce()
    {
        var host = new FakePeerTransportHost();
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(host, host, new WebTorrentSessionOptions { OffersPerTracker = 1 }, rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();
        await session.DisposeAsync();

        var stopped = JsonNode.Parse(socket.SentMessages.Last())!;
        Assert.Equal("stopped", stopped["event"]!.GetValue<string>());
    }

    private static async Task AssertEventuallyAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate());
    }

    private static string EncodeLatin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    private sealed class FakePeerTransportHost : ITorrent, IPeerTransportHost
    {
        public FakePeerTransportHost(params string[] trackers)
        {
            Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
            byte[] peerId = new byte[20];
            for (int i = 0; i < peerId.Length; i++)
            {
                peerId[i] = (byte)(i + 1);
            }

            PeerId = peerId;
            Trackers = new FakeTrackers(trackers.Length == 0 ? ["wss://tracker.example"] : trackers);
        }

        public List<(Stream Stream, bool Initiator)> AttachedStreams { get; } = new();
        public long DataDownloaded { get; set; }
        public long DataLeft { get; set; } = 1024;
        public long DataUploaded { get; set; }
        public int DownloadLimitBytesPerSecond { get; set; }
        public int DiskReadLimitBytesPerSecond { get; set; }
        public int DiskWriteLimitBytesPerSecond { get; set; }
        public DownloadStrategy DownloadStrategy { get; set; }
        public ITorrentEvents? Events => null;
        public int FileCount => 0;
        public IFiles Files => throw new NotSupportedException();
        public IFileTransfer FileTransfer => throw new NotSupportedException();
        public bool Finished { get; set; }
        public ulong FinishedBytes => 0;
        public ulong FinishedSelectedBytes => 0;
        public InfoHash Hash { get; }
        public InfoHash HashV2 => InfoHash.EmptyV2;
        public bool HasMetadata => true;
        public bool HasStreamableFiles => false;
        public Exception? LastException => null;
        public IMetadataDownload? MetadataDownload => null;
        public string Name => "fake";
        public IPeers Peers => throw new NotSupportedException();
        public ReadOnlyMemory<byte> PeerId { get; }
        public int PieceCount => 0;
        public uint PieceSize => 0;
        public int PiecesReceived => 0;
        public float Progress => 0;
        public bool QueueAutoStart { get; set; }
        public int QueuePriority { get; set; }
        public float? RatioLimit { get; set; }
        public TimeSpan? SeedTimeLimit { get; set; }
        public bool SelectionFinished => false;
        public float SelectionProgress => 0;
        public bool Started => false;
        public TorrentState State => TorrentState.Stopped;
        public DateTimeOffset StateTimestamp => DateTimeOffset.UtcNow;
        public IReadOnlyList<int> StreamableFileIndices => [];
        public DateTimeOffset TimeAdded { get; } = DateTimeOffset.UtcNow;
        public long TotalSize => DataLeft;
        public ITrackers Trackers { get; }
        public int UploadLimitBytesPerSecond { get; set; }

        public Task AttachPeerTransportAsync(Stream stream, bool initiator, CancellationToken cancellationToken = default)
        {
            AttachedStreams.Add((stream, initiator));
            return Task.CompletedTask;
        }

        public Task<int> ForceRecheckAsync(IProgress<PieceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<TorrentFileInfo> GetAllFileInfo() => throw new NotSupportedException();
        public IReadOnlyList<FileSelection> GetAllFileSelections() => throw new NotSupportedException();
        public TorrentFileInfo GetFileInfo(int fileIndex) => throw new NotSupportedException();
        public FileSelection GetFileSelection(int fileIndex) => throw new NotSupportedException();
        public byte[] GetPieceBitfield() => [];
        public TorrentResumeData GetResumeData() => throw new NotSupportedException();
        public Task<Stream> OpenStreamAsync(int fileIndex, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetAllFilesPriorityAsync(Priority priority, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetDownloadPathAsync(string path) => throw new NotSupportedException();
        public Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void RegisterPeerTransport(IPeerTransport transport) => throw new NotSupportedException();
    }

    private sealed class FakeTrackers : ITrackers
    {
        private readonly List<TrackerStatus> _trackers;

        public FakeTrackers(IEnumerable<string> urls)
        {
            _trackers = urls.Select(url => new TrackerStatus(url)).ToList();
        }

        public void AddTracker(string url) => _trackers.Add(new TrackerStatus(url));
        public Task AnnounceAsync(string? url = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IReadOnlyList<TrackerStatus> GetTrackers() => _trackers;
        public bool RemoveTracker(string url) => _trackers.RemoveAll(t => t.Url == url) > 0;
    }

    private sealed class FakeWebRtcConnectionFactory : IWebRtcConnectionFactory
    {
        public List<FakeWebRtcConnection> Created { get; } = new();
        public bool ConnectResult { get; set; } = true;
        public string OfferSdp { get; set; } = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=fake\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n";
        public string AnswerSdp { get; set; } = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=fake\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n";
        public IReadOnlyList<string> AnswerIceCandidates { get; set; } = [];
        public bool ThrowOnSetRemoteDescription { get; set; }

        public IWebRtcConnection Create()
        {
            var connection = new FakeWebRtcConnection(OfferSdp, AnswerSdp)
            {
                ConnectResult = ConnectResult,
                ThrowOnSetRemoteDescription = ThrowOnSetRemoteDescription
            };
            foreach (var candidate in AnswerIceCandidates)
            {
                connection.AnswerIceCandidates.Add(candidate);
            }

            Created.Add(connection);
            return connection;
        }
    }

    private sealed class FakeWebRtcConnection : IWebRtcConnection
    {
        private readonly System.Threading.Channels.Channel<WebRtcIceCandidateDescription> _iceCandidates = System.Threading.Channels.Channel.CreateUnbounded<WebRtcIceCandidateDescription>();
        private readonly System.Threading.Channels.Channel<IWebRtcDataChannel> _dataChannels = System.Threading.Channels.Channel.CreateUnbounded<IWebRtcDataChannel>();
        private readonly System.Threading.Channels.Channel<PeerConnectionState> _connectionStates = System.Threading.Channels.Channel.CreateUnbounded<PeerConnectionState>();
        private readonly System.Threading.Channels.Channel<SignalingState> _signalingStates = System.Threading.Channels.Channel.CreateUnbounded<SignalingState>();
        private readonly string _offerSdp;
        private readonly string _answerSdp;

        public FakeWebRtcConnection(string offerSdp, string answerSdp)
        {
            _offerSdp = offerSdp;
            _answerSdp = answerSdp;
        }

        public PeerConnectionState ConnectionState => PeerConnectionState.New;
        public SignalingState SignalingState => SignalingState.Stable;
        public IAsyncEnumerable<WebRtcIceCandidateDescription> IceCandidates => _iceCandidates.Reader.ReadAllAsync();
        public IAsyncEnumerable<IWebRtcDataChannel> DataChannels => _dataChannels.Reader.ReadAllAsync();
        public IAsyncEnumerable<PeerConnectionState> ConnectionStates => _connectionStates.Reader.ReadAllAsync();
        public IAsyncEnumerable<SignalingState> SignalingStates => _signalingStates.Reader.ReadAllAsync();
        public bool ConnectResult { get; set; } = true;
        public int ConnectCalls { get; private set; }
        public bool Disposed { get; private set; }
        public IWebRtcDataChannel? LastCreatedChannel { get; private set; }
        public List<string> AnswerIceCandidates { get; } = new();
        public List<string> RemoteCandidates { get; } = new();
        public WebRtcSessionDescription? RemoteDescription { get; private set; }
        public bool ThrowOnSetRemoteDescription { get; set; }

        public IWebRtcDataChannel CreateDataChannel(string label)
        {
            LastCreatedChannel = new FakeWebRtcDataChannel(label);
            return LastCreatedChannel;
        }

        public Task<Stream> OpenStreamAsync(string label, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Offer, _offerSdp));

        public Task<WebRtcSessionDescription> CreateAnswerAsync(CancellationToken cancellationToken = default)
        {
            foreach (var candidate in AnswerIceCandidates)
            {
                _iceCandidates.Writer.TryWrite(new WebRtcIceCandidateDescription(candidate));
            }

            return Task.FromResult(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Answer, _answerSdp));
        }

        public Task<WebRtcSessionDescription> CreateOfferAndSetLocalAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Offer, _offerSdp));

        public async Task<WebRtcSessionDescription> AcceptOfferAsync(WebRtcSessionDescription offer, CancellationToken cancellationToken = default)
        {
            await SetRemoteDescriptionAsync(offer, cancellationToken).ConfigureAwait(false);
            return new WebRtcSessionDescription(WebRtcSessionDescriptionType.Answer, _answerSdp);
        }

        public Task SetAnswerAsync(WebRtcSessionDescription answer, CancellationToken cancellationToken = default)
            => SetRemoteDescriptionAsync(answer, cancellationToken);

        public Task SetLocalDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetRemoteDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSetRemoteDescription)
            {
                throw new InvalidOperationException("simulated failure");
            }

            RemoteDescription = description;
            return Task.CompletedTask;
        }

        public Task AddRemoteIceCandidateAsync(WebRtcIceCandidateDescription candidate, CancellationToken cancellationToken = default)
        {
            RemoteCandidates.Add(candidate.Candidate);
            return Task.CompletedTask;
        }

        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            return Task.FromResult(ConnectResult);
        }

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => ConnectAsync(cancellationToken);

        public void EmitIceCandidate(string candidate)
        {
            _iceCandidates.Writer.TryWrite(new WebRtcIceCandidateDescription(candidate));
        }

        public void EmitInboundChannel(IWebRtcDataChannel channel)
        {
            _dataChannels.Writer.TryWrite(channel);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _iceCandidates.Writer.TryComplete();
            _dataChannels.Writer.TryComplete();
            _connectionStates.Writer.TryComplete();
            _signalingStates.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWebRtcDataChannel : IWebRtcDataChannel
    {
        private readonly System.Threading.Channels.Channel<ReadOnlyMemory<byte>> _messages = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly TaskCompletionSource _openTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeWebRtcDataChannel(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public RTCDataChannelState ReadyState => RTCDataChannelState.Open;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> Messages => _messages.Reader.ReadAllAsync();
        public List<byte[]> SentPayloads { get; } = new();

        public Task WaitUntilOpenAsync(CancellationToken cancellationToken = default)
            => _openTcs.Task.WaitAsync(cancellationToken);

        public Stream AsStream() => throw new NotSupportedException();

        public void EmitOpened() => _openTcs.TrySetResult();

        public void EmitMessage(byte[] payload) => _messages.Writer.TryWrite(payload);

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            SentPayloads.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWebSocketConnectionFactory : IWebSocketConnectionFactory
    {
        private readonly Queue<IWebSocketConnection> _sockets;

        public FakeWebSocketConnectionFactory(params IWebSocketConnection[] sockets)
        {
            if (sockets.Length == 0)
            {
                throw new ArgumentException("At least one socket required", nameof(sockets));
            }
            _sockets = new Queue<IWebSocketConnection>(sockets);
        }

        public IWebSocketConnection Create()
        {
            if (_sockets.Count == 0)
            {
                throw new InvalidOperationException("Fake socket factory exhausted");
            }
            return _sockets.Dequeue();
        }
    }

    private sealed class FakeWebSocketConnection : IWebSocketConnection
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly Queue<Func<CancellationToken, Task<string>>> _receiveSteps = new();
        private readonly Lock _receiveLock = new();
        private int _sendCalls;

        public Exception? ConnectException { get; set; }
        public Func<CancellationToken, Task>? ConnectStep { get; set; }
        public int ThrowOnSendCallNumber { get; set; }

        public List<string> SentMessages { get; } = new();

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (ConnectStep != null)
            {
                return ConnectStep(cancellationToken);
            }

            return ConnectException is null
                ? Task.CompletedTask
                : Task.FromException(ConnectException);
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            if (ThrowOnSendCallNumber > 0 && Interlocked.Increment(ref _sendCalls) == ThrowOnSendCallNumber)
            {
                throw new IOException("simulated send failure");
            }

            SentMessages.Add(text);
            return Task.CompletedTask;
        }

        public async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            Func<CancellationToken, Task<string>>? step = null;
            lock (_receiveLock)
            {
                if (_receiveSteps.Count > 0)
                {
                    step = _receiveSteps.Dequeue();
                }
            }

            if (step != null)
            {
                return await step(cancellationToken);
            }

            return await _incoming.Reader.ReadAsync(cancellationToken);
        }

        public void EnqueueReceive(string message)
        {
            _incoming.Writer.TryWrite(message);
        }

        public void EnqueueReceiveStep(Func<CancellationToken, Task<string>> step)
        {
            lock (_receiveLock)
            {
                _receiveSteps.Enqueue(step);
            }
        }

        public void CloseIncoming()
        {
            _incoming.Writer.TryComplete();
        }

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
