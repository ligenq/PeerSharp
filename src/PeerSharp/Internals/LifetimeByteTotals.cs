namespace PeerSharp.Internals;

/// <summary>
/// The engine's monotonic byte counters: what every torrent it has ever held has moved.
///
/// <para>
/// This exists as its own type because the property it guarantees is not a property of either of its
/// two operations - it is a property of how they interleave. Bytes live in two places: the torrents
/// still registered, and a running total for those that have gone. A reader that samples one before
/// the other, or a removal that updates one before the other, can observe a torrent's bytes in
/// neither - and the total dips. A counter that dips is read by a metrics backend as a process
/// restart, so it produces a spurious jump rather than the gap it looks like.
/// </para>
///
/// <para>
/// Both operations therefore take the same lock: a read sees the live set and the retired total as
/// they were at one instant, and a retirement moves a torrent from one to the other with no instant
/// in between. That is the whole reason for the class, and why the lock is not merely
/// <see cref="Interlocked"/> arithmetic on the two fields - atomic increments would still leave the
/// window between the removal and the increment observable.
/// </para>
/// </summary>
internal sealed class LifetimeByteTotals
{
    private readonly Func<IEnumerable<(long Downloaded, long Uploaded)>> _liveTotals;

    private readonly Lock _lock = new();
    private long _retiredDownloaded;
    private long _retiredUploaded;

    /// <param name="liveTotals">
    /// The totals of the torrents currently registered. Called under the lock, so it must not take a
    /// lock that anything else takes while holding this one.
    /// </param>
    public LifetimeByteTotals(Func<IEnumerable<(long Downloaded, long Uploaded)>> liveTotals)
    {
        ArgumentNullException.ThrowIfNull(liveTotals);
        _liveTotals = liveTotals;
    }

    /// <summary>Bytes moved over the engine's whole life. Never decreases between calls.</summary>
    public (long Downloaded, long Uploaded) Read()
    {
        lock (_lock)
        {
            long downloaded = _retiredDownloaded;
            long uploaded = _retiredUploaded;

            foreach (var (live, up) in _liveTotals())
            {
                downloaded += live;
                uploaded += up;
            }

            return (downloaded, uploaded);
        }
    }

    /// <summary>
    /// Removes a torrent and folds its totals into the retired figures as a single step.
    /// </summary>
    /// <param name="remove">
    /// Takes the torrent out of the live set, returning whether it was there to take. Its return
    /// value is what makes this exactly-once under concurrent removals: a second caller sees
    /// <see langword="false"/> and adds nothing, so bytes cannot be counted twice.
    /// </param>
    /// <param name="downloaded">The departing torrent's downloaded total.</param>
    /// <param name="uploaded">The departing torrent's uploaded total.</param>
    /// <returns>Whatever <paramref name="remove"/> returned.</returns>
    public bool RemoveAndRetire(Func<bool> remove, long downloaded, long uploaded)
    {
        ArgumentNullException.ThrowIfNull(remove);

        lock (_lock)
        {
            if (!remove())
            {
                return false;
            }

            _retiredDownloaded += downloaded;
            _retiredUploaded += uploaded;
            return true;
        }
    }
}
