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

    // Null until a caller adds or removes one, at which point it becomes the authority.
    private List<string>? _urls;

    public bool Add(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp))
        {
            return false;
        }

        lock (_lock)
        {
            var effective = EffectiveListLocked();
            if (effective.Any(u => u.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            effective.Add(url);
        }

        // Running torrents get it now; stopped ones pick it up from the configuration at start.
        _torrent.WebSeedManager?.AddSource(url);
        return true;
    }

    public bool Remove(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        bool removed;
        lock (_lock)
        {
            var effective = EffectiveListLocked();
            int index = effective.FindIndex(u => u.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            effective.RemoveAt(index);
            removed = true;
        }

        _torrent.WebSeedManager?.RemoveSource(url);
        return removed;
    }

    public IReadOnlyList<string> GetAll()
    {
        lock (_lock)
        {
            return [.. EffectiveListLocked()];
        }
    }

    /// <summary>
    /// The caller's list, created from the metadata's on first change. Kept null until then so a
    /// torrent nobody has touched reports exactly what its metadata says, even if that metadata
    /// arrives later.
    /// </summary>
    private List<string> EffectiveListLocked()
    {
        return _urls ??= [.. _torrent.InfoFile.WebSeedUrls];
    }
}
