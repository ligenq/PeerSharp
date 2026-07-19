using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using PeerSharp.WebTorrent.Configuration;
using RtcForge;
using RtcForge.Media;

namespace PeerSharp.WebTorrent.Network;

internal interface IWebRtcConnectionFactory
{
    IWebRtcConnection Create();
}

[ExcludeFromCodeCoverage]
internal sealed class DefaultWebRtcConnectionFactory : IWebRtcConnectionFactory
{
    private readonly WebRtcConnectionOptions _rtcOptions;

    public DefaultWebRtcConnectionFactory(WebTorrentSessionOptions options, ILoggerFactory loggerFactory)
    {
        _rtcOptions = new WebRtcConnectionOptions
        {
            LoggerFactory = loggerFactory
        };

        foreach (var s in options.IceServers)
        {
            _rtcOptions.IceServers.Add(new RTCIceServer
            {
                Urls = [.. s.Urls],
                Username = s.Username,
                Credential = s.Credential
            });
        }

        if (options.IceTransportPolicy == WebTorrentIceTransportPolicy.Relay)
        {
            _rtcOptions.IceTransportPolicy = RTCIceTransportPolicy.Relay;
        }
    }

    public IWebRtcConnection Create() => WebRtcConnection.Create(_rtcOptions);
}
