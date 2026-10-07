using System.Net;

namespace PeerSharp.Streaming;

/// <summary>
/// Where an <see cref="HttpStreamServer"/> listens, and what a request has to know to be answered.
/// </summary>
public sealed class HttpStreamServerOptions
{
    /// <summary>
    /// The address to listen on. Defaults to the loopback address, which only a player on the same
    /// device can reach.
    /// </summary>
    /// <remarks>
    /// To stream to another device - a Chromecast, a TV, a second computer - bind to this device's
    /// address on the network that device is on. The wildcard addresses are refused: the server
    /// advertises its URL, and a URL naming every interface names none of them.
    /// </remarks>
    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

    /// <summary>The port to listen on, or zero (the default) for any free port.</summary>
    public int Port { get; set; }

    /// <summary>
    /// A secret every request's path has to carry, or null to have one generated when it is needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A server bound to anything but loopback serves whoever can reach it, so it always requires a
    /// token: when this is null one is generated, and <see cref="HttpStreamServer.Url"/> includes it.
    /// A loopback server requires one only when it is given one here, so its URL stays
    /// <c>http://127.0.0.1:port/stream</c>.
    /// </para>
    /// <para>
    /// A token given here must be URL-safe: letters, digits, <c>-</c> and <c>_</c> only.
    /// </para>
    /// </remarks>
    public string? AccessToken { get; set; }
}
