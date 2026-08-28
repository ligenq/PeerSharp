namespace PeerSharp.Internals.Framework;

internal class FileSelectionManager : IFileSelectionManager
{
    private readonly TorrentFileMetadata _metadata;
    private readonly Lock _selectionLock = new();
    private IUnfinishedBytesProvider? _bytesProvider;
    private List<FileSelection> _fileSelection = [];
    private IReadOnlyList<FileSelection>? _fileSelectionSnapshot;
    private IFileSelectionObserver? _observer;

    /// <summary>
    /// <see cref="PiecesProgress.Generation"/> as it stood when the selected-piece counters were last
    /// known to agree with the map, or -1 before the first count. A generation is needed rather than
    /// the received count: a recheck can replace one valid piece with another and leave that count
    /// unchanged, even though the selected-piece answer changed.
    /// </summary>
    private long _piecesGenerationAtLastSync = -1;
    private PiecesProgress? _pieces; // Set during Initialize

    public FileSelectionManager(TorrentFileMetadata metadata)
    {
        _metadata = metadata;
    }

    public bool IsSelectionFinished
    {
        get
        {
            if (_pieces == null)
            {
                return false;
            }

            lock (_selectionLock)
            {
                if (_fileSelection.Count == 0)
                {
                    return _pieces.ReceivedCount == _pieces.Count;
                }

                EnsureStatsFresh();
                return ReceivedSelectedPieces >= TotalSelectedPieces;
            }
        }
    }

    public int ReceivedSelectedPieces { get; private set; }

    public int TotalSelectedPieces { get; private set; }

    public ulong CalculateFinishedSelectedBytes()
    {
        if (_pieces == null)
        {
            return 0;
        }

        lock (_selectionLock)
        {
            EnsureStatsFresh();
            ulong bytes = (ulong)ReceivedSelectedPieces * _metadata.Info.PieceSize;

            // Adjust for last piece if it's smaller and selected/received
            if (_pieces.Count > 0 &&
                _metadata.Info.IsPieceNeeded(_pieces.Count - 1, _fileSelection) &&
                _pieces.HasPiece(_pieces.Count - 1))
            {
                long lastPieceSize = _metadata.Info.FullSize % _metadata.Info.PieceSize;
                if (lastPieceSize > 0)
                {
                    bytes -= _metadata.Info.PieceSize;
                    bytes += (ulong)lastPieceSize;
                }
            }

            if (_bytesProvider != null)
            {
                bytes += (ulong)_bytesProvider.GetUnfinishedSelectedBytes(_fileSelection);
            }
            return bytes;
        }
    }

    public float CalculateSelectionProgress()
    {
        if (_pieces == null)
        {
            return 0.0f;
        }

        lock (_selectionLock)
        {
            EnsureStatsFresh();
            if (_fileSelection.Count == 0 || TotalSelectedPieces == 0)
            {
                return 1.0f;
            }

            float progress = (float)ReceivedSelectedPieces / TotalSelectedPieces;

            // Add partial progress from active pieces
            if (_metadata.Info.PieceSize > 0 && TotalSelectedPieces > 0 && _bytesProvider != null)
            {
                long unfinishedBytes = _bytesProvider.GetUnfinishedSelectedBytes(_fileSelection);
                progress += unfinishedBytes / (float)_metadata.Info.PieceSize / TotalSelectedPieces;
            }

            return Math.Min(progress, 1.0f);
        }
    }

    public IReadOnlyList<FileSelection> GetAllFileSelections()
    {
        lock (_selectionLock)
        {
            EnsureFileSelectionSize(_metadata.Info.Files.Count);
            return _fileSelectionSnapshot ??= _fileSelection.ToList().AsReadOnly();
        }
    }

    public FileSelection GetFileSelection(int fileIndex)
    {
        lock (_selectionLock)
        {
            if (fileIndex < 0 || fileIndex >= _fileSelection.Count)
            {
                return new FileSelection { Selected = true, Priority = Priority.Normal };
            }
            if (IsPaddingIndex(fileIndex))
            {
                return new FileSelection { Selected = false, Priority = Priority.DoNotDownload };
            }
            return _fileSelection[fileIndex];
        }
    }

    public void Initialize(List<FileSelection>? savedSelection, PiecesProgress pieces)
    {
        _pieces = pieces;

        lock (_selectionLock)
        {
            if (savedSelection?.Count > 0)
            {
                _fileSelection = [.. savedSelection];
            }
            else
            {
                InitializeDefaultFileSelection();
            }
            EnsureFileSelectionSize(_metadata.Info.Files.Count);
            RecalculateSelectionStats();
        }
    }

    public void OnPieceVerified(int pieceIndex)
    {
        if (_pieces == null)
        {
            return;
        }

        lock (_selectionLock)
        {
            // The piece map is the authority. One new piece is what this call is reporting, so the
            // increment is safe; any other change means the map moved without us and the counters
            // have to be rebuilt from it. This also absorbs a repeated notification for a piece
            // already counted, which would otherwise silently overshoot the total.
            long generation = _pieces.Generation;
            if (generation == _piecesGenerationAtLastSync + 1)
            {
                if (_metadata.Info.IsPieceNeeded(pieceIndex, _fileSelection))
                {
                    ReceivedSelectedPieces++;
                }

                _piecesGenerationAtLastSync = generation;
            }
            else
            {
                RecalculateSelectionStats();
            }
        }
    }

    public async Task SetAllFilesPriorityAsync(Priority priority, CancellationToken ct = default)
    {
        IReadOnlyList<FileSelection> snapshot;
        lock (_selectionLock)
        {
            EnsureFileSelectionSize(_metadata.Info.Files.Count);
            for (int i = 0; i < _fileSelection.Count; i++)
            {
                if (IsPaddingIndex(i))
                {
                    _fileSelection[i] = new FileSelection { Selected = false, Priority = Priority.DoNotDownload };
                }
                else
                {
                    _fileSelection[i] = new FileSelection
                    {
                        Priority = priority,
                        Selected = priority != Priority.DoNotDownload
                    };
                }
            }
            _fileSelectionSnapshot = null; // Invalidate cache
            RecalculateSelectionStats();
            snapshot = _fileSelection.ToList().AsReadOnly();
        }
        if (_observer != null)
        {
            await _observer.OnSelectionChangedAsync(snapshot, ct).ConfigureAwait(false);
        }
    }

    public void SetBytesProvider(IUnfinishedBytesProvider provider)
    {
        _bytesProvider = provider;
    }

    public async Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken ct = default)
    {
        IReadOnlyList<FileSelection>? snapshot = null;
        lock (_selectionLock)
        {
            EnsureFileSelectionSize(fileIndex + 1);
            if (fileIndex >= 0 && fileIndex < _fileSelection.Count)
            {
                if (IsPaddingIndex(fileIndex))
                {
                    _fileSelection[fileIndex] = new FileSelection { Selected = false, Priority = Priority.DoNotDownload };
                }
                else
                {
                    _fileSelection[fileIndex] = new FileSelection
                    {
                        Priority = priority,
                        Selected = priority != Priority.DoNotDownload
                    };
                }
                _fileSelectionSnapshot = null; // Invalidate cache
                RecalculateSelectionStats();
                snapshot = _fileSelection.ToList().AsReadOnly();
            }
        }
        if (snapshot != null && _observer != null)
        {
            await _observer.OnSelectionChangedAsync(snapshot, ct).ConfigureAwait(false);
        }
    }

    public async Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken ct = default)
    {
        IReadOnlyList<FileSelection>? snapshot = null;
        lock (_selectionLock)
        {
            EnsureFileSelectionSize(fileIndex + 1);
            if (fileIndex >= 0 && fileIndex < _fileSelection.Count)
            {
                if (IsPaddingIndex(fileIndex))
                {
                    _fileSelection[fileIndex] = new FileSelection { Selected = false, Priority = Priority.DoNotDownload };
                }
                else
                {
                    _fileSelection[fileIndex] = selection;
                }
                _fileSelectionSnapshot = null; // Invalidate cache
                RecalculateSelectionStats();
                snapshot = _fileSelection.ToList().AsReadOnly();
            }
        }
        if (snapshot != null && _observer != null)
        {
            await _observer.OnSelectionChangedAsync(snapshot, ct).ConfigureAwait(false);
        }
    }

    public void SetObserver(IFileSelectionObserver observer)
    {
        _observer = observer;
    }

    private void EnsureFileSelectionSize(int minSize)
    {
        if (_fileSelection.Count < minSize)
        {
            _fileSelectionSnapshot = null; // Invalidate cache
            while (_fileSelection.Count < minSize)
            {
                int index = _fileSelection.Count;
                _fileSelection.Add(GetDefaultSelection(index));
            }
        }
    }

    private void InitializeDefaultFileSelection()
    {
        _fileSelection = [];
        for (int i = 0; i < _metadata.Info.Files.Count; i++)
        {
            _fileSelection.Add(GetDefaultSelection(i));
        }
    }

    private bool IsPaddingIndex(int index)
    {
        return index >= 0
            && index < _metadata.Info.Files.Count
            && _metadata.Info.Files[index].IsPadding;
    }

    private FileSelection GetDefaultSelection(int index)
    {
        if (IsPaddingIndex(index))
        {
            return new FileSelection { Selected = false, Priority = Priority.DoNotDownload };
        }

        return new FileSelection { Selected = true, Priority = Priority.Normal };
    }

    private void RecalculateSelectionStats()
    {
        // Must be called inside _selectionLock
        if (_pieces == null)
        {
            return;
        }

        long generationBefore = _pieces.Generation;
        int total = 0;
        int received = 0;
        for (int i = 0; i < _pieces.Count; i++)
        {
            if (_metadata.Info.IsPieceNeeded(i, _fileSelection))
            {
                total++;
                if (_pieces.HasPiece(i))
                {
                    received++;
                }
            }
        }

        long generationAfter = _pieces.Generation;
        TotalSelectedPieces = total;
        ReceivedSelectedPieces = received;

        // A concurrent update can make this scan a transient mixture. Do not spin until a large,
        // active torrent happens to pause; leave it marked stale so the queued piece notification or
        // the next read recalculates it.
        _piecesGenerationAtLastSync = generationBefore == generationAfter ? generationAfter : -1;
    }

    /// <summary>
    /// Rebuilds the counters when the piece map has moved behind their back.
    /// </summary>
    /// <remarks>
    /// Must be called inside <c>_selectionLock</c>. This is the backstop for any path that adds
    /// pieces without announcing them - a recheck today, and whatever is written next. Comparing one
    /// generation keeps the ordinary case free and still notices an equal-count bitfield replacement.
    /// </remarks>
    private void EnsureStatsFresh()
    {
        if (_pieces != null && _pieces.Generation != _piecesGenerationAtLastSync)
        {
            RecalculateSelectionStats();
        }
    }
}
