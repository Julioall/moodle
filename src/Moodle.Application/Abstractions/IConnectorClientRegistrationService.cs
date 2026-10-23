namespace Moodle.Application.Abstractions;

public sealed record RegisterConnectorClientRequest(
    string ClientId,
    string MoodleAlias,
    string MoodleBaseUrl,
    string MoodleUsername,
    string MoodlePassword,
    string MoodleTarget,
    bool IsDefault,
    bool CanWrite);

public sealed record RegisterConnectorClientResult(
    string ClientId,
    string ConnectionId,
    string MoodleAlias,
    string ApiKey,
    bool ReplacedExistingClient);

public interface IConnectorClientRegistrationService
{
    Task<RegisterConnectorClientResult> RegisterOrRotateAsync(
        RegisterConnectorClientRequest request,
        CancellationToken cancellationToken);
}
