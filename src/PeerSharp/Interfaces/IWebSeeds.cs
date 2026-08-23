namespace PeerSharp.Interfaces;

/// <summary>
/// The BEP 19 web seeds a torrent may pull from, as opposed to its peers.
/// </summary>
/// <remarks>
/// A torrent's own metadata usually names these, and those are used automatically. This exists for
/// the ones it does not: a mirror the publisher set up later, or a local HTTP copy of the content
/// that a machine on the same network can serve far faster than the swarm will.
/// </remarks>
public interface IWebSeeds
{
    /// <summary>
    /// Adds a web seed URL for this torrent.
    /// </summary>
    /// <param name="url">An absolute <c>http</c>, <c>https</c> or <c>ftp</c> URL.</param>
    /// <returns>
    /// <see langword="true"/> if it was added; <see langword="false"/> if the torrent already had it,
    /// or the URL is blank, malformed, or not one of those schemes.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Takes effect immediately on a running torrent, and is remembered for the next start either
    /// way. Web seeds still need <see cref="Config.ConnectionSettings.EnableWebSeeds"/>.
    /// </para>
    /// <para>
    /// Unusable input is reported rather than thrown, because a web seed list is usually assembled
    /// from somewhere the caller does not control - a config file, a paste box, a mirror list - and
    /// one bad entry among many is a thing to skip, not a bug in the calling code.
    /// </para>
    /// </remarks>
    bool Add(string url);

    /// <summary>
    /// Removes a web seed URL, including one the torrent's own metadata declared.
    /// </summary>
    /// <param name="url">The URL to stop using.</param>
    /// <returns>
    /// <see langword="true"/> if it was in use; <see langword="false"/> if it was not, including when
    /// the URL is blank or malformed.
    /// </returns>
    /// <remarks>Downloads already in flight against it are left to finish.</remarks>
    bool Remove(string url);

    /// <summary>
    /// The URLs this torrent will use: those from its metadata, plus any added here, minus any
    /// removed.
    /// </summary>
    IReadOnlyList<string> GetAll();
}
