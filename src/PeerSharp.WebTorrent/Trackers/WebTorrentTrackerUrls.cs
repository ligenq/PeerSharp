using PeerSharp.Interfaces;
using PeerSharp.WebTorrent.Configuration;

namespace PeerSharp.WebTorrent.Trackers;

internal static class WebTorrentTrackerUrls
{
    public static bool IsWebSocketTracker(string url)
    {
        return url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> Collect(string? announce, IEnumerable<string>? announceList, IEnumerable<IEnumerable<string>>? announceTiers, IEnumerable<string> additionalTrackers)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (announce != null && IsWebSocketTracker(announce))
        {
            urls.Add(announce);
        }

        if (announceList != null)
        {
            foreach (var url in announceList)
            {
                if (IsWebSocketTracker(url))
                {
                    urls.Add(url);
                }
            }
        }

        if (announceTiers != null)
        {
            foreach (var tier in announceTiers)
            {
                foreach (var url in tier)
                {
                    if (IsWebSocketTracker(url))
                    {
                        urls.Add(url);
                    }
                }
            }
        }

        foreach (var url in additionalTrackers)
        {
            if (IsWebSocketTracker(url))
            {
                urls.Add(url);
            }
        }

        return urls.ToList();
    }

    public static IReadOnlyList<string> Collect(ITorrent torrent, WebTorrentSessionOptions options)
    {
        return Collect(
            torrent.Trackers.GetTrackers().FirstOrDefault()?.Url,
            torrent.Trackers.GetTrackers().Select(t => t.Url),
            null, // Torrent interface doesn't expose tiers directly in a simple way here
            options.AdditionalTrackers);
    }
}
