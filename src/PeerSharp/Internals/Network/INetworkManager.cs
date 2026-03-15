using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utp;

namespace PeerSharp.Internals.Network;

/// <summary>
/// Manages the lifecycle and orchestration of all network services (TCP, UDP, DHT, uTP, UPnP).
/// </summary>
internal interface INetworkManager : IAsyncDisposable
{
    IpBlocklist Blocklist { get; }

    /// <summary>
    /// Gets the actual TCP port the listener is bound to.
    /// </summary>
    int BoundTcpPort { get; }

    /// <summary>
    /// Gets the actual UDP port the listener is bound to.
    /// </summary>
    int BoundUdpPort { get; }

    IDhtManager Dht { get; }
    ILsdManager Lsd { get; }
    IPortListener PortListener { get; }
    IUtpManager Utp { get; }

    /// <summary>
    /// Gets the current status of all port mapping protocols.
    /// </summary>
    IReadOnlyList<PortMappingStatus> GetPortMappingStatus();

    /// <summary>
    /// Starts the network manager asynchronously.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops all network services.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}
