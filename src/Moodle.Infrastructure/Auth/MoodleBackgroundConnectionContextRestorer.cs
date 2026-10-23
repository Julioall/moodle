using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

/// <summary>
/// Compatibilidade para lotes persistidos antes de ConnectorClientId ser
/// salvo. A busca permanece limitada ao ID interno da conexão e a conexões
/// ativas; nenhum segredo é copiado para o lote ou para o contexto.
/// </summary>
internal sealed class MoodleBackgroundConnectionContextRestorer(
    ConnectorDbContext dbContext,
    IConnectorExecutionContext executionContext,
    IMoodleConnectionSelection connectionSelection)
    : IMoodleBackgroundConnectionContextRestorer
{
    public async Task<bool> TryRestoreAsync(
        string connectionId,
        string subject,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        var connection = await dbContext.ConnectorClients
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == connectionId.Trim() && item.IsActive,
                cancellationToken);
        if (connection is null || string.IsNullOrWhiteSpace(connection.ClientId))
        {
            return false;
        }

        executionContext.Enter(
            connection.ClientId,
            subject.Trim(),
            email: null);
        connectionSelection.Alias = MoodleConnectionAlias.Normalize(connection.MoodleAlias);
        return true;
    }
}
