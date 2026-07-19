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

    public async Task CheckAsync(IEnumerable<PeerCommunication> peers, int connectedCount)
    {
        long now = Environment.TickCount64;
        var closeTasks = new List<Task>();
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

            int speed = isSeeding ? peer.UploadSpeed : peer.SmoothedDownloadSpeed;
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

        if (closeTasks.Count > 0)
        {
            await Task.WhenAll(closeTasks).ConfigureAwait(false);
        }
    }
}
