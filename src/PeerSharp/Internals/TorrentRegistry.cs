using System.Diagnostics.CodeAnalysis;

namespace PeerSharp.Internals;

/// <summary>
/// Manages the registry of active torrents in the client engine.
/// Provides thread-safe access to adding, removing, and retrieving torrents.
/// </summary>
internal sealed class TorrentRegistry
{
    private readonly Lock _lock = new();
    private readonly List<Torrent> _torrents = new();
    private readonly Dictionary<string, Torrent> _torrentsByHash = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _torrents.Count;
            }
        }
    }

    public void Add(Torrent torrent)
    {
        lock (_lock)
        {
            var hashKey = GetTorrentKey(torrent);
            if (_torrentsByHash.TryGetValue(hashKey, out var existing))
            {
                throw new TorrentAlreadyExistsException(existing);
            }

            _torrents.Add(torrent);
            _torrentsByHash[hashKey] = torrent;
        }
    }

    public bool Contains(InfoHash hash)
    {
        lock (_lock)
        {
            // For Contains/TryGet by a single hash, we need to check both potential key mappings
            // or iterate. Given small number of torrents, we can check by iterating or maintaining 
            // separate lookups.
            return _torrents.Any(t => t.Hash == hash || t.HashV2 == hash);
        }
    }

    public IReadOnlyList<Torrent> GetAll()
    {
        lock (_lock)
        {
            return _torrents.ToList().AsReadOnly();
        }
    }

    public bool Remove(InfoHash hash, [NotNullWhen(true)] out Torrent? torrent)
    {
        lock (_lock)
        {
            torrent = _torrents.FirstOrDefault(t => t.Hash == hash || t.HashV2 == hash);
            if (torrent != null)
            {
                _torrents.Remove(torrent);
                _torrentsByHash.Remove(GetTorrentKey(torrent));
                return true;
            }
            return false;
        }
    }

    public bool TryGet(InfoHash hash, [NotNullWhen(true)] out Torrent? torrent)
    {
        lock (_lock)
        {
            torrent = _torrents.FirstOrDefault(t => t.Hash == hash || t.HashV2 == hash);
            return torrent != null;
        }
    }

    private static string GetTorrentKey(Torrent torrent)
    {
        return $"{torrent.Hash.ToHexStringUpper()}_{torrent.HashV2.ToHexStringUpper()}";
    }
}
