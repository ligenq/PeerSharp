using Microsoft.Extensions.Logging;
using PeerSharp.Interfaces;

namespace PeerSharp.WebTorrent;

/// <summary>
/// Extension methods that wire WebTorrent peer discovery into a torrent.
/// </summary>
public static class WebTorrentExtensions
{
    /// <summary>
    /// Enables WebTorrent peer discovery for this torrent. The transport is started and stopped
    /// alongside the torrent's own lifecycle. Call before <see cref="ITorrent.StartAsync"/>;
    /// transports registered after the torrent is already started will not be started until the
    /// next start cycle.
    /// </summary>
    /// <param name="torrent">The torrent to attach the WebTorrent transport to.</param>
    /// <param name="options">Optional WebTorrent session options. When null, defaults are used.</param>
    /// <param name="loggerFactory">Optional logger factory used by the WebTorrent session.</param>
    /// <returns>The torrent, for fluent chaining.</returns>
    public static ITorrent UseWebTorrent(
        this ITorrent torrent,
        WebTorrentSessionOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        torrent.RegisterPeerTransport(new WebTorrentTransport(torrent, options, loggerFactory));
        return torrent;
    }
}
