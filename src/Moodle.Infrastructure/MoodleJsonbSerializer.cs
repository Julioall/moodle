using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Moodle.Infrastructure;

/// <summary>
/// Serializes snapshot payloads into JSON that PostgreSQL jsonb can represent.
/// Moodle text fields occasionally contain a NUL or an unpaired UTF-16
/// surrogate. System.Text.Json escapes those values, but PostgreSQL rejects a
/// JSON string containing the <c>\u0000</c> escape (and invalid surrogate
/// sequences), so normalize string tokens before the value reaches Npgsql.
/// </summary>
internal static class MoodleJsonbSerializer
{
    public static (string Json, int SanitizedCharacters) Serialize<T>(
        T payload,
        JsonSerializerOptions options)
    {
        var serialized = JsonSerializer.Serialize(payload, options);
        using var document = JsonDocument.Parse(serialized);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
        }))
        {
            var sanitizedCharacters = 0;
            WriteElement(document.RootElement, writer, ref sanitizedCharacters);
            writer.Flush();
            return (Encoding.UTF8.GetString(buffer.ToArray()), sanitizedCharacters);
        }
    }

    private static void WriteElement(
        JsonElement element,
        Utf8JsonWriter writer,
        ref int sanitizedCharacters)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(Sanitize(property.Name, ref sanitizedCharacters));
                    WriteElement(property.Value, writer, ref sanitizedCharacters);
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(item, writer, ref sanitizedCharacters);
                }

                writer.WriteEndArray();
                return;

            case JsonValueKind.String:
                writer.WriteStringValue(Sanitize(element.GetString() ?? string.Empty, ref sanitizedCharacters));
                return;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                element.WriteTo(writer);
                return;

            default:
                throw new JsonException($"Valor JSON inesperado: {element.ValueKind}.");
        }
    }

    private static string Sanitize(string value, ref int sanitizedCharacters)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) &&
                index + 1 < value.Length &&
                char.IsLowSurrogate(value[index + 1]))
            {
                if (builder is not null)
                {
                    builder.Append(character);
                    builder.Append(value[++index]);
                }
                else
                {
                    index++;
                }

                continue;
            }

            var replacement = character == '\0' || char.IsSurrogate(character)
                ? '\ufffd'
                : character;
            if (replacement == character)
            {
                if (builder is not null)
                {
                    builder.Append(character);
                }

                continue;
            }

            if (builder is null)
            {
                builder = new StringBuilder(value.Length);
                builder.Append(value, 0, index);
            }

            builder.Append(replacement);
            sanitizedCharacters++;
        }

        return builder?.ToString() ?? value;
    }
}
