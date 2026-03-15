namespace PeerSharp.Internals.Framework;

/// <summary>
/// Service for resolving a torrent by its info hash.
/// </summary>
internal interface ITorrentResolver
{
    ITorrent? GetTorrent(InfoHash hash);

    IReadOnlyList<ITorrent> GetTorrents();
}
