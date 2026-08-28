namespace PeerSharp.Exceptions;

/// <summary>
/// A tracker that could not be reached, or that refused what it was asked.
/// </summary>
/// <remarks>
/// A swarm usually has more than one tracker and the engine keeps working without any of them, so
/// this is reported rather than fatal. <see cref="IsTransient"/> is the part worth acting on: a
/// timeout or an expired connection id is worth another attempt, a rejected info hash is not.
/// </remarks>
public class TrackerException : PeerSharpException
{
    /// <summary>Initializes a new instance with no message.</summary>
    public TrackerException()
    {
    }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">What the tracker said, or what went wrong reaching it.</param>
    public TrackerException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and cause.</summary>
    /// <param name="message">What the tracker said, or what went wrong reaching it.</param>
    /// <param name="innerException">The failure underneath.</param>
    public TrackerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance stating whether another attempt is worth making.</summary>
    /// <param name="message">What the tracker said, or what went wrong reaching it.</param>
    /// <param name="isTransient">Whether retrying could succeed.</param>
    /// <param name="trackerUrl">The tracker this concerns, where it is known.</param>
    public TrackerException(string message, bool isTransient, string? trackerUrl = null)
        : base(message)
    {
        IsTransient = isTransient;
        TrackerUrl = trackerUrl;
    }

    /// <summary>Initializes a new instance with a cause and whether retrying is worthwhile.</summary>
    /// <param name="message">What the tracker said, or what went wrong reaching it.</param>
    /// <param name="innerException">The failure underneath.</param>
    /// <param name="isTransient">Whether retrying could succeed.</param>
    /// <param name="trackerUrl">The tracker this concerns, where it is known.</param>
    protected TrackerException(string message, Exception innerException, bool isTransient, string? trackerUrl = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
        TrackerUrl = trackerUrl;
    }

    /// <summary>
    /// Whether another attempt could succeed. False for answers that will not change - a tracker
    /// that does not know this info hash, or one that rejected the request outright.
    /// </summary>
    public bool IsTransient { get; }

    /// <summary>The tracker this failure concerns, where it is known.</summary>
    public string? TrackerUrl { get; }
}
