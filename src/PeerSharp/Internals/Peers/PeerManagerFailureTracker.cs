namespace PeerSharp.Internals.Peers;

/// <summary>Tracks internal peer-manager failures and rate-limits their escalation.</summary>
internal sealed class PeerManagerFailureTracker
{
    private const int EscalationThreshold = 3;
    private static readonly TimeSpan EscalationWindow = TimeSpan.FromMinutes(1);
    private readonly Queue<DateTimeOffset> _recentFailures = new();
    private readonly Lock _sync = new();
    private DateTimeOffset _lastEscalation;
    private int _totalFailures;

    public int TotalFailures => Volatile.Read(ref _totalFailures);

    public FailureRecord Record(DateTimeOffset now)
    {
        Interlocked.Increment(ref _totalFailures);

        lock (_sync)
        {
            _recentFailures.Enqueue(now);
            DateTimeOffset cutoff = now - EscalationWindow;
            while (_recentFailures.TryPeek(out DateTimeOffset failureAt) && failureAt < cutoff)
            {
                _recentFailures.Dequeue();
            }

            if (_recentFailures.Count < EscalationThreshold || now - _lastEscalation < EscalationWindow)
            {
                return new FailureRecord(false, _recentFailures.Count);
            }

            _lastEscalation = now;
            return new FailureRecord(true, _recentFailures.Count);
        }
    }

    internal readonly record struct FailureRecord(bool ShouldEscalate, int RecentCount);
}
