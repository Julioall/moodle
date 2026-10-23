using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;

namespace Moodle.Presentation.Health;

internal sealed class ConnectorDatabaseHealthCheck(ConnectorDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Banco de dados acessível.")
                : HealthCheckResult.Unhealthy("Banco de dados indisponível.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Banco de dados indisponível.", ex);
        }
    }
}
