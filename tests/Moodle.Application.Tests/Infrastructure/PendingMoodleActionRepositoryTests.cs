using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using Moodle.Domain;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class PendingMoodleActionRepositoryTests
{
    [Fact]
    public async Task ListRecoverableGradingPublications_FiltraAutorizadasELeasesExpirados()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"pending-actions-{Guid.NewGuid():N}")
            .Options;
        var now = DateTimeOffset.UtcNow;
        await using (var seed = new ConnectorDbContext(options))
        {
            var authorized = Create("criar_previa_lancamento_lote");
            authorized.Authorize("teacher-1", now);

            // Compatibility: actions created before the durable Authorized
            // state may still be Confirmed when the process restarts.
            var legacyConfirmed = Create("confirmar_lancamento_lote_moodle");
            legacyConfirmed.Confirm("teacher-1", now);

            var expired = Create("confirmar_lancamento_lote_moodle");
            expired.Authorize("teacher-1", now);
            Assert.True(expired.BeginExecution("old-worker", now.AddMinutes(-20), TimeSpan.FromMinutes(5)));

            var active = Create("confirmar_lancamento_lote_moodle");
            active.Authorize("teacher-1", now);
            Assert.True(active.BeginExecution("active-worker", now, TimeSpan.FromMinutes(5)));

            var uncertain = Create("criar_previa_lancamento_lote");
            uncertain.MarkExecutionUnknown("resposta perdida");

            var generic = Create("confirmar_mensagem");
            generic.Authorize("teacher-1", now);

            seed.PendingMoodleActions.AddRange(authorized, legacyConfirmed, expired, active, uncertain, generic);
            await seed.SaveChangesAsync();
        }

        await using var db = new ConnectorDbContext(options);
        var repository = new PendingMoodleActionRepository(db);
        var result = await repository.ListRecoverableGradingPublicationsAsync(now, 100, CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, action => action.ToolName == "criar_previa_lancamento_lote");
        Assert.Contains(result, action => action.Status == PendingActionStatus.Confirmed);
        Assert.Contains(result, action => action.ToolName == "confirmar_lancamento_lote_moodle" && action.ExecutionOwner == "old-worker");
        Assert.DoesNotContain(result, action => action.ExecutionOwner == "active-worker");
        Assert.DoesNotContain(result, action => action.Status == PendingActionStatus.ExecutionUnknown);
        Assert.DoesNotContain(result, action => action.ToolName == "confirmar_mensagem");
    }

    [Fact]
    public async Task ListRecoverableGradingPublications_IncluiSomenteRetriesParciaisJaVencidos()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"pending-actions-scheduled-retry-{Guid.NewGuid():N}")
            .Options;
        var now = DateTimeOffset.UtcNow;
        var due = Create("criar_previa_lancamento_lote");
        due.MarkPartiallyCompleted("retry seguro", now.AddSeconds(-1));
        var future = Create("criar_previa_lancamento_lote");
        future.MarkPartiallyCompleted("retry ainda aguardando", now.AddMinutes(1));
        await using (var seed = new ConnectorDbContext(options))
        {
            seed.PendingMoodleActions.AddRange(due, future);
            await seed.SaveChangesAsync();
        }

        await using var db = new ConnectorDbContext(options);
        var repository = new PendingMoodleActionRepository(db);
        var result = await repository.ListRecoverableGradingPublicationsAsync(now, 100, CancellationToken.None);

        Assert.Contains(result, action => action.Id == due.Id);
        Assert.DoesNotContain(result, action => action.Id == future.Id);
    }

    [Fact]
    public async Task TryBeginExecution_ConsomeRetryParcialVencidoEApagaAgendamento()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"pending-actions-begin-retry-{Guid.NewGuid():N}")
            .Options;
        var now = DateTimeOffset.UtcNow;
        var action = Create("criar_previa_lancamento_lote");
        action.MarkPartiallyCompleted("retry seguro", now.AddSeconds(-1));
        await using (var seed = new ConnectorDbContext(options))
        {
            seed.PendingMoodleActions.Add(action);
            await seed.SaveChangesAsync();
        }

        await using var db = new ConnectorDbContext(options);
        var repository = new PendingMoodleActionRepository(db);
        var result = await repository.TryBeginExecutionAsync(
            action.Id,
            "retry-worker",
            now,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.True(result.Claimed);
        var reloaded = await repository.GetByIdAsync(action.Id, CancellationToken.None);
        Assert.Equal(PendingActionStatus.Executing, reloaded!.Status);
        Assert.Null(reloaded.NextExecutionAttemptAt);
        Assert.Equal(1, reloaded.ExecutionAttemptCount);
    }

    [Fact]
    public async Task ListTerminalPublicationIds_EncontraClaimsQuePrecisamDeLimpezaAposCrash()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"terminal-publications-{Guid.NewGuid():N}")
            .Options;
        var executedPublicationId = Guid.NewGuid();
        var partialPublicationId = Guid.NewGuid();
        var unknownPublicationId = Guid.NewGuid();

        var executed = Create(
            "criar_previa_lancamento_lote",
            JsonSerializer.Serialize(new { publicationId = executedPublicationId }));
        executed.MarkExecuted();
        var partial = Create(
            "confirmar_lancamento_lote_moodle",
            JsonSerializer.Serialize(new { publicationId = partialPublicationId }));
        partial.MarkPartiallyCompleted("um item falhou");
        var scheduledPublicationId = Guid.NewGuid();
        var scheduledPartial = Create(
            "confirmar_lancamento_lote_moodle",
            JsonSerializer.Serialize(new { publicationId = scheduledPublicationId }));
        scheduledPartial.MarkPartiallyCompleted("retry agendado", DateTimeOffset.UtcNow.AddMinutes(1));
        var unknown = Create(
            "confirmar_lancamento_lote_moodle",
            JsonSerializer.Serialize(new { publicationId = unknownPublicationId }));
        unknown.MarkExecutionUnknown("resposta perdida");

        await using (var seed = new ConnectorDbContext(options))
        {
            seed.PendingMoodleActions.AddRange(executed, partial, scheduledPartial, unknown);
            await seed.SaveChangesAsync();
        }

        await using var db = new ConnectorDbContext(options);
        var repository = new PendingMoodleActionRepository(db);
        var result = await repository.ListTerminalGradingPublicationIdsAsync(100, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(executedPublicationId, result);
        Assert.Contains(partialPublicationId, result);
        Assert.DoesNotContain(scheduledPublicationId, result);
        Assert.DoesNotContain(unknownPublicationId, result);
    }

    [Fact]
    public async Task TryAuthorizeWithAudit_ReenfileiraPublicacaoParcialAposRetryExplicito()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"partial-retry-{Guid.NewGuid():N}")
            .ConfigureWarnings(builder => builder.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var action = Create("confirmar_lancamento_lote_moodle");
        action.MarkPartiallyCompleted("um item falhou");
        await using (var seed = new ConnectorDbContext(options))
        {
            seed.PendingMoodleActions.Add(action);
            await seed.SaveChangesAsync();
        }

        await using var db = new ConnectorDbContext(options);
        var repository = new PendingMoodleActionRepository(db);
        var result = await repository.TryAuthorizeWithAuditAsync(
            action.Id,
            "teacher-1",
            DateTimeOffset.UtcNow.AddHours(1),
            new MoodleAuditLog
            {
                CorrelationId = action.CorrelationId,
                ToolName = action.ToolName,
                RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
                ActorSubject = "teacher-1",
                Status = "authorized"
            },
            CancellationToken.None);

        Assert.True(result.ConfirmedByCaller);
        Assert.Equal(PendingActionStatus.Authorized, result.Status);
        var reloaded = await repository.GetByIdAsync(action.Id, CancellationToken.None);
        Assert.Equal(PendingActionStatus.Authorized, reloaded!.Status);
    }

    [Fact]
    public async Task TryAuthorizeWithAudit_ExpiredGradingPreviewCannotBeReused()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"expired-grading-preview-{Guid.NewGuid():N}")
            .ConfigureWarnings(builder => builder.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var action = Create(
            "criar_previa_lancamento_lote",
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        await using (var seed = new ConnectorDbContext(options))
        {
            seed.PendingMoodleActions.Add(action);
            await seed.SaveChangesAsync();
        }

        await using var db = new ConnectorDbContext(options);
        var repository = new PendingMoodleActionRepository(db);
        var result = await repository.TryAuthorizeWithAuditAsync(
            action.Id,
            "teacher-1",
            DateTimeOffset.UtcNow,
            new MoodleAuditLog
            {
                CorrelationId = action.CorrelationId,
                ToolName = action.ToolName,
                RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
                ActorSubject = "teacher-1",
                Status = "authorized"
            },
            CancellationToken.None);

        Assert.False(result.ConfirmedByCaller);
        Assert.Equal(PendingActionStatus.Expired, result.Status);
        Assert.Equal(PendingActionStatus.Expired,
            (await repository.GetByIdAsync(action.Id, CancellationToken.None))!.Status);
        Assert.Empty(db.MoodleAuditLogs);
    }

    [Fact]
    public async Task ListGradingPublicationsByBatchId_EncontraAcoesAntigasMasNaoAcoesDeOutroTipo()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"pending-actions-by-batch-{Guid.NewGuid():N}")
            .Options;
        var batchId = Guid.NewGuid();
        var target = Create(
            "confirmar_lancamento_lote_moodle",
            JsonSerializer.Serialize(new { batchJobId = batchId, publicationId = Guid.NewGuid() }));
        target.Authorize("teacher-1", DateTimeOffset.UtcNow);
        var unknown = Create(
            "criar_previa_lancamento_lote",
            JsonSerializer.Serialize(new { batchJobId = batchId, publicationId = Guid.NewGuid() }));
        unknown.MarkExecutionUnknown("resposta perdida");
        var unrelated = Create(
            "confirmar_lancamento_lote_moodle",
            JsonSerializer.Serialize(new { batchJobId = Guid.NewGuid() }));

        await using (var seed = new ConnectorDbContext(options))
        {
            seed.PendingMoodleActions.AddRange(target, unknown, unrelated);
            await seed.SaveChangesAsync();
        }

        await using var db = new ConnectorDbContext(options);
        var repository = new PendingMoodleActionRepository(db);
        var result = await repository.ListGradingPublicationsByBatchIdAsync(batchId, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, action => action.Status == PendingActionStatus.Authorized);
        Assert.Contains(result, action => action.Status == PendingActionStatus.ExecutionUnknown);
        Assert.DoesNotContain(result, action => action.PayloadJson.Contains(unrelated.Id.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListGradingPublicationsByBatchIds_EncontraAcoesDeVariosSublotes()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"pending-actions-by-batches-{Guid.NewGuid():N}")
            .Options;
        var firstBatchId = Guid.NewGuid();
        var secondBatchId = Guid.NewGuid();
        var unrelatedBatchId = Guid.NewGuid();
        var first = Create(
            "criar_previa_lancamento_lote",
            JsonSerializer.Serialize(new { batchJobId = firstBatchId }));
        var second = Create(
            "confirmar_lancamento_lote_moodle",
            JsonSerializer.Serialize(new { batchJobId = secondBatchId }));
        var unrelated = Create(
            "confirmar_lancamento_lote_moodle",
            JsonSerializer.Serialize(new { batchJobId = unrelatedBatchId }));

        await using (var seed = new ConnectorDbContext(options))
        {
            seed.PendingMoodleActions.AddRange(first, second, unrelated);
            await seed.SaveChangesAsync();
        }

        await using var db = new ConnectorDbContext(options);
        var repository = new PendingMoodleActionRepository(db);
        var result = await repository.ListGradingPublicationsByBatchIdsAsync(
            [firstBatchId, secondBatchId, Guid.Empty],
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, action => action.Id == first.Id);
        Assert.Contains(result, action => action.Id == second.Id);
        Assert.DoesNotContain(result, action => action.Id == unrelated.Id);
    }

    private static PendingMoodleAction Create(
        string toolName,
        string payloadJson = "{}",
        DateTimeOffset? expiresAt = null) => new()
    {
        ToolName = toolName,
        RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
        CreatedBySubject = "teacher-1",
        PayloadJson = payloadJson,
        PreviewJson = "{}",
        ConfirmationText = "CONFIRMAR_PUBLICACAO",
        ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        CorrelationId = Guid.NewGuid().ToString("N")
    };
}
