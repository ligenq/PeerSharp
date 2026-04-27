using System.Text.Json.Serialization;

namespace PeerSharp.WebTorrent.Signaling;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WebTorrentSignalMessage))]
internal partial class WebTorrentJsonContext : JsonSerializerContext;
