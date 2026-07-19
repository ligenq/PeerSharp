using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;

namespace PeerSharp.PiecePicking;

/// <summary>
/// <para>Implements piece selection strategy for BitTorrent downloads.</para>
/// <para>
/// Uses the "Rarest First" algorithm (BEP-3 recommendation):
/// - Tracks availability of each piece across all connected peers
/// - Prioritizes downloading pieces that are least common in the swarm
/// - This improves overall swarm health by ensuring rare pieces are replicated
/// </para>
/// <para>
/// Also supports:
/// - File priority levels (High, Normal, Low, DoNotDownload)
/// - Randomization within same-availability groups to distribute load
/// - Fast-piece suggestions from peers (BEP-6)
/// </para>
/// </summary>
internal class PiecePicker : IDisposable
{
    private const int InitialRandomPieceThreshold = 4;
    private readonly ILogger<PiecePicker> _logger;

    private readonly IPiecePickerContext _context;
    private int[] _pieceAvailability;

    private readonly Random _random;
    private readonly Lock _selectionLock = new();
    private List<int> _sortedPieces = [];
    private readonly TimeProvider _timeProvider;
    private AtomicDisposal _disposal = new();
    private IReadOnlyList<FileSelection>? _fileSelectionSnapshot;
    private DateTimeOffset _lastSelectionRefresh = DateTimeOffset.MinValue;
    private bool _selectionInvalidated = true;

    /// <summary>
    /// Creates a PiecePicker with full dependency injection.
    /// </summary>
    public PiecePicker(IPiecePickerContext context, TimeProvider timeProvider, Random random)
        : this(context, timeProvider, random, NullLoggerFactory.Instance)
    {
    }

    public PiecePicker(IPiecePickerContext context, TimeProvider timeProvider, Random random, ILoggerFactory loggerFactory)
    {
        _context = context;
        _pieceAvailability = new int[context.PieceCount];
        _timeProvider = timeProvider;
        _random = random;
        _logger = loggerFactory.CreateLogger<PiecePicker>();
    }

    public void DecrementAvailability(int pieceIndex)
    {
        if (pieceIndex < 0)
        {
            return;
        }

        lock (_selectionLock)
        {
            EnsureCapacity(pieceIndex + 1);
            if (pieceIndex < _pieceAvailability.Length)
            {
                _pieceAvailability[pieceIndex]--;
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public int GetAvailability(int pieceIndex)
    {
        if (pieceIndex < 0)
        {
            return 0;
        }

        lock (_selectionLock)
        {
            EnsureCapacity(pieceIndex + 1);
            if (pieceIndex < _pieceAvailability.Length)
            {
                return _pieceAvailability[pieceIndex];
            }
            return 0;
        }
    }

    public IReadOnlyList<int> GetCandidates()
    {
        lock (_selectionLock)
        {
            if (_selectionInvalidated || (_timeProvider.GetUtcNow() - _lastSelectionRefresh).TotalSeconds > ProtocolConstants.PieceSelectionRefreshIntervalSeconds)
            {
                RefreshSelection();
            }

            return _sortedPieces;
        }
    }

    public IReadOnlyList<FileSelection>? GetSelectionSnapshot()
    {
        lock (_selectionLock)
        {
            return _fileSelectionSnapshot;
        }
    }

    public void IncrementAvailability(int pieceIndex)
    {
        if (pieceIndex < 0)
        {
            return;
        }

        lock (_selectionLock)
        {
            EnsureCapacity(pieceIndex + 1);
            if (pieceIndex < _pieceAvailability.Length)
            {
                _pieceAvailability[pieceIndex]++;
            }
        }
    }

    public void InvalidateSelection()
    {
        lock (_selectionLock)
        {
            _selectionInvalidated = true;
            _fileSelectionSnapshot = _context.GetFileSelectionSnapshot();
        }
    }

    public bool IsPieceNeeded(int pieceIndex)
    {
        // This is a hot path check
        lock (_selectionLock)
        {
            var selection = _fileSelectionSnapshot;
            if (selection == null)
            {
                return true; // Default needed if no selection logic applied yet?
            }

            return _context.IsPieceNeeded(pieceIndex, selection);
        }
    }

    /// <summary>
    /// Picks the next piece to download from a peer using PeerCommunication.
    /// </summary>
    public bool PickNextPiece(PeerCommunication peer, out int pieceIndex)
    {
        return PickNextPiece(new PeerCommunicationAdapter(peer), out pieceIndex);
    }

    /// <summary>
    /// Picks the next piece to download from a peer (interface version for testing).
    /// </summary>
    public bool PickNextPiece(IPeerPieceInfo peer, out int pieceIndex)
    {
        pieceIndex = -1;
        var selection = GetSelectionSnapshot();

        // STREAMING MODE: Prioritize streaming pieces first
        if (_context.DownloadStrategy == DownloadStrategy.Streaming)
        {
            var streamingPieces = _context.StreamingPriorityPieces;
            if (streamingPieces != null)
            {
                foreach (int i in streamingPieces)
                {
                    if (CanPick(i, peer, selection))
                    {
                        pieceIndex = i;
                        return true;
                    }
                }
            }
        }

        // SEQUENTIAL MODE: Pick pieces in order
        if (_context.DownloadStrategy == DownloadStrategy.Sequential)
        {
            for (int i = 0; i < _context.PieceCount; i++)
            {
                if (CanPick(i, peer, selection))
                {
                    pieceIndex = i;
                    return true;
                }
            }
            return false;
        }

        // Fast path: Peer has suggested pieces?
        if (!peer.IsChoking)
        {
            foreach (int i in peer.GetSuggestedPieces())
            {
                if (CanPick(i, peer, selection))
                {
                    pieceIndex = i;
                    return true;
                }
            }
        }

        // RANDOM FIRST PIECES MODE (Startup Optimization)
        // Complete a few random pieces quickly so the client can participate in Tit-for-Tat.
        if (_context.DownloadStrategy == DownloadStrategy.RarestFirst && _context.CompletedPieceCount < InitialRandomPieceThreshold)
        {
            int selectedIndex = -1;
            Priority selectedPriority = Priority.DoNotDownload;
            int count = 0;
            for (int i = 0; i < _context.PieceCount; i++)
            {
                if (CanPick(i, peer, selection))
                {
                    var priority = _context.GetPiecePriority(i, selection);
                    if (priority < selectedPriority)
                    {
                        continue;
                    }

                    if (priority > selectedPriority)
                    {
                        selectedPriority = priority;
                        selectedIndex = -1;
                        count = 0;
                    }

                    count++;
                    if (_random.Next(count) == 0)
                    {
                        selectedIndex = i;
                    }
                }
            }

            if (selectedIndex != -1)
            {
                pieceIndex = selectedIndex;
                return true;
            }

            return false;
        }

        // RAREST FIRST MODE (default)
        List<int> currentPieces;
        lock (_selectionLock)
        {
            if (_selectionInvalidated || (_timeProvider.GetUtcNow() - _lastSelectionRefresh).TotalSeconds > ProtocolConstants.PieceSelectionRefreshIntervalSeconds)
            {
                RefreshSelection();
                selection = _fileSelectionSnapshot; // Update local snapshot after refresh
            }

            currentPieces = _sortedPieces;
        }

        foreach (int i in currentPieces)
        {
            if (CanPick(i, peer, selection))
            {
                pieceIndex = i;
                return true;
            }
        }

        return false;
    }

    public void RefreshSelection()
    {
        // Caller must hold _selectionLock. Note: Monitor.IsEntered() does not work with
        // System.Threading.Lock, so this contract is enforced by convention only.
        EnsureCapacity(_context.PieceCount);

        _fileSelectionSnapshot ??= _context.GetFileSelectionSnapshot();
        var selection = _fileSelectionSnapshot;

        var highPriority = new List<int>();
        var normalPriority = new List<int>();
        var lowPriority = new List<int>();

        for (int i = 0; i < _context.PieceCount; i++)
        {
            if (!_context.HasPiece(i))
            {
                if (!_context.IsPieceNeeded(i, selection))
                {
                    continue;
                }

                var priority = _context.GetPiecePriority(i, selection);
                switch (priority)
                {
                    case Priority.High:
                        highPriority.Add(i);
                        break;

                    case Priority.Normal:
                        normalPriority.Add(i);
                        break;

                    case Priority.Low:
                        lowPriority.Add(i);
                        break;
                }
            }
        }

        // RAREST FIRST: Sort by availability in ascending order using bucket sort (O(N))
        // This is much faster than O(N log N) sort for large number of pieces
        BucketSort(highPriority, _pieceAvailability);
        BucketSort(normalPriority, _pieceAvailability);
        BucketSort(lowPriority, _pieceAvailability);

        var newSortedPieces = new List<int>(highPriority.Count + normalPriority.Count + lowPriority.Count);
        newSortedPieces.AddRange(highPriority);
        newSortedPieces.AddRange(normalPriority);
        newSortedPieces.AddRange(lowPriority);

        _sortedPieces = newSortedPieces;

        _lastSelectionRefresh = _timeProvider.GetUtcNow();
        _selectionInvalidated = false;

        _logger.LogDebug("Selection refreshed: {HighPriorityCount} high, {NormalPriorityCount} normal, {LowPriorityCount} low priority pieces",
            highPriority.Count, normalPriority.Count, lowPriority.Count);
    }

    public void RegisterPeerAvailability(PeerCommunication peer)
    {
        var peerPieces = peer.PeerPieces;

        lock (_selectionLock)
        {
            EnsureCapacity(_context.PieceCount);
            int count = Math.Min(_pieceAvailability.Length, peerPieces.Count);

            if (peerPieces.IsFull)
            {
                for (int i = 0; i < count; i++)
                {
                    _pieceAvailability[i]++;
                }
                return;
            }

            for (int i = 0; i < count; i++)
            {
                if (peerPieces.HasPiece(i))
                {
                    _pieceAvailability[i]++;
                }
            }
        }
    }

    public void UnregisterPeerAvailability(PeerCommunication peer)
    {
        var peerPieces = peer.PeerPieces;

        lock (_selectionLock)
        {
            EnsureCapacity(_context.PieceCount);
            int count = Math.Min(_pieceAvailability.Length, peerPieces.Count);

            if (peerPieces.IsFull)
            {
                for (int i = 0; i < count; i++)
                {
                    _pieceAvailability[i]--;
                }
                return;
            }

            for (int i = 0; i < count; i++)
            {
                if (peerPieces.HasPiece(i))
                {
                    _pieceAvailability[i]--;
                }
            }
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            // No disposable resources currently
        }
    }

    private void BucketSort(List<int> list, int[] availability)
    {
        // For small lists, standard sort is fine
        if (list.Count < 100)
        {
            ShuffleList(list); // Ensure random order before unstable sort? No, random order is needed for equal keys.
            // Since Sort is unstable, we should shuffle AFTER if we want random order for equal keys?
            // Actually, if we want random order for equal keys, we need to group them.
            // Bucketing handles this naturally.
            // But for small lists, let's just use buckets too for consistency, it's cheap enough.
        }

        const int MaxBuckets = 128;
        var buckets = new List<int>[MaxBuckets];

        foreach (var p in list)
        {
            if ((uint)p >= (uint)availability.Length)
            {
                // Piece count changed; skip out-of-range indices.
                continue;
            }
            int avail = availability[p];
            int bucketIdx = avail >= MaxBuckets ? MaxBuckets - 1 : avail;

            (buckets[bucketIdx] ??= []).Add(p);
        }

        list.Clear();
        for (int i = 0; i < MaxBuckets; i++)
        {
            var bucket = buckets[i];
            if (bucket?.Count > 0)
            {
                if (i == MaxBuckets - 1)
                {
                    // Sort overflow bucket to ensure correct ordering for very high availability pieces
                    bucket.Sort((a, b) => availability[a] - availability[b]);
                }
                else
                {
                    ShuffleList(bucket);
                }
                list.AddRange(bucket);
            }
        }
    }

    private void EnsureCapacity(int pieceCount)
    {
        if (pieceCount <= _pieceAvailability.Length)
        {
            return;
        }

        var next = new int[pieceCount];
        Array.Copy(_pieceAvailability, next, _pieceAvailability.Length);
        _pieceAvailability = next;
        _selectionInvalidated = true;
    }

    private bool CanPick(int i, IPeerPieceInfo peer, IReadOnlyList<FileSelection>? selection)
    {
        if (_context.HasPiece(i))
        {
            return false;
        }

        // If choked, only allow Fast pieces
        if (peer.IsChoking && !peer.IsAllowedFast(i))
        {
            return false;
        }

        if (!peer.HasPiece(i))
        {
            return false;
        }

        if (_context.IsPieceActive(i))
        {
            return false;
        }

        // Double check need
        if (selection != null && !_context.IsPieceNeeded(i, selection))
        {
            return false;
        }

        return true;
    }

    private void ShuffleList(List<int> list)
    {
        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = _random.Next(n + 1);
            (list[k], list[n]) = (list[n], list[k]);
        }
    }
}
