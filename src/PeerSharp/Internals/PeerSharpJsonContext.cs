using System.Text.Json.Serialization;
using PeerSharp.PieceWriter;

namespace PeerSharp.Internals;

[JsonSerializable(typeof(TorrentStateData))]
[JsonSerializable(typeof(SavedTorrentOptions))]
[JsonSerializable(typeof(SessionPersistence.DhtStateDto))]
internal partial class PeerSharpJsonContext : JsonSerializerContext
{
}
