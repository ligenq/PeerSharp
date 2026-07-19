using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Utilities;
using System.Collections;

namespace PeerSharp.Internals.Extensions;

internal class MetadataDownload : IMetadataDownload, IDisposable
{
    private readonly List<IPeerCommunication> _activePeers = [];
    private readonly Lock _lock = new();
    private readonly ILogger<MetadataDownload> _logger = TorrentLoggerFactory.CreateLogger<MetadataDownload>();
    private readonly Dictionary<int, PendingMetadataRequest> _pendingRequests = [];

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
    {
        _torrent = torrent;
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
            _pendingRequests.Remove(pieceIndex);

            // Check _receivedPieces.Length > 0 to handle uninitialized state (empty BitArray)
            if (!Active || Finished || _metadataSize == 0 || _receivedPieces.Length == 0 ||
                pieceIndex >= _receivedPieces.Length || _receivedPieces[pieceIndex])
            {
                if (Active && !Finished && _metadataSize > 0)
                {
                    FillMissingRequests(peer);
                }
                return;
            }

            int offset = pieceIndex * UtMetadata.PieceSize;
            if (offset + data.Length > _metadataBuffer.Length)
            {
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
                var newMetadata = TorrentFileParser.ParseInfoBytes(_metadataBuffer);

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
                    _receivedPieces.SetAll(false);
                    _pendingRequests.Clear();
                    FillMissingRequests();
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
                if (pieceIndex < _receivedPieces.Length && !_receivedPieces[pieceIndex])
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
                if (_pendingRequests.Remove(piece, out var pending) && pending.Attempts < MetadataMaxRequestAttempts)
                {
                    RequestPiece(piece, preferredPeer: GetAlternatePeer(pending.Peer));
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

        if (_activePeers.Count == 1)
        {
            return _activePeers[0];
        }

        return _activePeers[Random.Shared.Next(_activePeers.Count)];
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

    private void RequestPiece(int pieceIndex, IPeerCommunication? preferredPeer = null)
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
            int attempts = 1;
            if (_pendingRequests.TryGetValue(pieceIndex, out var existing))
            {
                attempts = existing.Attempts + 1;
            }
            _pendingRequests[pieceIndex] = new PendingMetadataRequest(
                peer,
                _torrent.Services.TimeProvider.GetUtcNow(),
                attempts);
            _logger.LogInformation("Requesting metadata piece {PieceIndex} from {PeerId} (attempt={Attempt})", pieceIndex, peer.PeerId, attempts);
            peer.UtMetadata.SendRequest(pieceIndex);
        }
        else
        {
            _logger.LogInformation("No metadata peers available for piece {PieceIndex}", pieceIndex);
        }
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

        // Deterministic scan for small lists to ensure we find a different peer
        foreach (var p in _activePeers)
        {
            if (p != current)
            {
                return p;
            }
        }

        return GetRandomMetadataPeer();
    }

    private readonly record struct PendingMetadataRequest(
        IPeerCommunication Peer,
        DateTimeOffset Timestamp,
        int Attempts);
}
