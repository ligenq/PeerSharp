using PeerSharp.Internals;
using PeerSharp.Internals.Peers;

namespace PeerSharp.PiecePicking;

/// <summary>
/// Interface that abstracts peer piece availability for PiecePicker.
/// </summary>
internal interface IPeerPieceInfo
{
    /// <summary>
    /// Gets the total number of pieces in the peer's bitfield.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets whether the peer is choking us.
    /// </summary>
    bool IsChoking { get; }

    /// <summary>
    /// Gets suggested pieces from the peer.
    /// </summary>
    IEnumerable<int> GetSuggestedPieces();

    /// <summary>
    /// Checks if the peer has a specific piece.
    /// </summary>
    bool HasPiece(int pieceIndex);

    /// <summary>
    /// Checks if a piece is allowed fast (can be requested even when choked).
    /// </summary>
    bool IsAllowedFast(int pieceIndex);
}

/// <summary>
/// Interface that abstracts the context needed by PieceChecker.
/// This enables unit testing PieceChecker without needing a full Torrent instance.
/// </summary>
internal interface IPieceCheckerContext
{
    long FullSize { get; }
    bool IsMerkle { get; }
    int PieceCount { get; }
    long PieceSize { get; }
    string TorrentName { get; }

    void AddPiece(int pieceIndex);

    byte[]? GetExpectedHash(int pieceIndex);

    void UpdatePiecesFromBitfield(byte[] bitfield);

    bool VerifyPiece(int pieceIndex, byte[] pieceData);
}

/// <summary>
/// Interface that abstracts the context needed by PiecePicker.
/// This enables unit testing PiecePicker without needing a full Torrent instance.
/// </summary>
internal interface IPiecePickerContext
{
    /// <summary>
    /// Gets the current download strategy (Streaming, Sequential, or RarestFirst).
    /// </summary>
    DownloadStrategy DownloadStrategy { get; }

    /// <summary>
    /// Gets the total number of pieces in the torrent.
    /// </summary>
    int PieceCount { get; }

    /// <summary>
    /// Gets priority pieces for streaming mode.
    /// </summary>
    IReadOnlyList<int>? StreamingPriorityPieces { get; }

    /// <summary>
    /// Gets a snapshot of current file selection.
    /// </summary>
    IReadOnlyList<FileSelection>? GetFileSelectionSnapshot();

    /// <summary>
    /// Gets the priority of a piece based on file selection.
    /// </summary>
    Priority GetPiecePriority(int pieceIndex, IReadOnlyList<FileSelection>? selection);

    /// <summary>
    /// Checks if we already have a specific piece.
    /// </summary>
    bool HasPiece(int pieceIndex);

    /// <summary>
    /// Checks if a piece is currently being downloaded by any peer.
    /// </summary>
    bool IsPieceActive(int pieceIndex);

    /// <summary>
    /// Checks if a piece is needed based on file selection.
    /// </summary>
    bool IsPieceNeeded(int pieceIndex, IReadOnlyList<FileSelection>? selection);
}

/// <summary>
/// Adapter that wraps a Torrent to provide IPieceCheckerContext.
/// </summary>
internal class TorrentPieceCheckerContext : IPieceCheckerContext
{
    private readonly Torrent _torrent;

    public TorrentPieceCheckerContext(Torrent torrent)
    {
        _torrent = torrent;
    }

    public long FullSize => _torrent.InfoFile.Info.FullSize;
    public bool IsMerkle => _torrent.InfoFile.Info.IsMerkle && _torrent.MerkleTree != null;
    public int PieceCount => _torrent.Pieces.Count;
    public long PieceSize => _torrent.InfoFile.Info.PieceSize;
    public string TorrentName => _torrent.Name;

    public void AddPiece(int pieceIndex)
    {
        _torrent.Pieces.AddPiece(pieceIndex);
    }

    public byte[]? GetExpectedHash(int pieceIndex)
    {
        if (_torrent.InfoFile.Info.Pieces.Count > pieceIndex)
        {
            return _torrent.InfoFile.Info.Pieces[pieceIndex];
        }
        return null;
    }

    public void UpdatePiecesFromBitfield(byte[] bitfield)
    {
        _torrent.Pieces.FromBitfield(bitfield);
    }

    public bool VerifyPiece(int pieceIndex, byte[] pieceData)
    {
        return _torrent.MerkleTree?.VerifyPiece(pieceIndex, pieceData) ?? false;
    }
}

/// <summary>
/// Adapter that wraps a Torrent to provide IPiecePickerContext.
/// </summary>
internal class TorrentPiecePickerContext : IPiecePickerContext
{
    private readonly Torrent _torrent;

    public TorrentPiecePickerContext(Torrent torrent)
    {
        _torrent = torrent;
    }

    public DownloadStrategy DownloadStrategy => _torrent.DownloadStrategy;
    public int PieceCount => _torrent.Pieces.Count;

    public IReadOnlyList<int>? StreamingPriorityPieces => _torrent.StreamingPriorityPieces;

    public IReadOnlyList<FileSelection>? GetFileSelectionSnapshot()
    {
        return _torrent.GetFileSelectionSnapshot();
    }

    public Priority GetPiecePriority(int pieceIndex, IReadOnlyList<FileSelection>? selection)
    {
        return _torrent.InfoFile.Info.GetPiecePriority(pieceIndex, selection);
    }

    public bool HasPiece(int pieceIndex)
    {
        return _torrent.Pieces.HasPiece(pieceIndex);
    }

    public bool IsPieceActive(int pieceIndex)
    {
        return _torrent.FileTransferInternal.IsPieceActive(pieceIndex);
    }

    public bool IsPieceNeeded(int pieceIndex, IReadOnlyList<FileSelection>? selection)
    {
        return _torrent.InfoFile.Info.IsPieceNeeded(pieceIndex, selection);
    }
}

/// <summary>
/// Adapter that wraps a PeerCommunication to provide IPeerPieceInfo.
/// </summary>
internal class PeerCommunicationAdapter : IPeerPieceInfo
{
    private readonly PeerCommunication _peer;

    public PeerCommunicationAdapter(PeerCommunication peer)
    {
        _peer = peer;
    }

    public int Count => _peer.PeerPieces.Count;

    public bool IsChoking => _peer.PeerChoking;

    public IEnumerable<int> GetSuggestedPieces()
    {
        return _peer.GetSuggestedPieces();
    }

    public bool HasPiece(int pieceIndex)
    {
        return _peer.PeerPieces.HasPiece(pieceIndex);
    }

    public bool IsAllowedFast(int pieceIndex)
    {
        return _peer.IsAllowedFast(pieceIndex);
    }
}
