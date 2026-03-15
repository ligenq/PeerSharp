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

    public bool Enabled { get; set; }

    public void Clear()
    {
        _fastIpToCountry = new();
        Enabled = false;
    }

    public string GetCountry(IPAddress ip)
    {
        if (!Enabled)
        {
            return string.Empty;
        }
        return _fastIpToCountry.GetCountry(ip);
    }

    public void Load(Stream stream)
    {
        _fastIpToCountry.Load(stream);
        Enabled = true;
    }

    public async Task LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await _fastIpToCountry.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        Enabled = true;
    }
}
