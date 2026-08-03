using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;

namespace PeerSharp.Internals;

/// <summary>
/// Holds references to the engine-level network managers assigned to this torrent.
/// </summary>
internal sealed class TorrentNetworkManager
{
    public IpBlocklist? Blocklist { get; set; }
    public IDhtManager? Dht { get; set; }
    public ILsdManager? Lsd { get; set; }
    public IUtpManager? Utp { get; set; }

    /// <summary>
    /// The listener we actually bound, so peers can be told where to reach us. The configured port may
    /// be zero, meaning "any", in which case only the listener knows the real answer.
    /// </summary>
    public IPortListener? PortListener { get; set; }
}
