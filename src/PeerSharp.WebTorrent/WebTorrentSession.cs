using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PeerSharp.Core;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Interfaces;
using WebRtcSharp;

namespace PeerSharp.WebTorrent;

public sealed class WebTorrentSessionOptions
{
    public string DataChannelLabel { get; init; } = "bittorrent";
    public int OffersPerTracker { get; init; } = 1;
    public IReadOnlyList<string> AdditionalTrackers { get; init; } = Array.Empty<string>();
}

public sealed class WebTorrentSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly IWebRtcConnectionFactory _rtcFactory;
    private readonly IWebSocketConnectionFactory _socketFactory;
    private readonly ConcurrentDictionary<string, PendingPeer> _pendingOffers = new(StringComparer.Ordinal);
    private readonly List<TrackerRuntime> _trackers = new();
    private readonly Torrent _torrent;
    private readonly WebTorrentSessionOptions _options;
    private int _started;

    public WebTorrentSession(ITorrent torrent, WebTorrentSessionOptions? options = null)
        : this((Torrent)torrent, options ?? new WebTorrentSessionOptions(), new DefaultWebRtcConnectionFactory(), new SystemWebSocketConnectionFactory())
    {
    }

    internal WebTorrentSession(Torrent torrent, WebTorrentSessionOptions options, IWebRtcConnectionFactory rtcFactory, IWebSocketConnectionFactory socketFactory)
    {
        _torrent = torrent;
        _options = options;
        _rtcFactory = rtcFactory;
        _socketFactory = socketFactory;
    }

    public static async Task<WebTorrentSession> AttachAsync(ITorrent torrent, WebTorrentSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var session = new WebTorrentSession(torrent, options);
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var trackers = GetTrackerUrls();
        foreach (var trackerUrl in trackers)
        {
            var socket = _socketFactory.Create();
            await socket.ConnectAsync(new Uri(trackerUrl), cancellationToken).ConfigureAwait(false);
            var runtime = new TrackerRuntime(trackerUrl, socket);
            _trackers.Add(runtime);
            runtime.ReceiveTask = RunReceiveLoopAsync(runtime, _cts.Token);
            await SendStartedOffersAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (var tracker in _trackers)
        {
            try
            {
                await tracker.Socket.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        foreach (var pending in _pendingOffers.Values)
        {
            try
            {
                await pending.Connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _cts.Dispose();
    }

    private IReadOnlyList<string> GetTrackerUrls()
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tier in _torrent.InfoFile.AnnounceTiers)
        {
            foreach (var url in tier)
            {
                if (IsWebSocketTracker(url))
                {
                    urls.Add(url);
                }
            }
        }

        foreach (var url in _torrent.InfoFile.AnnounceList)
        {
            if (IsWebSocketTracker(url))
            {
                urls.Add(url);
            }
        }

        if (IsWebSocketTracker(_torrent.InfoFile.Announce))
        {
            urls.Add(_torrent.InfoFile.Announce);
        }

        foreach (var url in _options.AdditionalTrackers)
        {
            if (IsWebSocketTracker(url))
            {
                urls.Add(url);
            }
        }

        return urls.ToList();
    }

    private async Task SendStartedOffersAsync(TrackerRuntime runtime, CancellationToken cancellationToken)
    {
        int offerCount = Math.Max(1, _options.OffersPerTracker);
        var offers = new JsonArray();

        for (int i = 0; i < offerCount; i++)
        {
            byte[] offerIdBytes = new byte[20];
            Random.Shared.NextBytes(offerIdBytes);
            string offerId = BinaryStringEncoding.Encode(offerIdBytes);
            var connection = _rtcFactory.Create();
            var channel = connection.CreateDataChannel(_options.DataChannelLabel);
            var pending = new PendingPeer(connection, channel, Initiator: true);
            _pendingOffers[offerId] = pending;
            HookChannelAttachment(pending);

            var offer = await connection.CreateOfferAsync(cancellationToken).ConfigureAwait(false);
            await connection.SetLocalDescriptionAsync(offer, cancellationToken).ConfigureAwait(false);

            offers.Add(new JsonObject
            {
                ["offer_id"] = offerId,
                ["offer"] = new JsonObject
                {
                    ["type"] = "offer",
                    ["sdp"] = offer.Sdp
                }
            });
        }

        var payload = new JsonObject
        {
            ["action"] = "announce",
            ["info_hash"] = BinaryStringEncoding.Encode(_torrent.Hash.ToArray()),
            ["peer_id"] = BinaryStringEncoding.Encode(_torrent.Settings.PeerId),
            ["uploaded"] = 0,
            ["downloaded"] = 0,
            ["left"] = _torrent.DataLeft,
            ["event"] = "started",
            ["numwant"] = offerCount,
            ["offers"] = offers
        };

        await runtime.Socket.SendTextAsync(payload.ToJsonString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task RunReceiveLoopAsync(TrackerRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string message = await runtime.Socket.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                var signal = WebTorrentProtocolCodec.Parse(message);
                if (!string.Equals(signal.InfoHash, BinaryStringEncoding.Encode(_torrent.Hash.ToArray()), StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(signal.PeerId, BinaryStringEncoding.Encode(_torrent.Settings.PeerId), StringComparison.Ordinal))
                {
                    continue;
                }

                if (signal.AnswerSdp != null && signal.OfferId != null)
                {
                    await HandleAnswerAsync(signal, cancellationToken).ConfigureAwait(false);
                }
                else if (signal.OfferSdp != null && signal.OfferId != null)
                {
                    await HandleOfferAsync(runtime, signal, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task HandleAnswerAsync(WebTorrentSignalMessage signal, CancellationToken cancellationToken)
    {
        if (!_pendingOffers.TryGetValue(signal.OfferId!, out var pending))
        {
            return;
        }

        await pending.Connection.SetRemoteDescriptionAsync(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Answer, signal.AnswerSdp!), cancellationToken).ConfigureAwait(false);
        await pending.Connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleOfferAsync(TrackerRuntime runtime, WebTorrentSignalMessage signal, CancellationToken cancellationToken)
    {
        var connection = _rtcFactory.Create();
        var pending = new PendingPeer(connection, Channel: null, Initiator: false);
        HookChannelAttachment(pending);

        await connection.SetRemoteDescriptionAsync(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Offer, signal.OfferSdp!), cancellationToken).ConfigureAwait(false);
        var answer = await connection.CreateAnswerAsync(cancellationToken).ConfigureAwait(false);
        await connection.SetLocalDescriptionAsync(answer, cancellationToken).ConfigureAwait(false);

        var payload = new JsonObject
        {
            ["action"] = "announce",
            ["info_hash"] = signal.InfoHash,
            ["peer_id"] = BinaryStringEncoding.Encode(_torrent.Settings.PeerId),
            ["to_peer_id"] = signal.PeerId,
            ["offer_id"] = signal.OfferId,
            ["answer"] = new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = answer.Sdp
            }
        };

        await runtime.Socket.SendTextAsync(payload.ToJsonString(), cancellationToken).ConfigureAwait(false);
        await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private void HookChannelAttachment(PendingPeer pending)
    {
        if (pending.Channel != null)
        {
            pending.Channel.Opened += async (_, _) => await AttachChannelAsync(pending.Channel, pending.Initiator).ConfigureAwait(false);
            return;
        }

        pending.Connection.DataChannelOpened += async (_, channel) => await AttachChannelAsync(channel, pending.Initiator).ConfigureAwait(false);
    }

    private async Task AttachChannelAsync(IWebRtcDataChannel channel, bool initiator)
    {
        var stream = new WebRtcDataChannelStream(channel);
        await _torrent.PeersInternal.AddConnectedPeerAsync(stream, initiator, remote: null, sourceKind: PeerSourceKind.WebTorrent).ConfigureAwait(false);
    }

    private static bool IsWebSocketTracker(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record PendingPeer(IWebRtcConnection Connection, IWebRtcDataChannel? Channel, bool Initiator);
    private sealed record TrackerRuntime(string Url, IWebSocketConnection Socket)
    {
        public Task? ReceiveTask { get; set; }
    }
}

internal interface IWebRtcConnectionFactory
{
    IWebRtcConnection Create();
}

internal sealed class DefaultWebRtcConnectionFactory : IWebRtcConnectionFactory
{
    public IWebRtcConnection Create() => new WebRtcConnection();
}

internal interface IWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    Task SendTextAsync(string text, CancellationToken cancellationToken);
    Task<string> ReceiveTextAsync(CancellationToken cancellationToken);
}

internal interface IWebSocketConnectionFactory
{
    IWebSocketConnection Create();
}

internal sealed class SystemWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    public IWebSocketConnection Create() => new SystemWebSocketConnection();
}

internal sealed class SystemWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _webSocket = new();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => _webSocket.ConnectAsync(uri, cancellationToken);

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        await _webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return string.Empty;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _webSocket.Dispose();
    }
}

internal sealed record WebTorrentSignalMessage(string InfoHash, string? PeerId, string? OfferId, string? OfferSdp, string? AnswerSdp);

internal static class WebTorrentProtocolCodec
{
    public static WebTorrentSignalMessage Parse(string json)
    {
        JsonNode node = JsonNode.Parse(json) ?? new JsonObject();
        string infoHash = node["info_hash"]?.GetValue<string>() ?? string.Empty;
        string? peerId = node["peer_id"]?.GetValue<string>();
        string? offerId = node["offer_id"]?.GetValue<string>();
        string? offerSdp = node["offer"]?["sdp"]?.GetValue<string>();
        string? answerSdp = node["answer"]?["sdp"]?.GetValue<string>();
        return new WebTorrentSignalMessage(infoHash, peerId, offerId, offerSdp, answerSdp);
    }
}

internal static class BinaryStringEncoding
{
    public static string Encode(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
