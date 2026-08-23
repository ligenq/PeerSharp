namespace PeerSharp.Exceptions;

/// <summary>
/// Storage that could not be read or written.
/// </summary>
/// <remarks>
/// A full disk, a file that has failed repeatedly, a path that became unwritable while a torrent was
/// running. <see cref="IsRecoverable"/> is what separates a hiccup worth retrying from a condition
/// that will not clear itself, and the engine stops a torrent rather than looping on the latter.
/// </remarks>
public class StorageException : PeerSharpException
{
    /// <summary>Initializes a new instance with no message.</summary>
    public StorageException()
    {
    }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">A description of the failure.</param>
    public StorageException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and cause.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="innerException">The underlying I/O error.</param>
    public StorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance and records whether the failure can be retried.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="inner">The underlying I/O error, if any.</param>
    /// <param name="isRecoverable">
    /// <see langword="true"/> if retrying may succeed; <see langword="false"/> for terminal
    /// conditions such as a full disk or a file that has failed repeatedly.
    /// </param>
    public StorageException(string message, Exception? inner, bool isRecoverable)
        : base(message, inner!)
    {
        IsRecoverable = isRecoverable;
    }

    /// <summary>
    /// Gets a value indicating whether the operation may succeed if retried. When
    /// <see langword="false"/>, the torrent stops rather than looping on a failure that cannot
    /// clear itself.
    /// </summary>
    public bool IsRecoverable { get; }
}
