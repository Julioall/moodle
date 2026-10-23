using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Moodle.Infrastructure.MoodleApi;

internal static class MoodleParameterSerializer
{
    public static IReadOnlyDictionary<string, string> Flatten(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Os nomes dos parametros Moodle nao podem ser vazios.", nameof(parameters));
            }

            AddValue(result, key, value);
        }

        return result;
    }

    private static void AddValue(IDictionary<string, string> target, string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        if (value is JsonElement json)
        {
            AddJsonValue(target, key, json);
            return;
        }

        if (IsScalar(value))
        {
            target[key] = ConvertToString(value);
            return;
        }

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry item in dictionary)
            {
                if (item.Key is not string childKey || string.IsNullOrWhiteSpace(childKey))
                {
                    throw new ArgumentException("Objetos de parametros Moodle devem usar chaves textuais nao vazias.");
                }

                AddValue(target, $"{key}[{childKey}]", item.Value);
            }

            return;
        }

        if (value is IEnumerable sequence and not string)
        {
            var index = 0;
            foreach (var item in sequence)
            {
                AddValue(target, $"{key}[{index++}]", item);
            }

            return;
        }

        var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0);
        foreach (var property in properties)
        {
            AddValue(target, $"{key}[{property.Name}]", property.GetValue(value));
        }
    }

    private static void AddJsonValue(IDictionary<string, string> target, string key, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    AddJsonValue(target, $"{key}[{property.Name}]", property.Value);
                }
                return;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    AddJsonValue(target, $"{key}[{index++}]", item);
                }
                return;
            case JsonValueKind.String:
                target[key] = value.GetString() ?? string.Empty;
                return;
            case JsonValueKind.True:
                target[key] = "1";
                return;
            case JsonValueKind.False:
                target[key] = "0";
                return;
            default:
                target[key] = value.GetRawText();
                return;
        }
    }

    private static bool IsScalar(object value) => value is string or char or bool or DateTime or DateTimeOffset or Guid ||
        value.GetType().IsPrimitive || value is decimal || value.GetType().IsEnum;

    private static string ConvertToString(object value) => value switch
    {
        bool boolean => boolean ? "1" : "0",
        DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
