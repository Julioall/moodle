using System.Text.Json.Nodes;
using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

public interface IResponseNormalizer
{
    JsonNode? Normalize(string profileName, JsonNode? rawResponse, NormalizationContext? context = null);
}
