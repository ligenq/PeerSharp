using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RtcForge;
using RtcForge.Media;

namespace PeerSharp.WebTorrent;

public sealed class WebTorrentSessionOptions
{
    public string DataChannelLabel { get; init; } = "bittorrent";
    public int OffersPerTracker { get; init; } = 1;
    public IReadOnlyList<string> AdditionalTrackers { get; init; } = Array.Empty<string>();
    public TimeSpan MinimumReannounceInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// ICE servers (STUN/TURN) used for WebRTC connectivity. Defaults to a set of public STUN servers
    /// so server-reflexive candidates can be gathered. Override to supply TURN credentials when peers
    /// are behind symmetric NATs.
    /// </summary>
    public IReadOnlyList<RTCIceServer> IceServers { get; init; } = new List<RTCIceServer>
    {
        new() { Urls = { "stun:stun.l.google.com:19302" } },
        new() { Urls = { "stun:stun1.l.google.com:19302" } }
    };
}

public sealed class WebTorrentSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly IWebRtcConnectionFactory _rtcFactory;
    private readonly IWebSocketConnectionFactory _socketFactory;
    private readonly ConcurrentDictionary<string, PendingPeer> _connections = new(StringComparer.Ordinal);
    private readonly IPeerTransportHost _host;
    private readonly ITorrent _torrent;
    private readonly List<TrackerRuntime> _trackers = new();
    private readonly WebTorrentSessionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebTorrentSession> _logger;
    private readonly ConcurrentBag<Task> _backgroundTasks = new();
    private int _started;

    public WebTorrentSession(ITorrent torrent, WebTorrentSessionOptions? options = null, ILoggerFactory? loggerFactory = null)
        : this(CreateDefaultConstructorArgs(torrent, options, loggerFactory))
    {
    }

    private WebTorrentSession(DefaultConstructorArgs args)
        : this(args.Torrent, args.Host, args.Options, args.RtcFactory, args.SocketFactory, args.LoggerFactory)
    {
    }

    private static DefaultConstructorArgs CreateDefaultConstructorArgs(ITorrent torrent, WebTorrentSessionOptions? options, ILoggerFactory? loggerFactory)
    {
        var resolvedOptions = options ?? new WebTorrentSessionOptions();
        var resolvedLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        return new DefaultConstructorArgs(
            torrent,
            ResolveHost(torrent),
            resolvedOptions,
            CreateDefaultRtcFactory(resolvedOptions, resolvedLoggerFactory),
            new SystemWebSocketConnectionFactory(),
            resolvedLoggerFactory);
    }

    private static IWebRtcConnectionFactory CreateDefaultRtcFactory(WebTorrentSessionOptions options, ILoggerFactory loggerFactory)
        => new DefaultWebRtcConnectionFactory(options, loggerFactory);

    internal WebTorrentSession(ITorrent torrent, IPeerTransportHost host, WebTorrentSessionOptions options, IWebRtcConnectionFactory rtcFactory, IWebSocketConnectionFactory socketFactory)
        : this(torrent, host, options, rtcFactory, socketFactory, NullLoggerFactory.Instance)
    {
    }

    internal WebTorrentSession(ITorrent torrent, IPeerTransportHost host, WebTorrentSessionOptions options, IWebRtcConnectionFactory rtcFactory, IWebSocketConnectionFactory socketFactory, ILoggerFactory loggerFactory)
    {
        _torrent = torrent;
        _host = host;
        _options = options;
        _timeProvider = options.TimeProvider;
        _rtcFactory = rtcFactory;
        _socketFactory = socketFactory;
        _logger = loggerFactory.CreateLogger<WebTorrentSession>();
    }

    public static async Task<WebTorrentSession> AttachAsync(ITorrent torrent, WebTorrentSessionOptions? options = null, ILoggerFactory? loggerFactory = null, CancellationToken cancellationToken = default)
    {
        var session = new WebTorrentSession(torrent, options, loggerFactory);
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
            try
            {
                var socket = _socketFactory.Create();
                await socket.ConnectAsync(new Uri(trackerUrl), cancellationToken).ConfigureAwait(false);
                var runtime = new TrackerRuntime(trackerUrl, socket);
                _trackers.Add(runtime);
                runtime.ReceiveTask = RunReceiveLoopAsync(runtime, _cts.Token);
                await SendOffersAsync(runtime, isInitial: true, cancellationToken).ConfigureAwait(false);
                runtime.NextAnnounce = _timeProvider.GetUtcNow() + runtime.ReannounceInterval;
                _logger.LogInformation("Connected to tracker {Url}", trackerUrl);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to connect to tracker {Url}: {Error}", trackerUrl, ex.Message);
            }
        }

        if (_trackers.Count > 0)
        {
            _backgroundTasks.Add(RunReannounceLoopAsync(_cts.Token));
        }
    }

    private async Task RunReannounceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, cancellationToken).ConfigureAwait(false);
                foreach (var runtime in _trackers)
                {
                    if (_timeProvider.GetUtcNow() < runtime.NextAnnounce)
                    {
                        continue;
                    }

                    try
                    {
                        await SendOffersAsync(runtime, isInitial: false, cancellationToken).ConfigureAwait(false);
                        runtime.NextAnnounce = _timeProvider.GetUtcNow() + runtime.ReannounceInterval;
                        _logger.LogDebug("Re-announced to tracker {Url}", runtime.Url);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Re-announce failed for tracker {Url}: {Error}", runtime.Url, ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the session is disposed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        foreach (var tracker in _trackers)
        {
            try
            {
                await SendStoppedAsync(tracker).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send stopped event to tracker {Url}", tracker.Url);
            }
        }

        foreach (var tracker in _trackers)
        {
            try
            {
                await tracker.Socket.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to dispose tracker socket {Url}", tracker.Url);
            }
        }

        foreach (var pending in _connections.Values)
        {
            try
            {
                await pending.Connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to dispose WebRTC connection for offer {OfferId}", FormatOfferIdForLog(pending.OfferId));
            }
        }

        _cts.Dispose();
    }

    private IReadOnlyList<string> GetTrackerUrls()
    {
        var urls = new HashSet<string>(
            _torrent.Trackers
                .GetTrackers()
                .Select(static tracker => tracker.Url)
                .Where(IsWebSocketTracker),
            StringComparer.OrdinalIgnoreCase);

        foreach (var url in _options.AdditionalTrackers)
        {
            if (IsWebSocketTracker(url))
            {
                urls.Add(url);
            }
        }

        return urls.ToList();
    }

    private async Task SendStoppedAsync(TrackerRuntime runtime)
    {
        var payload = CreateAnnounceBasePayload();
        payload["event"] = "stopped";
        await runtime.Socket.SendTextAsync(payload.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
    }

    private JsonObject CreateAnnounceBasePayload()
    {
        return new JsonObject
        {
            ["action"] = "announce",
            ["info_hash"] = BinaryStringEncoding.Encode(_host.Hash.ToArray()),
            ["peer_id"] = BinaryStringEncoding.Encode(_host.PeerId),
            ["uploaded"] = 0,
            ["downloaded"] = 0,
            ["left"] = _host.DataLeft
        };
    }

    private async Task SendOffersAsync(TrackerRuntime runtime, bool isInitial, CancellationToken cancellationToken)
    {
        // Clean up stale pending connections from previous announces to this tracker
        if (!isInitial)
        {
            var staleKeys = _connections.Where(kvp => kvp.Value.Runtime == runtime && kvp.Value.Initiator && !kvp.Value.IsAttached)
                .Select(kvp => kvp.Key).ToList();
            foreach (var key in staleKeys)
            {
                if (_connections.TryRemove(key, out var stale))
                {
                    await stale.Connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            _logger.LogDebug("Cleaned up {Count} stale pending connections for {Url}", staleKeys.Count, runtime.Url);
        }

        int offerCount = Math.Max(1, _options.OffersPerTracker);
        var offers = new JsonArray();

        for (int i = 0; i < offerCount; i++)
        {
            string offerId = BinaryStringEncoding.Encode(CreateRandomBytes(20));
            var connection = _rtcFactory.Create();
            var channel = connection.CreateDataChannel(_options.DataChannelLabel);
            var pending = new PendingPeer(offerId, connection, channel, initiator: true, runtime);
            ConfigurePendingPeer(pending);
            _connections[offerId] = pending;

            var offer = await connection.CreateOfferAsync(cancellationToken).ConfigureAwait(false);
            await connection.SetLocalDescriptionAsync(offer, cancellationToken).ConfigureAwait(false);
            var trackerOffer = new WebRtcSessionDescription(offer.Type, FilterUnsupportedIceCandidates(offer.Sdp));

            offers.Add(new JsonObject
            {
                ["offer_id"] = offerId,
                ["offer"] = new JsonObject
                {
                    ["type"] = "offer",
                    ["sdp"] = trackerOffer.Sdp
                }
            });
        }

        var payload = CreateAnnounceBasePayload();
        if (isInitial)
        {
            payload["event"] = "started";
        }
        payload["numwant"] = offerCount;
        payload["offers"] = offers;

        string json = payload.ToJsonString();
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var firstSdp = offers[0]?["offer"]?["sdp"]?.GetValue<string>() ?? string.Empty;
            _logger.LogDebug("Sending announce with {Count} offers, first SDP:{NewLine}{Sdp}", offerCount, Environment.NewLine, firstSdp);
        }
        await runtime.Socket.SendTextAsync(json, cancellationToken).ConfigureAwait(false);
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

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Tracker message from {Url}: {Message}", runtime.Url, SanitizeTrackerMessageForLog(message));
                }

                var signal = WebTorrentProtocolCodec.Parse(message);
                if (signal.Interval.HasValue)
                {
                    runtime.ReannounceInterval = TimeSpan.FromSeconds(Math.Max(
                        (int)_options.MinimumReannounceInterval.TotalSeconds,
                        signal.Interval.Value));
                    runtime.NextAnnounce = _timeProvider.GetUtcNow() + runtime.ReannounceInterval;
                }

                string localHash = BinaryStringEncoding.Encode(_host.Hash.ToArray());
                if (!string.Equals(signal.InfoHash, localHash, StringComparison.Ordinal))
                {
                    _logger.LogDebug("Info hash mismatch: local={Local}, remote={Remote}", localHash, signal.InfoHash);
                    continue;
                }

                if (string.Equals(signal.PeerId, BinaryStringEncoding.Encode(_host.PeerId), StringComparison.Ordinal))
                {
                    continue;
                }

                if (signal.AnswerSdp != null && signal.OfferId != null)
                {
                    _logger.LogInformation("Received answer from peer {PeerId} for offer {OfferId}", signal.PeerId, FormatOfferIdForLog(signal.OfferId));
                    await HandleAnswerAsync(signal, cancellationToken).ConfigureAwait(false);
                }
                else if (signal.OfferSdp != null && signal.OfferId != null)
                {
                    _logger.LogInformation("Received offer from peer {PeerId}", signal.PeerId);
                    await HandleOfferAsync(runtime, signal, cancellationToken).ConfigureAwait(false);
                }

                if (signal.Candidate != null && signal.OfferId != null)
                {
                    _logger.LogDebug("Received ICE candidate for offer {OfferId}", FormatOfferIdForLog(signal.OfferId));
                    await HandleCandidateAsync(signal, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the session is disposed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive loop failed for tracker {Url}", runtime.Url);
        }
    }

    private async Task HandleAnswerAsync(WebTorrentSignalMessage signal, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(signal.OfferId!, out var pending))
        {
            _logger.LogWarning("No pending connection found for offer_id (have {Count} pending)", _connections.Count);
            return;
        }

        try
        {
            pending.RemotePeerId = signal.PeerId;
            _logger.LogDebug("Setting remote description for peer {PeerId}", signal.PeerId);
            await pending.Connection.SetRemoteDescriptionAsync(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Answer, signal.AnswerSdp!), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Flushing buffered ICE candidates");
            await FlushBufferedCandidatesAsync(pending, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Calling ConnectAsync");
            bool connected = await pending.Connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ConnectAsync returned {Connected} for peer {PeerId}", connected, signal.PeerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle answer from peer {PeerId}", signal.PeerId);
        }
    }

    private async Task HandleOfferAsync(TrackerRuntime runtime, WebTorrentSignalMessage signal, CancellationToken cancellationToken)
    {
        var connection = _rtcFactory.Create();
        var pending = new PendingPeer(signal.OfferId!, connection, initiator: false, runtime)
        {
            RemotePeerId = signal.PeerId
        };
        ConfigurePendingPeer(pending);
        _connections[pending.OfferId] = pending;

        await connection.SetRemoteDescriptionAsync(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Offer, FilterUnsupportedIceCandidates(signal.OfferSdp!)), cancellationToken).ConfigureAwait(false);
        var answer = await connection.CreateAnswerAsync(cancellationToken).ConfigureAwait(false);
        await connection.SetLocalDescriptionAsync(answer, cancellationToken).ConfigureAwait(false);
        await SendAnswerAsync(runtime, pending, new WebRtcSessionDescription(answer.Type, FilterUnsupportedIceCandidates(answer.Sdp)), cancellationToken).ConfigureAwait(false);
        await FlushBufferedCandidatesAsync(pending, cancellationToken).ConfigureAwait(false);
        await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCandidateAsync(WebTorrentSignalMessage signal, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(signal.OfferId!, out var pending))
        {
            return;
        }

        if (!IsSupportedIceCandidate(signal.Candidate!))
        {
            _logger.LogDebug("Ignoring unsupported ICE candidate for offer {OfferId}: {Candidate}", FormatOfferIdForLog(signal.OfferId), signal.Candidate);
            return;
        }

        await pending.Connection.AddRemoteIceCandidateAsync(new WebRtcIceCandidateDescription(signal.Candidate!), cancellationToken).ConfigureAwait(false);
    }

    private void ConfigurePendingPeer(PendingPeer pending)
    {
        _backgroundTasks.Add(Task.Run(() => PumpLocalIceCandidatesAsync(pending)));

        if (pending.Channel != null)
        {
            _backgroundTasks.Add(Task.Run(() => WaitForLocalChannelOpenAsync(pending)));
            return;
        }

        _backgroundTasks.Add(Task.Run(() => PumpRemoteDataChannelsAsync(pending)));
    }

    private async Task PumpLocalIceCandidatesAsync(PendingPeer pending)
    {
        try
        {
            await foreach (var candidate in pending.Connection.IceCandidates.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                if (!IsSupportedIceCandidate(candidate.Candidate))
                {
                    _logger.LogTrace("Ignoring unsupported local ICE candidate: {Candidate}", candidate.Candidate);
                    continue;
                }

                _logger.LogDebug("Local ICE candidate ready: {Candidate}", candidate.Candidate);
                await OnLocalIceCandidateAsync(pending, candidate, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the session is disposed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling local ICE candidate");
        }
    }

    private async Task WaitForLocalChannelOpenAsync(PendingPeer pending)
    {
        try
        {
            await pending.Channel!.WaitUntilOpenAsync(_cts.Token).ConfigureAwait(false);
            _logger.LogInformation("Data channel opened for peer {PeerId}", pending.RemotePeerId);
            await AttachChannelAsync(pending, pending.Channel, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the session is disposed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error attaching data channel for peer {PeerId}", pending.RemotePeerId);
        }
    }

    private async Task PumpRemoteDataChannelsAsync(PendingPeer pending)
    {
        try
        {
            await foreach (var channel in pending.Connection.DataChannels.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await channel.WaitUntilOpenAsync(_cts.Token).ConfigureAwait(false);
                    await AttachChannelAsync(pending, channel, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error attaching remote data channel for peer {PeerId}", pending.RemotePeerId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the session is disposed.
        }
    }

    private async Task OnLocalIceCandidateAsync(PendingPeer pending, WebRtcIceCandidateDescription candidate, CancellationToken cancellationToken)
    {
        lock (pending.SyncRoot)
        {
            if (string.IsNullOrWhiteSpace(pending.RemotePeerId))
            {
                pending.BufferedLocalCandidates.Add(candidate);
                return;
            }
        }

        await SendCandidateAsync(pending, candidate, cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushBufferedCandidatesAsync(PendingPeer pending, CancellationToken cancellationToken)
    {
        List<WebRtcIceCandidateDescription> buffered;
        lock (pending.SyncRoot)
        {
            if (pending.BufferedLocalCandidates.Count == 0 || string.IsNullOrWhiteSpace(pending.RemotePeerId))
            {
                return;
            }

            buffered = new List<WebRtcIceCandidateDescription>(pending.BufferedLocalCandidates);
            pending.BufferedLocalCandidates.Clear();
        }

        _logger.LogDebug("Flushing {Count} buffered local ICE candidates to peer {PeerId}", buffered.Count, pending.RemotePeerId);
        foreach (var candidate in buffered)
        {
            await SendCandidateAsync(pending, candidate, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAnswerAsync(TrackerRuntime runtime, PendingPeer pending, WebRtcSessionDescription answer, CancellationToken cancellationToken)
    {
        var payload = CreateAnnounceBasePayload();
        payload["to_peer_id"] = pending.RemotePeerId;
        payload["offer_id"] = pending.OfferId;
        payload["answer"] = new JsonObject
        {
            ["type"] = "answer",
            ["sdp"] = answer.Sdp
        };

        await runtime.Socket.SendTextAsync(payload.ToJsonString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCandidateAsync(PendingPeer pending, WebRtcIceCandidateDescription candidate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pending.RemotePeerId))
        {
            return;
        }

        if (!IsSupportedIceCandidate(candidate.Candidate))
        {
            _logger.LogDebug("Not sending unsupported ICE candidate for offer {OfferId}: {Candidate}", FormatOfferIdForLog(pending.OfferId), candidate.Candidate);
            return;
        }

        var payload = CreateAnnounceBasePayload();
        payload["to_peer_id"] = pending.RemotePeerId;
        payload["offer_id"] = pending.OfferId;
        payload["candidate"] = new JsonObject
        {
            ["candidate"] = candidate.Candidate,
            ["sdpMid"] = "0",
            ["sdpMLineIndex"] = 0
        };
        await pending.Runtime.Socket.SendTextAsync(payload.ToJsonString(), cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Sent local ICE candidate to peer {PeerId} for offer {OfferId}: {Candidate}", pending.RemotePeerId, FormatOfferIdForLog(pending.OfferId), candidate.Candidate);
    }

    private async Task AttachChannelAsync(PendingPeer pending, IWebRtcDataChannel channel, CancellationToken cancellationToken)
    {
        if (!pending.TryMarkAttached())
        {
            return;
        }

        var stream = new WebTorrentDataChannelStream(channel);
        stream.Start();
        try
        {
            await _host.AttachPeerTransportAsync(stream, pending.Initiator, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsWebSocketTracker(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase));
    }

    private static IPeerTransportHost ResolveHost(ITorrent torrent)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        return torrent as IPeerTransportHost
            ?? throw new ArgumentException("The provided torrent does not support external peer transport attachment.", nameof(torrent));
    }

    private static byte[] CreateRandomBytes(int count)
    {
        byte[] bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static string FilterUnsupportedIceCandidates(string sdp)
    {
        var builder = new StringBuilder(sdp.Length);
        foreach (var rawLine in sdp.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase)
                && !IsSupportedIceCandidate(line.Substring("a=".Length)))
            {
                continue;
            }

            builder.Append(line);
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    private static string FormatOfferIdForLog(string? offerId)
    {
        if (string.IsNullOrEmpty(offerId))
        {
            return string.Empty;
        }

        var bytes = Encoding.Latin1.GetBytes(offerId);
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string SanitizeTrackerMessageForLog(string message)
    {
        var builder = new StringBuilder(message.Length);
        foreach (var ch in message)
        {
            if (ch >= 0x20 && ch < 0x7F)
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('?');
            }
        }

        return builder.ToString();
    }

    private static bool IsSupportedIceCandidate(string candidateLine)
    {
        if (candidateLine.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
        {
            candidateLine = candidateLine.Substring("a=".Length);
        }

        if (candidateLine.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
        {
            candidateLine = candidateLine.Substring("candidate:".Length);
        }

        var parts = candidateLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6 || !parts[2].Equals("udp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Hostname candidates (notably browser mDNS `UUID.local`) are kept so the ICE
        // agent can resolve them at pair-check time. Only drop explicit IPv6 literals.
        if (System.Net.IPAddress.TryParse(parts[4], out var address))
        {
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }

        return true;
    }

    private sealed class PendingPeer
    {
        private int _attached;

        public PendingPeer(string offerId, IWebRtcConnection connection, IWebRtcDataChannel channel, bool initiator, TrackerRuntime runtime)
            : this(offerId, connection, initiator, runtime)
        {
            Channel = channel;
        }

        public PendingPeer(string offerId, IWebRtcConnection connection, bool initiator, TrackerRuntime runtime)
        {
            OfferId = offerId;
            Connection = connection;
            Initiator = initiator;
            Runtime = runtime;
        }

        public List<WebRtcIceCandidateDescription> BufferedLocalCandidates { get; } = new();
        public IWebRtcDataChannel? Channel { get; }
        public IWebRtcConnection Connection { get; }
        public bool Initiator { get; }
        public bool IsAttached => Volatile.Read(ref _attached) == 1;
        public string OfferId { get; }
        public string? RemotePeerId { get; set; }
        public TrackerRuntime Runtime { get; }
        public object SyncRoot { get; } = new();

        public bool TryMarkAttached() => Interlocked.Exchange(ref _attached, 1) == 0;
    }

    private sealed record TrackerRuntime(string Url, IWebSocketConnection Socket)
    {
        public DateTimeOffset NextAnnounce { get; set; } = DateTimeOffset.MinValue;
        public TimeSpan ReannounceInterval { get; set; } = TimeSpan.FromSeconds(120);
        public Task? ReceiveTask { get; set; }
    }

    private sealed record DefaultConstructorArgs(
        ITorrent Torrent,
        IPeerTransportHost Host,
        WebTorrentSessionOptions Options,
        IWebRtcConnectionFactory RtcFactory,
        IWebSocketConnectionFactory SocketFactory,
        ILoggerFactory LoggerFactory);
}

internal interface IWebRtcConnectionFactory
{
    IWebRtcConnection Create();
}

internal sealed class DefaultWebRtcConnectionFactory : IWebRtcConnectionFactory
{
    private readonly WebRtcConnectionOptions _rtcOptions;

    public DefaultWebRtcConnectionFactory(WebTorrentSessionOptions options, ILoggerFactory loggerFactory)
    {
        _rtcOptions = new WebRtcConnectionOptions
        {
            IceServers = options.IceServers.Select(s => new RTCIceServer
            {
                Urls = new List<string>(s.Urls),
                Username = s.Username,
                Credential = s.Credential
            }).ToList(),
            LoggerFactory = loggerFactory
        };
    }

    public IWebRtcConnection Create() => WebRtcConnection.Create(_rtcOptions);
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
    private HttpMessageInvoker? _invoker;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme == "wss")
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };
            _invoker = new HttpMessageInvoker(handler);
            return _webSocket.ConnectAsync(uri, _invoker, cancellationToken);
        }

        return _webSocket.ConnectAsync(uri, cancellationToken);
    }

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
            catch (WebSocketException)
            {
                // Best-effort close during disposal.
            }
        }

        _webSocket.Dispose();
        _invoker?.Dispose();
    }
}

internal sealed record WebTorrentSignalMessage(string InfoHash, string? PeerId, string? OfferId, string? OfferSdp, string? AnswerSdp, string? Candidate, int? Interval);

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
        int? interval = node["interval"]?.GetValue<int>();
        string? candidate = node["candidate"] switch
        {
            JsonValue value => value.GetValue<string>(),
            JsonObject candidateObject => candidateObject["candidate"]?.GetValue<string>(),
            _ => null
        };

        return new WebTorrentSignalMessage(infoHash, peerId, offerId, offerSdp, answerSdp, candidate, interval);
    }
}

internal static class BinaryStringEncoding
{
    public static string Encode(ReadOnlyMemory<byte> bytes) => Encoding.Latin1.GetString(bytes.Span);
    public static string Encode(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
