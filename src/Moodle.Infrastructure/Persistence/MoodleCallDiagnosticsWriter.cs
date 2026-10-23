using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure.Configuration;
using Moodle.Infrastructure.MoodleApi;
using Npgsql;

namespace Moodle.Infrastructure;

internal interface IMoodleCallDiagnosticsWriter
{
    Task WriteAsync(IReadOnlyList<MoodleCallDiagnosticRecord> records, CancellationToken cancellationToken);
}

internal sealed class MoodleCallDiagnosticsWriter(
    IDbContextFactory<ConnectorDbContext> dbContextFactory,
    IOptions<MoodleCallDiagnosticsOptions> options,
    IMoodleCallTelemetry telemetry,
    ILogger<MoodleCallDiagnosticsWriter> logger) : IMoodleCallDiagnosticsWriter
{
    public async Task WriteAsync(
        IReadOnlyList<MoodleCallDiagnosticRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        if (!options.Value.Enabled)
        {
            try { telemetry.RecordMoodleDiagnosticWrite("disabled", records.Count); }
            catch (Exception) { }
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(options.Value.WriteTimeoutMilliseconds));
        try
        {
            using var transactionScope = new TransactionScope(
                TransactionScopeOption.Suppress,
                TransactionScopeAsyncFlowOption.Enabled);
            await using var db = await dbContextFactory.CreateDbContextAsync(timeout.Token);
            await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
            foreach (var record in records)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO moodle_call_diagnostics (
                        "EventId", "CallId", "RetryGroupId", "AttemptId", "EventType", "SchemaVersion", "CatalogVersion",
                        "CorrelationId", "SnapshotRunId", "SnapshotRunItemId", "ConnectionKey", "MoodleFunction",
                        "OperationKind", "ErrorCategory", "ClassificationReason", "MoodleErrorCode", "MoodleExceptionType",
                        "SafeMessage", "CodeState", "ExceptionState", "HttpStatus", "ResponseKind", "TransportErrorType",
                        "SocketErrorCode", "DurationMs", "AttemptNumber", "AttemptCount", "RetryDisposition", "RetryDelayMs",
                        "Outcome", "LastFailureEventId", "RawPayloadState", "RawPayloadSizeBytes", "RawPayloadSha256",
                        "RawPayloadContentType", "CreatedAtUtc")
                    VALUES (
                        {record.EventId}, {record.CallId}, {record.RetryGroupId}, {record.AttemptId}, {record.EventType}, {record.SchemaVersion}, {record.CatalogVersion},
                        {record.CorrelationId}, {record.SnapshotRunId}, {record.SnapshotRunItemId}, {record.ConnectionKey}, {record.MoodleFunction},
                        {record.OperationKind}, {record.ErrorCategory}, {record.ClassificationReason}, {record.MoodleErrorCode}, {record.MoodleExceptionType},
                        {record.SafeMessage}, {record.CodeState}, {record.ExceptionState}, {record.HttpStatus}, {record.ResponseKind}, {record.TransportErrorType},
                        {record.SocketErrorCode}, {record.DurationMs}, {record.AttemptNumber}, {record.AttemptCount}, {record.RetryDisposition}, {record.RetryDelayMs},
                        {record.Outcome}, {record.LastFailureEventId}, {record.RawPayloadState}, {record.RawPayloadSizeBytes},
                        {record.RawPayloadSha256}, {record.RawPayloadContentType}, {record.CreatedAtUtc})
                    ON CONFLICT ("EventId") DO NOTHING
                    """, timeout.Token);

                if (record.RawPayload is { } rawPayload)
                {
                    if (rawPayload.Length > MoodleCallDiagnosticsState.MaxRawErrorPayloadBytes)
                        throw new InvalidOperationException("Raw Moodle error payload exceeds its storage limit.");
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO moodle_call_diagnostic_payloads (
                            "EventId", "Payload", "CapturedAtUtc", "ExpiresAtUtc")
                        VALUES (
                            {record.EventId}, {rawPayload}, {record.CreatedAtUtc}, {record.CreatedAtUtc.AddDays(7)})
                        ON CONFLICT DO NOTHING
                        """, timeout.Token);
                }
            }
            await transaction.CommitAsync(timeout.Token);
            transactionScope.Complete();
            try { telemetry.RecordMoodleDiagnosticWrite("confirmed", records.Count); }
            catch (Exception) { }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            RecordWriteFailure(records, "timeout");
        }
        catch (Exception exception)
        {
            RecordWriteFailure(records, GetSafeFailureReason(exception));
        }
    }

    private static string GetSafeFailureReason(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException)
                return $"postgres_{postgresException.SqlState}";
        }

        return exception is NpgsqlException ? "npgsql_error" : "database_error";
    }

    private void RecordWriteFailure(IReadOnlyList<MoodleCallDiagnosticRecord> records, string reason)
    {
        try { telemetry.RecordMoodleDiagnosticWrite("unconfirmed", records.Count); }
        catch (Exception) { }
        try
        {
            logger.LogWarning(
                "MoodleDiagnosticWriteFailed. EventCount={EventCount} FailureReason={FailureReason}",
                records.Count,
                reason);
        }
        catch (Exception) { }
    }

}

internal sealed class MoodleCallDiagnosticsRetentionService(
    IDbContextFactory<ConnectorDbContext> dbContextFactory,
    IOptions<MoodleCallDiagnosticsOptions> options,
    IMoodleCallTelemetry telemetry,
    ILogger<MoodleCallDiagnosticsRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await DeleteExpiredAsync(stoppingToken);

            try
            {
                await Task.Delay(TimeSpan.FromHours(options.Value.CleanupIntervalHours), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.RetentionDays);
        var removed = 0;
        var rawPayloadsRemoved = 0;
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var payloadCutoff = DateTimeOffset.UtcNow;
            rawPayloadsRemoved = await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM moodle_call_diagnostic_payloads
                WHERE "ExpiresAtUtc" < {payloadCutoff}
                """, cancellationToken);

            if (options.Value.Enabled)
            {
                for (var batchNumber = 0; batchNumber < options.Value.MaxCleanupBatchesPerRun; batchNumber++)
                {
                    var batch = await db.MoodleCallDiagnostics
                        .Where(record => record.CreatedAtUtc < cutoff)
                        .OrderBy(record => record.CreatedAtUtc)
                        .Take(options.Value.CleanupBatchSize)
                        .ToListAsync(cancellationToken);
                    if (batch.Count == 0) break;
                    db.MoodleCallDiagnostics.RemoveRange(batch);
                    await db.SaveChangesAsync(cancellationToken);
                    removed += batch.Count;
                }
            }

            var oldestExpiredAtUtc = options.Value.Enabled
                ? await db.MoodleCallDiagnostics
                    .Where(record => record.CreatedAtUtc < cutoff)
                    .OrderBy(record => record.CreatedAtUtc)
                    .Select(record => (DateTimeOffset?)record.CreatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken)
                : null;
            var cleanupOutcome = oldestExpiredAtUtc is null ? "success" : "backlog";
            try { telemetry.RecordMoodleDiagnosticCleanup(cleanupOutcome, removed); }
            catch (Exception) { }
            var logMessage = "Moodle diagnostic retention completed. Outcome={Outcome} RowsDeleted={RowsDeleted} RawPayloadsDeleted={RawPayloadsDeleted} RetentionDays={RetentionDays} RawPayloadRetentionDays={RawPayloadRetentionDays} OldestExpiredAtUtc={OldestExpiredAtUtc}";
            if (oldestExpiredAtUtc is null)
            {
                logger.LogInformation(
                    logMessage,
                    cleanupOutcome,
                    removed,
                    rawPayloadsRemoved,
                    options.Value.RetentionDays,
                    7,
                    oldestExpiredAtUtc);
            }
            else
            {
                logger.LogWarning(
                    logMessage,
                    cleanupOutcome,
                    removed,
                    rawPayloadsRemoved,
                    options.Value.RetentionDays,
                    7,
                    oldestExpiredAtUtc);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            try { telemetry.RecordMoodleDiagnosticCleanup("failure", removed); }
            catch (Exception) { }
            try
            {
                logger.LogWarning(
                    "Moodle diagnostic retention failed. RowsDeleted={RowsDeleted} FailureReason={FailureReason}",
                    removed,
                    "database_error");
            }
            catch (Exception) { }
        }
    }
}
