namespace Moodle.Application.Abstractions;

public interface IMoodleCurrentUserIdGateway
{
    Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken);
}
