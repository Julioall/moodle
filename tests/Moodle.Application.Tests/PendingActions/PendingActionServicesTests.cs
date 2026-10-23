using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.PendingActions;
using Moodle.Application.Tools;
using Moodle.Domain;

namespace Moodle.Application.Tests.PendingActions;

public sealed class PendingActionServicesTests
{
    [Fact]
    public void PendingActionOptions_DefaultsUseVolatile48HoursAndStable168Hours()
    {
        var options = new PendingActionOptions();

        Assert.Equal(TimeSpan.FromHours(48), options.ResolveExpiration(PendingActionTtlClass.Volatile));
        Assert.Equal(TimeSpan.FromHours(168), options.ResolveExpiration(PendingActionTtlClass.Stable));
    }

    [Fact]
    public async Task CreatePendingActionAsync_CreatesPendingActionAndAuditLog()
    {
        var fixture = new Fixture();

        var response = await fixture.PendingService.CreatePendingActionAsync(
            "demo_tool",
            ToolRiskLevel.HumanConfirmedWrite,
            new { value = "secret" },
            new { value = "preview" },
            "CONFIRMAR",
            TimeSpan.FromMinutes(15),
            courseId: 10,
            CancellationToken.None);

        Assert.Equal("pending_confirmation", response.Status);
        Assert.Single(fixture.PendingRepository.Actions);
        Assert.Single(fixture.AuditRepository.Logs);
        Assert.Equal("pending_created", fixture.AuditRepository.Logs[0].Status);
    }

    [Fact]
    public async Task CreatePendingActionAsync_SanitizaPayloadPreviewEAuditoria()
    {
        var fixture = new Fixture();

        var response = await fixture.PendingService.CreatePendingActionAsync(
            "demo_tool",
            ToolRiskLevel.HumanConfirmedWrite,
            new { password = "senha-real", value = "payload" },
            new
            {
                token = "token-real",
                url = "https://moodle.tests/course/view.php?id=10&wstoken=abc&sesskey=xyz",
                value = "preview"
            },
            "CONFIRMAR",
            TimeSpan.FromMinutes(15),
            courseId: 10,
            CancellationToken.None);

        var action = Assert.Single(fixture.PendingRepository.Actions);
        var auditLog = Assert.Single(fixture.AuditRepository.Logs);
        var preview = Assert.IsType<JsonElement>(response.Preview);

        Assert.DoesNotContain("senha-real", action.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("token-real", action.PreviewJson, StringComparison.Ordinal);
        Assert.DoesNotContain("wstoken", action.PreviewJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("[REDACTED]", preview.GetProperty("token").GetString());
        Assert.Equal("https://moodle.tests/course/view.php?id=10", preview.GetProperty("url").GetString());
        Assert.Equal(action.PreviewJson, auditLog.RequestSanitizedJson);
    }

    [Fact]
    public async Task ConfirmAsync_WithExactText_ConfirmsAndAudits()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("CONFIRMAR");

        var response = await fixture.ConfirmationService.ConfirmAsync(
            pending.PendingActionId,
            "CONFIRMAR",
            requiredScope: null,
            CancellationToken.None);

        Assert.Equal("confirmed", response.Status);
        Assert.Equal(PendingActionStatus.Confirmed, fixture.PendingRepository.Actions[0].Status);
        Assert.Contains(fixture.AuditRepository.Logs, log => log.Status == "confirmed");
    }

    [Fact]
    public async Task ConfirmAsync_WithWrongText_Throws()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("CONFIRMAR");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ConfirmationService.ConfirmAsync(pending.PendingActionId, "ERRADO", null, CancellationToken.None));

        Assert.Equal("Texto de confirmacao invalido.", ex.Message);
        Assert.Equal(PendingActionStatus.PendingConfirmation, fixture.PendingRepository.Actions[0].Status);
    }

    [Fact]
    public async Task ConfirmAsync_GradingPublication_DoesNotRequireLiteralConfirmationText()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("CONFIRMAR_PUBLICACAO", "criar_previa_lancamento_lote");

        var response = await fixture.ConfirmationService.ConfirmAsync(
            pending.PendingActionId,
            null,
            requiredScope: null,
            CancellationToken.None);

        Assert.Equal("authorized", response.Status);
        Assert.Equal(PendingActionStatus.Authorized, fixture.PendingRepository.Actions[0].Status);
        Assert.Contains(fixture.AuditRepository.Logs, log => log.Status == "authorized");
    }

    [Fact]
    public async Task ConfirmAsync_ScheduledGradingRetryDoesNotRequireAnotherAuthorization()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("", "criar_previa_lancamento_lote");
        var action = Assert.Single(fixture.PendingRepository.Actions);
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(10);
        action.MarkPartiallyCompleted("retry seguro", retryAt);

        var response = await fixture.ConfirmationService.ConfirmAsync(
            pending.PendingActionId,
            confirmationText: null,
            requiredScope: "moodle.write.assignments.grade",
            CancellationToken.None);

        Assert.Equal("authorized", response.Status);
        Assert.Equal(PendingActionStatus.PartiallyCompleted, action.Status);
        Assert.Equal(retryAt, action.NextExecutionAttemptAt);
        Assert.DoesNotContain(fixture.AuditRepository.Logs, log => log.Status == "authorized");
    }

    [Fact]
    public async Task ConfirmAsync_WhenExpired_MarksExpiredAndThrows()
    {
        var fixture = new Fixture();
        var pending = await fixture.PendingService.CreatePendingActionAsync(
            "demo_tool",
            ToolRiskLevel.HumanConfirmedWrite,
            new { value = "payload" },
            new { value = "preview" },
            "CONFIRMAR",
            TimeSpan.FromMinutes(-1),
            null,
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ConfirmationService.ConfirmAsync(pending.PendingActionId, "CONFIRMAR", null, CancellationToken.None));

        Assert.Equal("A acao pendente expirou.", ex.Message);
        Assert.Equal(PendingActionStatus.Expired, fixture.PendingRepository.Actions[0].Status);
    }

    [Fact]
    public async Task ConfirmAsync_FromDifferentUserWithoutAdminScope_Throws()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("CONFIRMAR");
        fixture.CurrentUser.Subject = "other-user";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ConfirmationService.ConfirmAsync(pending.PendingActionId, "CONFIRMAR", null, CancellationToken.None));

        Assert.Equal("Apenas o criador da acao ou um administrador Moodle pode confirma-la.", ex.Message);
        Assert.Contains(fixture.AuthorizationAudit.Requests, request => request.Reason == "pending_action_actor_mismatch");
    }

    [Fact]
    public async Task ConfirmAsync_SecondConfirmation_ReturnsAlreadyConfirmedWithoutDuplicateAudit()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("CONFIRMAR");

        await fixture.ConfirmationService.ConfirmAsync(pending.PendingActionId, "CONFIRMAR", null, CancellationToken.None);
        var auditCountAfterFirstConfirmation = fixture.AuditRepository.Logs.Count;

        var response = await fixture.ConfirmationService.ConfirmAsync(
            pending.PendingActionId,
            "CONFIRMAR",
            null,
            CancellationToken.None);

        Assert.Equal("already_confirmed", response.Status);
        Assert.Equal(auditCountAfterFirstConfirmation, fixture.AuditRepository.Logs.Count);
    }

    [Fact]
    public async Task ConfirmAsync_PartiallyCompletedRequiresExplicitRetryAndReauthorizes()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("CONFIRMAR", "confirmar_lancamento_lote_moodle");
        fixture.PendingRepository.Actions[0].MarkPartiallyCompleted("um item falhou");

        var response = await fixture.ConfirmationService.ConfirmAsync(
            pending.PendingActionId,
            "CONFIRMAR",
            requiredScope: null,
            CancellationToken.None);

        Assert.Equal("authorized", response.Status);
        Assert.Equal(PendingActionStatus.Authorized, fixture.PendingRepository.Actions[0].Status);
        Assert.Contains(fixture.AuditRepository.Logs, log => log.Status == "authorized");
    }

    [Fact]
    public async Task ConfirmAsync_ConcurrentRequests_HasOneClaimAndOneConfirmationAudit()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("CONFIRMAR");

        var results = await Task.WhenAll(
            fixture.ConfirmationService.ConfirmAsync(pending.PendingActionId, "CONFIRMAR", null, CancellationToken.None),
            fixture.ConfirmationService.ConfirmAsync(pending.PendingActionId, "CONFIRMAR", null, CancellationToken.None));

        Assert.Single(results, result => result.Status == "confirmed");
        Assert.Single(results, result => result.Status == "already_confirmed");
        Assert.Single(fixture.AuditRepository.Logs, log => log.Status == "confirmed");
    }

    [Fact]
    public async Task ConfirmAsync_SecondConfirmationFromDifferentUserWithoutAdminScope_Throws()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("CONFIRMAR");

        await fixture.ConfirmationService.ConfirmAsync(pending.PendingActionId, "CONFIRMAR", null, CancellationToken.None);
        fixture.CurrentUser.Subject = "other-user";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ConfirmationService.ConfirmAsync(
                pending.PendingActionId,
                "CONFIRMAR",
                null,
                CancellationToken.None));

        Assert.Equal("Apenas o criador da acao ou um administrador Moodle pode confirma-la.", ex.Message);
        Assert.Contains(fixture.AuthorizationAudit.Requests, request => request.Reason == "pending_action_actor_mismatch");
    }

    [Fact]
    public async Task ConfirmAsync_SecondConfirmationWithWrongText_Throws()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("CONFIRMAR");

        await fixture.ConfirmationService.ConfirmAsync(pending.PendingActionId, "CONFIRMAR", null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ConfirmationService.ConfirmAsync(
                pending.PendingActionId,
                "ERRADO",
                null,
                CancellationToken.None));

        Assert.Equal("Texto de confirmacao invalido.", ex.Message);
    }

    [Fact]
    public async Task ConfirmAsync_RequiresConfiguredScope()
    {
        var fixture = new Fixture();
        var pending = await fixture.CreatePendingAsync("CONFIRMAR");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ConfirmationService.ConfirmAsync(
                pending.PendingActionId,
                "CONFIRMAR",
                "moodle.write.messages",
                CancellationToken.None));

        Assert.Equal("Escopo obrigatorio ausente: moodle.write.messages.", ex.Message);
        Assert.Contains(fixture.AuthorizationAudit.Requests, request => request.Reason == "missing_required_scope");
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            PendingRepository.AuditRepository = AuditRepository;
            PendingService = new PendingActionService(PendingRepository, AuditRepository, CurrentUser, MoodleUserResolver);
            ConfirmationService = new ActionConfirmationService(PendingRepository, CurrentUser, MoodleUserResolver, AuthorizationAudit);
        }

        public FakePendingActionRepository PendingRepository { get; } = new();
        public FakeAuditLogRepository AuditRepository { get; } = new();
        public FakeCurrentUserContext CurrentUser { get; } = new();
        public FakeMoodleUserResolver MoodleUserResolver { get; } = new();
        public FakeAuthorizationAuditService AuthorizationAudit { get; } = new();
        public PendingActionService PendingService { get; }
        public ActionConfirmationService ConfirmationService { get; }

        public Task<PendingActionResponse> CreatePendingAsync(
            string confirmationText,
            string toolName = "demo_tool")
        {
            return PendingService.CreatePendingActionAsync(
                toolName,
                ToolRiskLevel.HumanConfirmedWrite,
                new { value = "payload" },
                new { value = "preview" },
                confirmationText,
                TimeSpan.FromMinutes(15),
                courseId: null,
                CancellationToken.None);
        }
    }

    private sealed class FakePendingActionRepository : IPendingMoodleActionRepository
    {
        public List<PendingMoodleAction> Actions { get; } = [];
        public FakeAuditLogRepository? AuditRepository { get; set; }

        public Task AddAsync(PendingMoodleAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task<PendingMoodleAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Actions.SingleOrDefault(action => action.Id == id));
        }

        public Task<PendingActionConfirmationClaimResult> TryConfirmWithAuditAsync(
            Guid id,
            string confirmedBySubject,
            DateTimeOffset confirmedAt,
            MoodleAuditLog confirmationAudit,
            CancellationToken cancellationToken)
        {
            lock (Actions)
            {
                var action = Actions.SingleOrDefault(candidate => candidate.Id == id)
                    ?? throw new InvalidOperationException("Acao pendente nao encontrada.");
                if (action.Status != PendingActionStatus.PendingConfirmation)
                {
                    return Task.FromResult(new PendingActionConfirmationClaimResult(false, action.Status, action.ConfirmedAt));
                }

                if (action.ExpiresAt <= confirmedAt)
                {
                    action.MarkExpired();
                    return Task.FromResult(new PendingActionConfirmationClaimResult(false, action.Status, action.ConfirmedAt));
                }

                action.Confirm(confirmedBySubject, confirmedAt);
                AuditRepository?.Logs.Add(confirmationAudit);
                return Task.FromResult(new PendingActionConfirmationClaimResult(true, action.Status, action.ConfirmedAt));
            }
        }

        public Task<PendingActionConfirmationClaimResult> TryAuthorizeWithAuditAsync(
            Guid id,
            string confirmedBySubject,
            DateTimeOffset confirmedAt,
            MoodleAuditLog confirmationAudit,
            CancellationToken cancellationToken)
        {
            lock (Actions)
            {
                var action = Actions.SingleOrDefault(candidate => candidate.Id == id)
                    ?? throw new InvalidOperationException("Acao pendente nao encontrada.");
                if (action.Status is not (PendingActionStatus.PendingConfirmation or PendingActionStatus.PartiallyCompleted))
                {
                    return Task.FromResult(new PendingActionConfirmationClaimResult(false, action.Status, action.ConfirmedAt));
                }

                if (action.ExpiresAt <= confirmedAt && action.Status != PendingActionStatus.PartiallyCompleted)
                {
                    action.MarkExpired();
                    return Task.FromResult(new PendingActionConfirmationClaimResult(false, action.Status, action.ConfirmedAt));
                }

                action.Authorize(confirmedBySubject, confirmedAt);
                AuditRepository?.Logs.Add(confirmationAudit);
                return Task.FromResult(new PendingActionConfirmationClaimResult(true, action.Status, action.ConfirmedAt));
            }
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditLogRepository : IMoodleAuditLogRepository
    {
        public List<MoodleAuditLog> Logs { get; } = [];

        public Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(
            string correlationId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var items = Logs
                .Where(log => log.CorrelationId == correlationId)
                .OrderBy(log => log.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            return Task.FromResult<IReadOnlyList<MoodleAuditLog>>(items);
        }

        public Task<int> CountByCorrelationIdAsync(
            string correlationId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Logs.Count(log => log.CorrelationId == correlationId));
        }

        public Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(
            Guid batchJobId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var items = Logs
                .Where(log => log.BatchJobId == batchJobId)
                .OrderBy(log => log.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            return Task.FromResult<IReadOnlyList<MoodleAuditLog>>(items);
        }

        public Task<int> CountByBatchJobIdAsync(Guid batchJobId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Logs.Count(log => log.BatchJobId == batchJobId));
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCurrentUserContext : ICurrentUserContext
    {
        public string Subject { get; set; } = "user-1";
        public string? Email { get; set; } = "user@example.com";
        public IReadOnlyCollection<string> Scopes { get; set; } = [];

        public bool HasScope(string scope)
        {
            return Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class FakeMoodleUserResolver : IMoodleUserResolver
    {
        public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<long?>(123);
        }
    }

    private sealed class FakeAuthorizationAuditService : IAuthorizationAuditService
    {
        public List<AuthorizationFailureAuditRequest> Requests { get; } = [];

        public Task RecordFailureAsync(AuthorizationFailureAuditRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
