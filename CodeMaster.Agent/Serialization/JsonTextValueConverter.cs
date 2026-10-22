using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeMaster.Agent.Serialization;

/// <summary>
/// Accepts either a normal string or a native JSON array/object and stores the
/// latter as compact JSON text. Agent-facing contracts use this for metadata
/// columns that are persisted as strings but are naturally emitted by models as
/// structured JSON.
/// </summary>
public sealed class JsonTextValueConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();
        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return JsonSerializer.Serialize(document.RootElement);
        }

        throw new JsonException(
            $"Expected a string, JSON array, or JSON object, but received {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }
}
