namespace PeerSharp.Core;

/// <summary>
/// Progress information for piece checking.
/// </summary>
public sealed class PieceCheckProgress
{
    /// <summary>
    /// Gets the number of pieces that have been checked so far.
    /// </summary>
    public int CheckedPieces { get; init; }

    /// <summary>
    /// Gets the index of the piece currently being checked.
    /// </summary>
    public int CurrentPiece { get; init; }

    /// <summary>
    /// Gets the overall checking progress (0.0 to 1.0).
    /// </summary>
    public float Progress { get; init; }

    /// <summary>
    /// Gets the total number of pieces to be checked.
    /// </summary>
    public int TotalPieces { get; init; }

    /// <summary>
    /// Gets the number of pieces found to be valid (matching their hash).
    /// </summary>
    public int ValidPieces { get; init; }
}

