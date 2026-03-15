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
}
