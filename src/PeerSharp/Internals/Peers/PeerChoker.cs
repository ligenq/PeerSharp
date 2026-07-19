using Microsoft.Extensions.Logging;

namespace PeerSharp.Internals.Peers;

/// <summary>Owns upload-slot selection, rechoking, and optimistic-unchoke state.</summary>
internal sealed class PeerChoker
{
    private const int OptimisticUnchokeIntervalMinSeconds = 5;
    private const double GradualUnchokeThreshold = 0.7;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private readonly List<PeerCommunication> _peers = [];
    private readonly List<PeerCommunication> _candidates = [];
    private readonly HashSet<PeerCommunication> _selected = [];
    private PeerCommunication? _optimisticPeer;
    private DateTimeOffset _lastOptimisticChange = DateTimeOffset.MinValue;

    public PeerChoker(Torrent torrent, TimeProvider timeProvider, ILogger logger)
    {
        _torrent = torrent;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public void SetOptimisticPeerForTesting(PeerCommunication? peer, DateTimeOffset changedAt)
    {
        _optimisticPeer = peer;
        _lastOptimisticChange = changedAt;
    }

    public int GetOptimisticUnchokeIntervalSeconds() => Math.Max(OptimisticUnchokeIntervalMinSeconds, _torrent.Settings.Connection.OptimisticUnchokeIntervalSeconds);

    public bool HasAvailableUploadSlot(IEnumerable<PeerCommunication> connectedPeers, int connectedCount)
    {
        return connectedPeers.Count(peer => !peer.AmChoking) < GetUploadSlots(connectedCount);
    }

    public void Rechoke(IEnumerable<PeerCommunication> connectedPeers, int connectedCount)
    {
        bool isSeeding = _torrent.Finished;
        if (isSeeding && !_torrent.Settings.Connection.EnableLsd && !_torrent.Settings.Dht.Enabled && connectedPeers.All(peer => peer.PeerPieces?.IsFull == true))
        {
            _logger.LogTrace("Seeding saturated - skipping unchoke cycle");
            return;
        }

        _peers.Clear();
        _candidates.Clear();
        _selected.Clear();
        if (_peers.Capacity < connectedCount) _peers.Capacity = connectedCount;
        _peers.AddRange(connectedPeers);

        foreach (var peer in _peers)
        {
            peer.UpdateSpeed();
            if (peer.PeerInterested) _candidates.Add(peer);
        }

        if (isSeeding)
        {
            long pieceLength = _torrent.PieceSize;
            var now = _timeProvider.GetUtcNow();
            _candidates.Sort((a, b) => SeedingChoker.Compare(SeedingChoker.FromPeer(a), SeedingChoker.FromPeer(b), pieceLength, SeedingChoker.DefaultPieceQuota, now));
        }
        else
        {
            _candidates.Sort((a, b) => b.SmoothedDownloadSpeed.CompareTo(a.SmoothedDownloadSpeed));
        }

        int slots = GetUploadSlots(connectedCount);
        int regularSlots = _candidates.Count > slots ? slots - 1 : slots;
        int bestSpeed = 0;
        if (_candidates.Count > 0)
        {
            bestSpeed = isSeeding ? _candidates[0].UploadSpeed : _candidates[0].SmoothedDownloadSpeed;
        }
        int gradualThreshold = (int)(bestSpeed * GradualUnchokeThreshold);
        for (int i = 0; i < Math.Min(regularSlots, _candidates.Count); i++) _selected.Add(_candidates[i]);

        int keptFromPrevious = 0;
        foreach (var peer in _peers)
        {
            if (!peer.AmChoking && peer.PeerInterested && !_selected.Contains(peer))
            {
                int speed = isSeeding ? peer.UploadSpeed : peer.SmoothedDownloadSpeed;
                if (speed >= gradualThreshold && _selected.Count < slots + 2)
                {
                    _selected.Add(peer);
                    keptFromPrevious++;
                }
            }
        }

        if (_candidates.Count > slots)
        {
            int optimisticIndex = -1;
            if (_optimisticPeer != null && (_timeProvider.GetUtcNow() - _lastOptimisticChange).TotalSeconds < GetOptimisticUnchokeIntervalSeconds())
            {
                optimisticIndex = _candidates.IndexOf(_optimisticPeer, regularSlots);
            }
            if (optimisticIndex == -1)
            {
                optimisticIndex = regularSlots + Random.Shared.Next(_candidates.Count - regularSlots);
                _optimisticPeer = _candidates[optimisticIndex];
                _lastOptimisticChange = _timeProvider.GetUtcNow();
            }
            _selected.Add(_candidates[optimisticIndex]);
        }
        else _optimisticPeer = null;

        int unchoked = 0;
        int choked = 0;
        foreach (var peer in _peers)
        {
            if (_selected.Contains(peer))
            {
                if (peer.AmChoking) unchoked++;
                peer.Unchoke();
            }
            else
            {
                if (!peer.AmChoking) choked++;
                peer.Choke();
            }
        }

        double avgSpeed = _candidates.Count == 0 ? 0 : _candidates.Average(peer => isSeeding ? peer.UploadSpeed : peer.SmoothedDownloadSpeed);
        double maxSpeed = _candidates.Count == 0 ? 0 : _candidates.Max(peer => isSeeding ? peer.UploadSpeed : peer.SmoothedDownloadSpeed);
        _logger.LogDebug("Unchoke ({Mode}): {TotalPeers} peers, {InterestedPeers} interested, {UnchokedCount} unchoked (+{NewUnchoked} new, {Kept} kept), {NewChoked} newly choked, avg={AvgSpeed}B/s, max={MaxSpeed}B/s, threshold={Threshold}B/s", isSeeding ? "Seeding" : "Leeching", _peers.Count, _candidates.Count, _selected.Count, unchoked, keptFromPrevious, choked, Math.Round(avgSpeed), maxSpeed, gradualThreshold);
    }

    internal int GetUploadSlotsForTesting(int connectedCount) => GetUploadSlots(connectedCount);

    private int GetUploadSlots(int connectedCount)
    {
        int minSlots = Math.Max(1, _torrent.Settings.Connection.UploadSlotsMin);
        int maxSlots = Math.Max(minSlots, _torrent.Settings.Connection.UploadSlotsMax);
        int uploadLimit = _torrent.UploadLimitBytesPerSecond;
        if (uploadLimit <= 0) uploadLimit = (int)_torrent.Settings.Transfer.MaxUploadSpeed;
        if (uploadLimit <= 0) return Math.Min(maxSlots, Math.Max(minSlots, connectedCount));
        int targetPerSlot = Math.Max(8000, _torrent.Settings.Connection.TargetUploadPerSlotBytesPerSec);
        int slots = Math.Clamp((int)Math.Ceiling(uploadLimit / (double)targetPerSlot), minSlots, maxSlots);
        return Math.Min(slots, Math.Max(minSlots, connectedCount));
    }
}
