using PeerSharp.Internals.Utilities;
using System.Net;

namespace PeerSharp.Internals.Framework;

/// <summary>
/// Service for resolving IP addresses to country codes.
/// </summary>
internal interface IGeoIpService
{
    /// <summary>
    /// Gets or sets whether GeoIP lookups are enabled.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Clears the GeoIP database and disables lookups.
    /// </summary>
    void Clear();

    /// <summary>
    /// Returns the country code for the specified IP address.
    /// </summary>
    /// <param name="ip">The IP address to resolve.</param>
    /// <returns>A country string (e.g. "US", "GB") or an empty string if unknown.</returns>
    string GetCountry(IPAddress ip);

    /// <summary>
    /// Loads the GeoIP database from a stream and enables lookups.
    /// </summary>
    void Load(Stream stream);

    /// <summary>
    /// Loads the GeoIP database from a stream asynchronously and enables lookups.
    /// </summary>
    Task LoadAsync(Stream stream, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of IGeoIpService using the FastIpToCountry utility.
/// </summary>
internal class GeoIpService : IGeoIpService
{
    private FastIpToCountry _fastIpToCountry = new();
    private readonly Lock _loadLock = new();
    private long _loadGeneration;
    private bool _enabled;

    public bool Enabled { get => Volatile.Read(ref _enabled); set => Volatile.Write(ref _enabled, value); }

    public void Clear()
    {
        lock (_loadLock)
        {
            _loadGeneration++;
            Volatile.Write(ref _fastIpToCountry, new FastIpToCountry());
            Enabled = false;
        }
    }

    public string GetCountry(IPAddress ip)
    {
        if (!Enabled)
        {
            return string.Empty;
        }
        return Volatile.Read(ref _fastIpToCountry).GetCountry(ip);
    }

    public void Load(Stream stream)
    {
        long generation;
        lock (_loadLock) generation = ++_loadGeneration;
        var candidate = new FastIpToCountry();
        candidate.Load(stream);
        Publish(candidate, generation);
    }

    public async Task LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long generation;
        lock (_loadLock) generation = ++_loadGeneration;
        var candidate = new FastIpToCountry();
        await candidate.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Publish(candidate, generation);
    }

    private void Publish(FastIpToCountry candidate, long generation)
    {
        lock (_loadLock)
        {
            if (generation != _loadGeneration || !candidate.IsLoaded) return;
            Volatile.Write(ref _fastIpToCountry, candidate);
            Enabled = true;
        }
    }
}
