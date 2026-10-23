namespace Moodle.Application.Abstractions;

/// <summary>
/// Métricas agregadas das fases da correção. Implementações não devem receber
/// texto acadêmico, nomes, tokens ou URLs autenticadas.
/// </summary>
public interface IGradingOperationTelemetry
{
    void RecordPhase(
        string operation,
        string phase,
        string result,
        double durationMs,
        int queryCount = 0,
        int itemCount = 0,
        long bytes = 0);

    void RecordPackageSubmissionRead(
        string cacheResult,
        double durationMs,
        int submissionCount,
        long estimatedBytes,
        int gatewayCallCount) =>
        RecordPhase("grading_package", "submission_read", cacheResult, durationMs,
            queryCount: gatewayCallCount, itemCount: submissionCount, bytes: estimatedBytes);

    void RecordPackageOutcome(
        string status,
        int attemptCount,
        string? retryReason,
        int artifactReadFailedCount,
        int contextUnavailableCount)
    {
    }
}
