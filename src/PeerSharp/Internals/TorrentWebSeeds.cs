using PeerSharp.Interfaces;

namespace PeerSharp.Internals;

/// <summary>
/// The web seed list a caller can change, layered over the one the torrent's metadata declares.
/// </summary>
/// <remarks>
/// The effective list lives in the torrent's configuration rather than in <c>WebSeedManager</c>,
/// because that manager only exists while the torrent is running and is rebuilt when a magnet's
/// metadata arrives. A caller that adds a mirror to a stopped torrent expects it to still be there
/// when it starts.
/// </remarks>
internal sealed class TorrentWebSeeds(Torrent torrent) : IWebSeeds
{
    private readonly Lock _lock = new();
    private readonly Torrent _torrent = torrent;

    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    public bool Add(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!IsSupportedUrl(url))
        {
            return false;
        }

        lock (_lock)
        {
            if (EffectiveListLocked().Any(u => u.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _removed.Remove(url);
            _added.Add(url);
        }

        // Running torrents get it now; stopped ones pick it up from the configuration at start.
        _torrent.WebSeedManager?.AddSource(url);
        return true;
    }

    public bool Remove(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        lock (_lock)
        {
            if (!EffectiveListLocked().Any(u => u.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _added.Remove(url);
            _removed.Add(url);
        }

        _torrent.WebSeedManager?.RemoveSource(url);
        return true;
    }

    public IReadOnlyList<string> GetAll()
    {
        lock (_lock)
        {
            return [.. EffectiveListLocked()];
        }
    }

    private List<string> EffectiveListLocked()
    {
        var result = new List<string>();
        foreach (string url in _torrent.InfoFile.WebSeedUrls.Concat(_added))
        {
            if (IsSupportedUrl(url) &&
                !_removed.Contains(url) &&
                !result.Any(existing => existing.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(url);
            }
        }

        return result;
    }

    private static bool IsSupportedUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp
                || uri.Scheme == Uri.UriSchemeHttps
                || uri.Scheme == Uri.UriSchemeFtp);
}
