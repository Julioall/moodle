using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;

namespace Moodle.Application.Grading;

/// <summary>
/// Limpa periodicamente o texto extraído de artifacts antigos. A operação é
/// deliberadamente redacional: o registro técnico permanece para auditoria,
/// cobertura e reconciliação, sem reter o conteúdo acadêmico indefinidamente.
/// </summary>
public sealed class GradingRetentionWorkerService(
    IServiceScopeFactory scopeFactory,
    IOptions<GradingLimitsOptions> limits,
    ILogger<GradingRetentionWorkerService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunCleanupAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunCleanupAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var retentionDays = Math.Clamp(limits.Value.RawFileRetentionDays, 1, 3650);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetService<IGradingRetentionStore>();
            if (store is null)
            {
                return;
            }

            var redacted = await store.RedactExpiredArtifactTextAsync(cutoff, cancellationToken);
            var resources = scope.ServiceProvider.GetService<IMoodleResourceRepository>();
            var expiredResources = resources is null
                ? 0
                : await resources.RemoveExpiredAsync(DateTimeOffset.UtcNow, cancellationToken);
            var packageStore = scope.ServiceProvider.GetService<IGradingPackageStore>();
            var packageBlobs = scope.ServiceProvider.GetService<IGradingPackageBlobStore>();
            var packageArtifactCache = scope.ServiceProvider.GetService<IGradingPackageArtifactCache>();
            var recoveredPackageClaims = 0;
            var expiredPackageBlobs = 0;
            var expiredPackageArtifacts = 0;
            if (packageStore is not null)
            {
                recoveredPackageClaims = await packageStore.RecoverExpiredClaimsAsync(
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                if (packageBlobs is not null)
                {
                    foreach (var package in await packageStore.ListExpiredBlobsAsync(
                                 DateTimeOffset.UtcNow,
                                 cancellationToken))
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(package.BlobKey))
                            {
                                await packageBlobs.DeleteAsync(package.BlobKey, cancellationToken);
                            }

                            package.MarkBlobExpired(DateTimeOffset.UtcNow);
                            expiredPackageBlobs++;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            logger.LogWarning(
                                "Nao foi possivel remover um blob expirado de pacote; o manifesto foi preservado. FailureReason={FailureReason}",
                                "blob_delete_failed");
                        }
                    }

                    if (expiredPackageBlobs > 0)
                    {
                        await packageStore.SaveChangesAsync(cancellationToken);
                    }
                }
            }

            if (packageArtifactCache is not null)
            {
                expiredPackageArtifacts = await packageArtifactCache.RemoveExpiredAsync(
                    cutoff,
                    cancellationToken);
            }

            if (redacted > 0 || expiredResources > 0 || recoveredPackageClaims > 0 ||
                expiredPackageBlobs > 0 || expiredPackageArtifacts > 0)
            {
                logger.LogInformation(
                    "Retenção de correção assistida aplicada a {Count} artifact(s), {ResourceCount} resource(s), {RecoveredPackageClaims} lease(s) de pacote, {ExpiredPackageBlobs} blob(s) e {ExpiredPackageArtifacts} cache(s) de artifact; cutoff={Cutoff}.",
                    redacted,
                    expiredResources,
                    recoveredPackageClaims,
                    expiredPackageBlobs,
                    expiredPackageArtifacts,
                    cutoff);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            logger.LogWarning("Falha ao aplicar retenção de artifacts de correção assistida. FailureReason={FailureReason}", "retention_error");
        }
    }
}
