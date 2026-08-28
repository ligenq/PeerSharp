using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PeerSharp.Internals.Peers;

/// <summary>Applies idle and sustained-low-throughput disconnect policy to connected peers.</summary>
internal sealed class PeerHealthMonitor
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<PeerCommunication, long> _slowPeers = new();
    private readonly Torrent _torrent;

    public PeerHealthMonitor(Torrent torrent, ILogger logger)
    {
        _torrent = torrent;
        _logger = logger;
    }

    public void Remove(PeerCommunication peer) => _slowPeers.TryRemove(peer, out _);

    internal int SlowPeerCountForTesting => _slowPeers.Count;

    internal void MarkSlowForTesting(PeerCommunication peer, long startedAt) => _slowPeers[peer] = startedAt;

    private RedundantConnectionPolicy.Verdict Judge(PeerCommunication peer) =>
        RedundantConnectionPolicy.Judge(
            weHaveMetadata: _torrent.HasMetadata,
            peerHasMetadata: peer.HasReportedPieces,
            peerIsUploadOnly: peer.RemoteHasEverything,

            // Both, because they can disagree. SelectionFinished counts through a cached
            // selected-piece total that a recheck does not refresh, so a torrent holding every piece
            // can report its selection unfinished; Finished is computed from the piece map each time
            // and cannot go stale. Either one being true means we will not be downloading.
            weAreUploadOnly: _torrent.Finished || _torrent.SelectionFinished,
            weAreInterested: peer.AmInterested);

    private Task CloseRedundantAsync(PeerCommunication peer)
    {
        _logger.LogDebug("Closing redundant connection to {PeerName} ({Verdict})", peer.Name, Judge(peer));
        Remove(peer);
        return peer.CloseAsync();
    }

    public async Task CheckAsync(IEnumerable<PeerCommunication> peers, int connectedCount)
    {
        long now = Environment.TickCount64;
        var closeTasks = new List<Task>();
        var keepAliveTasks = new List<Task>();
        var settings = _torrent.Settings.Connection;
        bool isSeeding = _torrent.Finished;

        foreach (var peer in peers)
        {
            if (now - peer.LastActivityTicks > ProtocolConstants.IdleTimeoutMs)
            {
                _logger.LogDebug("Connection timed out for {PeerName} (Idle > {IdleTimeout}ms)", peer.Name, ProtocolConstants.IdleTimeoutMs);
                closeTasks.Add(peer.CloseAsync());
                Remove(peer);
                continue;
            }

            // A connection neither side can use is closed now rather than left for the idle timeout
            // two minutes from now. Both ends holding everything is the common case at the end of a
            // transfer, and the slot it wastes is one a leecher could be using.
            if (Judge(peer) != RedundantConnectionPolicy.Verdict.Keep)
            {
                closeTasks.Add(CloseRedundantAsync(peer));
                continue;
            }

            // BEP 3 keepalive. A connection with nothing to say - a seed serving a peer that is not
            // interested, say - would otherwise go silent and be dropped by the remote's own timeout.
            if (now - peer.LastSentTicks > ProtocolConstants.KeepAliveIntervalMs)
            {
                keepAliveTasks.Add(peer.SendKeepAliveAsync());
            }

            if (connectedCount < settings.SlowPeerMinConnectedPeers)
            {
                Remove(peer);
                continue;
            }

            int threshold = isSeeding ? settings.SlowPeerMinUploadSpeedBytesPerSec : settings.SlowPeerMinDownloadSpeedBytesPerSec;
            bool activeTransfer = isSeeding ? peer.PeerInterested && !peer.AmChoking : peer.AmInterested && !peer.PeerChoking;
            if (threshold <= 0 || !activeTransfer)
            {
                Remove(peer);
                continue;
            }

            long speed = isSeeding ? peer.UploadSpeed : peer.SmoothedDownloadSpeed;
            if (speed >= threshold)
            {
                Remove(peer);
                continue;
            }

            long start = _slowPeers.GetOrAdd(peer, _ => now);
            long elapsedMs = now - start;
            if (elapsedMs >= Math.Max(1, settings.SlowPeerGraceSeconds) * 1000L)
            {
                _logger.LogDebug("Disconnecting slow peer {PeerName} (speed={Speed}B/s < {Threshold}B/s for {Elapsed}ms)", peer.Name, speed, threshold, elapsedMs);
                closeTasks.Add(peer.CloseAsync());
                Remove(peer);
            }
        }

        if (keepAliveTasks.Count > 0)
        {
            await Task.WhenAll(keepAliveTasks).ConfigureAwait(false);
        }

        if (closeTasks.Count > 0)
        {
            await Task.WhenAll(closeTasks).ConfigureAwait(false);
        }
    }
}
