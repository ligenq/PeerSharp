using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using System.Collections;

namespace PeerSharp.Internals.Extensions;

internal class MetadataDownload : IMetadataDownload, IDisposable
{
    private readonly List<IPeerCommunication> _activePeers = [];
    private readonly Lock _lock = new();
    private readonly ILogger<MetadataDownload> _logger;
    private readonly ILoggerFactory _loggerFactory;
    /// <summary>
    /// How many peers to ask for the same metadata piece at once. A piece is at most 16 KiB, so the
    /// redundancy is negligible next to being held up by one unresponsive peer.
    /// </summary>
    private const int MetadataRequestRedundancy = 3;

    private readonly Dictionary<int, PendingMetadataRequest> _pendingRequests = [];

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
    private byte[] _metadataBuffer = [];
    private int _metadataSize = 0;
    private int MetadataRequestTimeoutSeconds => Math.Max(1, _torrent.Settings.Transfer.MetadataRequestTimeoutSeconds);
    private int MetadataRequestPipeline => Math.Clamp(_torrent.Settings.Transfer.MetadataRequestPipeline, 1, 32);
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
                FillMissingRequests();
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
            _pendingRequests.Remove(pieceIndex);
            if (peer is PeerCommunication communication)
            {
                communication.MarkUsefulDataExchanged();
            }

            if (_receivedPieces[pieceIndex])
            {
                FillMissingRequests(peer);
                return;
            }

            Array.Copy(data, 0, _metadataBuffer, offset, data.Length);
            _receivedPieces[pieceIndex] = true;
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
                catch (FormatException ex)
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
                FillMissingRequests(peer);
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
            if (_pendingRequests.Remove(pieceIndex))
            {
                _logger.LogWarning("Peer {PeerId} rejected metadata piece {PieceIndex}", peer.PeerId, pieceIndex);
                if (pieceIndex >= 0 && pieceIndex < _receivedPieces.Length && !_receivedPieces[pieceIndex])
                {
                    RequestPiece(pieceIndex, preferredPeer: GetAlternatePeer(peer));
                }
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

            if (peer.RemoteSupportsExtensions && peer.RemoteExtensions?.MessageIds.ContainsKey(UtMetadata.Name) == true)
            {
                if (!_activePeers.Contains(peer))
                {
                    _activePeers.Add(peer);
                }
                _logger.LogInformation("Metadata peer connected {PeerId} (id={MessageId}, size={MetadataSize})", peer.PeerId, peer.UtMetadata.RemoteMessageId, peer.RemoteExtensions.MetadataSize);
                _logger.LogDebug("Peer {PeerId} supports ut_metadata. Adding to active list.", peer.PeerId);

                // Request metadata_size if peer sent it
                if (peer.RemoteExtensions.MetadataSize.HasValue)
                {
                    if (_metadataSize == 0)
                    {
                        InitializeMetadataBuffer(peer.RemoteExtensions.MetadataSize.Value);
                    }

                    if (Active && !Finished)
                    {
                        // Top up first: a peer that arrives while the pipeline is full would otherwise
                        // be given nothing to do, which is the whole reason the first round was slow.
                        TopUpOutstandingRequests(peer);
                        FillMissingRequests();
                    }
                }
                else if (Active && !Finished && _metadataSize == 0)
                {
                    // Some peers omit metadata_size in the extended handshake; probe piece 0.
                    if (!_pendingRequests.ContainsKey(0))
                    {
                        RequestPiece(0, preferredPeer: peer);
                    }
                }
                else if (Active && !Finished && _metadataSize > 0)
                {
                    TopUpOutstandingRequests(peer);
                    FillMissingRequests(peer);
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
            if (_pendingRequests.Count > 0)
            {
                var toRemove = _pendingRequests
                    .Where(kv => kv.Value.Peer == peer)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var piece in toRemove)
                {
                    _pendingRequests.Remove(piece);
                }
            }
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
                if (_activePeers.Count > 0 && !_pendingRequests.ContainsKey(0))
                {
                    RequestPiece(0, preferredPeer: GetRandomMetadataPeer());
                }
                return;
            }

            var now = _torrent.Services.TimeProvider.GetUtcNow();
            var timedOut = new List<int>();
            foreach (var kvp in _pendingRequests)
            {
                if ((now - kvp.Value.Timestamp).TotalSeconds > MetadataRequestTimeoutSeconds)
                {
                    timedOut.Add(kvp.Key);
                }
            }

            foreach (var piece in timedOut)
            {
                if (_pendingRequests.Remove(piece, out var pending))
                {
                    RecordFor(pending.Peer).TimedOut++;
                    if (pending.Attempts < MetadataMaxRequestAttempts)
                    {
                        RequestPiece(piece, GetAlternatePeer(pending.Peer), pending.Attempts);
                    }
                }
            }

            FillMissingRequests();
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

    private IPeerCommunication? GetRandomMetadataPeer()
    {
        if (_activePeers.Count == 0)
        {
            return null;
        }

        // Prefer a peer that has declared a metadata size, for the same reason the redundant requests
        // do: a peer that speaks the extension but holds nothing can never answer.
        var candidates = EligibleMetadataPeers(exclude: null);
        if (candidates.Count == 0)
        {
            return _activePeers[Random.Shared.Next(_activePeers.Count)];
        }

        return candidates[Random.Shared.Next(candidates.Count)];
    }

    private void FillMissingRequests(IPeerCommunication? preferredPeer = null)
    {
        if (!Active || Finished || _metadataSize == 0)
        {
            return;
        }

        if (_pendingRequests.Count >= MetadataRequestPipeline)
        {
            return;
        }

        int totalPieces = _receivedPieces.Length;
        if (totalPieces == 0)
        {
            return;
        }

        int attempts = 0;
        while (_pendingRequests.Count < MetadataRequestPipeline && attempts < totalPieces)
        {
            int idx = _nextPieceCursor % totalPieces;
            _nextPieceCursor = (idx + 1) % totalPieces;
            attempts++;

            if (!_receivedPieces[idx] && !_pendingRequests.ContainsKey(idx))
            {
                RequestPiece(idx, preferredPeer);
            }
        }
    }

    /// <summary>
    /// Asks a peer for one piece of the metadata, recording the request so it can be timed out.
    /// </summary>
    /// <param name="pieceIndex">Which metadata piece to ask for.</param>
    /// <param name="preferredPeer">Peer to ask, or null to pick one at random from the active list.</param>
    /// <param name="attemptsSoFar">
    /// How many times this piece has already been asked for. The retry path removes the pending request
    /// before calling back in, so the count cannot be recovered from <c>_pendingRequests</c> - it has to
    /// be carried. Losing it pinned every request at attempt 1, which made the give-up check
    /// (<c>Attempts &lt; MetadataMaxRequestAttempts</c>) permanently true: a peer that never answered
    /// was asked again every ten seconds for as long as the torrent ran, and the piece was never handed
    /// back to the random-peer path that would have found a different one.
    /// </param>
    private void RequestPiece(int pieceIndex, IPeerCommunication? preferredPeer = null, int attemptsSoFar = 0)
    {
        if (!Active || Finished)
        {
            return;
        }

        if (_metadataSize == 0 && pieceIndex != 0)
        {
            return;
        }

        var peer = preferredPeer ?? GetRandomMetadataPeer();
        if (peer == null && preferredPeer != null && !_activePeers.Contains(preferredPeer))
        {
            peer = preferredPeer;
        }
        if (peer != null)
        {
            if (peer.UtMetadata.RemoteMessageId == null)
            {
                _logger.LogInformation("Skipping metadata request for piece {PieceIndex}; peer {PeerId} has no ut_metadata id", pieceIndex, peer.PeerId);
                return;
            }
            int attempts = attemptsSoFar + 1;
            if (attemptsSoFar == 0 && _pendingRequests.TryGetValue(pieceIndex, out var existing))
            {
                attempts = existing.Attempts + 1;
            }
            _logger.LogInformation("Requesting metadata piece {PieceIndex} from {PeerId} (attempt={Attempt})", pieceIndex, peer.PeerId, attempts);
            SendMetadataRequest(peer, pieceIndex);

            int alsoAsked = AskAdditionalPeers(pieceIndex, peer);

            _pendingRequests[pieceIndex] = new PendingMetadataRequest(
                peer,
                _torrent.Services.TimeProvider.GetUtcNow(),
                attempts,
                AskedCount: 1 + alsoAsked);
        }
        else
        {
            _logger.LogInformation("No metadata peers available for piece {PieceIndex}", pieceIndex);
        }
    }

    /// <summary>
    /// Asks a few more peers for the same piece, without tracking them.
    ///
    /// <para>
    /// Metadata is the whole download for a magnet and a piece is at most 16 KiB, so latency matters
    /// enormously and bandwidth does not matter at all. Asking one peer and waiting for a timeout makes
    /// the transfer only as fast as the slowest peer we happened to pick: three torrents in a live
    /// session took between thirty-six seconds and never, with over a hundred willing peers connected
    /// the whole time. Asking several means the fastest answer wins.
    /// </para>
    ///
    /// <para>
    /// Only the first peer is recorded in <c>_pendingRequests</c>, because that entry exists to drive
    /// the timeout and one timer per piece is enough. The extra replies are still accepted -
    /// <see cref="MetadataPieceReceivedAsync"/> judges a piece on its own merits rather than on whether
    /// it was expected - and a duplicate is discarded by the already-received check.
    /// </para>
    ///
    /// <para>
    /// libtorrent goes further and lets every peer hold its own outstanding requests, throttled only by
    /// not re-asking for the same piece within three seconds.
    /// </para>
    /// </summary>
    /// <returns>How many additional peers were asked.</returns>
    private int AskAdditionalPeers(int pieceIndex, IPeerCommunication alreadyAsked)
    {
        var candidates = EligibleMetadataPeers(alreadyAsked);
        if (candidates.Count == 0)
        {
            return 0;
        }

        // Draw without replacement rather than walking the list. Taking the first matches sends every
        // round to the same two peers, so if those are silent the redundancy buys nothing at all -
        // which is the mistake this replaces, and the same one that made GetAlternatePeer ping-pong.
        int wanted = Math.Min(MetadataRequestRedundancy - 1, candidates.Count);
        for (int i = 0; i < wanted; i++)
        {
            int pick = Random.Shared.Next(i, candidates.Count);
            (candidates[i], candidates[pick]) = (candidates[pick], candidates[i]);
            SendMetadataRequest(candidates[i], pieceIndex);
        }

        _logger.LogDebug(
            "Also asked {Count} other peer(s) for metadata piece {PieceIndex}", wanted, pieceIndex);
        return wanted;
    }

    /// <summary>
    /// Hands a peer that has just arrived the requests that are already outstanding.
    ///
    /// <para>
    /// Without this a peer can connect, advertise the metadata, and be asked for nothing at all. The
    /// pipeline is a cap on distinct pieces in flight, not on peers, so once the first peer to turn up
    /// has taken every slot, <see cref="FillMissingRequests"/> returns immediately - the count is at
    /// the limit - and every piece it skips is skipped again for having a pending entry. If that first
    /// peer never answers, nothing breaks the deadlock until the timeout fires.
    /// </para>
    ///
    /// <para>
    /// Measured on a real magnet: the size arrived with one peer connected, all eight slots went to it,
    /// and seven more capable peers connected over the next 3.5 seconds and sat idle until the whole
    /// round timed out. The pieces then arrived from those peers in 400 ms. The first request round was
    /// pure latency, and it is the round that decides how fast a magnet starts.
    /// </para>
    ///
    /// <para>
    /// Bounded by <see cref="MetadataRequestRedundancy"/> so that a large swarm does not turn every
    /// piece into a broadcast: the existing pending entry still owns the timeout, exactly as in
    /// <see cref="AskAdditionalPeers"/>, and a duplicate reply is discarded by the already-received
    /// check in <see cref="MetadataPieceReceivedAsync"/>.
    /// </para>
    /// </summary>
    private void TopUpOutstandingRequests(IPeerCommunication peer)
    {
        if (!Active || Finished || _metadataSize == 0 || _pendingRequests.Count == 0)
        {
            return;
        }

        if (peer.UtMetadata.RemoteMessageId == null)
        {
            return;
        }

        int asked = 0;
        foreach (int pieceIndex in _pendingRequests.Keys.ToList())
        {
            var pending = _pendingRequests[pieceIndex];
            if (pending.AskedCount >= MetadataRequestRedundancy || pending.Peer == peer)
            {
                continue;
            }

            if (pieceIndex >= 0 && pieceIndex < _receivedPieces.Length && _receivedPieces[pieceIndex])
            {
                continue;
            }

            SendMetadataRequest(peer, pieceIndex);
            _pendingRequests[pieceIndex] = pending with { AskedCount = pending.AskedCount + 1 };
            asked++;
        }

        if (asked > 0)
        {
            _logger.LogDebug(
                "Asked newly connected peer {PeerId} for {Count} already-outstanding metadata piece(s)",
                peer.PeerId,
                asked);
        }
    }

    /// <summary>
    /// Peers worth asking, preferring those that told us how large the metadata is.
    ///
    /// <para>
    /// Advertising ut_metadata only says a peer speaks the extension; the size in its handshake is what
    /// says it actually holds the data. Asking a peer that has none is a request that can never be
    /// answered, and a magnet has nothing else to do meanwhile. libtorrent gates its requests on the
    /// same signal, relaxing it on a timer so a peer is not written off forever - hence the fallback
    /// here to anyone at all when nobody has declared a size.
    /// </para>
    /// </summary>
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
            .Where(entry => entry.Value.Peer?.RemoteExtensions?.MetadataSize is not > 0)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var piece in speculative)
        {
            _pendingRequests.Remove(piece);
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
    private static void SendMetadataRequest(IPeerCommunication peer, int pieceIndex)
    {
        peer.UtMetadata.SendRequest(pieceIndex);
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
    /// Resets a completed but unauthentic metadata set and demotes the peers that supplied it.
    /// A peer is promoted as soon as it serves a piece for latency reasons, but that evidence must not
    /// trap every retry on the same corrupt suppliers once the complete info hash disproves the set.
    /// </summary>
    private void RejectCompletedMetadata()
    {
        foreach (var record in _peerRecords.Values)
        {
            if (record.Answered > 0)
            {
                record.Answered = 0;
                record.TimedOut = Math.Max(record.TimedOut, UnansweredRequestsBeforeDemotion);
            }
        }

        _receivedPieces.SetAll(false);
        _pendingRequests.Clear();
        FillMissingRequests();
    }

    /// <summary>
    /// Peers worth asking, best first: those that have actually served a piece, then those never asked,
    /// and only then those that have been asked and stayed silent.
    ///
    /// <para>
    /// Advertising ut_metadata with a size says a peer holds the metadata, not that it will part with
    /// it, and in a real swarm most will not. Measured on Ubuntu's: of 75 peers asked, 8 ever answered,
    /// and that willing group barely grows with the swarm - so drawing uniformly at random, which is
    /// what this did, gives a hit rate that falls as the swarm gets larger. The effect was plainly
    /// visible across eight runs of the same magnet: 28 connected peers took 6.5 seconds to collect
    /// sixteen pieces and 148 took 55, monotonically in between. Having more peers made it slower.
    /// </para>
    ///
    /// <para>
    /// Tiering fixes that without any per-client knowledge: a peer earns its place by answering. The
    /// tiers also give exploration for free, because <see cref="AskAdditionalPeers"/> excludes the peer
    /// it just asked - so while a proven peer serves the piece, the redundant asks fall through to peers
    /// nobody has tried yet, which is how the proven set grows.
    /// </para>
    /// </summary>
    private List<IPeerCommunication> EligibleMetadataPeers(IPeerCommunication? exclude)
    {
        var proven = new List<IPeerCommunication>();
        var untried = new List<IPeerCommunication>();
        var silent = new List<IPeerCommunication>();
        var anySpeaker = new List<IPeerCommunication>();

        foreach (var candidate in _activePeers)
        {
            if (candidate == exclude || candidate.UtMetadata.RemoteMessageId == null)
            {
                continue;
            }

            anySpeaker.Add(candidate);

            if (candidate.RemoteExtensions?.MetadataSize is not > 0)
            {
                continue;
            }

            _peerRecords.TryGetValue(candidate, out var record);
            if (record is { Answered: > 0 })
            {
                proven.Add(candidate);
            }
            else if (record is { TimedOut: >= UnansweredRequestsBeforeDemotion })
            {
                silent.Add(candidate);
            }
            else
            {
                untried.Add(candidate);
            }
        }

        if (proven.Count > 0)
        {
            return proven;
        }

        if (untried.Count > 0)
        {
            return untried;
        }

        return silent.Count > 0 ? silent : anySpeaker;
    }

    private IPeerCommunication? GetAlternatePeer(IPeerCommunication? current)
    {
        if (_activePeers.Count == 0)
        {
            return null;
        }

        if (current == null || _activePeers.Count == 1)
        {
            return GetRandomMetadataPeer();
        }

        // Use the same evidence-based tiers as an initial request, while excluding the peer whose
        // request just timed out. Random selection within the best tier avoids the old two-peer
        // ping-pong without putting another known-silent peer ahead of an untried one.
        var candidates = EligibleMetadataPeers(current);
        if (candidates.Count > 0)
        {
            return candidates[Random.Shared.Next(candidates.Count)];
        }

        return GetRandomMetadataPeer();
    }

    /// <summary>
    /// One outstanding metadata piece request: the peer that owns its timeout, when it was made, how
    /// many times the piece has been asked for, and how many peers currently hold a copy of the request.
    ///
    /// <para>
    /// That last count is what bounds <see cref="TopUpOutstandingRequests"/> to
    /// <see cref="MetadataRequestRedundancy"/> however many peers turn up, so redundancy stays a fixed
    /// multiple rather than scaling with the size of the swarm.
    /// </para>
    /// </summary>
    private readonly record struct PendingMetadataRequest(
        IPeerCommunication Peer,
        DateTimeOffset Timestamp,
        int Attempts,
        int AskedCount = 1);
}
