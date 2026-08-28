namespace PeerSharp.Exceptions;

/// <summary>
/// A torrent, magnet link, or piece of metadata that could not be read.
/// </summary>
/// <remarks>
/// <para>
/// Raised where bytes that were supposed to describe a torrent do not: a truncated or malformed
/// <c>.torrent</c> file, bencode that does not parse, a magnet URI missing its info hash, metadata
/// fetched from a peer that does not match what was asked for.
/// </para>
/// <para>
/// This is a statement about the data, not about the caller. Passing <see langword="null"/> where a
/// path was required is still an <see cref="ArgumentNullException"/>, because that is a bug in the
/// calling code rather than a bad torrent.
/// </para>
/// </remarks>
public class TorrentMetadataException : PeerSharpException
{
    /// <summary>Initializes a new instance with no message.</summary>
    public TorrentMetadataException()
    {
    }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">What about the metadata could not be read.</param>
    public TorrentMetadataException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and cause.</summary>
    /// <param name="message">What about the metadata could not be read.</param>
    /// <param name="innerException">The parse failure underneath.</param>
    public TorrentMetadataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance naming where the metadata came from.</summary>
    /// <param name="message">What about the metadata could not be read.</param>
    /// <param name="metadataSource">
    /// The file path, magnet URI, or peer the metadata came from, for a caller loading several at
    /// once that would otherwise not know which one failed.
    /// </param>
    public TorrentMetadataException(string message, string? metadataSource)
        : base(message)
    {
        MetadataSource = metadataSource;
    }

    /// <summary>
    /// Where the unreadable metadata came from, when it was known. Named to avoid colliding with
    /// <see cref="Exception.Source"/>, which the runtime uses for the throwing assembly.
    /// </summary>
    public string? MetadataSource { get; }
}
