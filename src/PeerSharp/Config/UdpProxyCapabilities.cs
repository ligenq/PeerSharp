namespace PeerSharp.Config;

/// <summary>
/// UDP features permitted by the configured proxy routing. This does not test proxy connectivity
/// or SOCKS5 server support, and does not enable, disable, or modify any settings.
/// </summary>
/// <param name="SupportsDht">Whether DHT traffic can be sent without bypassing the configured proxy.</param>
/// <param name="SupportsUtp">Whether uTP peer traffic is permitted by its proxy setting.</param>
/// <param name="SupportsUdpTrackers">Whether UDP tracker traffic is permitted by its proxy setting.</param>
/// <remarks>
/// DHT and uTP share a listener. An unsupported enabled DHT also prevents that listener starting,
/// even if uTP on its own is allowed. Applications can use this snapshot to offer compatible settings.
/// </remarks>
public readonly record struct UdpProxyCapabilities(bool SupportsDht, bool SupportsUtp, bool SupportsUdpTrackers);
