using System.Text.Json;
using System.Text.Json.Serialization;
using PeerSharp.PieceWriter;

namespace PeerSharp.Internals;

// Keep the existing resume format while avoiding a contiguous base64 token on the LOH.
internal sealed class ResumePieceJsonConverter : JsonConverter<TorrentStateData.UnfinishedPieceData>
{
    internal const int ChunkSize = 12 * 1024;

    public override TorrentStateData.UnfinishedPieceData Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected an unfinished piece object.");
        var piece = new TorrentStateData.UnfinishedPieceData();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            string? name = reader.GetString();
            if (!reader.Read()) throw new JsonException();
            switch (name)
            {
                case "Index": piece.Index = reader.GetInt32(); break;
                case "Data": piece.Data = reader.TokenType == JsonTokenType.Null ? null! : reader.GetBytesFromBase64(); break;
                case "Blocks":
                    if (reader.TokenType == JsonTokenType.Null) { piece.Blocks = null!; break; }
                    if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
                    var blocks = new List<bool>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) blocks.Add(reader.GetBoolean());
                    if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException();
                    piece.Blocks = [.. blocks];
                    break;
                default: reader.Skip(); break;
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException();
        return piece;
    }

    public override void Write(Utf8JsonWriter writer, TorrentStateData.UnfinishedPieceData value, JsonSerializerOptions options)
    {
        value.SerializationCancellationToken.ThrowIfCancellationRequested();
        writer.WriteStartObject();
        writer.WritePropertyName("Blocks");
        if (value.Blocks == null) writer.WriteNullValue();
        else
        {
            writer.WriteStartArray();
            foreach (bool block in value.Blocks) writer.WriteBooleanValue(block);
            writer.WriteEndArray();
        }
        writer.WritePropertyName("Data");
        if (value.DeferredData is { } deferred) deferred.WriteBase64(writer, value.SerializationCancellationToken);
        else if (value.Data == null) writer.WriteNullValue();
        else
        {
            var data = value.Data.AsSpan();
            while (data.Length > ChunkSize)
            {
                value.SerializationCancellationToken.ThrowIfCancellationRequested();
                writer.WriteBase64StringSegment(data[..ChunkSize], isFinalSegment: false);
                writer.Flush();
                data = data[ChunkSize..];
            }
            value.SerializationCancellationToken.ThrowIfCancellationRequested();
            writer.WriteBase64StringSegment(data, isFinalSegment: true);
        }
        writer.WriteNumber("Index", value.Index);
        writer.WriteEndObject();
        writer.Flush();
    }
}
