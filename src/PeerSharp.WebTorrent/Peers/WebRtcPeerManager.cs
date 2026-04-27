using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using PeerSharp.WebTorrent.Configuration;
using PeerSharp.WebTorrent.Network;
using PeerSharp.WebTorrent.Signaling;
using PeerSharp.WebTorrent.Trackers;
using PeerSharp.WebTorrent.Utilities;
using RtcForge;

namespace PeerSharp.WebTorrent.Peers;

internal sealed class WebRtcPeerManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PendingPeer> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _earlyRemoteCandidates = new(StringComparer.Ordinal);
    private readonly IWebRtcConnectionFactory _rtcFactory;
    private readonly WebTorrentSessionOptions _options;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<LocalSubnet> _localSubnets;
    private readonly Action<PendingPeer, IWebRtcDataChannel> _onChannelOpened;
    private readonly Action<PendingPeer, WebRtcIceCandidateDescription> _onLocalIceCandidate;
    private readonly Action<Task> _trackBackgroundTask;
    private readonly CancellationToken _shutdownToken;

    private const int MaxPendingConnections = 256;
    private static readonly TimeSpan PendingPeerTimeout = TimeSpan.FromSeconds(30);

    public WebRtcPeerManager(
        IWebRtcConnectionFactory rtcFactory,
        WebTorrentSessionOptions options,
        ILogger logger,
        IReadOnlyList<LocalSubnet> localSubnets,
        Action<PendingPeer, IWebRtcDataChannel> onChannelOpened,
        Action<PendingPeer, WebRtcIceCandidateDescription> onLocalIceCandidate,
        Action<Task> trackBackgroundTask,
        CancellationToken shutdownToken)
    {
        _rtcFactory = rtcFactory;
        _options = options;
        _logger = logger;
        _localSubnets = localSubnets;
        _onChannelOpened = onChannelOpened;
        _onLocalIceCandidate = onLocalIceCandidate;
        _trackBackgroundTask = trackBackgroundTask;
        _shutdownToken = shutdownToken;
    }

    public int PendingConnectionCount => _connections.Count;
    public int EarlyCandidateOfferCount => _earlyRemoteCandidates.Count;
    public IEnumerable<PendingPeer> PendingPeers => _connections.Values;

    public async Task<(PendingPeer Peer, WebRtcSessionDescription Offer)> CreateOutgoingPendingPeerAsync(string offerId, TrackerRuntime runtime, CancellationToken cancellationToken)
    {
        var connection = _rtcFactory.Create();
        var channel = connection.CreateDataChannel(_options.DataChannelLabel);
        var pending = new PendingPeer(offerId, connection, channel, initiator: true, runtime, _options.TimeProvider.GetUtcNow() + PendingPeerTimeout);
        
        ConfigurePendingPeer(pending);
        _connections[offerId] = pending;

        var offer = await connection.CreateOfferAsync(cancellationToken).ConfigureAwait(false);
        await connection.SetLocalDescriptionAsync(offer, cancellationToken).ConfigureAwait(false);
        
        return (pending, offer);
    }

    public async Task HandleAnswerAsync(WebTorrentSignalMessage signal, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(signal.OfferId!, out var pending))
        {
            _logger.LogWarning("No pending connection found for offer_id {OfferId}", FormatOfferIdForLog(signal.OfferId));
            return;
        }

        pending.ExpiresAt = _options.TimeProvider.GetUtcNow() + PendingPeerTimeout;

        if (!RemoteSdpReachability.IsLikelyReachable(signal.AnswerSdp!, _localSubnets))
        {
            _logger.LogInformation("Skipping unreachable peer {PeerId}: SDP advertises only private host candidates with no shared subnet", signal.PeerId);
            await RemovePendingAsync(signal.OfferId!).ConfigureAwait(false);
            return;
        }

        try
        {
            pending.RemotePeerId = signal.PeerId;
            pending.LocalCandidateSignalingReady = true;
            
            var answer = new WebRtcSessionDescription(WebRtcSessionDescriptionType.Answer, IceCandidateFilter.FilterUnsupportedIceCandidates(signal.AnswerSdp!));
            await pending.Connection.SetRemoteDescriptionAsync(answer, cancellationToken).ConfigureAwait(false);
            pending.RemoteDescriptionSet = true;

            FlushBufferedLocalCandidates(pending);
            await FlushBufferedRemoteCandidatesAsync(pending, cancellationToken).ConfigureAwait(false);
            
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

    public async Task HandleOfferAsync(string offerId, string peerId, string offerSdp, TrackerRuntime runtime, Func<PendingPeer, WebRtcSessionDescription, CancellationToken, Task<bool>> sendAnswerFunc, CancellationToken cancellationToken)
    {
        if (!RemoteSdpReachability.IsLikelyReachable(offerSdp, _localSubnets))
        {
            _logger.LogInformation("Skipping unreachable peer {PeerId}: offer SDP advertises only private host candidates with no shared subnet", peerId);
            return;
        }

        if (_connections.Count >= MaxPendingConnections)
        {
            _logger.LogWarning("Dropping inbound offer from peer {PeerId}: pending-connection cap ({Cap}) reached", peerId, MaxPendingConnections);
            return;
        }

        await ReplaceExistingPendingForInboundOfferAsync(offerId).ConfigureAwait(false);

        var connection = _rtcFactory.Create();
        var pending = new PendingPeer(offerId, connection, null, initiator: false, runtime, _options.TimeProvider.GetUtcNow() + PendingPeerTimeout)
        {
            RemotePeerId = peerId
        };
        
        ConfigurePendingPeer(pending);
        _connections[offerId] = pending;

        try
        {
            var offer = new WebRtcSessionDescription(WebRtcSessionDescriptionType.Offer, IceCandidateFilter.FilterUnsupportedIceCandidates(offerSdp));
            await connection.SetRemoteDescriptionAsync(offer, cancellationToken).ConfigureAwait(false);
            pending.RemoteDescriptionSet = true;
            
            var answer = await connection.CreateAnswerAsync(cancellationToken).ConfigureAwait(false);
            await connection.SetLocalDescriptionAsync(answer, cancellationToken).ConfigureAwait(false);
            
            var trackerAnswer = new WebRtcSessionDescription(answer.Type, IceCandidateFilter.FilterUnsupportedIceCandidates(answer.Sdp));
            bool answerSent = await sendAnswerFunc(pending, trackerAnswer, cancellationToken).ConfigureAwait(false);
            if (!answerSent)
            {
                await RemovePendingAsync(offerId).ConfigureAwait(false);
                return;
            }

            lock (pending.SyncRoot)
            {
                pending.LocalCandidateSignalingReady = true;
            }

            FlushBufferedLocalCandidates(pending);
            await FlushBufferedRemoteCandidatesAsync(pending, cancellationToken).ConfigureAwait(false);
            
            bool connected = await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ConnectAsync returned {Connected} for inbound peer {PeerId}", connected, peerId);
            if (!connected)
            {
                await RemovePendingAsync(offerId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await RemovePendingAsync(offerId).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle offer from peer {PeerId}", peerId);
            await RemovePendingAsync(offerId).ConfigureAwait(false);
        }
    }

    public async Task HandleCandidateAsync(WebTorrentSignalMessage signal, CancellationToken cancellationToken)
    {
        if (!IceCandidateFilter.IsSupportedIceCandidate(signal.Candidate!))
        {
            _logger.LogDebug("Ignoring unsupported ICE candidate for offer {OfferId}: {Candidate}", FormatOfferIdForLog(signal.OfferId), signal.Candidate);
            return;
        }

        if (!_connections.TryGetValue(signal.OfferId!, out var pending))
        {
            BufferEarlyRemoteCandidate(signal.OfferId!, signal.Candidate!);
            return;
        }

        pending.ExpiresAt = _options.TimeProvider.GetUtcNow() + PendingPeerTimeout;

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
        _trackBackgroundTask(RunPendingTaskAsync(pending, ct => PumpLocalIceCandidatesAsync(pending, ct)));

        if (pending.Channel != null)
        {
            _trackBackgroundTask(RunPendingTaskAsync(pending, ct => WaitForLocalChannelOpenAsync(pending, ct)));
            return;
        }

        _trackBackgroundTask(RunPendingTaskAsync(pending, ct => PumpRemoteDataChannelsAsync(pending, ct)));
    }

    private async Task RunPendingTaskAsync(PendingPeer pending, Func<CancellationToken, Task> work)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, pending.LifetimeToken);
        var token = linkedCts.Token;
        await Task.Run(() => work(token), token).ConfigureAwait(false);
    }

    private async Task PumpLocalIceCandidatesAsync(PendingPeer pending, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var candidate in pending.Connection.IceCandidates.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                HandleLocalIceCandidate(pending, candidate);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the session shuts down or the pending peer is disposed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling local ICE candidate");
        }
    }

    private async Task WaitForLocalChannelOpenAsync(PendingPeer pending, CancellationToken cancellationToken)
    {
        try
        {
            await pending.Channel!.WaitUntilOpenAsync(cancellationToken).ConfigureAwait(false);
            _onChannelOpened(pending, pending.Channel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the session shuts down or the pending peer is disposed before the channel opened.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error attaching data channel for peer {PeerId}", pending.RemotePeerId);
        }
    }

    private async Task PumpRemoteDataChannelsAsync(PendingPeer pending, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var channel in pending.Connection.DataChannels.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await channel.WaitUntilOpenAsync(cancellationToken).ConfigureAwait(false);
                    _onChannelOpened(pending, channel);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Expected when the session shuts down or the pending peer is disposed.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error attaching remote data channel for peer {PeerId}", pending.RemotePeerId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the session shuts down or the pending peer is disposed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pumping remote data channels for peer {PeerId}", pending.RemotePeerId);
        }
    }

    private void HandleLocalIceCandidate(PendingPeer pending, WebRtcIceCandidateDescription candidate)
    {
        if (!IceCandidateFilter.IsSupportedIceCandidate(candidate.Candidate)) return;

        lock (pending.SyncRoot)
        {
            if (string.IsNullOrWhiteSpace(pending.RemotePeerId) || !pending.LocalCandidateSignalingReady)
            {
                pending.BufferedLocalCandidates.Add(candidate);
                return;
            }
        }

        _onLocalIceCandidate(pending, candidate);
    }

    private void FlushBufferedLocalCandidates(PendingPeer pending)
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

        foreach (var candidate in buffered)
        {
            _onLocalIceCandidate(pending, candidate);
        }
    }

    private void BufferEarlyRemoteCandidate(string offerId, string candidate)
    {
        if (!_earlyRemoteCandidates.ContainsKey(offerId) && _earlyRemoteCandidates.Count >= MaxPendingConnections) return;

        var candidates = _earlyRemoteCandidates.GetOrAdd(offerId, static _ => new ConcurrentQueue<string>());
        candidates.Enqueue(candidate);
        while (candidates.Count > 32 && candidates.TryDequeue(out _))
        {
            _logger.LogDebug("Dropped oldest buffered ICE candidate for offer {OfferId}", FormatOfferIdForLog(offerId));
        }
    }

    private async Task FlushBufferedRemoteCandidatesAsync(PendingPeer pending, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (_earlyRemoteCandidates.TryRemove(pending.OfferId, out var earlyCandidates))
        {
            while (earlyCandidates.TryDequeue(out var candidate)) candidates.Add(candidate);
        }

        lock (pending.SyncRoot)
        {
            if (pending.BufferedRemoteCandidates.Count > 0)
            {
                candidates.AddRange(pending.BufferedRemoteCandidates);
                pending.BufferedRemoteCandidates.Clear();
            }
        }

        foreach (var candidate in candidates.Where(IceCandidateFilter.IsSupportedIceCandidate))
        {
            await pending.Connection.AddRemoteIceCandidateAsync(new WebRtcIceCandidateDescription(candidate), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReplaceExistingPendingForInboundOfferAsync(string offerId)
    {
        if (_connections.TryRemove(offerId, out var existing))
        {
            await DisposePendingAsync(existing, "replacing pending peer").ConfigureAwait(false);
        }
    }

    public async Task RemovePendingAsync(string offerId)
    {
        _earlyRemoteCandidates.TryRemove(offerId, out _);
        if (_connections.TryRemove(offerId, out var removed))
        {
            await DisposePendingAsync(removed, "removing pending peer").ConfigureAwait(false);
        }
    }

    private async Task DisposePendingAsync(PendingPeer pending, string reason)
    {
        pending.CancelLifetime();
        try
        {
            await pending.Connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignored error while {Reason} for offer {OfferId}", reason, FormatOfferIdForLog(pending.OfferId));
        }
        finally
        {
            pending.DisposeLifetime();
        }
    }

    public async Task CleanupExpiredPendingPeersAsync()
    {
        var now = _options.TimeProvider.GetUtcNow();
        var expiredOfferIds = _connections
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (string offerId in expiredOfferIds)
        {
            await RemovePendingAsync(offerId).ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        foreach (var pending in _connections.Values)
        {
            await DisposePendingAsync(pending, "disposing pending peer").ConfigureAwait(false);
        }
        _connections.Clear();
        _earlyRemoteCandidates.Clear();
    }
}
