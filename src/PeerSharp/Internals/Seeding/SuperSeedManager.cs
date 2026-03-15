using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Extensions;
using System.Collections.Concurrent;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Seeding;

/// <summary>
/// <para>BEP 16: Super-seeding (Initial Seeding) Manager</para>
/// <para>
/// Super-seeding is designed to reduce the amount of data a seed must upload
/// to get a torrent fully distributed throughout the swarm.
/// </para>
/// <para>
/// Instead of advertising all pieces, we:
/// 1. Appear as having no pieces (send HaveNone or empty bitfield)
/// 2. Give each peer a single piece HAVE message at a time
/// 3. Wait until that piece is seen from another peer before giving more
/// 4. Prioritize rarest pieces to maximize distribution
/// </para>
/// </summary>
internal class SuperSeedManager
{
    // Track which piece each peer is currently working on (peer -> piece index, or -1 if none)
    private readonly ConcurrentDictionary<IPeerCommunication, int> _assignedPieces = new();

    // Pieces that have been "distributed" (seen from at least one other peer)
    private readonly HashSet<int> _distributedPieces = new();

    private readonly Lock _lock = new();
    private readonly ILogger<SuperSeedManager> _logger = TorrentLoggerFactory.CreateLogger<SuperSeedManager>();

    // Track which pieces each peer has reported having
    private readonly ConcurrentDictionary<IPeerCommunication, HashSet<int>> _peerPieces = new();

    // Track which peer was given each piece (piece -> first peer we gave it to)
    private readonly ConcurrentDictionary<int, IPeerCommunication> _pieceOrigin = new();

    // Track how many times each piece has been seen from OTHER peers (piece -> count)
    private readonly int[] _pieceSightings;

    private readonly Torrent _torrent;

    public SuperSeedManager(Torrent torrent)
    {
        _torrent = torrent;
        _pieceSightings = new int[torrent.Pieces.Count];
    }

    public bool Enabled { get; set; }

    /// <summary>
    /// Give the peer a piece to download. Called after handshake and when
    /// their previous piece has been distributed.
    /// </summary>
    public async Task AssignPieceToPeerAsync(IPeerCommunication peer)
    {
        if (!Enabled)
        {
            return;
        }

        int pieceToGive = SelectPieceForPeer(peer);
        if (pieceToGive < 0)
        {
            _logger.LogDebug("SuperSeed: No suitable piece to give to {RemoteEndPoint}", peer.RemoteEndPoint);
            return;
        }

        lock (_lock)
        {
            _assignedPieces[peer] = pieceToGive;

            // Only track origin if this is the first peer to get this piece
            _pieceOrigin.TryAdd(pieceToGive, peer);
        }

        // Send HAVE for this piece
        var msg = new PeerMessage(MessageId.Have) { HavePieceIndex = pieceToGive };
        await peer.SendMessageAsync(msg).ConfigureAwait(false);

        _logger.LogDebug("SuperSeed: Assigned piece {PieceIndex} to {RemoteEndPoint}", pieceToGive, peer.RemoteEndPoint);
    }

    /// <summary>
    /// Get statistics about superseed progress.
    /// </summary>
    public (int TotalPieces, int DistributedPieces, int ActivePeers) GetStats()
    {
        lock (_lock)
        {
            return (_torrent.Pieces.Count, _distributedPieces.Count, _assignedPieces.Count);
        }
    }

    /// <summary>
    /// Called when we receive a bitfield from a peer.
    /// Track all pieces they have.
    /// </summary>
    public void HandlePeerBitfield(IPeerCommunication peer, PiecesProgress peerPieces)
    {
        if (!Enabled)
        {
            return;
        }

        if (!_peerPieces.TryGetValue(peer, out var peerHas))
        {
            peerHas = new HashSet<int>();
            _peerPieces[peer] = peerHas;
        }

        lock (_lock)
        {
            for (int i = 0; i < peerPieces.Count; i++)
            {
                if (peerPieces.HasPiece(i))
                {
                    peerHas.Add(i);

                    // Check if this piece was distributed
                    if (_pieceOrigin.TryGetValue(i, out var originalPeer) && originalPeer != peer)
                    {
                        _pieceSightings[i]++;
                        _distributedPieces.Add(i);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Called when a peer connects. In superseed mode, we don't send our full bitfield.
    /// Instead, we send HaveNone and then selectively send HAVE messages.
    /// </summary>
    /// <returns>True if superseed mode handled the bitfield, false to send normal bitfield</returns>
    public bool HandlePeerConnected(IPeerCommunication peer)
    {
        if (!Enabled)
        {
            return false;
        }

        // Track this peer
        _peerPieces.TryAdd(peer, new HashSet<int>());
        _assignedPieces.TryAdd(peer, -1);

        // After handshake, give them their first piece
        // This is done asynchronously after HaveNone is sent
        return true;
    }

    /// <summary>
    /// Called when a peer disconnects to clean up tracking state.
    /// </summary>
    public void HandlePeerDisconnected(IPeerCommunication peer)
    {
        _assignedPieces.TryRemove(peer, out _);
        _peerPieces.TryRemove(peer, out _);

        // Don't remove from _pieceOrigin - we still want to track piece distribution
    }

    /// <summary>
    /// Called when we receive a HAVE message from a peer.
    /// Track that this peer has this piece and check if we need to give
    /// the original peer a new piece.
    /// </summary>
    public async Task HandlePeerHaveAsync(IPeerCommunication peer, int pieceIndex)
    {
        if (!Enabled)
        {
            return;
        }

        if (pieceIndex < 0 || pieceIndex >= _torrent.Pieces.Count)
        {
            return;
        }

        // Track that this peer has this piece
        if (_peerPieces.TryGetValue(peer, out var peerHas))
        {
            lock (_lock)
            {
                peerHas.Add(pieceIndex);
            }
        }

        // Check if we gave this piece to a different peer
        if (_pieceOrigin.TryGetValue(pieceIndex, out var originalPeer) && originalPeer != peer)
        {
            // This piece has been distributed! Increment sighting count
            int newSightings;
            lock (_lock)
            {
                _pieceSightings[pieceIndex]++;
                newSightings = _pieceSightings[pieceIndex];
                _distributedPieces.Add(pieceIndex);
            }

            _logger.LogInformation("SuperSeed: Piece {PieceIndex} distributed! Seen by {RemoteEndPoint} (sightings: {Sightings})", pieceIndex, peer.RemoteEndPoint, newSightings);

            // Check if the original peer's assigned piece was this one
            if (_assignedPieces.TryGetValue(originalPeer, out var assignedPiece) && assignedPiece == pieceIndex)
            {
                // Give the original peer a new piece
                await AssignPieceToPeerAsync(originalPeer).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Check if a piece request should be allowed.
    /// In superseed mode, we only allow requests for pieces we've assigned to the peer.
    /// </summary>
    public bool ShouldAllowRequest(IPeerCommunication peer, int pieceIndex)
    {
        if (!Enabled)
        {
            return true;
        }

        // Allow request if this is the piece we assigned to them
        if (_assignedPieces.TryGetValue(peer, out var assignedPiece))
        {
            return assignedPiece == pieceIndex;
        }

        return false;
    }

    /// <summary>
    /// Select the best piece to give to a peer.
    /// Prioritizes:
    /// 1. Pieces that haven't been given to anyone yet
    /// 2. Pieces with the fewest sightings (rarest)
    /// </summary>
    private int SelectPieceForPeer(IPeerCommunication peer)
    {
        lock (_lock)
        {
            int bestPiece = -1;
            int lowestSightings = int.MaxValue;

            // Get pieces this peer already has
            if (!_peerPieces.TryGetValue(peer, out var peerHas))
            {
                peerHas = new HashSet<int>();
            }

            for (int i = 0; i < _torrent.Pieces.Count; i++)
            {
                // Skip pieces we don't have
                if (!_torrent.Pieces.HasPiece(i))
                {
                    continue;
                }

                // Skip pieces this peer already has
                if (peerHas.Contains(i))
                {
                    continue;
                }

                // Check if peer already got this piece from us
                if (_assignedPieces.TryGetValue(peer, out var currentPiece) && currentPiece == i)
                {
                    continue;
                }

                // Prefer pieces not yet given to anyone
                if (!_pieceOrigin.ContainsKey(i))
                {
                    // Give priority to completely undistributed pieces
                    return i;
                }

                // Otherwise, pick the piece with fewest sightings
                if (_pieceSightings[i] < lowestSightings)
                {
                    lowestSightings = _pieceSightings[i];
                    bestPiece = i;
                }
            }

            return bestPiece;
        }
    }
}

