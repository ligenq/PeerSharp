using System.Net;

namespace PeerSharp.Core;

/// <summary>
/// Provides detailed information about a connected peer.
/// </summary>
/// <param name="EndPoint">The peer's endpoint (IP and port).</param>
/// <param name="Country">The peer's country name (if GeoIP is enabled).</param>
/// <param name="ClientName">The name and version of the peer's BitTorrent client.</param>
/// <param name="DownloadSpeed">The current download speed from this peer in bytes per second.</param>
/// <param name="UploadSpeed">The current upload speed to this peer in bytes per second.</param>
/// <param name="Downloaded">The total bytes downloaded from this peer.</param>
/// <param name="Uploaded">The total bytes uploaded to this peer.</param>
/// <param name="AmChoking">Whether the local client is choking the peer.</param>
/// <param name="AmInterested">Whether the local client is interested in the peer.</param>
/// <param name="PeerChoking">Whether the peer is choking the local client.</param>
/// <param name="PeerInterested">Whether the peer is interested in the local client.</param>
/// <param name="IsUtp">Whether the connection is using uTP (UDP) or TCP.</param>
/// <param name="IsEncrypted">Whether the connection is encrypted.</param>
/// <param name="Progress">The peer's download progress (0.0 to 1.0).</param>
/// <param name="RttMs">The estimated round-trip time (RTT) to the peer in milliseconds.</param>
public sealed record PeerInfo(
    IPEndPoint EndPoint,
    string Country = "",
    string ClientName = "Unknown",
    int DownloadSpeed = 0,
    int UploadSpeed = 0,
    long Downloaded = 0,
    long Uploaded = 0,
    bool AmChoking = false,
    bool AmInterested = false,
    bool PeerChoking = false,
    bool PeerInterested = false,
    bool IsUtp = false,
    bool IsEncrypted = false,
    float Progress = 0,
    int RttMs = 0);

