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
/// <param name="Progress">
/// The peer's download progress (0.0 to 1.0). Only meaningful when <see cref="PeerInfo.HasReportedPieces"/>
/// is true; a peer that has not said what it holds also reports zero.
/// </param>
/// <param name="RttMs">The estimated round-trip time (RTT) to the peer in milliseconds.</param>
public sealed record PeerInfo(
    IPEndPoint EndPoint,
    string Country = "",
    string ClientName = "Unknown",
    long DownloadSpeed = 0,
    long UploadSpeed = 0,
    long Downloaded = 0,
    long Uploaded = 0,
    bool AmChoking = false,
    bool AmInterested = false,
    bool PeerChoking = false,
    bool PeerInterested = false,
    bool IsUtp = false,
    bool IsEncrypted = false,
    float Progress = 0,
    int RttMs = 0)
{
    /// <summary>
    /// Whether the peer has told us which pieces it holds, by bitfield, have, have-all or have-none.
    ///
    /// <para>
    /// Read this before drawing a conclusion from <see cref="Progress"/>. A peer that has said nothing
    /// and a peer that holds nothing both report zero, and they mean opposite things: the second wants
    /// everything we have, the first tells us nothing at all. Most connections on a busy swarm end
    /// before the peer gets round to saying anything.
    /// </para>
    ///
    /// <para>
    /// An init-only property rather than a positional parameter, so that adding it does not change the
    /// primary constructor or <c>Deconstruct</c> and break already-compiled consumers.
    /// </para>
    /// </summary>
    public bool HasReportedPieces { get; init; }
}

