using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moodle.Infrastructure.Reports;

internal sealed class ReportArtifactDeletionWorkerService(
    IServiceScopeFactory scopeFactory,
    ILogger<ReportArtifactDeletionWorkerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ReportArtifactDeletionProcessor>();
                await processor.ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogError(
                    "Report artifact deletion cycle failed. FailureReason={FailureReason}",
                    "worker_error");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
