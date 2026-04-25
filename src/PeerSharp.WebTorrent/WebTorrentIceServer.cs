namespace PeerSharp.WebTorrent;

/// <summary>
/// Controls which ICE candidate types WebTorrent WebRTC connections may use.
/// </summary>
public enum WebTorrentIceTransportPolicy
{
    /// <summary>Allow direct host, server-reflexive STUN, and relayed TURN candidates.</summary>
    All,

    /// <summary>Allow only relayed TURN candidates.</summary>
    Relay
}

/// <summary>
/// ICE server configuration for WebTorrent WebRTC connections.
/// </summary>
public sealed class WebTorrentIceServer
{
    /// <summary>
    /// STUN/TURN server URLs.
    /// </summary>
    public List<string> Urls { get; } = [];

    /// <summary>
    /// Optional TURN username.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Optional TURN credential.
    /// </summary>
    public string? Credential { get; set; }
}
