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
    private readonly Lock _connectionsLock = new();
    private bool _disposed;
    private readonly ConcurrentDictionary<string, PendingPeer> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(TrackerRuntime Runtime, string OfferId, string PeerId), EarlyCandidates> _earlyRemoteCandidates = new();
    private sealed class EarlyCandidates(DateTimeOffset expiresAt)
    {
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public ConcurrentQueue<string> Candidates { get; } = new();
    }
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

    public int PendingConnectionCount => _connections.Values.Count(peer => !peer.IsAttached);
    public int EarlyCandidateOfferCount => _earlyRemoteCandidates.Count;
    public IEnumerable<PendingPeer> PendingPeers => _connections.Values.Where(peer => !peer.IsAttached);

    public async Task<(PendingPeer Peer, WebRtcSessionDescription Offer)> CreateOutgoingPendingPeerAsync(string offerId, TrackerRuntime runtime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PendingPeer pending;
        lock (_connectionsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (PendingConnectionCount >= MaxPendingConnections) throw new InvalidOperationException("Too many pending WebRTC peers.");
            if (_connections.ContainsKey(offerId)) throw new InvalidOperationException("Duplicate WebRTC offer ID.");
            var connection = _rtcFactory.Create();
            try
            {
                var channel = connection.CreateDataChannel(_options.DataChannelLabel);
                pending = new PendingPeer(offerId, connection, channel, true, runtime, _options.TimeProvider.GetUtcNow() + PendingPeerTimeout);
                _connections[offerId] = pending;
            }
            catch
            {
                _trackBackgroundTask(connection.DisposeAsync().AsTask());
                throw;
            }
        }
        try
        {
            ConfigurePendingPeer(pending);
            using var timeout = new CancellationTokenSource(PendingPeerTimeout, _options.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken, pending.LifetimeToken, timeout.Token);
            var offer = await AwaitNegotiationAsync(pending.Connection.CreateOfferAsync(linked.Token), linked.Token).ConfigureAwait(false);
            await AwaitNegotiationAsync(pending.Connection.SetLocalDescriptionAsync(offer, linked.Token), linked.Token).ConfigureAwait(false);
            return (pending, offer);
        }
        catch
        {
            await RemovePendingAsync(pending).ConfigureAwait(false);
            throw;
        }
    }

    public async Task HandleAnswerAsync(WebTorrentSignalMessage signal, TrackerRuntime runtime, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(signal.OfferId!, out var pending))
        {
            _logger.LogWarning("No pending connection found for offer_id {OfferId}", FormatOfferIdForLog(signal.OfferId));
            return;
        }

        if (!ReferenceEquals(pending.Runtime, runtime) || !pending.Initiator || pending.IsAttached) return;
        lock (pending.SyncRoot)
        {
            if (pending.AnswerReceived) return;
            pending.AnswerReceived = true;
        }
        using var timeout = new CancellationTokenSource(PendingPeerTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken, pending.LifetimeToken, timeout.Token);
        cancellationToken = linked.Token;

        if (!RemoteSdpReachability.IsLikelyReachable(signal.AnswerSdp!, _localSubnets))
        {
            _logger.LogInformation("Skipping unreachable peer {PeerId}: SDP advertises only private host candidates with no shared subnet", signal.PeerId);
            await RemovePendingAsync(pending).ConfigureAwait(false);
            return;
        }

        try
        {
            lock (pending.SyncRoot)
            {
                pending.RemotePeerId = signal.PeerId;
                pending.LocalCandidateSignalingReady = true;
            }

            var answer = new WebRtcSessionDescription(WebRtcSessionDescriptionType.Answer, IceCandidateFilter.FilterUnsupportedIceCandidates(signal.AnswerSdp!));
            await AwaitNegotiationAsync(pending.Connection.SetRemoteDescriptionAsync(answer, cancellationToken), cancellationToken).ConfigureAwait(false);
            lock (pending.SyncRoot) { pending.RemoteDescriptionSet = true; }

            FlushBufferedLocalCandidates(pending);
            await FlushBufferedRemoteCandidatesAsync(pending, cancellationToken).ConfigureAwait(false);

            bool connected = await AwaitNegotiationAsync(pending.Connection.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ConnectAsync returned {Connected} for peer {PeerId}", connected, signal.PeerId);
            if (!connected)
            {
                await RemovePendingAsync(pending).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await RemovePendingAsync(pending).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle answer from peer {PeerId}", signal.PeerId);
            await RemovePendingAsync(pending).ConfigureAwait(false);
        }
    }

    public async Task HandleOfferAsync(string offerId, string peerId, string offerSdp, TrackerRuntime runtime, Func<PendingPeer, WebRtcSessionDescription, CancellationToken, Task<bool>> sendAnswerFunc, CancellationToken cancellationToken)
    {
        if (!RemoteSdpReachability.IsLikelyReachable(offerSdp, _localSubnets))
        {
            _logger.LogInformation("Skipping unreachable peer {PeerId}: offer SDP advertises only private host candidates with no shared subnet", peerId);
            return;
        }

        PendingPeer pending;
        PendingPeer? replaced = null;
        lock (_connectionsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connections.TryGetValue(offerId, out var existing))
            {
                // A duplicate inbound offer cannot replace a live negotiation or stream.
                if (!existing.Initiator || existing.IsAttached || !ReferenceEquals(existing.Runtime, runtime)) return;
                replaced = existing;
            }
            if (replaced == null && PendingConnectionCount >= MaxPendingConnections) return;
            var connection = _rtcFactory.Create();
            pending = new PendingPeer(offerId, connection, null, false, runtime, _options.TimeProvider.GetUtcNow() + PendingPeerTimeout) { RemotePeerId = peerId };
            _connections[offerId] = pending;
        }
        if (replaced != null) await DisposePendingAsync(replaced, "replacing pending peer").ConfigureAwait(false);
        ConfigurePendingPeer(pending);
        using var timeout = new CancellationTokenSource(PendingPeerTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken, pending.LifetimeToken, timeout.Token);
        cancellationToken = linked.Token;
        var connectionForOffer = pending.Connection;
        try
        {
            var offer = new WebRtcSessionDescription(WebRtcSessionDescriptionType.Offer, IceCandidateFilter.FilterUnsupportedIceCandidates(offerSdp));
            await AwaitNegotiationAsync(connectionForOffer.SetRemoteDescriptionAsync(offer, cancellationToken), cancellationToken).ConfigureAwait(false);
            lock (pending.SyncRoot) { pending.RemoteDescriptionSet = true; }

            var answer = await AwaitNegotiationAsync(connectionForOffer.CreateAnswerAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            await AwaitNegotiationAsync(connectionForOffer.SetLocalDescriptionAsync(answer, cancellationToken), cancellationToken).ConfigureAwait(false);

            var trackerAnswer = new WebRtcSessionDescription(answer.Type, IceCandidateFilter.FilterUnsupportedIceCandidates(answer.Sdp));
            bool answerSent = await sendAnswerFunc(pending, trackerAnswer, cancellationToken).ConfigureAwait(false);
            if (!answerSent)
            {
                await RemovePendingAsync(pending).ConfigureAwait(false);
                return;
            }

            lock (pending.SyncRoot)
            {
                pending.LocalCandidateSignalingReady = true;
            }

            FlushBufferedLocalCandidates(pending);
            await FlushBufferedRemoteCandidatesAsync(pending, cancellationToken).ConfigureAwait(false);

            bool connected = await AwaitNegotiationAsync(connectionForOffer.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ConnectAsync returned {Connected} for inbound peer {PeerId}", connected, peerId);
            if (!connected)
            {
                await RemovePendingAsync(pending).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await RemovePendingAsync(pending).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle offer from peer {PeerId}", peerId);
            await RemovePendingAsync(pending).ConfigureAwait(false);
        }
    }

    public async Task HandleCandidateAsync(WebTorrentSignalMessage signal, TrackerRuntime runtime, CancellationToken cancellationToken)
    {
        if (!IceCandidateFilter.IsSupportedIceCandidate(signal.Candidate!))
        {
            _logger.LogDebug("Ignoring unsupported ICE candidate for offer {OfferId}: {Candidate}", FormatOfferIdForLog(signal.OfferId), signal.Candidate);
            return;
        }

        if (!_connections.TryGetValue(signal.OfferId!, out var pending))
        {
            BufferEarlyRemoteCandidate(runtime, signal.OfferId!, signal.PeerId, signal.Candidate!);
            return;
        }

        if (!ReferenceEquals(pending.Runtime, runtime)
            || (pending.RemotePeerId != null && pending.RemotePeerId != signal.PeerId)) return;

        lock (pending.SyncRoot)
        {
            if (!pending.RemoteDescriptionSet)
            {
                if (pending.BufferedRemoteCandidates.Count < 32) pending.BufferedRemoteCandidates.Add(signal.Candidate!);
                return;
            }
        }

        await pending.Connection.AddRemoteIceCandidateAsync(new WebRtcIceCandidateDescription(signal.Candidate!), cancellationToken).ConfigureAwait(false);
    }

    private static Task AwaitNegotiationAsync(Task task, CancellationToken ct)
    {
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    private static Task<T> AwaitNegotiationAsync<T>(Task<T> task, CancellationToken ct)
    {
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task.WaitAsync(ct);
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
        await work(token).ConfigureAwait(false);
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
            // S8969 is wrong here: removing the null-forgiving operator produces CS8602, so the
            // compiler demonstrably does not know this is non-null. Verified by doing it.
#pragma warning disable S8969
            await pending.Channel!.WaitUntilOpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore S8969
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
        if (!IceCandidateFilter.IsSupportedIceCandidate(candidate.Candidate))
        {
            return;
        }

        lock (pending.SyncRoot)
        {
            if (string.IsNullOrWhiteSpace(pending.RemotePeerId) || !pending.LocalCandidateSignalingReady)
            {
                if (pending.BufferedLocalCandidates.Count < 32) pending.BufferedLocalCandidates.Add(candidate);
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

            buffered = [.. pending.BufferedLocalCandidates];
            pending.BufferedLocalCandidates.Clear();
        }

        foreach (var candidate in buffered)
        {
            _onLocalIceCandidate(pending, candidate);
        }
    }

    private void BufferEarlyRemoteCandidate(TrackerRuntime runtime, string offerId, string peerId, string candidate)
    {
        lock (_connectionsLock)
        {
            if (_disposed) return;
            var key = (runtime, offerId, peerId);
            if (!_earlyRemoteCandidates.TryGetValue(key, out var buffered))
            {
                if (_earlyRemoteCandidates.Count >= MaxPendingConnections) return;
                _earlyRemoteCandidates[key] = buffered = new EarlyCandidates(_options.TimeProvider.GetUtcNow() + PendingPeerTimeout);
            }
            buffered.Candidates.Enqueue(candidate);
            while (buffered.Candidates.Count > 32) buffered.Candidates.TryDequeue(out _);
        }
    }

    private async Task FlushBufferedRemoteCandidatesAsync(PendingPeer pending, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (_earlyRemoteCandidates.TryRemove((pending.Runtime, pending.OfferId, pending.RemotePeerId!), out var earlyBuffer))
        {
            while (earlyBuffer.Candidates.TryDequeue(out var candidate))
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

        foreach (var candidate in candidates.Where(IceCandidateFilter.IsSupportedIceCandidate))
        {
            await pending.Connection.AddRemoteIceCandidateAsync(new WebRtcIceCandidateDescription(candidate), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RemovePendingAsync(string offerId)
    {
        PendingPeer? removed;
        lock (_connectionsLock)
        {
            foreach (var key in _earlyRemoteCandidates.Keys.Where(key => key.OfferId == offerId)) _earlyRemoteCandidates.TryRemove(key, out _);
            _connections.TryRemove(offerId, out removed);
        }
        if (removed != null) await DisposePendingAsync(removed, "removing pending peer").ConfigureAwait(false);
    }

    internal async Task RemovePendingAsync(PendingPeer pending)
    {
        lock (_connectionsLock)
        {
            if (!_connections.TryGetValue(pending.OfferId, out var current) || !ReferenceEquals(current, pending)) return;
            _connections.TryRemove(pending.OfferId, out _);
            foreach (var key in _earlyRemoteCandidates.Keys.Where(key => key.OfferId == pending.OfferId && ReferenceEquals(key.Runtime, pending.Runtime))) _earlyRemoteCandidates.TryRemove(key, out _);
        }
        await DisposePendingAsync(pending, "removing pending peer").ConfigureAwait(false);
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
            pending.Dispose();
        }
    }

    public async Task CleanupExpiredPendingPeersAsync()
    {
        var now = _options.TimeProvider.GetUtcNow();
        foreach (var (key, buffered) in _earlyRemoteCandidates)
            if (buffered.ExpiresAt <= now) _earlyRemoteCandidates.TryRemove(key, out _);
        var expiredOfferIds = _connections
            .Where(kvp => !kvp.Value.IsAttached && kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Value)
            .ToList();

        foreach (var pending in expiredOfferIds)
        {
            await RemovePendingAsync(pending).ConfigureAwait(false);
        }
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

    public async ValueTask DisposeAsync()
    {
        PendingPeer[] peers;
        lock (_connectionsLock)
        {
            if (_disposed) return;
            _disposed = true;
            peers = [.. _connections.Values];
            _connections.Clear();
            _earlyRemoteCandidates.Clear();
        }
        await Task.WhenAll(peers.Select(peer => DisposePendingAsync(peer, "disposing pending peer"))).ConfigureAwait(false);
    }
}
