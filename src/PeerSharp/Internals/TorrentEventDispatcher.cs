using Microsoft.Extensions.Logging;

namespace PeerSharp.Internals;

internal static class TorrentEventDispatcher
{
    internal static void Invoke<T>(Action<ITorrent, T>? callbacks, ITorrent torrent, T value, ILogger logger)
    {
        foreach (var callback in Delegate.EnumerateInvocationList(callbacks))
        {
            try { callback(torrent, value); }
            catch (Exception ex) { logger.LogWarning(ex, "Torrent event callback failed for {TorrentName}", torrent.Name); }
        }
    }

    internal static void Invoke(Action<ITorrent>? callbacks, ITorrent torrent, ILogger logger)
    {
        foreach (var callback in Delegate.EnumerateInvocationList(callbacks))
        {
            try { callback(torrent); }
            catch (Exception ex) { logger.LogWarning(ex, "Torrent event callback failed for {TorrentName}", torrent.Name); }
        }
    }
}
