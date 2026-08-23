namespace PeerSharp.Exceptions;

/// <summary>
/// A UDP tracker that answered with a protocol error, or did not answer at all.
/// </summary>
/// <remarks>
/// BEP 15's exchange has its own failure modes - an expired connection id, a transaction id that
/// does not match, a reply too short to be one - which is why it has a type of its own rather than
/// sharing <see cref="TrackerException"/> with the HTTP announce.
/// </remarks>
public class UdpTrackerException : TrackerException
{
    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="isTransient">
    /// <see langword="true"/> for failures worth retrying, such as a timeout or an expired
    /// connection id; <see langword="false"/> for protocol errors that will recur.
    /// </param>
    public UdpTrackerException(string message, bool isTransient = false)
        : base(message, isTransient)
    {
    }

    /// <summary>Initializes a new instance with the specified message and cause.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="inner">The underlying error.</param>
    /// <param name="isTransient">
    /// <see langword="true"/> for failures worth retrying, such as a timeout or an expired
    /// connection id; <see langword="false"/> for protocol errors that will recur.
    /// </param>
    public UdpTrackerException(string message, Exception inner, bool isTransient = false)
        : base(message, inner, isTransient)
    {
    }
}
