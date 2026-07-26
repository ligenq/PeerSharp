namespace PeerSharp.Internals.Trackers;

/// <summary>
/// Thrown when a tracker answers with a <c>failure reason</c> (BEP 3) instead of a result. Carries
/// the optional BEP 31 <c>retry in</c> hint so the scheduling layer can honour it rather than
/// falling back to its own guess.
///
/// This replaces a plain <see cref="InvalidDataException"/>, which is sealed and so cannot carry the
/// hint. It never leaves <see cref="HttpTracker"/> - each caller catches it and reports the failure
/// through <see cref="ITrackerCallback"/> - so the change of base type is not observable.
/// </summary>
internal sealed class TrackerFailureException : IOException
{
    public TrackerFailureException(string reason, TrackerRetryHint? retryHint)
        : base($"Tracker returned failure: {reason}")
    {
        RetryHint = retryHint;
    }

    public TrackerRetryHint? RetryHint { get; }
}
