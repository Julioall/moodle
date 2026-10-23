using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

public interface IConnectionRegistry
{
    Task<ConnectionInfo?> ResolveConnectionAsync(string? alias, CancellationToken cancellationToken = default);
}
