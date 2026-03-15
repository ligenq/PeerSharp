namespace PeerSharp.Internals.Framework;

/// <summary>
/// Observer for file selection changes.
/// </summary>
internal interface IFileSelectionObserver
{
    /// <summary>
    /// Called when file selection changes.
    /// </summary>
    Task OnSelectionChangedAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default);
}

/// <summary>
/// Provides unfinished bytes information for progress calculations.
/// </summary>
internal interface IUnfinishedBytesProvider
{
    /// <summary>
    /// Gets the number of unfinished bytes for a specific file index.
    /// </summary>
    long GetUnfinishedBytesForFile(int fileIndex);

    /// <summary>
    /// Gets the number of unfinished bytes for selected files.
    /// </summary>
    long GetUnfinishedSelectedBytes(IReadOnlyList<FileSelection>? selection);
}

internal interface IFileSelectionManager
{
    /// <summary>
    /// Checks if all selected files have been downloaded.
    /// </summary>
    bool IsSelectionFinished { get; }

    /// <summary>
    /// Gets the number of selected pieces that have been fully received.
    /// </summary>
    int ReceivedSelectedPieces { get; }

    /// <summary>
    /// Gets the total number of pieces selected for download.
    /// </summary>
    int TotalSelectedPieces { get; }

    /// <summary>
    /// Calculates the total bytes downloaded for selected files.
    /// </summary>
    ulong CalculateFinishedSelectedBytes();

    /// <summary>
    /// Calculates the progress of selected files (0.0 to 1.0).
    /// </summary>
    float CalculateSelectionProgress();

    /// <summary>
    /// Gets a read-only snapshot of all file selections.
    /// </summary>
    IReadOnlyList<FileSelection> GetAllFileSelections();

    /// <summary>
    /// Gets the current file selection for a specific file index.
    /// </summary>
    FileSelection GetFileSelection(int fileIndex);

    /// <summary>
    /// Initializes the selection manager with default or saved state.
    /// </summary>
    void Initialize(List<FileSelection>? savedSelection, PiecesProgress pieces);

    /// <summary>
    /// Called when a piece is verified to update internal stats.
    /// </summary>
    void OnPieceVerified(int pieceIndex);

    /// <summary>
    /// Sets the priority for all files.
    /// </summary>
    Task SetAllFilesPriorityAsync(Priority priority, CancellationToken ct = default);

    /// <summary>
    /// Sets the unfinished bytes provider for progress calculations.
    /// </summary>
    void SetBytesProvider(IUnfinishedBytesProvider provider);

    /// <summary>
    /// Sets the priority for a specific file.
    /// </summary>
    Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken ct = default);

    /// <summary>
    /// Sets the selection for a specific file.
    /// </summary>
    Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken ct = default);

    /// <summary>
    /// Sets the observer for selection changes.
    /// </summary>
    void SetObserver(IFileSelectionObserver observer);
}

