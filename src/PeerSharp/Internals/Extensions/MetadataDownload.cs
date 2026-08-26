using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using System.Collections;

using PeerSharp.Exceptions;

namespace PeerSharp.Internals.Extensions;

internal class MetadataDownload : IMetadataDownload, IDisposable
{
    private static readonly TimeSpan StallWarningAfter = TimeSpan.FromSeconds(30);
    private const int StallWarningMinimumRequests = 6;
    private readonly List<IPeerCommunication> _activePeers = [];
    private readonly Lock _lock = new();
    private readonly ILogger<MetadataDownload> _logger;
    private readonly ILoggerFactory _loggerFactory;
    // One outstanding request per peer. Piece pressure is derived from these values so every peer can
    // independently choose the least-requested missing piece while per-piece redundancy stays bounded.
    private readonly Dictionary<(IPeerCommunication Peer, int Piece), PendingMetadataRequest> _pendingRequests = [];
    private readonly Dictionary<(IPeerCommunication Peer, int Piece), int> _requestAttempts = [];

    /// <summary>
    /// Pairs a peer has explicitly refused, or has been disqualified from by supplying metadata that
    /// failed its info hash.
    ///
    /// <para>
    /// Kept apart from <see cref="_requestAttempts"/> because the two mean different things. A timeout
    /// is ambiguous - a slow link produces one just as readily as an unwilling peer - so its budget is
    /// allowed to be restored when the alternative is having nobody left to ask. A reject is an answer,
    /// and re-asking a peer that already said no is how a reject storm starts.
    /// </para>
    /// </summary>
    private readonly HashSet<(IPeerCommunication Peer, int Piece)> _refusedRequests = [];

    /// <summary>
    /// Which peer supplied each piece currently held in the buffer.
    ///
    /// <para>
    /// Only the first correctly sized arrival for a piece is stored, so this is the peer whose bytes
    /// are actually in the assembled metadata. That distinction is the whole point: requests are
    /// deliberately redundant, so several peers answer for the same piece and every one of them looks
    /// like a contributor. Blaming all of them when the completed set fails its info hash convicts the
    /// honest majority along with the one peer whose bytes were used.
    /// </para>
    /// </summary>
    private readonly Dictionary<int, IPeerCommunication> _pieceSuppliers = [];

    /// <summary>
    /// How many timed-out requests a peer is given before it stops being a first choice. Requests which
    /// are merely in flight do not count: metadata requests are deliberately sent in bursts, so
    /// counting sends would demote a peer before it had any opportunity to reply.
    /// </summary>
    private const int UnansweredRequestsBeforeDemotion = 3;

    /// <summary>What a peer has done with the metadata requests sent to it.</summary>
    private sealed class MetadataPeerRecord
    {
        public int Answered { get; set; }

        public int TimedOut { get; set; }
    }

    private readonly Dictionary<IPeerCommunication, MetadataPeerRecord> _peerRecords = [];

    /// <summary>Test hook: number of in-flight metadata piece requests.</summary>
    internal int PendingRequestCountForTesting
    {
        get
        {
            lock (_lock)
            {
                return _pendingRequests.Count;
            }
        }
    }
    private readonly Torrent _torrent;
    private AtomicDisposal _disposal = new();

    private float _lastReportedProgress = -1f;
    private DateTimeOffset? _firstRequestAt;
    private long _requestsSent;
    private long _responsesReceived;
    private bool _stallReported;
    private byte[] _metadataBuffer = [];
    private int _metadataSize = 0;
    private int MetadataRequestTimeoutSeconds => Math.Max(1, _torrent.Settings.Transfer.MetadataRequestTimeoutSeconds);
    private int MetadataRequestPipeline => Math.Clamp(_torrent.Settings.Transfer.MetadataRequestPipeline, 1, 32);
    private int MetadataRequestRedundancy => Math.Clamp(_torrent.Settings.Transfer.MetadataRequestRedundancy, 1, 16);
    private int MetadataMaxRequestAttempts => Math.Max(1, _torrent.Settings.Transfer.MetadataMaxRequestAttempts);
    private int MaxMetadataSizeBytes => Math.Max(1, _torrent.Settings.Transfer.MaxMetadataSizeBytes);

    // Initialized to empty BitArray to avoid null reference; resized in InitializeMetadataBuffer
    private BitArray _receivedPieces = new(0);
    private int _nextPieceCursor = 0;

    public MetadataDownload(Torrent torrent)
        : this(torrent, NullLoggerFactory.Instance)
    {
    }

    internal MetadataDownload(Torrent torrent, ILoggerFactory loggerFactory)
    {
        _torrent = torrent;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MetadataDownload>();
    }

    public bool Active { get; private set; }
    public bool Finished { get; private set; }

    /// <summary>
    /// Gets the progress of metadata download (0.0 to 1.0).
    /// </summary>
    public float Progress
    {
        get
        {
            if (Finished)
            {
                return 1.0f;
            }

            lock (_lock)
            {
                if (_receivedPieces.Count == 0)
                {
                    return 0.0f;
                }

                int received = 0;
                for (int i = 0; i < _receivedPieces.Count; i++)
                {
                    if (_receivedPieces[i])
                    {
                        received++;
                    }
                }

                return (float)received / _receivedPieces.Count;
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void InitializeMetadataBuffer(int size)
    {
        lock (_lock)
        {
            if (size <= 0 || size > MaxMetadataSizeBytes)
            {
                throw new InvalidDataException($"Invalid metadata size {size}. Maximum allowed is {MaxMetadataSizeBytes} bytes.");
            }

            if (_metadataSize != 0)
            {
                if (_metadataSize != size)
                {
                    throw new InvalidDataException($"Metadata size changed from {_metadataSize} to {size}.");
                }

                return; // Already initialized
            }

            _metadataSize = size;
            _metadataBuffer = new byte[size];
            _receivedPieces = new BitArray((size + UtMetadata.PieceSize - 1) / UtMetadata.PieceSize, false);
            _logger.LogInformation("Initialized metadata buffer for size: {Size}", size);

            ReleaseSpeculativeRequests();

            if (Active && _activePeers.Count > 0)
            {
                FillPeerRequests();
            }
        }
    }

    public async Task MetadataPieceReceivedAsync(IPeerCommunication peer, int pieceIndex, byte[] data)
    {
        bool finished = false;
        lock (_lock)
        {
            // Check _receivedPieces.Length > 0 to handle uninitialized state (empty BitArray)
            if (!Active || Finished || _metadataSize == 0 || _receivedPieces.Length == 0 ||
                pieceIndex < 0 || pieceIndex >= _receivedPieces.Length)
            {
                return;
            }

            int offset = pieceIndex * UtMetadata.PieceSize;
            int expectedLength = Math.Min(UtMetadata.PieceSize, _metadataBuffer.Length - offset);
            if (data.Length != expectedLength)
            {
                _logger.LogWarning(
                    "Ignoring metadata piece {PieceIndex} from {PeerId}: expected {ExpectedSize} bytes, received {ActualSize}",
                    pieceIndex,
                    peer.PeerId,
                    expectedLength,
                    data.Length);
                return;
            }

            // A correctly sized duplicate still proves that this peer serves metadata. Validate it
            // first, though: malformed or out-of-range messages must not promote a peer or cancel the
            // valid request which is still in flight.
            RecordFor(peer).Answered++;
            _responsesReceived++;
            ReleaseRequestsForReceivedPiece(pieceIndex);
            if (peer is PeerCommunication communication)
            {
                communication.MarkUsefulDataExchanged();
            }

            if (_receivedPieces[pieceIndex])
            {
                FillPeerRequests(peer);
                return;
            }

            Array.Copy(data, 0, _metadataBuffer, offset, data.Length);
            _receivedPieces[pieceIndex] = true;
            _pieceSuppliers[pieceIndex] = peer;
            _logger.LogInformation("Received metadata piece {PieceIndex} from {PeerId} (size={Size})", pieceIndex, peer.PeerId, data.Length);

            // Fire progress event
            FireProgressEvent();

            if (_receivedPieces.Cast<bool>().All(b => b)) // All pieces received
            {
                // Reconstruct TorrentFileMetadata from raw info dictionary bytes
                TorrentFileMetadata newMetadata;
                try
                {
                    newMetadata = TorrentFileParser.ParseInfoBytes(_metadataBuffer, _loggerFactory);
                }
                catch (TorrentMetadataException ex)
                {
                    _logger.LogWarning(ex, "Downloaded metadata is not a valid info dictionary. Discarding metadata.");
                    RejectCompletedMetadata();
                    return;
                }

                // SECURITY: Verify the downloaded metadata hash matches the requested hash.
                // Require at least one expected hash to be present: with no known hash we
                // cannot authenticate attacker-supplied metadata, so it must be rejected
                // rather than accepted unverified.
                bool haveExpectedHash = !_torrent.InfoFile.Info.Hash.IsEmpty || !_torrent.InfoFile.Info.HashV2.IsEmpty;
                bool hashMatches = haveExpectedHash;
                if (!_torrent.InfoFile.Info.Hash.IsEmpty && !newMetadata.Info.Hash.Equals(_torrent.InfoFile.Info.Hash))
                {
                    hashMatches = false;
                }
                if (!_torrent.InfoFile.Info.HashV2.IsEmpty && !newMetadata.Info.HashV2.Equals(_torrent.InfoFile.Info.HashV2))
                {
                    hashMatches = false;
                }

                if (!hashMatches)
                {
                    _logger.LogWarning("Downloaded metadata hash does not match expected hash. Discarding metadata.");
                    RejectCompletedMetadata();
                    return;
                }

                Finished = true;
                Active = false;
                _pendingRequests.Clear();
                _requestAttempts.Clear();
                _refusedRequests.Clear();
                _pieceSuppliers.Clear();

                if (string.IsNullOrEmpty(newMetadata.Announce))
                {
                    newMetadata.Announce = _torrent.InfoFile.Announce;
                }
                if (newMetadata.AnnounceList.Count == 0 && _torrent.InfoFile.AnnounceList.Count > 0)
                {
                    newMetadata.AnnounceList.AddRange(_torrent.InfoFile.AnnounceList);
                }
                if (newMetadata.WebSeedUrls.Count == 0 && _torrent.InfoFile.WebSeedUrls.Count > 0)
                {
                    newMetadata.WebSeedUrls.AddRange(_torrent.InfoFile.WebSeedUrls);
                }
                if (newMetadata.AnnounceTiers.Count == 0 && newMetadata.AnnounceList.Count > 0)
                {
                    newMetadata.AnnounceTiers.Add([.. newMetadata.AnnounceList]);
                }

                // Update Torrent's InfoFile
                _torrent.InfoFile.Info = newMetadata.Info;
                _torrent.InfoFile.InfoBytes = _metadataBuffer; // Store raw bytes
                _torrent.InfoFile.Announce = newMetadata.Announce;
                _torrent.InfoFile.AnnounceList = newMetadata.AnnounceList;
                _torrent.InfoFile.AnnounceTiers = newMetadata.AnnounceTiers;

                finished = true;
            }
            else
            {
                FillPeerRequests(peer);
            }
        }

        if (finished)
        {
            // Fire metadata received events
            FireMetadataReceivedEvent();

            // Re-initialize Torrent (Pieces, FileTransfer, TrackerManager)
            await _torrent.ReinitializeAfterMetadataAsync().ConfigureAwait(false);

            _logger.LogInformation("Metadata download finished for {TorrentName}", _torrent.Name);
        }
    }

    public void MetadataRejectReceived(IPeerCommunication peer, int pieceIndex)
    {
        lock (_lock)
        {
            if (_pendingRequests.Remove((peer, pieceIndex)))
            {
                RecordFor(peer).TimedOut++;
                _refusedRequests.Add((peer, pieceIndex));
                _logger.LogWarning("Peer {PeerId} rejected metadata piece {PieceIndex}", peer.PeerId, pieceIndex);
                FillPeerRequests(peer);
            }
        }
    }

    public void MetadataRequestReceived(IPeerCommunication peer, int pieceIndex)
    {
        lock (_lock)
        {
            if (!Finished || _metadataBuffer.Length == 0)
            {
                peer.UtMetadata.SendReject(pieceIndex);
                return;
            }

            if (pieceIndex < 0)
            {
                peer.UtMetadata.SendReject(pieceIndex);
                return;
            }

            int offset = pieceIndex * UtMetadata.PieceSize;
            if (offset >= _metadataBuffer.Length)
            {
                peer.UtMetadata.SendReject(pieceIndex);
                return;
            }

            int length = Math.Min(UtMetadata.PieceSize, _metadataBuffer.Length - offset);
            byte[] data = new byte[length];
            Array.Copy(_metadataBuffer, offset, data, 0, length);

            peer.UtMetadata.SendData(pieceIndex, data, _metadataBuffer.Length);
            if (peer is PeerCommunication communication)
            {
                communication.MarkUsefulDataExchanged();
            }
        }
    }

    public void PeerConnected(IPeerCommunication peer)
    {
        lock (_lock)
        {
            if (Finished)
            {
                return; // If finished, we don't need to track peers for downloading
            }

            if (!Active)
            {
                return;
            }

            if (peer.RemoteSupportsExtensions &&
                peer.RemoteExtensions is { } remoteExtensions &&
                remoteExtensions.GetEnabledMessageId(UtMetadata.Name).HasValue &&
                peer.UtMetadata.RemoteMessageId.HasValue)
            {
                if (!_activePeers.Contains(peer))
                {
                    _activePeers.Add(peer);
                }
                _logger.LogInformation("Metadata peer connected {PeerId} (id={MessageId}, size={MetadataSize})", peer.PeerId, peer.UtMetadata.RemoteMessageId, remoteExtensions.MetadataSize);
                _logger.LogDebug("Peer {PeerId} supports ut_metadata. Adding to active list.", peer.PeerId);

                // Request metadata_size if peer sent it
                if (remoteExtensions.MetadataSize.HasValue)
                {
                    if (_metadataSize == 0)
                    {
                        InitializeMetadataBuffer(remoteExtensions.MetadataSize.Value);
                    }

                    if (Active && !Finished)
                    {
                        FillPeerRequests(peer);
                    }
                }
                else if (Active && !Finished && _metadataSize == 0)
                {
                    // Some peers omit metadata_size in the extended handshake; probe piece 0.
                    TryAssignPeer(peer);
                }
                else if (Active && !Finished && _metadataSize > 0)
                {
                    FillPeerRequests(peer);
                }
            }
        }
    }

    public void PeerDisconnected(IPeerCommunication peer)
    {
        lock (_lock)
        {
            _activePeers.Remove(peer);
            _peerRecords.Remove(peer);
            foreach (var key in _pendingRequests.Keys.Where(key => key.Peer == peer).ToList())
            {
                _pendingRequests.Remove(key);
            }
            foreach (var key in _requestAttempts.Keys.Where(key => key.Peer == peer).ToList())
            {
                _requestAttempts.Remove(key);
            }
            _refusedRequests.RemoveWhere(key => key.Peer == peer);
            FillPeerRequests();
        }
    }

    public void SetMetadata(byte[] data)
    {
        lock (_lock)
        {
            _metadataBuffer = data;
            _metadataSize = data.Length;
            Finished = true;
            Active = false;
            _pendingRequests.Clear();
            _requestAttempts.Clear();
            _refusedRequests.Clear();
            _pieceSuppliers.Clear();
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (Finished)
            {
                return;
            }

            _pendingRequests.Clear();
            _requestAttempts.Clear();
            _refusedRequests.Clear();
            _pieceSuppliers.Clear();
            _firstRequestAt = null;
            _requestsSent = 0;
            _responsesReceived = 0;
            _stallReported = false;
            Active = true;
        }

        // Start looking for peers
        // For now, rely on active peers connecting to us or we connect to them
        // And they announce ut_metadata support in extended handshake.
    }

    public void Stop()
    {
        lock (_lock)
        {
            Active = false;
            _pendingRequests.Clear();
            _requestAttempts.Clear();
            _refusedRequests.Clear();
            _pieceSuppliers.Clear();
        }
    }

    public void Update()
    {
        lock (_lock)
        {
            if (!Active || Finished)
            {
                return;
            }

            if (_metadataSize == 0)
            {
                FillPeerRequests();
                return;
            }

            var now = _torrent.Services.TimeProvider.GetUtcNow();
            var timedOut = new List<(IPeerCommunication Peer, int Piece)>();
            foreach (var kvp in _pendingRequests)
            {
                if ((now - kvp.Value.Timestamp).TotalSeconds > MetadataRequestTimeoutSeconds)
                {
                    timedOut.Add(kvp.Key);
                }
            }

            foreach (var key in timedOut)
            {
                if (_pendingRequests.Remove(key, out _))
                {
                    RecordFor(key.Peer).TimedOut++;
                }
            }

            ReportStalledMetadataDownload(now);
            FillPeerRequests();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            Stop();
        }
    }

    private void FireMetadataReceivedEvent()
    {
        // Fire callback
        _torrent.Events?.MetadataReceived?.Invoke(_torrent);

        // Fire alert
        _torrent.Alerts.MetadataAlert(AlertId.MetadataInitialized, _torrent);

        // Fire state change
        _torrent.FireStateChangedEvent(TorrentState.Active);
    }

    private void FireProgressEvent()
    {
        float currentProgress = Progress;

        // Only fire if progress changed by at least 5%
        if (_lastReportedProgress >= 0 && (currentProgress - _lastReportedProgress) < 0.05f)
        {
            return;
        }

        _lastReportedProgress = currentProgress;

        int received = 0;
        int total = _receivedPieces.Count;
        for (int i = 0; i < total; i++)
        {
            if (_receivedPieces[i])
            {
                received++;
            }
        }

        var progressInfo = new MetadataProgress
        {
            Progress = currentProgress,
            ReceivedPieces = received,
            TotalPieces = total
        };

        // Fire callback
        _torrent.Events?.MetadataProgress?.Invoke(_torrent, progressInfo);

        // Fire alert
        _torrent.Alerts.MetadataProgressAlert(_torrent, currentProgress, received, total);
    }

    /// <summary>
    /// Gives every eligible peer one independently owned request. Each peer chooses a least-requested
    /// missing piece; the global pipeline limits distinct pieces, while redundancy limits simultaneous
    /// owners of the same piece. This makes timeout, reject and disconnect recovery peer-local instead
    /// of allowing one piece-owned timer to hide several untracked requests.
    /// </summary>
    private void FillPeerRequests(IPeerCommunication? first = null)
    {
        if (!Active || Finished)
        {
            return;
        }

        AssignEligiblePeers(first);

        // The attempt budget has only one direction: nothing decrements it and nothing expires it, so
        // it is spent for good. That is what stops one peer being asked the same thing forever, but it
        // also means enough timeouts can spend every (peer, piece) pair the swarm has - and the
        // download is then indistinguishable from one with no peers at all, except that the peers are
        // still connected and still advertising the metadata. The default timeout is one second, which
        // a peer on a slow link misses routinely, so this is reached by latency rather than malice.
        //
        // Nothing outstanding, work still to do and peers still here is the one state where that has
        // happened. Clearing the ledger there keeps the budget doing its job as rotation pressure
        // without letting it become the end of the download.
        if (_pendingRequests.Count > 0 || _requestAttempts.Count == 0 ||
            _activePeers.Count == 0 || !HasMissingPieces())
        {
            return;
        }

        _logger.LogInformation(
            "Every metadata request budget is spent with pieces still missing and {PeerCount} peer(s) connected; restarting exploration",
            _activePeers.Count);
        _requestAttempts.Clear();
        AssignEligiblePeers(first);
    }

    private void AssignEligiblePeers(IPeerCommunication? first)
    {
        bool haveDeclaredHolder = _activePeers.Any(PeerCanDeclareMetadata);
        bool assigned;
        do
        {
            assigned = false;
            if (first != null && (!haveDeclaredHolder || PeerCanDeclareMetadata(first)))
            {
                assigned |= TryAssignPeer(first);
            }

            foreach (IPeerCommunication peer in _activePeers)
            {
                if ((!haveDeclaredHolder || PeerCanDeclareMetadata(peer)) && !ReferenceEquals(peer, first))
                {
                    assigned |= TryAssignPeer(peer);
                }
            }
        }
        while (assigned);
    }

    /// <summary>
    /// Whether anything is still worth asking for. Before the size is known that is the probe for
    /// piece 0, which is the only request that can be made at all.
    /// </summary>
    private bool HasMissingPieces()
    {
        if (_metadataSize == 0)
        {
            return true;
        }

        for (int piece = 0; piece < _receivedPieces.Length; piece++)
        {
            if (!_receivedPieces[piece])
            {
                return true;
            }
        }

        return false;
    }

    internal IReadOnlyList<(IPeerCommunication Peer, int Piece, DateTimeOffset Timestamp, int Attempts)> GetPendingRequestsForTesting()
    {
        lock (_lock)
        {
            return _pendingRequests
                .Select(entry => (entry.Key.Peer, entry.Key.Piece, entry.Value.Timestamp, entry.Value.Attempts))
                .ToList();
        }
    }

    internal void SetPendingRequestForTesting(
        IPeerCommunication peer,
        int piece,
        DateTimeOffset timestamp,
        int attempts)
    {
        lock (_lock)
        {
            _pendingRequests[(peer, piece)] = new PendingMetadataRequest(timestamp, attempts);
            _requestAttempts[(peer, piece)] = attempts;
        }
    }

    private static bool PeerCanDeclareMetadata(IPeerCommunication peer) =>
        peer.UtMetadata.RemoteMessageId != null && peer.RemoteExtensions?.MetadataSize is > 0;

    private bool TryAssignPeer(IPeerCommunication peer)
    {
        if (peer.UtMetadata.RemoteMessageId == null)
        {
            return false;
        }

        int? pieceIndex = SelectPieceForPeer(peer);
        if (pieceIndex == null)
        {
            return false;
        }

        var key = (peer, pieceIndex.Value);
        int attempts = _requestAttempts.TryGetValue(key, out int previous) ? previous + 1 : 1;
        _requestAttempts[key] = attempts;
        _pendingRequests[key] = new PendingMetadataRequest(
            _torrent.Services.TimeProvider.GetUtcNow(), attempts);

        _logger.LogInformation(
            "Requesting metadata piece {PieceIndex} from {PeerId} (peer attempt={Attempt})",
            pieceIndex.Value,
            peer.PeerId,
            attempts);
        SendMetadataRequest(peer, pieceIndex.Value);
        return true;
    }

    private int? SelectPieceForPeer(IPeerCommunication peer)
    {
        if (_metadataSize == 0)
        {
            int probeCount = _pendingRequests.Keys.Count(key => key.Piece == 0);
            return probeCount < MetadataRequestRedundancy &&
                   !_pendingRequests.ContainsKey((peer, 0)) &&
                   !_refusedRequests.Contains((peer, 0)) &&
                   AttemptsFor(peer, 0) < MetadataMaxRequestAttempts ? 0 : null;
        }

        int totalPieces = _receivedPieces.Length;
        if (totalPieces == 0)
        {
            return null;
        }

        var requestCounts = new int[totalPieces];
        var activePieces = new HashSet<int>();
        foreach (var key in _pendingRequests.Keys)
        {
            if ((uint)key.Piece < (uint)totalPieces && !_receivedPieces[key.Piece])
            {
                requestCounts[key.Piece]++;
                activePieces.Add(key.Piece);
            }
        }

        bool mayOpenAnotherPiece = activePieces.Count < Math.Min(MetadataRequestPipeline, totalPieces);
        int bestCount = int.MaxValue;
        int? bestPiece = null;
        for (int offset = 0; offset < totalPieces; offset++)
        {
            int piece = (_nextPieceCursor + offset) % totalPieces;
            if (_receivedPieces[piece] || requestCounts[piece] >= MetadataRequestRedundancy ||
                _pendingRequests.ContainsKey((peer, piece)) ||
                _refusedRequests.Contains((peer, piece)) ||
                AttemptsFor(peer, piece) >= MetadataMaxRequestAttempts)
            {
                continue;
            }

            if (!mayOpenAnotherPiece && !activePieces.Contains(piece))
            {
                continue;
            }

            if (requestCounts[piece] < bestCount)
            {
                bestCount = requestCounts[piece];
                bestPiece = piece;
                if (bestCount == 0)
                {
                    break;
                }
            }
        }

        if (bestPiece.HasValue)
        {
            _nextPieceCursor = (bestPiece.Value + 1) % totalPieces;
        }
        return bestPiece;
    }

    private int AttemptsFor(IPeerCommunication peer, int pieceIndex) =>
        _requestAttempts.TryGetValue((peer, pieceIndex), out int attempts) ? attempts : 0;

    private void ReleaseRequestsForReceivedPiece(int pieceIndex)
    {
        foreach (var owner in _pendingRequests
                     .Where(entry => entry.Key.Piece == pieceIndex)
                     .Select(entry => entry.Key)
                     .ToList())
        {
            _pendingRequests.Remove(owner);
        }
    }
    /// <summary>
    /// Drops pending requests that were guesses, now that there is something better to go on.
    ///
    /// <para>
    /// Before the metadata size is known there is nothing to do but probe piece 0 from whoever turned
    /// up first, including peers that never declared a size and may hold nothing at all. Once a peer
    /// does declare one, that probe is a stale guess occupying the piece's only pending slot - and
    /// because a piece with a pending request is skipped, nobody else is asked until it times out. In a
    /// live run that left piece 0 parked on a non-declaring peer for 3.3 seconds while six peers that
    /// had the metadata connected and were never asked; re-asked properly, it arrived in 0.3 seconds.
    /// </para>
    /// </summary>
    private void ReleaseSpeculativeRequests()
    {
        if (_pendingRequests.Count == 0)
        {
            return;
        }

        var speculative = _pendingRequests
            .Where(entry => entry.Key.Peer.RemoteExtensions?.MetadataSize is not > 0)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var peer in speculative)
        {
            _pendingRequests.Remove(peer);
        }

        if (speculative.Count > 0)
        {
            _logger.LogDebug(
                "Released {Count} speculative metadata request(s) now that the size is known", speculative.Count);
        }
    }

    /// <summary>
    /// Sends a metadata request. Silence is recorded only if the request later times out; merely
    /// sending one is not evidence against a peer because requests are deliberately issued in bursts.
    /// </summary>
    private void SendMetadataRequest(IPeerCommunication peer, int pieceIndex)
    {
        _firstRequestAt ??= _torrent.Services.TimeProvider.GetUtcNow();
        _requestsSent++;
        peer.UtMetadata.SendRequest(pieceIndex);
    }

    private void ReportStalledMetadataDownload(DateTimeOffset now)
    {
        if (_stallReported || _responsesReceived > 0 || _firstRequestAt == null ||
            _requestsSent < StallWarningMinimumRequests)
        {
            return;
        }

        TimeSpan elapsed = now - _firstRequestAt.Value;
        if (elapsed < StallWarningAfter)
        {
            return;
        }

        int capablePeers = _activePeers.Count(peer => peer.RemoteExtensions?.MetadataSize is > 0);
        if (capablePeers == 0)
        {
            return;
        }

        _stallReported = true;
        _logger.LogWarning(
            "Metadata download is stalled: {CapablePeers} capable peer(s), {RequestsSent} requests over {ElapsedSeconds:F0}s, no pieces received",
            capablePeers,
            _requestsSent,
            elapsed.TotalSeconds);
        _torrent.Alerts.PostAlert(new MetadataDownloadStalledAlert
        {
            Id = AlertId.MetadataDownloadStalled,
            Torrent = _torrent,
            CapablePeers = capablePeers,
            RequestsSent = _requestsSent,
            Elapsed = elapsed,
            Timestamp = now
        });
    }

    private MetadataPeerRecord RecordFor(IPeerCommunication peer)
    {
        if (!_peerRecords.TryGetValue(peer, out var record))
        {
            record = new MetadataPeerRecord();
            _peerRecords[peer] = record;
        }

        return record;
    }

    /// <summary>
    /// Resets a completed but unauthentic metadata set and refuses the peers whose bytes were in it.
    ///
    /// <para>
    /// Refusal here is permanent for the rest of the download, so it has to be aimed at the peers that
    /// actually supplied the rejected bytes and nobody else. Requests are deliberately redundant and a
    /// peer is credited with an answer for any correctly sized piece, duplicates included, so
    /// "answered something" describes most of the swarm rather than the culprit - and refusing on that
    /// basis can blacklist every connected peer over one bad supplier, which leaves the download with
    /// nothing to ask until a new peer happens to arrive.
    /// </para>
    ///
    /// <para>
    /// <see cref="_pieceSuppliers"/> names the peer whose copy of each piece was the one stored, which
    /// is exactly the set that produced the failing hash.
    /// </para>
    /// </summary>
    private void RejectCompletedMetadata()
    {
        var suppliers = _pieceSuppliers.Values.Distinct().ToList();

        foreach (var supplier in suppliers)
        {
            var record = RecordFor(supplier);
            record.Answered = 0;
            record.TimedOut = Math.Max(record.TimedOut, UnansweredRequestsBeforeDemotion);
        }

        _receivedPieces.SetAll(false);
        _pendingRequests.Clear();
        foreach (var supplier in suppliers)
        {
            for (int piece = 0; piece < _receivedPieces.Length; piece++)
            {
                _refusedRequests.Add((supplier, piece));
            }
        }

        _pieceSuppliers.Clear();
        FillPeerRequests();
    }

    /// <summary>
    /// One peer-owned metadata request and its per-peer/per-piece attempt number.
    /// </summary>
    private readonly record struct PendingMetadataRequest(
        DateTimeOffset Timestamp,
        int Attempts);
}
