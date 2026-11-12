using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moodle.Infrastructure;

/// <summary>
/// Bounded retention for one-time password recovery capabilities.
/// Retain at most 24 hours after use/expiry for operational diagnostics.
/// </summary>
internal sealed class PasswordResetTokenCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<PasswordResetTokenCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            if (!db.Database.IsRelational()) return;
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var removed = await db.PasswordResetTokens
                .Where(token => token.ExpiresAtUtc < cutoff ||
                    (token.ConsumedAtUtc != null && token.ConsumedAtUtc < cutoff))
                .ExecuteDeleteAsync(cancellationToken);
            if (removed > 0)
                logger.LogInformation("Expired password recovery capabilities purged: Count={Count}", removed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Schema migration may not have completed; retry on the next sweep.
            logger.LogWarning("Password recovery retention sweep skipped. Reason={Reason}", ex.GetType().Name);
        }
    }
}
