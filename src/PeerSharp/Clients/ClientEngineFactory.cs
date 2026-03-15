using PeerSharp.Internals;

namespace PeerSharp.Clients;

/// <summary>
/// Static factory class for creating BitTorrent client instances.
/// </summary>
public static class ClientEngineFactory
{
    /// <summary>
    /// Creates a new BitTorrent client engine with default settings.
    /// </summary>
    /// <returns>A new client engine instance.</returns>
    public static IClientEngine Create()
    {
        return ClientEngine.Create();
    }

    /// <summary>
    /// Creates a new BitTorrent client engine with the specified options.
    /// </summary>
    /// <param name="options">Configuration options for the client.</param>
    /// <returns>A new client engine instance.</returns>
    public static IClientEngine Create(TorrentClientOptions options)
    {
        return ClientEngine.Create(options);
    }
}
