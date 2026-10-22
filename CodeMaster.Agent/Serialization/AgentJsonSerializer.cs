using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeMaster.Agent.Serialization;

public static class AgentJsonSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new LongAsStringConverter());
        options.Converters.Add(new NullableLongAsStringConverter());
        return options;
    }

    private sealed class LongAsStringConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), out var value))
                return value;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out value))
                return value;

            throw new JsonException("Expected an Int64 integer or decimal integer string; fractions, exponents and out-of-range values are not supported.");
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class NullableLongAsStringConverter : JsonConverter<long?>
    {
        public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;
            if (reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), out var value))
                return value;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out value))
                return value;

            throw new JsonException("Expected an Int64 integer, decimal integer string, or null; fractions, exponents and out-of-range values are not supported.");
        }

        public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value.ToString());
            else
                writer.WriteNullValue();
        }
    }
}
