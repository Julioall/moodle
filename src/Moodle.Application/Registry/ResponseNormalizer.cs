using System.Text.Json.Nodes;
using System.Text;
using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

public sealed class ResponseNormalizer : IResponseNormalizer
{
    public JsonNode? Normalize(string profileName, JsonNode? rawResponse, NormalizationContext? context = null)
    {
        context ??= new NormalizationContext();
        if (rawResponse is null) return null;

        var maxPayloadBytes = Math.Max(1, context.MaxPayloadBytes);

        if (context.Mode == NormalizationMode.Agent && rawResponse is JsonArray array)
        {
            var maxItems = Math.Max(1, context.MaxItems);
            if (array.Count > maxItems || Encoding.UTF8.GetByteCount(rawResponse.ToJsonString()) > maxPayloadBytes)
            {
                var safeArray = new JsonArray();
                foreach (var item in array.Take(maxItems))
                {
                    var clone = JsonNode.Parse(item?.ToJsonString() ?? "null");
                    safeArray.Add(clone);

                    var candidate = BuildTruncatedArray(safeArray, array.Count, maxPayloadBytes);
                    if (Encoding.UTF8.GetByteCount(candidate.ToJsonString()) > maxPayloadBytes)
                    {
                        safeArray.RemoveAt(safeArray.Count - 1);
                        break;
                    }
                }
                return BuildTruncatedArray(safeArray, array.Count, maxPayloadBytes);
            }
        }

        var serialized = rawResponse.ToJsonString();
        if (context.Mode == NormalizationMode.Agent && Encoding.UTF8.GetByteCount(serialized) > maxPayloadBytes)
        {
            return new JsonObject
            {
                ["truncated"] = true,
                ["hasMore"] = true,
                ["payloadBytes"] = Encoding.UTF8.GetByteCount(serialized),
                ["maxPayloadBytes"] = maxPayloadBytes,
                ["preview"] = serialized[..(int)Math.Min(serialized.Length, Math.Max(1, maxPayloadBytes / 2))]
            };
        }

        return rawResponse;
    }

    private static JsonObject BuildTruncatedArray(JsonArray items, int total, long maxPayloadBytes) =>
        new()
        {
            ["items"] = JsonNode.Parse(items.ToJsonString()),
            ["returned"] = items.Count,
            ["total"] = total,
            ["hasMore"] = items.Count < total,
            ["truncated"] = true,
            ["maxPayloadBytes"] = maxPayloadBytes
        };
}
