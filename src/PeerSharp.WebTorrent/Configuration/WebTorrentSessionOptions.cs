namespace PeerSharp.WebTorrent.Configuration;

/// <summary>
/// Options that control WebTorrent peer discovery, signaling, and WebRTC behavior.
/// </summary>
public sealed class WebTorrentSessionOptions
{
    /// <summary>Label advertised on the WebRTC data channel. Defaults to <c>"bittorrent"</c>.</summary>
    public string DataChannelLabel { get; init; } = "bittorrent";

    /// <summary>Number of WebRTC offers to publish to each WebSocket tracker per announce.</summary>
    public int OffersPerTracker { get; init; } = 10;

    /// <summary>Additional WebSocket tracker URLs to announce to. The library does not include public trackers by default.</summary>
    public IReadOnlyList<string> AdditionalTrackers { get; init; } = [];

    /// <summary>Maximum accepted WebSocket tracker message size in bytes.</summary>
    public int MaxTrackerMessageBytes { get; init; } = 256 * 1024;

    /// <summary>Lower bound on the tracker reannounce interval.</summary>
    public TimeSpan MinimumReannounceInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time to wait for an initial WebSocket tracker connection before giving up.</summary>
    public TimeSpan TrackerConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Restricts ICE to relayed candidates when set to <see cref="WebTorrentIceTransportPolicy.Relay"/>.</summary>
    public WebTorrentIceTransportPolicy IceTransportPolicy { get; init; } = WebTorrentIceTransportPolicy.All;

    /// <summary>Time provider used for tracker scheduling and pending-peer expiry. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// ICE servers (STUN/TURN) used for WebRTC connectivity. Defaults are STUN-only, which is
    /// sufficient for peers on open or cone-NAT networks. Peers behind a symmetric NAT (common
    /// on corporate / carrier networks) will not complete an ICE check with STUN alone — supply
    /// TURN credentials here to relay those peers. There is no auto-provisioned TURN server;
    /// callers must bring their own.
    /// </summary>
    public IReadOnlyList<WebTorrentIceServer> IceServers { get; init; } =
    [
        new() { Urls = { "stun:stun.l.google.com:19302" } },
        new() { Urls = { "stun:stun1.l.google.com:19302" } }
    ];
}
