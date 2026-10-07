using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly HashSet<int> _distributedPieces = [];

    private readonly Lock _lock = new();
    private readonly ILogger<SuperSeedManager> _logger;

    // Track which pieces each peer has reported having
    private readonly ConcurrentDictionary<IPeerCommunication, HashSet<int>> _peerPieces = new();

    // Track which peer was given each piece (piece -> first peer we gave it to)
    private readonly ConcurrentDictionary<int, IPeerCommunication> _pieceOrigin = new();

    // Track how many times each piece has been seen from OTHER peers (piece -> count)
    private readonly int[] _pieceSightings;

    private readonly Torrent _torrent;

    public SuperSeedManager(Torrent torrent)
        : this(torrent, NullLogger<SuperSeedManager>.Instance)
    {
    }

    internal SuperSeedManager(Torrent torrent, ILogger<SuperSeedManager> logger)
    {
        _torrent = torrent;
        _logger = logger;
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

        // BEP 21: superseeding deliberately releases very few pieces at a time, so an assignment is a
        // scarce resource. Spending one on a peer that has told us it will not download anything wastes
        // the slot and stalls distribution behind a peer that will never pass the piece on.
        if (peer.RemoteIsUploadOnly)
        {
            _logger.LogDebug("SuperSeed: {RemoteEndPoint} is upload-only, so no piece was assigned", peer.RemoteEndPoint);
            return;
        }

        int pieceToGive;
        lock (_lock)
        {
            if (!_assignedPieces.TryGetValue(peer, out int current)) return;
            if (current >= 0) return;
            pieceToGive = SelectPieceForPeer(peer);
            if (pieceToGive < 0) return;
            _assignedPieces[peer] = pieceToGive;
            _pieceOrigin.TryAdd(pieceToGive, peer);
        }

        // Send HAVE for this piece
        var msg = new PeerMessage(MessageId.Have) { HavePieceIndex = pieceToGive };
        try { await peer.SendMessageAsync(msg).ConfigureAwait(false); }
        catch
        {
            lock (_lock)
            {
                if (_assignedPieces.TryGetValue(peer, out int assigned) && assigned == pieceToGive) _assignedPieces[peer] = -1;
                if (_pieceOrigin.TryGetValue(pieceToGive, out var origin) && ReferenceEquals(origin, peer)) _pieceOrigin.TryRemove(pieceToGive, out _);
            }
            throw;
        }

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

        var released = new HashSet<IPeerCommunication>();
        lock (_lock)
        {
            if (!_peerPieces.TryGetValue(peer, out var peerHas)) return;
            for (int i = 0; i < Math.Min(peerPieces.Count, _pieceSightings.Length); i++)
                if (peerPieces.HasPiece(i) && peerHas.Add(i))
                {
                    var original = RecordDistributionLocked(peer, i);
                    if (original != null) released.Add(original);
                }
        }
        _ = ReassignAfterBitfieldAsync(released).ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
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
        lock (_lock)
        {
            _peerPieces.TryAdd(peer, []);
            _assignedPieces.TryAdd(peer, -1);
        }

        // After handshake, give them their first piece
        // This is done asynchronously after HaveNone is sent
        return true;
    }

    /// <summary>
    /// Called when a peer disconnects to clean up tracking state.
    /// </summary>
    public void HandlePeerDisconnected(IPeerCommunication peer)
    {
        lock (_lock)
        {
            _assignedPieces.TryRemove(peer, out _);
            _peerPieces.TryRemove(peer, out _);
            foreach (var piece in _pieceOrigin.Where(pair => ReferenceEquals(pair.Value, peer)).Select(pair => pair.Key).ToArray())
                _pieceOrigin.TryRemove(piece, out _);
        }
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

        IPeerCommunication? original;
        lock (_lock)
        {
            if (!_peerPieces.TryGetValue(peer, out var peerHas) || !peerHas.Add(pieceIndex)) return;
            original = RecordDistributionLocked(peer, pieceIndex);
        }
        if (original != null) await AssignPieceToPeerAsync(original).ConfigureAwait(false);
    }

    private IPeerCommunication? RecordDistributionLocked(IPeerCommunication peer, int pieceIndex)
    {
        if (!_pieceOrigin.TryGetValue(pieceIndex, out var original) || ReferenceEquals(original, peer)) return null;
        _pieceSightings[pieceIndex]++;
        _distributedPieces.Add(pieceIndex);
        if (_assignedPieces.TryGetValue(original, out int assigned) && assigned == pieceIndex)
        {
            if (_peerPieces.TryGetValue(original, out var pieces)) pieces.Add(pieceIndex);
            _assignedPieces[original] = -1;
            return original;
        }
        return null;
    }

    private async Task ReassignAfterBitfieldAsync(IEnumerable<IPeerCommunication> peers)
    {
        try
        {
            foreach (var peer in peers) await AssignPieceToPeerAsync(peer).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SuperSeed: Failed to advertise the next piece"); }
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
                peerHas = [];
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

