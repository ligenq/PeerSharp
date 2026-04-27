using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Interfaces;
using RtcForge;
using RtcForge.Media;

namespace PeerSharp.WebTorrent;

/// <summary>
/// Options that control WebTorrent peer discovery, signaling, and WebRTC behavior.
/// </summary>
public sealed class WebTorrentSessionOptions
{
    /// <summary>Label advertised on the WebRTC data channel. Defaults to <c>"bittorrent"</c>.</summary>
    public string DataChannelLabel { get; init; } = "bittorrent";

    /// <summary>Number of WebRTC offers to publish to each WebSocket tracker per announce.</summary>
    public int OffersPerTracker { get; init; } = 10;

    /// <summary>Additional WebSocket tracker URLs to announce to. The library does not include public trackers by default.</summary>
    public IReadOnlyList<string> AdditionalTrackers { get; init; } = Array.Empty<string>();

    /// <summary>Maximum accepted WebSocket tracker message size in bytes.</summary>
    public int MaxTrackerMessageBytes { get; init; } = 256 * 1024;

    /// <summary>Lower bound on the tracker reannounce interval.</summary>
    public TimeSpan MinimumReannounceInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time to wait for an initial WebSocket tracker connection before giving up.</summary>
    public TimeSpan TrackerConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Restricts ICE to relayed candidates when set to <see cref="WebTorrentIceTransportPolicy.Relay"/>.</summary>
    public WebTorrentIceTransportPolicy IceTransportPolicy { get; init; } = WebTorrentIceTransportPolicy.All;

    /// <summary>Time provider used for tracker scheduling and pending-peer expiry. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// ICE servers (STUN/TURN) used for WebRTC connectivity. Defaults are STUN-only, which is
    /// sufficient for peers on open or cone-NAT networks. Peers behind a symmetric NAT (common
    /// on corporate / carrier networks) will not complete an ICE check with STUN alone — supply
    /// TURN credentials here to relay those peers. There is no auto-provisioned TURN server;
    /// callers must bring their own.
    /// </summary>
    public IReadOnlyList<WebTorrentIceServer> IceServers { get; init; } = new List<WebTorrentIceServer>
    {
        new() { Urls = { "stun:stun.l.google.com:19302" } },
        new() { Urls = { "stun:stun1.l.google.com:19302" } }
    };
}

/// <summary>
/// BitTorrent-over-WebRTC session that discovers peers via WebSocket trackers (WebTorrent
/// protocol) and attaches each negotiated data channel to the owning torrent as a peer
/// transport.
/// </summary>
/// <remarks>
/// <para>
/// <b>SCTP payload cap.</b> The underlying RtcForge transport limits a single SCTP user
/// message to 256 KB. BitTorrent messages are well under that (largest is a 16 KB piece
/// block) for any torrent whose bitfield fits the cap. In practice this rules out torrents
/// with roughly more than ~2 million pieces, which corresponds to multi-PB files at typical
/// piece sizes — far above real-world content. Torrents within that ceiling work normally.
/// </para>
/// </remarks>
public sealed class WebTorrentSession : IAsyncDisposable
{
    private static readonly TimeSpan ActiveLoopMaxDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IdleLoopMaxDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingPeerTimeout = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cts = new();
    private readonly IWebRtcConnectionFactory _rtcFactory;
    private readonly IWebSocketConnectionFactory _socketFactory;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _earlyRemoteCandidates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingPeer> _connections = new(StringComparer.Ordinal);
    private readonly IPeerTransportHost _host;
    private readonly ITorrent _torrent;
    private readonly List<TrackerRuntime> _trackers = new();
    private readonly WebTorrentSessionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebTorrentSession> _logger;
    private readonly Lock _backgroundTasksLock = new();
    private readonly List<Task> _backgroundTasks = new();
    private readonly IReadOnlyList<LocalSubnet> _localSubnets;
    private long _uploadedBaseline;
    private long _downloadedBaseline;
    private int _started;

    private const int MaxPendingConnections = 256;

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
            new SystemWebSocketConnectionFactory(resolvedOptions.MaxTrackerMessageBytes),
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
        _localSubnets = RemoteSdpReachability.EnumerateLocalSubnets();
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

        // Snapshot host totals at session start so tracker announces carry session-relative
        // deltas (BEP-3 semantics) rather than the host's monotonic lifetime totals.
        _uploadedBaseline = _host.DataUploaded;
        _downloadedBaseline = _host.DataDownloaded;

        var trackers = GetTrackerUrls();
        foreach (var trackerUrl in trackers)
        {
            var runtime = new TrackerRuntime(trackerUrl, this);
            lock (_trackers)
            {
                _trackers.Add(runtime);
            }

            try
            {
                await ConnectTrackerAsync(runtime, isInitial: true, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Connected to tracker {Url}", trackerUrl);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (TrackerConnectFailureClassifier.IsTerminal(ex))
            {
                LogTrackerFailure(LogLevel.Information, ex, "Skipping tracker {Url}: {Reason}", trackerUrl);
                MarkTrackerTerminalFailure(runtime, ex);
            }
            catch (Exception ex)
            {
                LogTrackerFailure(LogLevel.Warning, ex, "Failed to connect to tracker {Url}: {Reason}", trackerUrl);
                ScheduleTrackerReconnect(runtime, ex);
            }
        }

        // Always run the background loop so it can attempt reconnects for runtimes that
        // failed their initial connect with a transient error.
        if (_trackers.Count > 0)
        {
            TrackBackgroundTask(RunReannounceLoopAsync(_cts.Token));
        }
    }

    private async Task ConnectTrackerAsync(TrackerRuntime runtime, bool isInitial, CancellationToken cancellationToken)
    {
        var socket = _socketFactory.Create();
        var connectTask = socket.ConnectAsync(new Uri(runtime.Url), cancellationToken);
        try
        {
            await connectTask.WaitAsync(_options.TrackerConnectTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = connectTask.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            await socket.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        int generation;
        lock (runtime.SyncRoot)
        {
            runtime.Socket = socket;
            runtime.IsConnected = true;
            runtime.ConsecutiveFailures = 0;
            runtime.LastError = null;
            runtime.NextReconnectAt = DateTimeOffset.MinValue;
            runtime.ReconnectInProgress = false;
            // Re-send the completed event on reconnect so the new socket's tracker learns
            // we're a seed. Tracker state doesn't persist across WebSocket reconnects.
            runtime.CompletedSent = false;
            generation = ++runtime.Generation;
        }

        runtime.ReceiveTask = RunReceiveLoopAsync(runtime, generation, _cts.Token);
        await SendOffersAsync(runtime, isInitial: isInitial, cancellationToken).ConfigureAwait(false);

        lock (runtime.SyncRoot)
        {
            runtime.NextAnnounce = _timeProvider.GetUtcNow() + runtime.ReannounceInterval;
        }
    }

    private void ScheduleTrackerReconnect(TrackerRuntime runtime, Exception ex)
    {
        lock (runtime.SyncRoot)
        {
            ScheduleTrackerReconnectLocked(runtime, ex);
        }
    }

    private void ScheduleTrackerReconnectForGeneration(TrackerRuntime runtime, int generation, Exception ex)
    {
        lock (runtime.SyncRoot)
        {
            // Caller (typically the receive loop) observed a failure on a specific socket
            // generation. If the runtime has since reconnected (generation advanced) or was
            // scheduled for reconnect by someone else, don't clobber that fresher state.
            if (runtime.Generation != generation)
            {
                return;
            }

            ScheduleTrackerReconnectLocked(runtime, ex);
        }
    }

    private void ScheduleTrackerReconnectLocked(TrackerRuntime runtime, Exception ex)
    {
        runtime.Generation++;
        runtime.IsConnected = false;
        runtime.ConsecutiveFailures++;
        runtime.LastError = TrackerConnectFailureClassifier.Describe(ex);
        // Exponential backoff capped at 5 minutes.
        int seconds = Math.Min(300, (int)Math.Pow(2, Math.Min(runtime.ConsecutiveFailures, 8)));
        runtime.NextReconnectAt = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(seconds);
    }

    private static void MarkTrackerTerminalFailure(TrackerRuntime runtime, Exception ex)
    {
        lock (runtime.SyncRoot)
        {
            runtime.IsConnected = false;
            runtime.Socket = null;
            runtime.LastError = TrackerConnectFailureClassifier.Describe(ex);
            runtime.NextReconnectAt = DateTimeOffset.MinValue;
            runtime.ReconnectInProgress = false;
        }
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (_backgroundTasksLock)
        {
            for (int i = _backgroundTasks.Count - 1; i >= 0; i--)
            {
                if (_backgroundTasks[i].IsCompleted)
                {
                    _backgroundTasks.RemoveAt(i);
                }
            }

            _backgroundTasks.Add(task);
        }
    }

    private async Task RunReannounceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay = GetBackgroundLoopDelay();
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);

                TrackerRuntime[] snapshot;
                lock (_trackers)
                {
                    snapshot = _trackers.ToArray();
                }

                await CleanupExpiredPendingPeersAsync().ConfigureAwait(false);

                bool finished = _torrent.Finished;

                foreach (var runtime in snapshot)
                {
                    bool isConnected;
                    bool completedSent;
                    bool reconnectInProgress;
                    DateTimeOffset nextReconnectAt;
                    DateTimeOffset nextAnnounce;
                    lock (runtime.SyncRoot)
                    {
                        isConnected = runtime.IsConnected;
                        completedSent = runtime.CompletedSent;
                        reconnectInProgress = runtime.ReconnectInProgress;
                        nextReconnectAt = runtime.NextReconnectAt;
                        nextAnnounce = runtime.NextAnnounce;
                    }

                    if (!isConnected)
                    {
                        if (reconnectInProgress)
                        {
                            continue;
                        }

                        if (nextReconnectAt == DateTimeOffset.MinValue ||
                            _timeProvider.GetUtcNow() < nextReconnectAt)
                        {
                            continue;
                        }

                        await TryReconnectAsync(runtime, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (finished && !completedSent)
                    {
                        try
                        {
                            await SendCompletedAsync(runtime, cancellationToken).ConfigureAwait(false);
                            lock (runtime.SyncRoot)
                            {
                                runtime.CompletedSent = true;
                            }
                            _logger.LogDebug("Sent completed event to tracker {Url}", runtime.Url);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Completed event failed for tracker {Url}: {Error}", runtime.Url, ex.Message);
                            ScheduleTrackerReconnect(runtime, ex);
                            continue;
                        }
                    }

                    if (_timeProvider.GetUtcNow() < nextAnnounce)
                    {
                        continue;
                    }

                    try
                    {
                        await SendOffersAsync(runtime, isInitial: false, cancellationToken).ConfigureAwait(false);
                        lock (runtime.SyncRoot)
                        {
                            runtime.NextAnnounce = _timeProvider.GetUtcNow() + runtime.ReannounceInterval;
                        }
                        _logger.LogDebug("Re-announced to tracker {Url}", runtime.Url);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Re-announce failed for tracker {Url}: {Error}", runtime.Url, ex.Message);
                        ScheduleTrackerReconnect(runtime, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the session is disposed.
        }
    }

    private TimeSpan GetBackgroundLoopDelay()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset? nextDue = null;
        bool hasActiveWork = _connections.Count > 0;

        lock (_trackers)
        {
            foreach (var runtime in _trackers)
            {
                lock (runtime.SyncRoot)
                {
                    if (runtime.ReconnectInProgress)
                    {
                        hasActiveWork = true;
                        continue;
                    }

                    if (!runtime.IsConnected)
                    {
                        if (runtime.NextReconnectAt != DateTimeOffset.MinValue)
                        {
                            nextDue = Min(nextDue, runtime.NextReconnectAt);
                        }

                        continue;
                    }

                    hasActiveWork = true;

                    if (!runtime.CompletedSent && _torrent.Finished)
                    {
                        nextDue = Min(nextDue, now);
                    }
                    else
                    {
                        nextDue = Min(nextDue, runtime.NextAnnounce);
                    }
                }
            }
        }

        foreach (var pending in _connections.Values)
        {
            nextDue = Min(nextDue, pending.ExpiresAt);
        }

        if (!nextDue.HasValue)
        {
            return hasActiveWork ? ActiveLoopMaxDelay : IdleLoopMaxDelay;
        }

        TimeSpan delay = nextDue.Value - now;
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(50);
        }

        TimeSpan maxDelay = hasActiveWork ? ActiveLoopMaxDelay : IdleLoopMaxDelay;
        return delay < maxDelay ? delay : maxDelay;
    }

    private static DateTimeOffset Min(DateTimeOffset? current, DateTimeOffset candidate)
        => !current.HasValue || candidate < current.Value ? candidate : current.Value;

    private async Task CleanupExpiredPendingPeersAsync()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var expiredOfferIds = _connections
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (string offerId in expiredOfferIds)
        {
            _logger.LogDebug("Expiring pending WebTorrent peer for offer {OfferId}", FormatOfferIdForLog(offerId));
            await RemovePendingAsync(offerId).ConfigureAwait(false);
        }
    }

    private async Task TryReconnectAsync(TrackerRuntime runtime, CancellationToken cancellationToken)
    {
        IWebSocketConnection? staleSocket;
        Task? staleReceiveTask;
        lock (runtime.SyncRoot)
        {
            if (runtime.ReconnectInProgress)
            {
                return;
            }

            runtime.ReconnectInProgress = true;
            staleSocket = runtime.Socket;
            staleReceiveTask = runtime.ReceiveTask;
            runtime.Socket = null;
            runtime.ReceiveTask = null;
            runtime.IsConnected = false;
            runtime.Generation++;
        }

        try
        {
            if (staleSocket != null)
            {
                try
                {
                    await staleSocket.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to dispose stale tracker socket {Url}", runtime.Url);
                }
            }

            if (staleReceiveTask != null)
            {
                try
                {
                    await staleReceiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Stale receive loop ended with an error for tracker {Url}", runtime.Url);
                }
            }

            try
            {
                await ConnectTrackerAsync(runtime, isInitial: true, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Reconnected to tracker {Url}", runtime.Url);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (TrackerConnectFailureClassifier.IsTerminal(ex))
            {
                LogTrackerFailure(LogLevel.Information, ex, "Stopping reconnect attempts for tracker {Url}: {Reason}", runtime.Url);
                MarkTrackerTerminalFailure(runtime, ex);
            }
            catch (Exception ex)
            {
                LogTrackerFailure(LogLevel.Warning, ex, "Reconnect failed for tracker {Url}: {Reason}", runtime.Url);
                ScheduleTrackerReconnect(runtime, ex);
            }
        }
        finally
        {
            lock (runtime.SyncRoot)
            {
                runtime.ReconnectInProgress = false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        TrackerRuntime[] trackerSnapshot;
        lock (_trackers)
        {
            trackerSnapshot = _trackers.ToArray();
        }

        foreach (var tracker in trackerSnapshot)
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

        foreach (var tracker in trackerSnapshot)
        {
            if (tracker.Socket == null)
            {
                continue;
            }

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

        Task[] pendingTasks;
        lock (_backgroundTasksLock)
        {
            pendingTasks = _backgroundTasks.ToArray();
            _backgroundTasks.Clear();
        }

        try
        {
            await Task.WhenAll(pendingTasks).ConfigureAwait(false);
        }
        catch
        {
            // Individual task failures are logged at the source; swallow on dispose.
        }

        _cts.Dispose();
    }

    private IReadOnlyList<string> GetTrackerUrls()
    {
        var urls = new HashSet<string>(
            _torrent.Trackers
                .GetTrackers()
                .Select(static tracker => tracker.Url)
                .Where(WebTorrentTrackerUrls.IsWebSocketTracker),
            StringComparer.OrdinalIgnoreCase);

        foreach (var url in _options.AdditionalTrackers)
        {
            if (WebTorrentTrackerUrls.IsWebSocketTracker(url))
            {
                urls.Add(url);
            }
        }

        return urls.ToList();
    }

    private async Task SendStoppedAsync(TrackerRuntime runtime)
    {
        if (!TryGetConnectedSocket(runtime, out var socket))
        {
            return;
        }

        var payload = CreateAnnounceBasePayload();
        payload["event"] = "stopped";
        await socket.SendTextAsync(payload.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SendCompletedAsync(TrackerRuntime runtime, CancellationToken cancellationToken)
    {
        if (!TryGetConnectedSocket(runtime, out var socket))
        {
            return;
        }

        var payload = CreateAnnounceBasePayload();
        payload["event"] = "completed";
        await socket.SendTextAsync(payload.ToJsonString(), cancellationToken).ConfigureAwait(false);
    }

    private JsonObject CreateAnnounceBasePayload()
    {
        // BEP-3: uploaded/downloaded are relative to the 'started' event we sent. Subtract
        // the baseline captured at StartAsync so we don't advertise the host's lifetime total.
        long uploaded = Math.Max(0, _host.DataUploaded - _uploadedBaseline);
        long downloaded = Math.Max(0, _host.DataDownloaded - _downloadedBaseline);
        return new JsonObject
        {
            ["action"] = "announce",
            ["info_hash"] = BinaryStringEncoding.Encode(_host.Hash.ToArray()),
            ["peer_id"] = BinaryStringEncoding.Encode(_host.PeerId),
            ["uploaded"] = uploaded,
            ["downloaded"] = downloaded,
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
                    _earlyRemoteCandidates.TryRemove(key, out _);
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

            offers.Add((JsonNode)new JsonObject
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
        if (!TryGetConnectedSocket(runtime, out var socket))
        {
            throw new InvalidOperationException("tracker socket is not connected");
        }
        await socket.SendTextAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunReceiveLoopAsync(TrackerRuntime runtime, int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IWebSocketConnection? socket;
                lock (runtime.SyncRoot)
                {
                    if (runtime.Generation != generation)
                    {
                        return;
                    }
                    socket = runtime.Socket;
                }
                if (socket == null)
                {
                    break;
                }

                string message = await socket.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
                if (!IsCurrentTrackerSocket(runtime, generation, socket))
                {
                    return;
                }

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
                    var interval = TimeSpan.FromSeconds(Math.Max(
                        (int)_options.MinimumReannounceInterval.TotalSeconds,
                        signal.Interval.Value));
                    lock (runtime.SyncRoot)
                    {
                        runtime.ReannounceInterval = interval;
                        runtime.NextAnnounce = _timeProvider.GetUtcNow() + interval;
                    }
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

                if (signal.AnswerSdp != null
                    && signal.OfferId != null
                    && signal.AnswerType?.Equals("offer", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogInformation(
                        "Received offer in answer envelope from peer {PeerId} for offer {OfferId}",
                        signal.PeerId,
                        FormatOfferIdForLog(signal.OfferId));
                    await HandleOfferAsync(runtime, signal.AsOfferFromAnswer(), cancellationToken).ConfigureAwait(false);
                }
                else if (signal.AnswerSdp != null
                    && signal.OfferId != null
                    && (signal.AnswerType == null || signal.AnswerType.Equals("answer", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("Received answer from peer {PeerId} for offer {OfferId}", signal.PeerId, FormatOfferIdForLog(signal.OfferId));
                    await HandleAnswerAsync(signal, cancellationToken).ConfigureAwait(false);
                }
                else if (signal.AnswerSdp != null && signal.OfferId != null)
                {
                    _logger.LogWarning(
                        "Ignoring unsupported WebTorrent answer type {Type} from peer {PeerId} for offer {OfferId}",
                        signal.AnswerType,
                        signal.PeerId,
                        FormatOfferIdForLog(signal.OfferId));
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
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive loop failed for tracker {Url}", runtime.Url);
            ScheduleTrackerReconnectForGeneration(runtime, generation, ex);
            return;
        }

        // Clean exit (socket closed by peer or runtime.Socket == null) — schedule reconnect
        // unless the session itself is shutting down, and only if nothing newer has taken
        // ownership of the runtime.
        if (!cancellationToken.IsCancellationRequested)
        {
            ScheduleTrackerReconnectForGeneration(runtime, generation, new InvalidOperationException("tracker socket closed"));
        }
    }

    private async Task HandleAnswerAsync(WebTorrentSignalMessage signal, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(signal.OfferId!, out var pending))
        {
            _logger.LogWarning("No pending connection found for offer_id (have {Count} pending)", _connections.Count);
            return;
        }

        pending.RefreshExpiry();

        if (!RemoteSdpReachability.IsLikelyReachable(signal.AnswerSdp!, _localSubnets))
        {
            _logger.LogInformation(
                "Skipping unreachable peer {PeerId}: SDP advertises only private host candidates with no shared subnet",
                signal.PeerId);

            await RemovePendingAsync(signal.OfferId!).ConfigureAwait(false);
            return;
        }

        try
        {
            pending.RemotePeerId = signal.PeerId;
            pending.LocalCandidateSignalingReady = true;
            _logger.LogDebug("Setting remote description for peer {PeerId}", signal.PeerId);
            await pending.Connection.SetRemoteDescriptionAsync(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Answer, FilterUnsupportedIceCandidates(signal.AnswerSdp!)), cancellationToken).ConfigureAwait(false);
            pending.RemoteDescriptionSet = true;
            _logger.LogDebug("Flushing buffered ICE candidates");
            await FlushBufferedCandidatesAsync(pending, cancellationToken).ConfigureAwait(false);
            await FlushBufferedRemoteCandidatesAsync(pending, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Calling ConnectAsync");
            bool connected = await pending.Connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ConnectAsync returned {Connected} for peer {PeerId}", connected, signal.PeerId);
            if (!connected)
            {
                await RemovePendingAsync(signal.OfferId!).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await RemovePendingAsync(signal.OfferId!).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle answer from peer {PeerId}", signal.PeerId);
            await RemovePendingAsync(signal.OfferId!).ConfigureAwait(false);
        }
    }

    private async Task HandleOfferAsync(TrackerRuntime runtime, WebTorrentSignalMessage signal, CancellationToken cancellationToken)
    {
        if (!RemoteSdpReachability.IsLikelyReachable(signal.OfferSdp!, _localSubnets))
        {
            _logger.LogInformation(
                "Skipping unreachable peer {PeerId}: offer SDP advertises only private host candidates with no shared subnet",
                signal.PeerId);
            return;
        }

        // Bound the pending-connection map so a misbehaving tracker flooding offers can't
        // drive unbounded resource growth while ICE checks stall.
        if (_connections.Count >= MaxPendingConnections)
        {
            _logger.LogWarning("Dropping inbound offer from peer {PeerId}: pending-connection cap ({Cap}) reached",
                signal.PeerId, MaxPendingConnections);
            return;
        }

        var connection = _rtcFactory.Create();
        await ReplaceExistingPendingForInboundOfferAsync(signal.OfferId!).ConfigureAwait(false);

        var pending = new PendingPeer(signal.OfferId!, connection, initiator: false, runtime)
        {
            RemotePeerId = signal.PeerId
        };
        ConfigurePendingPeer(pending);
        _connections[pending.OfferId] = pending;

        try
        {
            await connection.SetRemoteDescriptionAsync(new WebRtcSessionDescription(WebRtcSessionDescriptionType.Offer, FilterUnsupportedIceCandidates(signal.OfferSdp!)), cancellationToken).ConfigureAwait(false);
            pending.RemoteDescriptionSet = true;
            var answer = await connection.CreateAnswerAsync(cancellationToken).ConfigureAwait(false);
            await connection.SetLocalDescriptionAsync(answer, cancellationToken).ConfigureAwait(false);
            bool answerSent = await SendAnswerAsync(runtime, pending, new WebRtcSessionDescription(answer.Type, FilterUnsupportedIceCandidates(answer.Sdp)), cancellationToken).ConfigureAwait(false);
            if (!answerSent)
            {
                await RemovePendingAsync(pending.OfferId).ConfigureAwait(false);
                return;
            }
            lock (pending.SyncRoot)
            {
                pending.LocalCandidateSignalingReady = true;
            }
            await FlushBufferedCandidatesAsync(pending, cancellationToken).ConfigureAwait(false);
            await FlushBufferedRemoteCandidatesAsync(pending, cancellationToken).ConfigureAwait(false);
            bool connected = await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ConnectAsync returned {Connected} for inbound peer {PeerId}", connected, signal.PeerId);
            if (!connected)
            {
                await RemovePendingAsync(pending.OfferId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await RemovePendingAsync(pending.OfferId).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle offer from peer {PeerId}", signal.PeerId);
            await RemovePendingAsync(pending.OfferId).ConfigureAwait(false);
        }
    }

    private async Task ReplaceExistingPendingForInboundOfferAsync(string offerId)
    {
        if (!_connections.TryRemove(offerId, out var existing))
        {
            return;
        }

        try
        {
            await existing.Connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispose replaced WebRTC connection for offer {OfferId}", FormatOfferIdForLog(offerId));
        }
    }

    private async Task RemovePendingAsync(string offerId)
    {
        _earlyRemoteCandidates.TryRemove(offerId, out _);
        if (_connections.TryRemove(offerId, out var removed))
        {
            try
            {
                await removed.Connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to dispose WebRTC connection for offer {OfferId}", FormatOfferIdForLog(offerId));
            }
        }
    }

    private void LogTrackerFailure(LogLevel level, Exception ex, string messageTemplate, string trackerUrl)
    {
        string reason = TrackerConnectFailureClassifier.Describe(ex);
        if (TrackerConnectFailureClassifier.IsExpected(ex))
        {
            _logger.Log(level, messageTemplate, trackerUrl, reason);
            _logger.LogDebug(ex, "WebTorrent tracker failure detail for {Url}", trackerUrl);
            return;
        }

        _logger.Log(level, ex, messageTemplate, trackerUrl, reason);
    }

    private async Task HandleCandidateAsync(WebTorrentSignalMessage signal, CancellationToken cancellationToken)
    {
        if (!IsSupportedIceCandidate(signal.Candidate!))
        {
            _logger.LogDebug("Ignoring unsupported ICE candidate for offer {OfferId}: {Candidate}", FormatOfferIdForLog(signal.OfferId), signal.Candidate);
            return;
        }

        if (!_connections.TryGetValue(signal.OfferId!, out var pending))
        {
            BufferEarlyRemoteCandidate(signal.OfferId!, signal.Candidate!);
            return;
        }

        pending.RefreshExpiry();

        if (!pending.RemoteDescriptionSet)
        {
            lock (pending.SyncRoot)
            {
                pending.BufferedRemoteCandidates.Add(signal.Candidate!);
            }

            return;
        }

        await pending.Connection.AddRemoteIceCandidateAsync(new WebRtcIceCandidateDescription(signal.Candidate!), cancellationToken).ConfigureAwait(false);
    }

    private void ConfigurePendingPeer(PendingPeer pending)
    {
        TrackBackgroundTask(Task.Run(() => PumpLocalIceCandidatesAsync(pending)));

        if (pending.Channel != null)
        {
            TrackBackgroundTask(Task.Run(() => WaitForLocalChannelOpenAsync(pending)));
            return;
        }

        TrackBackgroundTask(Task.Run(() => PumpRemoteDataChannelsAsync(pending)));
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
            if (string.IsNullOrWhiteSpace(pending.RemotePeerId) || !pending.LocalCandidateSignalingReady)
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
            if (pending.BufferedLocalCandidates.Count == 0
                || string.IsNullOrWhiteSpace(pending.RemotePeerId)
                || !pending.LocalCandidateSignalingReady)
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

    private void BufferEarlyRemoteCandidate(string offerId, string candidate)
    {
        // Drop buffered candidates for unknown offers when we're already tracking the cap of
        // distinct offer IDs — otherwise a flooding tracker could grow the dictionary.
        if (!_earlyRemoteCandidates.ContainsKey(offerId) && _earlyRemoteCandidates.Count >= MaxPendingConnections)
        {
            return;
        }

        var candidates = _earlyRemoteCandidates.GetOrAdd(offerId, static _ => new ConcurrentQueue<string>());
        candidates.Enqueue(candidate);

        while (candidates.Count > 32 && candidates.TryDequeue(out _))
        {
            // Keep a bounded backlog for out-of-order tracker candidate messages.
        }
    }

    private async Task FlushBufferedRemoteCandidatesAsync(PendingPeer pending, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (_earlyRemoteCandidates.TryRemove(pending.OfferId, out var earlyCandidates))
        {
            while (earlyCandidates.TryDequeue(out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        lock (pending.SyncRoot)
        {
            if (pending.BufferedRemoteCandidates.Count > 0)
            {
                candidates.AddRange(pending.BufferedRemoteCandidates);
                pending.BufferedRemoteCandidates.Clear();
            }
        }

        foreach (var candidate in candidates.Where(IsSupportedIceCandidate))
        {
            await pending.Connection.AddRemoteIceCandidateAsync(new WebRtcIceCandidateDescription(candidate), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> SendAnswerAsync(TrackerRuntime runtime, PendingPeer pending, WebRtcSessionDescription answer, CancellationToken cancellationToken)
    {
        var payload = CreateAnnounceBasePayload();
        payload["to_peer_id"] = pending.RemotePeerId;
        payload["offer_id"] = pending.OfferId;
        payload["answer"] = new JsonObject
        {
            ["type"] = "answer",
            ["sdp"] = answer.Sdp
        };

        if (!TryGetConnectedSocket(runtime, out var socket))
        {
            return false;
        }

        await socket.SendTextAsync(payload.ToJsonString(), cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Sent answer to peer {PeerId} for offer {OfferId}", pending.RemotePeerId, FormatOfferIdForLog(pending.OfferId));
        return true;
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
        if (!TryGetConnectedSocket(pending.Runtime, out var socket))
        {
            _logger.LogDebug("Skipping ICE candidate for offer {OfferId}: tracker socket disconnected", FormatOfferIdForLog(pending.OfferId));
            return;
        }
        await socket.SendTextAsync(payload.ToJsonString(), cancellationToken).ConfigureAwait(false);
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
            bool bitTorrentInitiator = pending.Initiator;
            _logger.LogInformation(
                "Attaching WebTorrent data channel for peer {PeerId} offer {OfferId} label={Label} rtcInitiator={RtcInitiator} bitTorrentInitiator={BitTorrentInitiator}",
                pending.RemotePeerId,
                FormatOfferIdForLog(pending.OfferId),
                channel.Label,
                pending.Initiator,
                bitTorrentInitiator);

            await _host.AttachPeerTransportAsync(stream, bitTorrentInitiator, cancellationToken).ConfigureAwait(false);
            _earlyRemoteCandidates.TryRemove(pending.OfferId, out _);
            _connections.TryRemove(pending.OfferId, out _);

            _logger.LogInformation("Attached WebTorrent peer {PeerId} offer {OfferId}", pending.RemotePeerId, FormatOfferIdForLog(pending.OfferId));
        }
        catch (Exception)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
        if (IPAddress.TryParse(parts[4], out var address))
        {
            return address.AddressFamily == AddressFamily.InterNetwork;
        }

        return true;
    }

    private static bool TryGetConnectedSocket(TrackerRuntime runtime, [NotNullWhen(true)] out IWebSocketConnection? socket)
    {
        lock (runtime.SyncRoot)
        {
            if (runtime.IsConnected && runtime.Socket != null)
            {
                socket = runtime.Socket;
                return true;
            }
        }

        socket = null;
        return false;
    }

    private static bool IsCurrentTrackerSocket(TrackerRuntime runtime, int generation, IWebSocketConnection socket)
    {
        lock (runtime.SyncRoot)
        {
            return runtime.Generation == generation
                && ReferenceEquals(runtime.Socket, socket);
        }
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
            ExpiresAt = runtime.Session._timeProvider.GetUtcNow() + PendingPeerTimeout;
        }

        public List<WebRtcIceCandidateDescription> BufferedLocalCandidates { get; } = new();
        public List<string> BufferedRemoteCandidates { get; } = new();
        public IWebRtcDataChannel? Channel { get; }
        public IWebRtcConnection Connection { get; }
        public bool Initiator { get; }
        public bool IsAttached => Volatile.Read(ref _attached) == 1;
        public DateTimeOffset ExpiresAt { get; set; }
        public string OfferId { get; }
        public bool LocalCandidateSignalingReady { get; set; }
        public bool RemoteDescriptionSet { get; set; }
        public string? RemotePeerId { get; set; }
        public TrackerRuntime Runtime { get; }
        public object SyncRoot { get; } = new();

        public void RefreshExpiry() => ExpiresAt = Runtime.Session._timeProvider.GetUtcNow() + PendingPeerTimeout;
        public bool TryMarkAttached() => Interlocked.Exchange(ref _attached, 1) == 0;
    }

    private sealed class TrackerRuntime
    {
        public TrackerRuntime(string url, WebTorrentSession session)
        {
            Url = url;
            Session = session;
        }

        public WebTorrentSession Session { get; }
        public string Url { get; }
        public object SyncRoot { get; } = new();
        public IWebSocketConnection? Socket { get; set; }
        public DateTimeOffset NextAnnounce { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset NextReconnectAt { get; set; } = DateTimeOffset.MinValue;
        public TimeSpan ReannounceInterval { get; set; } = TimeSpan.FromSeconds(120);
        public Task? ReceiveTask { get; set; }
        public bool IsConnected { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? LastError { get; set; }
        public bool CompletedSent { get; set; }
        public bool ReconnectInProgress { get; set; }

        // Incremented every time ConnectTrackerAsync binds a fresh socket. Receive loops
        // capture the generation at entry and refuse to schedule reconnects once the runtime
        // has moved on to a newer socket — otherwise a stale loop could clobber fresh state.
        public int Generation { get; set; }
    }

    /// <summary>
    /// Snapshot of a WebTorrent tracker's current health.
    /// </summary>
    public sealed record TrackerHealth(string Url, bool IsConnected, int ConsecutiveFailures, string? LastError, DateTimeOffset NextReconnectAt);

    /// <summary>
    /// Snapshot of overall WebTorrent session health and resource usage.
    /// </summary>
    public sealed record SessionDiagnostics(
        int TrackerCount,
        int ConnectedTrackers,
        int ReconnectingTrackers,
        int PendingPeerCount,
        int EarlyCandidateOfferCount,
        bool TorrentFinished);

    /// <summary>
    /// Returns a snapshot of health information for every WebSocket tracker the session
    /// attempted to contact. Callers can surface this to UI or diagnostics; an all-disconnected
    /// session indicates no swarm will be discovered.
    /// </summary>
    public IReadOnlyList<TrackerHealth> GetTrackerHealth()
    {
        TrackerRuntime[] trackers;
        lock (_trackers)
        {
            trackers = _trackers.ToArray();
        }

        var snapshot = new List<TrackerHealth>(trackers.Length);
        foreach (var runtime in trackers)
        {
            lock (runtime.SyncRoot)
            {
                snapshot.Add(new TrackerHealth(runtime.Url, runtime.IsConnected, runtime.ConsecutiveFailures, runtime.LastError, runtime.NextReconnectAt));
            }
        }
        return snapshot;
    }

    /// <summary>
    /// Returns a point-in-time snapshot of overall WebTorrent session state for diagnostics.
    /// </summary>
    public SessionDiagnostics GetDiagnostics()
    {
        TrackerRuntime[] trackers;
        lock (_trackers)
        {
            trackers = _trackers.ToArray();
        }

        int connectedTrackers = 0;
        int reconnectingTrackers = 0;
        foreach (var runtime in trackers)
        {
            lock (runtime.SyncRoot)
            {
                if (runtime.IsConnected)
                {
                    connectedTrackers++;
                }

                if (runtime.ReconnectInProgress || (!runtime.IsConnected && runtime.NextReconnectAt != DateTimeOffset.MinValue))
                {
                    reconnectingTrackers++;
                }
            }
        }

        return new SessionDiagnostics(
            TrackerCount: trackers.Length,
            ConnectedTrackers: connectedTrackers,
            ReconnectingTrackers: reconnectingTrackers,
            PendingPeerCount: _connections.Count,
            EarlyCandidateOfferCount: _earlyRemoteCandidates.Count,
            TorrentFinished: _torrent.Finished);
    }

    [ExcludeFromCodeCoverage]
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

[ExcludeFromCodeCoverage]
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
            IceTransportPolicy = options.IceTransportPolicy switch
            {
                WebTorrentIceTransportPolicy.Relay => RTCIceTransportPolicy.Relay,
                _ => RTCIceTransportPolicy.All
            },
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

[ExcludeFromCodeCoverage]
internal sealed class SystemWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    private readonly int _maxMessageBytes;

    public SystemWebSocketConnectionFactory(int maxMessageBytes)
    {
        _maxMessageBytes = maxMessageBytes;
    }

    public IWebSocketConnection Create() => new SystemWebSocketConnection(_maxMessageBytes);
}

[ExcludeFromCodeCoverage]
internal sealed class SystemWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly int _maxMessageBytes;

    public SystemWebSocketConnection(int maxMessageBytes)
    {
        _maxMessageBytes = Math.Max(1, maxMessageBytes);
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
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
        using var ms = new CappedMemoryStream(_maxMessageBytes);

        while (true)
        {
            var result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return string.Empty;
            }

            await ms.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
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
    }
}

internal sealed class TrackerMessageTooLargeException : IOException
{
    public TrackerMessageTooLargeException(int maxMessageBytes)
        : base($"WebTorrent tracker message exceeded the configured limit of {maxMessageBytes} bytes.")
    {
    }
}

internal sealed class CappedMemoryStream : MemoryStream
{
    private readonly int _maxLength;

    public CappedMemoryStream(int maxLength)
    {
        _maxLength = Math.Max(1, maxLength);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureWithinLimit(count);
        base.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWithinLimit(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    private void EnsureWithinLimit(int count)
    {
        if (Length + count > _maxLength)
        {
            throw new TrackerMessageTooLargeException(_maxLength);
        }
    }
}

internal static class TrackerConnectFailureClassifier
{
    public static bool IsExpected(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case AuthenticationException:
                case TimeoutException:
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain or SocketError.ConnectionRefused or SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable }:
                    return true;
                case HttpRequestException httpEx when httpEx.StatusCode is HttpStatusCode code && (int)code is >= 400 and < 500:
                    return true;
                case WebSocketException wsEx when wsEx.Message.Contains("status code", StringComparison.OrdinalIgnoreCase):
                    return true;
            }
        }

        return false;
    }

    public static bool IsTerminal(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case AuthenticationException:
                    return true;
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }:
                    return true;
                case HttpRequestException httpEx when httpEx.StatusCode is HttpStatusCode code && IsTerminalStatusCode(code):
                    return true;
                case WebSocketException wsEx when TryGetHandshakeStatusCode(wsEx.Message, out var statusCode) && IsTerminalStatusCode(statusCode):
                    return true;
            }
        }

        return false;
    }

    public static string Describe(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case AuthenticationException:
                    return "TLS certificate validation failed";
                case SocketException sockEx when sockEx.SocketErrorCode == SocketError.HostNotFound:
                    return "DNS lookup failed";
                case SocketException sockEx:
                    return $"socket error ({sockEx.SocketErrorCode})";
                case TimeoutException:
                    return "tracker connect timed out";
                case HttpRequestException httpEx when httpEx.StatusCode.HasValue:
                    return $"HTTP {(int)httpEx.StatusCode.Value} from tracker";
                case WebSocketException wsEx when wsEx.Message.Contains("status code", StringComparison.OrdinalIgnoreCase):
                    return wsEx.Message;
            }
        }

        return ex.Message;
    }

    private static bool IsTerminalStatusCode(HttpStatusCode statusCode)
        => (int)statusCode is >= 400 and < 500
            && statusCode is not HttpStatusCode.RequestTimeout
            && (int)statusCode != 429;

    private static bool TryGetHandshakeStatusCode(string message, out HttpStatusCode statusCode)
    {
        statusCode = default;

        const string marker = "status code '";
        int markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        int digitsStart = markerIndex + marker.Length;
        int digitsEnd = digitsStart;
        while (digitsEnd < message.Length && char.IsDigit(message[digitsEnd]))
        {
            digitsEnd++;
        }

        if (digitsEnd == digitsStart || !int.TryParse(message.AsSpan(digitsStart, digitsEnd - digitsStart), out int value))
        {
            return false;
        }

        statusCode = (HttpStatusCode)value;
        return true;
    }
}

internal readonly record struct LocalSubnet(IPAddress Address, IPAddress Mask);

internal static class RemoteSdpReachability
{
    public static IReadOnlyList<LocalSubnet> EnumerateLocalSubnets()
    {
        var subnets = new List<LocalSubnet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (IPAddress.IsLoopback(info.Address))
                    {
                        continue;
                    }

                    if (info.IPv4Mask is null || info.IPv4Mask.Equals(IPAddress.Any))
                    {
                        continue;
                    }

                    subnets.Add(new LocalSubnet(info.Address, info.IPv4Mask));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Best-effort: if interfaces can't be enumerated, treat all peers as reachable.
        }

        return subnets;
    }

    public static bool IsLikelyReachable(string sdp, IReadOnlyList<LocalSubnet> localSubnets)
    {
        bool endOfCandidates = false;
        bool sawAnyHostCandidate = false;
        bool sawSupportedHost = false;
        var hostAddresses = new List<IPAddress>();

        foreach (var rawLine in sdp.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Equals("a=end-of-candidates", StringComparison.OrdinalIgnoreCase))
            {
                endOfCandidates = true;
                continue;
            }

            if (!line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Substring("a=candidate:".Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int typIndex = Array.IndexOf(parts, "typ");
            if (typIndex < 0 || typIndex + 1 >= parts.Length || parts.Length < 5)
            {
                continue;
            }

            string typ = parts[typIndex + 1];
            if (typ.Equals("srflx", StringComparison.OrdinalIgnoreCase) ||
                typ.Equals("relay", StringComparison.OrdinalIgnoreCase) ||
                typ.Equals("prflx", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!typ.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sawAnyHostCandidate = true;

            // ICE candidate format: foundation component transport priority address port typ ...
            // (RFC 5245 §15.1) — connection-address is parts[4].
            string connectionAddress = parts[4];
            if (IPAddress.TryParse(connectionAddress, out var addr))
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    sawSupportedHost = true;
                    hostAddresses.Add(addr);
                }
                // IPv6 literals are stripped by FilterUnsupportedIceCandidates before
                // being forwarded to ICE — treat them as unsupported.
            }
            else
            {
                // Hostname/mDNS: resolvable at pair-check time by the ICE agent.
                sawSupportedHost = true;
            }
        }

        // Trickle ICE: if the peer hasn't sent end-of-candidates yet, more may arrive
        // out-of-band — defer the verdict and let the ICE agent try.
        if (!endOfCandidates)
        {
            return true;
        }

        // If the peer advertised host candidates but every one was unsupported (IPv6 literal,
        // TCP-only, etc.), the filter will strip them all and ICE will hit its 15s timeout.
        if (sawAnyHostCandidate && !sawSupportedHost)
        {
            return false;
        }

        foreach (var addr in hostAddresses)
        {
            if (!IsPrivateIPv4(addr))
            {
                return true;
            }

            foreach (var subnet in localSubnets)
            {
                if (IsAddressInSubnet(addr, subnet))
                {
                    return true;
                }
            }
        }

        // No usable IPv4 host found. Defer only when the SDP left room for ICE to find
        // something (mDNS hostnames, no candidates at all yet).
        return hostAddresses.Count == 0;
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out _))
        {
            return false;
        }

        // 10.0.0.0/8
        if (bytes[0] == 10)
        {
            return true;
        }
        // 172.16.0.0/12
        if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
        {
            return true;
        }
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true;
        }
        // 169.254.0.0/16 link-local
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }
        // 127.0.0.0/8 loopback
        if (bytes[0] == 127)
        {
            return true;
        }
        // 100.64.0.0/10 CGNAT
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
        {
            return true;
        }

        return false;
    }

    private static bool IsAddressInSubnet(IPAddress address, LocalSubnet subnet)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            subnet.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> addrBytes = stackalloc byte[4];
        Span<byte> netBytes = stackalloc byte[4];
        Span<byte> maskBytes = stackalloc byte[4];
        if (!address.TryWriteBytes(addrBytes, out _) ||
            !subnet.Address.TryWriteBytes(netBytes, out _) ||
            !subnet.Mask.TryWriteBytes(maskBytes, out _))
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if ((addrBytes[i] & maskBytes[i]) != (netBytes[i] & maskBytes[i]))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record WebTorrentSignalMessage(
    string InfoHash,
    string? PeerId,
    string? OfferId,
    string? OfferSdp,
    string? OfferType,
    string? AnswerSdp,
    string? AnswerType,
    string? Candidate,
    int? Interval)
{
    public WebTorrentSignalMessage AsOfferFromAnswer()
        => this with
        {
            OfferSdp = AnswerSdp,
            OfferType = AnswerType,
            AnswerSdp = null,
            AnswerType = null
        };
}

internal static class WebTorrentProtocolCodec
{
    public static WebTorrentSignalMessage Parse(string json)
    {
        JsonNode node = JsonNode.Parse(json) ?? new JsonObject();
        string infoHash = node["info_hash"]?.GetValue<string>() ?? string.Empty;
        string? peerId = node["peer_id"]?.GetValue<string>();
        string? offerId = node["offer_id"]?.GetValue<string>();
        string? offerSdp = node["offer"]?["sdp"]?.GetValue<string>();
        string? offerType = node["offer"]?["type"]?.GetValue<string>();
        string? answerSdp = node["answer"]?["sdp"]?.GetValue<string>();
        string? answerType = node["answer"]?["type"]?.GetValue<string>();
        int? interval = node["interval"]?.GetValue<int>();
        string? candidate = node["candidate"] switch
        {
            JsonValue value => value.GetValue<string>(),
            JsonObject candidateObject => candidateObject["candidate"]?.GetValue<string>(),
            _ => null
        };

        return new WebTorrentSignalMessage(infoHash, peerId, offerId, offerSdp, offerType, answerSdp, answerType, candidate, interval);
    }
}

internal static class BinaryStringEncoding
{
    public static string Encode(ReadOnlyMemory<byte> bytes) => Encoding.Latin1.GetString(bytes.Span);
    public static string Encode(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}

internal static class WebTorrentTrackerUrls
{
    public static bool IsWebSocketTracker(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> Collect(
        string? announce,
        IEnumerable<string> announceList,
        IEnumerable<IEnumerable<string>> announceTiers,
        IEnumerable<string> additionalTrackers)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (IsWebSocketTracker(announce))
        {
            urls.Add(announce!);
        }

        foreach (var url in announceList)
        {
            if (IsWebSocketTracker(url))
            {
                urls.Add(url);
            }
        }

        foreach (var url in announceTiers.SelectMany(static tier => tier))
        {
            if (IsWebSocketTracker(url))
            {
                urls.Add(url);
            }
        }

        foreach (var url in additionalTrackers)
        {
            if (IsWebSocketTracker(url))
            {
                urls.Add(url);
            }
        }

        return urls.ToList();
    }
}
