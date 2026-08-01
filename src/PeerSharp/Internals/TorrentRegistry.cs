using System.Diagnostics.CodeAnalysis;

namespace PeerSharp.Internals;

/// <summary>
/// Manages the registry of active torrents in the client engine.
/// Provides thread-safe access to adding, removing, and retrieving torrents.
/// </summary>
internal sealed class TorrentRegistry
{
    private readonly Lock _lock = new();
    private readonly List<Torrent> _torrents = [];
    private readonly Dictionary<string, Torrent> _torrentsByHash = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Torrents the engine added on its own behalf - a metadata fetch - rather than the caller's.
    ///
    /// <para>
    /// They are kept apart from the real ones on purpose. They must be resolvable by hash, because
    /// that is how an inbound connection finds the torrent it is for, but they must not appear in
    /// <see cref="GetAll"/>, must not block a caller from adding that hash for real, and must be
    /// removed by identity so that tearing one down cannot take a real torrent with it.
    /// </para>
    /// </summary>
    private readonly List<Torrent> _transient = [];

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

    /// <summary>
    /// Registers a torrent the engine owns for the duration of one operation. Deliberately not
    /// subject to the duplicate guard: a metadata fetch for a hash the caller already holds is a
    /// legitimate thing to do, and the fetch must not be able to fail a caller's own add either.
    /// </summary>
    public void AddTransient(Torrent torrent)
    {
        lock (_lock)
        {
            _transient.Add(torrent);
        }
    }

    /// <summary>Removes a transient torrent by identity. Returns false if it was already gone.</summary>
    public bool RemoveTransient(Torrent torrent)
    {
        lock (_lock)
        {
            return _transient.Remove(torrent);
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

    /// <summary>
    /// Resolves a hash to a torrent the caller owns. Transient torrents are deliberately invisible
    /// here: this backs the public lookup, and handing back a torrent the caller never added would
    /// let them stop, mutate or remove one.
    /// </summary>
    public bool TryGet(InfoHash hash, [NotNullWhen(true)] out Torrent? torrent)
    {
        lock (_lock)
        {
            torrent = _torrents.FirstOrDefault(t => t.Hash == hash || t.HashV2 == hash);
            return torrent != null;
        }
    }

    /// <summary>
    /// Resolves a hash for the engine's own plumbing - inbound handshakes, DHT peer results, tracker
    /// callbacks - where a metadata fetch in progress does need to receive what arrives for it.
    ///
    /// <para>
    /// Real torrents win. If the caller holds this hash, an inbound peer belongs to their torrent,
    /// not to a fetch that happens to be running for the same one.
    /// </para>
    /// </summary>
    public bool TryGetForRouting(InfoHash hash, [NotNullWhen(true)] out Torrent? torrent)
    {
        lock (_lock)
        {
            torrent = _torrents.FirstOrDefault(t => t.Hash == hash || t.HashV2 == hash)
                ?? _transient.FirstOrDefault(t => t.Hash == hash || t.HashV2 == hash);
            return torrent != null;
        }
    }

    private static string GetTorrentKey(Torrent torrent)
    {
        return $"{torrent.Hash.ToHexStringUpper()}_{torrent.HashV2.ToHexStringUpper()}";
    }
}
