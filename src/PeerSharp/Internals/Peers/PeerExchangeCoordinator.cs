using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Extensions;

namespace PeerSharp.Internals.Peers;

/// <summary>Builds and broadcasts BEP 11 peer-exchange updates.</summary>
internal sealed class PeerExchangeCoordinator
{
    private static readonly Random Random = new();
    private readonly ConcurrentDictionary<IPEndPoint, PeerHistory> _knownPeers;
    private readonly ILogger _logger;
    private readonly Torrent _torrent;

    public PeerExchangeCoordinator(Torrent torrent, ConcurrentDictionary<IPEndPoint, PeerHistory> knownPeers, ILogger logger)
    {
        _torrent = torrent;
        _knownPeers = knownPeers;
        _logger = logger;
    }

    public static void ApplyFlags(PeerHistory history, byte flags)
    {
        if ((flags & (byte)UtPex.Peer.Seed) != 0)
        {
            history.IsSeed = true;
        }

        if ((flags & (byte)UtPex.Peer.Utp) != 0)
        {
            history.UtpSupported = true;
            history.UtpHinted = true;
        }
    }

    public void Broadcast(IEnumerable<PeerCommunication> connectedPeers)
    {
        if (_torrent.InfoFile.Info.IsPrivate)
        {
            return;
        }

        var peerData = new List<(IPEndPoint, byte)>();
        var connectedEndpoints = new HashSet<IPEndPoint>();
        var peers = connectedPeers.ToList();
        foreach (var peer in peers)
        {
            // BEP 10 'p'. The endpoint a connection arrived on carries an ephemeral source port, so
            // gossiping it would put addresses in the swarm that nobody can connect to - and every
            // recipient would then waste attempts on them. Peers that have not told us where they
            // listen are left out rather than guessed at.
            var shareable = peer.RemoteListenEndPoint;
            if (shareable == null)
            {
                if (peer.RemoteEndPoint != null)
                {
                    connectedEndpoints.Add(peer.RemoteEndPoint);
                }

                continue;
            }

            connectedEndpoints.Add(shareable);
            if (peer.RemoteEndPoint != null)
            {
                // The peer manager may also know this connection by its ephemeral source endpoint.
                // Exclude that alias from known-peer candidates or recipients will waste connection
                // attempts on it alongside the valid listening endpoint.
                connectedEndpoints.Add(peer.RemoteEndPoint);
            }
            byte flags = 0;
            if (peer.PeerPieces != null && peer.PeerPieces.ReceivedCount == peer.PeerPieces.Count) flags |= (byte)UtPex.Peer.Seed;
            if (peer.UtpStream != null) flags |= (byte)UtPex.Peer.Utp;
            if (peer.Stream is EncryptedStream) flags |= (byte)UtPex.Peer.Encryption;
            if (peer.RemoteExtensions?.MessageIds.ContainsKey(UtHolepunch.Name) == true) flags |= (byte)UtPex.Peer.Holepunch;
            peerData.Add((shareable, flags));
        }

        var knownCandidates = new List<IPEndPoint>();
        foreach (var (endpoint, history) in _knownPeers)
        {
            // Only addresses a peer actually listens on. The cache is keyed by whatever endpoint each
            // connection used, so every peer that dialled us left an entry under its ephemeral source
            // port - unconnectable, and previously gossiped like any other. That closes a loop: the
            // recipient records it, ranks it, dials it, and fails, having learnt it from us.
            if (history.IsListenAddress && !connectedEndpoints.Contains(endpoint))
            {
                knownCandidates.Add(endpoint);
            }
        }

        int takeCount = Math.Min(50, knownCandidates.Count);
        for (int i = 0; i < takeCount; i++)
        {
            int j = i + Random.Next(knownCandidates.Count - i);
            (knownCandidates[i], knownCandidates[j]) = (knownCandidates[j], knownCandidates[i]);
        }

        var allPeers = new List<(IPEndPoint, byte)>(peerData.Count + takeCount);
        allPeers.AddRange(peerData);
        for (int i = 0; i < takeCount; i++) allPeers.Add((knownCandidates[i], 0));

        var filteredPeers = new List<(IPEndPoint, byte)>(allPeers.Count);
        foreach (var peer in peers)
        {
            try
            {
                filteredPeers.Clear();
                foreach (var candidate in allPeers)
                {
                    // Never tell a peer about itself, under either of the addresses we might know it by.
                    if (candidate.Item1.Equals(peer.RemoteListenEndPoint)
                        || candidate.Item1.Equals(peer.RemoteEndPoint))
                    {
                        continue;
                    }

                    filteredPeers.Add(candidate);
                }
                peer.UtPex.Update(filteredPeers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BroadcastPex error for {RemoteEndPoint}", peer.RemoteEndPoint);
            }
        }
    }
}
