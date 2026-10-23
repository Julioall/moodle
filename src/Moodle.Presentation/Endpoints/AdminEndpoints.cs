using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Moodle.Application.Abstractions;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Superfície administrativa protegida pelo middleware de chave administrativa.
/// </summary>
internal static class AdminEndpoints
{
    public static void MapConnectorClientRegistration(WebApplication app, string rateLimitPolicy)
    {
        app.MapPost("/admin/connector-clients/register", async (
            RegisterConnectorClientInput input,
            IConnectorClientRegistrationService registrationService,
            CancellationToken cancellationToken) =>
        {
            var request = new RegisterConnectorClientRequest(
                input.ClientId,
                input.MoodleAlias,
                input.MoodleBaseUrl,
                input.MoodleUsername,
                input.MoodlePassword,
                input.MoodleTarget,
                input.IsDefault,
                input.CanWrite);

            var result = await registrationService.RegisterOrRotateAsync(request, cancellationToken);

            return Results.Ok(new
            {
                ok = true,
                result.ClientId,
                result.ConnectionId,
                result.MoodleAlias,
                result.ApiKey,
                result.ReplacedExistingClient,
                message = "Credenciais Moodle persistidas e API key emitida/rotacionada para o cliente."
            });
        }).RequireRateLimiting(rateLimitPolicy);
    }
}

public sealed record RegisterConnectorClientInput(
    string ClientId,
    string MoodleAlias,
    string MoodleBaseUrl,
    string MoodleUsername,
    string MoodlePassword,
    string MoodleTarget,
    bool IsDefault,
    bool CanWrite);
