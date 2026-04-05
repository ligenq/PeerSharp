using System.Text.Json.Nodes;
using System.Threading.Channels;
using PeerSharp.Internals;
using PeerSharp.WebTorrent;
using WebRtcSharp;

namespace PeerSharp.Tests.Integration;

public class WebTorrentSessionTests
{
    [Fact]
    public async Task StartAsync_SendsStartedAnnounceWithOffer()
    {
        string path = CreateTempPath();
        var torrent = CreateTorrent(path, "wss://tracker.example");
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(torrent, new WebTorrentSessionOptions(), rtcFactory, new FakeWebSocketConnectionFactory(socket));

        await session.StartAsync();

        string sent = Assert.Single(socket.SentMessages);
        var node = JsonNode.Parse(sent)!;

        Assert.Equal("announce", node["action"]!.GetValue<string>());
        Assert.Equal("started", node["event"]!.GetValue<string>());
        Assert.NotNull(node["offers"]);
        Assert.Equal(1, rtcFactory.Created.Count);

        await session.DisposeAsync();
        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task ReceiveAnswer_AppliesRemoteDescriptionAndConnectsOfferer()
    {
        string path = CreateTempPath();
        var torrent = CreateTorrent(path, "wss://tracker.example");
        var rtcFactory = new FakeWebRtcConnectionFactory();
        var socket = new FakeWebSocketConnection();
        var session = new WebTorrentSession(torrent, new WebTorrentSessionOptions(), rtcFactory, new FakeWebSocketConnectionFactory(socket));

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
        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    private static Torrent CreateTorrent(string path, string trackerUrl)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize, Offset = 0 });
        metadata.Announce = trackerUrl;
        metadata.AnnounceList.Add(trackerUrl);
        metadata.AnnounceTiers.Add(new List<string> { trackerUrl });
        return TorrentTestUtility.CreateMinimal(metadata, path);
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "PeerSharp_WebTorrentTests", Guid.NewGuid().ToString("N"));
    }

    private static void CleanupPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
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

    private sealed class FakeWebRtcConnectionFactory : IWebRtcConnectionFactory
    {
        public List<FakeWebRtcConnection> Created { get; } = new();

        public IWebRtcConnection Create()
        {
            var connection = new FakeWebRtcConnection();
            Created.Add(connection);
            return connection;
        }
    }

    private sealed class FakeWebRtcConnection : IWebRtcConnection
    {
        public event EventHandler<WebRtcIceCandidateDescription>? IceCandidateReady;
        public event EventHandler<IWebRtcDataChannel>? DataChannelOpened;

        public PeerConnectionState ConnectionState => PeerConnectionState.New;
        public SignalingState SignalingState => SignalingState.Stable;
        public int ConnectCalls { get; private set; }
        public WebRtcSessionDescription? RemoteDescription { get; private set; }

        public IWebRtcDataChannel CreateDataChannel(string label) => new FakeWebRtcDataChannel(label);

        public Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Offer, "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=fake\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"));

        public Task<WebRtcSessionDescription> CreateAnswerAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Answer, "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=fake\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n"));

        public Task SetLocalDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetRemoteDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default)
        {
            RemoteDescription = description;
            return Task.CompletedTask;
        }

        public Task AddRemoteIceCandidateAsync(WebRtcIceCandidateDescription candidate, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWebRtcDataChannel : IWebRtcDataChannel
    {
        public FakeWebRtcDataChannel(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public RTCDataChannelState ReadyState => RTCDataChannelState.Connecting;
        public event EventHandler? Opened;
        public event WebRtcDataReceivedHandler? MessageReceived;
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWebSocketConnectionFactory : IWebSocketConnectionFactory
    {
        private readonly IWebSocketConnection _socket;

        public FakeWebSocketConnectionFactory(IWebSocketConnection socket)
        {
            _socket = socket;
        }

        public IWebSocketConnection Create() => _socket;
    }

    private sealed class FakeWebSocketConnection : IWebSocketConnection
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

        public List<string> SentMessages { get; } = new();

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            SentMessages.Add(text);
            return Task.CompletedTask;
        }

        public async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            return await _incoming.Reader.ReadAsync(cancellationToken);
        }

        public void EnqueueReceive(string message)
        {
            _incoming.Writer.TryWrite(message);
        }

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
