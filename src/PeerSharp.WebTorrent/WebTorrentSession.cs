using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Interfaces;
using PeerSharp.WebTorrent.Configuration;
using PeerSharp.WebTorrent.Network;
using PeerSharp.WebTorrent.Peers;
using PeerSharp.WebTorrent.Signaling;
using PeerSharp.WebTorrent.Trackers;
using PeerSharp.WebTorrent.Transport;
using PeerSharp.WebTorrent.Utilities;
using RtcForge;

namespace PeerSharp.WebTorrent;

/// <summary>
/// BitTorrent-over-WebRTC session that discovers peers via WebSocket trackers (WebTorrent
/// protocol) and attaches each negotiated data channel to the owning torrent as a peer
/// transport.
/// </summary>
public sealed class WebTorrentSession : IAsyncDisposable
{
    private static readonly TimeSpan ActiveLoopMaxDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IdleLoopMaxDelay = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts = new();
    private readonly IPeerTransportHost _host;
    private readonly ITorrent _torrent;
    private readonly WebTorrentSessionOptions _options;
    private readonly ILogger<WebTorrentSession> _logger;
    private readonly Lock _backgroundTasksLock = new();
    private readonly List<Task> _backgroundTasks = new();
    private readonly WebTorrentTrackerManager _trackerManager;
    private readonly WebRtcPeerManager _peerManager;
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
            new DefaultWebRtcConnectionFactory(resolvedOptions, resolvedLoggerFactory),
            new SystemWebSocketConnectionFactory(resolvedOptions.MaxTrackerMessageBytes),
            resolvedLoggerFactory);
    }

    internal WebTorrentSession(ITorrent torrent, IPeerTransportHost host, WebTorrentSessionOptions options, IWebRtcConnectionFactory rtcFactory, IWebSocketConnectionFactory socketFactory, ILoggerFactory? loggerFactory = null)
    {
        _torrent = torrent;
        _host = host;
        _options = options;
        var actualLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = actualLoggerFactory.CreateLogger<WebTorrentSession>();

        _trackerManager = new WebTorrentTrackerManager(
            torrent,
            host,
            options,
            socketFactory,
            actualLoggerFactory,
            host.DataUploaded,
            host.DataDownloaded,
            HandleSignalReceived
        );

        _peerManager = new WebRtcPeerManager(
            rtcFactory,
            options,
            actualLoggerFactory.CreateLogger<WebRtcPeerManager>(),
            RemoteSdpReachability.EnumerateLocalSubnets(),
            AttachChannelAsync,
            (pending, candidate) => TrackBackgroundTask(SendCandidateAsync(pending, candidate, _cts.Token)),
            TrackBackgroundTask,
            _cts.Token
        );
    }

    public static async Task<WebTorrentSession> AttachAsync(ITorrent torrent, WebTorrentSessionOptions? options = null, ILoggerFactory? loggerFactory = null, CancellationToken cancellationToken = default)
    {
        var session = new WebTorrentSession(torrent, options, loggerFactory);
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        await _trackerManager.StartAsync(cancellationToken).ConfigureAwait(false);

        // Send initial "started" announce for all connected trackers
        foreach (var runtime in _trackerManager.GetRuntimes())
        {
            if (runtime.IsConnected)
            {
                await SendOffersAsync(runtime, "started", cancellationToken).ConfigureAwait(false);
                lock (runtime.SyncRoot)
                {
                    runtime.NextAnnounce = _options.TimeProvider.GetUtcNow() + runtime.ReannounceInterval;
                }
            }
        }

        TrackBackgroundTask(RunBackgroundLoopAsync(_cts.Token));
    }

    public IReadOnlyList<TrackerHealth> GetTrackerHealth()
    {
        return _trackerManager.GetRuntimes().Select(runtime =>
        {
            lock (runtime.SyncRoot)
            {
                return new TrackerHealth(
                    runtime.Url,
                    runtime.IsConnected,
                    runtime.ConsecutiveFailures,
                    runtime.LastError,
                    runtime.NextReconnectAt);
            }
        }).ToList();
    }

    public SessionDiagnostics GetDiagnostics()
    {
        return new SessionDiagnostics(
            _trackerManager.GetRuntimes().Count,
            _trackerManager.GetRuntimes().Count(r => { lock (r.SyncRoot) return r.IsConnected; }),
            _trackerManager.GetRuntimes().Count(r => { lock (r.SyncRoot) return r.ReconnectInProgress; }),
            _peerManager.PendingConnectionCount,
            _peerManager.EarlyCandidateOfferCount,
            _torrent.Finished);
    }

    private async Task RunBackgroundLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(GetBackgroundLoopDelay(), _options.TimeProvider, cancellationToken).ConfigureAwait(false);

                await _peerManager.CleanupExpiredPendingPeersAsync().ConfigureAwait(false);

                bool finished = _torrent.Finished;
                foreach (var runtime in _trackerManager.GetRuntimes())
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
                        if (reconnectInProgress || nextReconnectAt == DateTimeOffset.MinValue || _options.TimeProvider.GetUtcNow() < nextReconnectAt) continue;
                        if (await _trackerManager.TryReconnectAsync(runtime, cancellationToken).ConfigureAwait(false))
                        {
                            await SendOffersAsync(runtime, "started", cancellationToken).ConfigureAwait(false);
                            lock (runtime.SyncRoot)
                            {
                                runtime.NextAnnounce = _options.TimeProvider.GetUtcNow() + runtime.ReannounceInterval;
                            }
                        }
                        continue;
                    }

                    if (finished && !completedSent)
                    {
                        try
                        {
                            await _trackerManager.ReannounceAsync(runtime, "completed", null, cancellationToken).ConfigureAwait(false);
                            lock (runtime.SyncRoot) runtime.CompletedSent = true;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to send completed announce to tracker {Url}", runtime.Url);
                        }
                    }

                    if (_options.TimeProvider.GetUtcNow() >= nextAnnounce)
                    {
                        try
                        {
                            await SendOffersAsync(runtime, null, cancellationToken).ConfigureAwait(false);
                            lock (runtime.SyncRoot)
                            {
                                runtime.NextAnnounce = _options.TimeProvider.GetUtcNow() + runtime.ReannounceInterval;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to reannounce offers to tracker {Url}", runtime.Url);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "WebTorrent background loop canceled.");
        }
    }

    private TimeSpan GetBackgroundLoopDelay()
    {
        var now = _options.TimeProvider.GetUtcNow();
        DateTimeOffset? nextDue = null;
        bool hasActiveWork = _peerManager.PendingConnectionCount > 0;

        foreach (var runtime in _trackerManager.GetRuntimes())
        {
            lock (runtime.SyncRoot)
            {
                if (runtime.ReconnectInProgress) { hasActiveWork = true; continue; }
                if (!runtime.IsConnected)
                {
                    if (runtime.NextReconnectAt != DateTimeOffset.MinValue)
                    {
                        nextDue = Min(nextDue, runtime.NextReconnectAt);
                        hasActiveWork = true;
                    }
                    continue;
                }
                hasActiveWork = true;
                nextDue = Min(nextDue, (!runtime.CompletedSent && _torrent.Finished) ? now : runtime.NextAnnounce);
            }
        }

        foreach (var pending in _peerManager.PendingPeers) nextDue = Min(nextDue, pending.ExpiresAt);

        if (!nextDue.HasValue) return hasActiveWork ? ActiveLoopMaxDelay : IdleLoopMaxDelay;
        var delay = nextDue.Value - now;
        if (delay <= TimeSpan.Zero) return TimeSpan.FromMilliseconds(50);
        var maxDelay = hasActiveWork ? ActiveLoopMaxDelay : IdleLoopMaxDelay;
        return delay < maxDelay ? delay : maxDelay;
    }

    private static DateTimeOffset Min(DateTimeOffset? current, DateTimeOffset candidate) => !current.HasValue || candidate < current.Value ? candidate : current.Value;

    private void HandleSignalReceived(WebTorrentSignalMessage signal, TrackerRuntime runtime)
    {
        TrackBackgroundTask(HandleSignalReceivedAsync(signal, runtime));
    }

    private async Task HandleSignalReceivedAsync(WebTorrentSignalMessage signal, TrackerRuntime runtime)
    {
        try
        {
            if (signal.AnswerSdp != null && signal.OfferId != null && signal.AnswerType?.Equals("offer", StringComparison.OrdinalIgnoreCase) == true)
            {
                await _peerManager.HandleOfferAsync(signal.OfferId, signal.PeerId!, signal.AnswerSdp, runtime, SendAnswerAsync, _cts.Token).ConfigureAwait(false);
            }
            else if (signal.AnswerSdp != null && signal.OfferId != null && (signal.AnswerType == null || signal.AnswerType.Equals("answer", StringComparison.OrdinalIgnoreCase)))
            {
                await _peerManager.HandleAnswerAsync(signal, _cts.Token).ConfigureAwait(false);
            }
            else if (signal.OfferSdp != null && signal.OfferId != null)
            {
                await _peerManager.HandleOfferAsync(signal.OfferId, signal.PeerId!, signal.OfferSdp, runtime, SendAnswerAsync, _cts.Token).ConfigureAwait(false);
            }

            if (signal.Candidate != null && signal.OfferId != null)
            {
                await _peerManager.HandleCandidateAsync(signal, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling signal from tracker");
        }
    }

    private async Task SendOffersAsync(TrackerRuntime runtime, string? @event, CancellationToken cancellationToken)
    {
        int offerCount = Math.Max(1, _options.OffersPerTracker);
        var offers = new JsonArray();

        for (int i = 0; i < offerCount; i++)
        {
            string offerId = BinaryStringEncoding.Encode(CreateRandomBytes(20));
            var (_, offer) = await _peerManager.CreateOutgoingPendingPeerAsync(offerId, runtime, cancellationToken).ConfigureAwait(false);
            string offerSdp = IceCandidateFilter.FilterUnsupportedIceCandidates(offer.Sdp);

            var offerNode = new JsonObject
            {
                ["offer_id"] = offerId,
                ["offer"] = new JsonObject
                {
                    ["type"] = "offer",
                    ["sdp"] = offerSdp
                }
            };
            offers.Add((JsonNode?)offerNode);
        }

        await _trackerManager.ReannounceAsync(runtime, @event, offers, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendAnswerAsync(PendingPeer pending, WebRtcSessionDescription answer, CancellationToken cancellationToken)
    {
        var signalData = new JsonObject
        {
            ["answer"] = new JsonObject
            {
                ["type"] = "answer",
                ["sdp"] = answer.Sdp
            }
        };

        try
        {
            await _trackerManager.SendSignalAsync(pending.Runtime, pending.RemotePeerId!, pending.OfferId, signalData, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Sent answer to peer {PeerId} for offer {OfferId}", pending.RemotePeerId, FormatOfferIdForLog(pending.OfferId));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send answer to peer {PeerId}", pending.RemotePeerId);
            return false;
        }
    }

    private async Task SendCandidateAsync(PendingPeer pending, WebRtcIceCandidateDescription candidate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pending.RemotePeerId)) return;

        var signalData = new JsonObject
        {
            ["candidate"] = new JsonObject
            {
                ["candidate"] = candidate.Candidate,
                ["sdpMid"] = "0",
                ["sdpMLineIndex"] = 0
            }
        };

        try
        {
            await _trackerManager.SendSignalAsync(pending.Runtime, pending.RemotePeerId, pending.OfferId, signalData, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Sent local ICE candidate to peer {PeerId} for offer {OfferId}", pending.RemotePeerId, FormatOfferIdForLog(pending.OfferId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send ICE candidate to peer {PeerId}", pending.RemotePeerId);
        }
    }

    private static string FormatOfferIdForLog(string? offerId)
    {
        if (string.IsNullOrEmpty(offerId)) return string.Empty;
        var bytes = Encoding.Latin1.GetBytes(offerId);
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private void AttachChannelAsync(PendingPeer pending, IWebRtcDataChannel channel)
    {
        TrackBackgroundTask(AttachChannelInternalAsync(pending, channel, _cts.Token));
    }

    private async Task AttachChannelInternalAsync(PendingPeer pending, IWebRtcDataChannel channel, CancellationToken cancellationToken)
    {
        if (!pending.TryMarkAttached()) return;

        var stream = new WebTorrentDataChannelStream(channel);
        stream.Start();
        try
        {
            await _host.AttachPeerTransportAsync(stream, pending.Initiator, cancellationToken).ConfigureAwait(false);
            await _peerManager.RemovePendingAsync(pending.OfferId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error attaching data channel");
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (_backgroundTasksLock)
        {
            _backgroundTasks.RemoveAll(t => t.IsCompleted);
            _backgroundTasks.Add(task);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) await _cts.CancelAsync().ConfigureAwait(false);

        // Send "stopped" announce to all connected trackers
        foreach (var runtime in _trackerManager.GetRuntimes())
        {
            if (runtime.IsConnected)
            {
                try
                {
                    await _trackerManager.ReannounceAsync(runtime, "stopped", null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Ignored error while sending stopped announce to tracker {Url}", runtime.Url);
                }
            }
        }

        await _trackerManager.DisposeAsync().ConfigureAwait(false);
        await _peerManager.DisposeAsync().ConfigureAwait(false);

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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignored error while awaiting WebTorrent background tasks during shutdown.");
        }

        _cts.Dispose();
    }

    private static IPeerTransportHost ResolveHost(ITorrent torrent)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        return torrent as IPeerTransportHost ?? throw new ArgumentException("The provided torrent does not support external peer transport attachment.", nameof(torrent));
    }

    private static byte[] CreateRandomBytes(int count)
    {
        byte[] bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
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
