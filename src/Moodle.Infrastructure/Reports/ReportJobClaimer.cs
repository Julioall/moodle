using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moodle.Infrastructure;

namespace Moodle.Infrastructure.Reports;

internal sealed class ReportJobClaimer(ConnectorDbContext dbContext)
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly SemaphoreSlim StaleRecoveryGate = new(1, 1);
    private static long nextStaleRecoveryUtcTicks = DateTime.MinValue.Ticks;

    private const string RecoverStaleSql = """
        UPDATE report_jobs
            SET "Status" = 'failed',
                "ErrorMessage" = 'A geração foi marcada como falha após a concessão de execução expirar.',
                "CompletedAt" = @recoveredAt,
                "UpdatedAt" = @recoveredAt,
                "WorkerLeaseId" = NULL,
                "LeaseUntil" = NULL
        WHERE "Status" = 'running'
          AND ("LeaseUntil" IS NULL OR "LeaseUntil" < @recoveredAt);
        """;

    private const string ClaimSql = """
        WITH candidate AS (
            SELECT job."Id"
            FROM report_jobs AS job
            INNER JOIN user_accounts AS account ON account."Id" = job."OwnerId"
            WHERE job."Status" = 'queued'
              AND NOT EXISTS (
                  SELECT 1
                  FROM report_jobs AS running
                  WHERE running."OwnerId" = job."OwnerId"
                    AND running."Status" = 'running')
              AND (@ownerId IS NULL OR job."OwnerId" = @ownerId)
            ORDER BY job."RequestedAt", job."Id"
            FOR UPDATE OF account, job SKIP LOCKED
            LIMIT 1
        )
        UPDATE report_jobs AS job
        SET "Status" = 'running',
            "StartedAt" = @startedAt,
            "UpdatedAt" = @startedAt,
            "WorkerLeaseId" = @workerLeaseId,
            "LeaseUntil" = @leaseUntil
        FROM candidate
        WHERE job."Id" = candidate."Id"
        RETURNING job."Id";
        """;

    private const string LockOwnerSql = """
        SELECT account."Id"
        FROM report_jobs AS job
        INNER JOIN user_accounts AS account ON account."Id" = job."OwnerId"
        WHERE job."Status" = 'queued'
          AND NOT EXISTS (
              SELECT 1
              FROM report_jobs AS running
              WHERE running."OwnerId" = job."OwnerId"
                AND running."Status" = 'running')
          AND (@ownerId IS NULL OR job."OwnerId" = @ownerId)
        ORDER BY job."RequestedAt", job."Id"
        FOR UPDATE OF account SKIP LOCKED
        LIMIT 1;
        """;

    public async Task<ReportJobEntity?> ClaimNextAsync(CancellationToken cancellationToken, Guid? ownerId = null)
    {
        if (!dbContext.Database.IsNpgsql())
        {
            var queuedJob = await dbContext.ReportJobs
                .Where(item => item.Status == "queued" && (ownerId == null || item.OwnerId == ownerId))
                .OrderBy(item => item.RequestedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (queuedJob is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            queuedJob.Status = "running";
            queuedJob.StartedAt = now;
            queuedJob.UpdatedAt = now;
            queuedJob.WorkerLeaseId = Guid.NewGuid();
            queuedJob.LeaseUntil = now.Add(LeaseDuration);
            await dbContext.SaveChangesAsync(cancellationToken);
            return queuedJob;
        }

        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            if (ownerId is not null || now.UtcTicks >= Volatile.Read(ref nextStaleRecoveryUtcTicks))
            {
                await StaleRecoveryGate.WaitAsync(cancellationToken);
                try
                {
                    if (ownerId is not null || DateTimeOffset.UtcNow.UtcTicks >= Volatile.Read(ref nextStaleRecoveryUtcTicks))
                    {
                        await RecoverStaleJobsAsync(connection, now, cancellationToken);
                        Interlocked.Exchange(
                            ref nextStaleRecoveryUtcTicks,
                            DateTimeOffset.UtcNow.AddMinutes(1).UtcTicks);
                    }
                }
                finally
                {
                    StaleRecoveryGate.Release();
                }
            }

            IDbContextTransaction? ownedTransaction = null;
            try
            {
                if (dbContext.Database.CurrentTransaction is null)
                {
                    ownedTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                }

                var claimOwnerId = await LockNextOwnerAsync(connection, ownerId, cancellationToken);
                if (claimOwnerId is null)
                {
                    if (ownedTransaction is not null)
                    {
                        await ownedTransaction.CommitAsync(cancellationToken);
                    }

                    return null;
                }

                await using var command = connection.CreateCommand();
                command.CommandText = ClaimSql;
                if (dbContext.Database.CurrentTransaction is { } currentTransaction)
                {
                    command.Transaction = currentTransaction.GetDbTransaction();
                }

                AddParameter(command, "startedAt", DbType.DateTimeOffset, now);
                AddParameter(command, "workerLeaseId", DbType.Guid, Guid.NewGuid());
                AddParameter(command, "leaseUntil", DbType.DateTimeOffset, now.Add(LeaseDuration));
                AddParameter(command, "ownerId", DbType.Guid, claimOwnerId.Value);

                var claimedId = await command.ExecuteScalarAsync(cancellationToken);
                var claimedJob = claimedId is Guid jobId
                    ? await dbContext.ReportJobs.SingleOrDefaultAsync(job => job.Id == jobId, CancellationToken.None)
                    : null;

                if (ownedTransaction is not null)
                {
                    await ownedTransaction.CommitAsync(cancellationToken);
                }

                return claimedJob;
            }
            catch
            {
                if (ownedTransaction is not null)
                {
                    await ownedTransaction.RollbackAsync(CancellationToken.None);
                }

                throw;
            }
            finally
            {
                if (ownedTransaction is not null)
                {
                    await ownedTransaction.DisposeAsync();
                }
            }
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<Guid?> LockNextOwnerAsync(
        DbConnection connection,
        Guid? ownerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = LockOwnerSql;
        if (dbContext.Database.CurrentTransaction is { } currentTransaction)
        {
            command.Transaction = currentTransaction.GetDbTransaction();
        }

        AddParameter(command, "ownerId", DbType.Guid, ownerId is null ? DBNull.Value : ownerId.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid lockedOwnerId ? lockedOwnerId : null;
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task RecoverStaleJobsAsync(
        System.Data.Common.DbConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var recoveryCommand = connection.CreateCommand();
        recoveryCommand.CommandText = RecoverStaleSql;
        if (dbContext.Database.CurrentTransaction is { } recoveryTransaction)
        {
            recoveryCommand.Transaction = recoveryTransaction.GetDbTransaction();
        }

        var recoveredAt = recoveryCommand.CreateParameter();
        recoveredAt.ParameterName = "recoveredAt";
        recoveredAt.DbType = DbType.DateTimeOffset;
        recoveredAt.Value = now;
        recoveryCommand.Parameters.Add(recoveredAt);
        await recoveryCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
