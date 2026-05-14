namespace PeerSharp.Internals.Peers;

internal readonly struct SeedingRankEntry
{
    public required int UploadSpeed { get; init; }
    public required bool IsChoked { get; init; }
    public required long UploadedSinceUnchoked { get; init; }
    public required DateTimeOffset LastUnchokedAt { get; init; }
}

/// <summary>
/// Round-robin seeding choker.
/// Peers that have exceeded their piece quota and been unchoked for at least one minute
/// are de-prioritized so that waiting peers can rotate in.
/// </summary>
internal static class SeedingChoker
{
    internal const int DefaultPieceQuota = 20;
    private const int QuotaMinUnchokeSeconds = 60;

    /// <summary>
    /// Returns a negative value when <paramref name="x"/> should be unchoked before
    /// <paramref name="y"/>, positive when <paramref name="y"/> should come first.
    /// </summary>
    internal static int Compare(
        SeedingRankEntry x,
        SeedingRankEntry y,
        long pieceLength,
        int pieceQuota,
        DateTimeOffset now)
    {
        bool xDone = IsQuotaComplete(x, pieceLength, pieceQuota, now);
        bool yDone = IsQuotaComplete(y, pieceLength, pieceQuota, now);

        // De-prioritize quota-complete peers so waiting peers can rotate in.
        // false (0) < true (1), so not-done sorts before done.
        if (xDone != yDone)
        {
            return xDone.CompareTo(yDone);
        }

        // Higher upload rate first. Choked peers are treated as rate=0 to prevent
        // residual in-flight bytes from inflating the rank of a just-choked peer.
        int xRate = x.IsChoked ? 0 : x.UploadSpeed;
        int yRate = y.IsChoked ? 0 : y.UploadSpeed;
        if (xRate != yRate)
        {
            return yRate.CompareTo(xRate); // descending
        }

        // Longest-waiting peer wins (fairness tiebreak).
        return x.LastUnchokedAt.CompareTo(y.LastUnchokedAt);
    }

    internal static SeedingRankEntry FromPeer(PeerCommunication peer) => new()
    {
        UploadSpeed = peer.UploadSpeed,
        IsChoked = peer.AmChoking,
        UploadedSinceUnchoked = peer.UploadedSinceUnchoked,
        LastUnchokedAt = peer.LastUnchokedAt,
    };

    private static bool IsQuotaComplete(SeedingRankEntry e, long pieceLength, int pieceQuota, DateTimeOffset now)
    {
        return !e.IsChoked
            && e.UploadedSinceUnchoked > pieceLength * pieceQuota
            && (now - e.LastUnchokedAt).TotalSeconds >= QuotaMinUnchokeSeconds;
    }
}
