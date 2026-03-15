namespace PeerSharp.Internals.Network;

/// <summary>
/// Defines a protocol for automatic port mapping (UPnP, NAT-PMP).
/// </summary>
internal interface IPortMapper
{
    /// <summary>
    /// Gets the name of the port mapping protocol (e.g., "UPnP", "NAT-PMP").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the current status of the port mapper.
    /// </summary>
    IReadOnlyList<PortMappingStatus> GetStatus();

    /// <summary>
    /// Creates a port mapping on all discovered gateways.
    /// </summary>
    /// <param name="port">The port to map.</param>
    /// <param name="protocol">Protocol ("TCP" or "UDP").</param>
    /// <param name="description">Description for the mapping.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> MapPortAsync(int port, string protocol, string description, CancellationToken ct);

    /// <summary>
    /// Starts the discovery process for compatible gateways.
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Removes all port mappings created by this instance.
    /// </summary>
    Task UnmapAllAsync(CancellationToken ct);
}
