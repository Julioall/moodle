using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Moodle.Infrastructure;

public static class MoodleConnectionIdentity
{
    public static async Task<string> ResolveAsync(
        ConnectorDbContext db,
        Guid ownerId,
        string clientId,
        string connectionAlias,
        CancellationToken cancellationToken,
        Action? onDatabaseCommand = null)
    {
        var normalizedAlias = connectionAlias.Trim().ToLowerInvariant();
        var resolvedClientId = string.IsNullOrWhiteSpace(clientId)
            ? await ResolveOwnerClientIdAsync(db, ownerId, cancellationToken, onDatabaseCommand)
            : clientId.Trim();

        if (!string.IsNullOrWhiteSpace(resolvedClientId))
        {
            onDatabaseCommand?.Invoke();
            var connection = await db.ConnectorClients
                .AsNoTracking()
                .Where(item => item.ClientId == resolvedClientId &&
                               item.MoodleAlias.ToLower() == normalizedAlias)
                .Select(item => new { item.Id, item.IsActive })
                .SingleOrDefaultAsync(cancellationToken);
            if (connection is not null)
            {
                if (!connection.IsActive)
                {
                    throw new InvalidOperationException("A conexão Moodle selecionada está desativada.");
                }

                return connection.Id;
            }
        }

        // Legacy service clients may not have a local connector row. This
        // deterministic key is only a compatibility bridge until they are
        // registered; it is never presented as a Moodle alias.
        var source = $"legacy:{ownerId:N}:{resolvedClientId}:{normalizedAlias}";
        return $"legacy-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()}";
    }

    private static async Task<string?> ResolveOwnerClientIdAsync(
        ConnectorDbContext db,
        Guid ownerId,
        CancellationToken cancellationToken,
        Action? onDatabaseCommand)
    {
        onDatabaseCommand?.Invoke();
        return await db.UserAccounts
            .AsNoTracking()
            .Where(item => item.Id == ownerId)
            .Select(item => item.ConnectorClientId)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
