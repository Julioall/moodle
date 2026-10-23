using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

public interface ICapabilityRegistry
{
    Task<CapabilitySnapshot> GetSnapshotAsync(ConnectionInfo connectionInfo, string userToken, CancellationToken cancellationToken = default);
    void Invalidate(ConnectionInfo connectionInfo, string userToken);
}
