using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moodle.Presentation.Configuration;

/// <summary>
/// The MCP schema exporter serializes optional parameter defaults while
/// building tool schemas. An omitted JsonElement is Undefined, which the
/// framework converter cannot serialize. Treat only that schema-time default
/// as JSON null; valid input values, including an explicit JSON null, retain
/// their original JsonElement kind.
/// </summary>
internal sealed class UndefinedJsonElementAsNullConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
            return;
        }

        value.WriteTo(writer);
    }
}
