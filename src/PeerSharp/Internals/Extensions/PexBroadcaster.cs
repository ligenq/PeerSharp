using System.Net;
using Microsoft.Extensions.Logging;

namespace PeerSharp.Internals.Extensions;

/// <summary>
/// Tells each peer which other peers we have gained and lost since we last told it.
///
/// <para>
/// We have always accepted ut_pex from others and acted on it while never sending any, which makes us
/// a peer that takes swarm knowledge and returns none. This is the other half.
/// </para>
///
/// <para>
/// Two rules matter more than the rest. Private torrents never get a message at all, because BEP 27
/// makes peer discovery outside the tracker the whole thing a private torrent is trying to prevent -
/// libtorrent does not even construct its PEX plugin for one. And the address we share is the peer's
/// <em>listen</em> endpoint, not the endpoint its connection came from: the latter's port is ephemeral,
/// so gossiping it would fill the swarm with addresses nobody can connect to. Peers that have not told
/// us where they listen are therefore left out entirely rather than guessed at.
/// </para>
/// </summary>
internal sealed class PexBroadcaster(
    Func<bool> isPrivate,
    Func<IEnumerable<IPexPeer>> getPeers,
    Func<TimeSpan> interval,
    ILogger logger)
{
    /// <summary>Caps the message size, as libtorrent's max_peer_entries does.</summary>
    private const int MaxEntriesPerMessage = 100;

    // What we last told each peer about and when, so each message is a diff rather than the whole
    // swarm. Held here rather than on the connection: it is this component's bookkeeping.
    private readonly Dictionary<IPexPeer, PeerPexState> _sent = [];

    public void Broadcast(DateTimeOffset now)
    {
        // BEP 27. Checked here as well as at construction so that a torrent which learns it is private
        // after metadata arrives cannot leak a swarm through a broadcaster built before that.
        if (isPrivate())
        {
            return;
        }

        var peers = getPeers().ToList();

        // Only peers that told us where they listen can be shared: see the note on the class.
        var connectable = new Dictionary<IPEndPoint, IPexPeer>();
        foreach (var peer in peers)
        {
            if (peer.RemoteListenEndPoint is { } listen)
            {
                connectable[listen] = peer;
            }
        }

        foreach (var peer in peers)
        {
            if (!peer.SupportsPex)
            {
                continue;
            }

            if (!_sent.TryGetValue(peer, out var state))
            {
                state = new PeerPexState();
                _sent[peer] = state;
            }

            if (state.LastSent is { } last && now - last < interval())
            {
                continue;
            }

            var known = state.Known;

            var added = new List<IPEndPoint>();
            var flags = new List<byte>();
            foreach (var (endpoint, other) in connectable)
            {
                // Never tell a peer about itself.
                if (ReferenceEquals(other, peer) || known.Contains(endpoint))
                {
                    continue;
                }

                added.Add(endpoint);
                flags.Add(other.PexFlags);

                if (added.Count >= MaxEntriesPerMessage)
                {
                    break;
                }
            }

            var dropped = known.Where(e => !connectable.ContainsKey(e)).Take(MaxEntriesPerMessage).ToList();

            if (added.Count == 0 && dropped.Count == 0)
            {
                continue;
            }

            peer.SendPex(added, flags, dropped);
            state.LastSent = now;

            foreach (var endpoint in added)
            {
                known.Add(endpoint);
            }

            foreach (var endpoint in dropped)
            {
                known.Remove(endpoint);
            }

            logger.LogDebug(
                "PEX to {PeerName}: {Added} added, {Dropped} dropped", peer.Name, added.Count, dropped.Count);
        }

        // Forget peers that have gone, so the diff state does not grow for the life of the torrent.
        foreach (var gone in _sent.Keys.Where(p => !peers.Contains(p)).ToList())
        {
            _sent.Remove(gone);
        }
    }

    private sealed class PeerPexState
    {
        public HashSet<IPEndPoint> Known { get; } = [];

        public DateTimeOffset? LastSent { get; set; }
    }
}
