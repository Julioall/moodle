using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

internal sealed class DatabaseConnectorClientResolver(ConnectorDbContext dbContext) : IMcpConnectorClientResolver
{
    public async Task<ConnectorClientContext?> ResolveByApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        var hash = ApiKeyHasher.Hash(apiKey);
        var entity = await dbContext.ConnectorClients
            .AsNoTracking()
            .Where(client => client.IsActive && client.ApiKeyHash == hash)
            .SingleOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var canWrite = await dbContext.ConnectorClients
            .AsNoTracking()
            .AnyAsync(client => client.ClientId == entity.ClientId && client.IsActive && client.CanWrite, cancellationToken);

        return new ConnectorClientContext(entity.ClientId, canWrite);
    }
}
