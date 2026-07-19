namespace PeerSharp.Internals.Framework;

internal class FileSelectionManager : IFileSelectionManager
{
    private readonly TorrentFileMetadata _metadata;
    private readonly Lock _selectionLock = new();
    private IUnfinishedBytesProvider? _bytesProvider;
    private List<FileSelection> _fileSelection = [];
    private IReadOnlyList<FileSelection>? _fileSelectionSnapshot;
    private IFileSelectionObserver? _observer;
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
            if (_metadata.Info.IsPieceNeeded(pieceIndex, _fileSelection))
            {
                ReceivedSelectedPieces++;
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
        TotalSelectedPieces = total;
        ReceivedSelectedPieces = received;
    }
}

