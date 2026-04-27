using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeerSharp.WebTorrent.Signaling;

internal sealed class WebTorrentCandidateConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        string? candidate = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return candidate;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            string? propertyName = reader.GetString();
            if (!reader.Read())
            {
                return candidate;
            }

            if (string.Equals(propertyName, "candidate", StringComparison.OrdinalIgnoreCase)
                && reader.TokenType == JsonTokenType.String)
            {
                candidate = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return candidate;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }
}
