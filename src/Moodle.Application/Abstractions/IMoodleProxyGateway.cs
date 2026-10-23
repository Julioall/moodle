using System.Text.Json;

namespace Moodle.Application.Abstractions;

public interface IMoodleProxyGateway
{
    Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken);

    Task<JsonElement> GetSessionStatusAsync(CancellationToken cancellationToken);
}