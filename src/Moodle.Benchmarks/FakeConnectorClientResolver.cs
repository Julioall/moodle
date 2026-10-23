using System.Threading;
using System.Threading.Tasks;
using Moodle.Application.Abstractions;

namespace Moodle.Benchmarks;

public class FakeConnectorClientResolver : IMcpConnectorClientResolver
{
    public Task<ConnectorClientContext?> ResolveByApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ConnectorClientContext?>(new ConnectorClientContext("test-client-id", true));
    }
}
