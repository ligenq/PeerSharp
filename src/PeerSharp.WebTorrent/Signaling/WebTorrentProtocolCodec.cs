using System.Text.Json;

namespace PeerSharp.WebTorrent.Signaling;

internal static class WebTorrentProtocolCodec
{
    public static WebTorrentSignalMessage Parse(string json)
    {
        return JsonSerializer.Deserialize(json, WebTorrentJsonContext.Default.WebTorrentSignalMessage)
            ?? throw new InvalidOperationException("Failed to deserialize WebTorrent signal message.");
    }
}
