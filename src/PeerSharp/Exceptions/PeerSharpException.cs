namespace PeerSharp.Exceptions;

/// <summary>
/// The base of every failure this library reports as its own.
/// </summary>
/// <remarks>
/// <para>
/// Catching this catches everything PeerSharp raises deliberately: a malformed torrent, a tracker
/// that refused, storage that could not be written. It deliberately does not catch a mistake in the
/// calling code. <see cref="ArgumentException"/> and its relatives, and
/// <see cref="InvalidOperationException"/> for an operation attempted in the wrong state, are still
/// thrown as themselves, because they say the caller has a bug rather than that the world did
/// something. Cancellation likewise stays <see cref="OperationCanceledException"/>, which is the
/// contract every .NET caller already knows.
/// </para>
/// <para>
/// The distinction is the point of the hierarchy. Before it, telling "the tracker rejected this
/// announce" from "the disk is full" from "this .torrent is malformed" meant matching on message
/// strings, because all three arrived as framework exceptions from three different namespaces.
/// </para>
/// </remarks>
public abstract class PeerSharpException : Exception
{
    /// <summary>Initializes a new instance with no message.</summary>
    protected PeerSharpException()
    {
    }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">A description of the failure.</param>
    protected PeerSharpException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and cause.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="innerException">The failure this one was raised in response to.</param>
    protected PeerSharpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
