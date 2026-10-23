using System.Text.Json.Nodes;
using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

public interface ISafeReadExecutor
{
    Task<JsonNode?> ExecuteAsync(
        string operationName,
        Dictionary<string, object?> parameters,
        string? moodleAlias = null,
        NormalizationContext? context = null,
        CancellationToken cancellationToken = default);
}
