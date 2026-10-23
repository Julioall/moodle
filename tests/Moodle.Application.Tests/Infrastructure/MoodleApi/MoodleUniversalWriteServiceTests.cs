using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Application.PendingActions;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleUniversalWriteServiceTests
{
    [Fact]
    public async Task PrepareAsync_NaoChamaMoodleEAguardaConfirmacao()
    {
        var rest = new FakeRestClient();
        var pendingActions = new FakePendingActions();
        var sut = CreateService(rest, pendingActions, enabled: true);

        var preview = await sut.PrepareAsync(
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["assignmentid"] = 10, ["userid"] = 20, ["grade"] = 85 },
            CancellationToken.None);

        Assert.Equal(0, rest.Calls);
        Assert.Equal("mod_assign_save_grade", preview.Function);
        Assert.Equal(1, preview.EstimatedAffectedRecords);
        Assert.Contains("grade", preview.ParameterNames);
        Assert.StartsWith("CONFIRMAR ESCRITA MOODLE", preview.ConfirmationText, StringComparison.Ordinal);
        Assert.NotNull(pendingActions.Action);
    }

    [Fact]
    public async Task PrepareAsync_ExponePreviewSemanticoEEstimativaDeRecursos()
    {
        using var document = JsonDocument.Parse("[{\"touserid\":211211,\"text\":\"Ola\"},{\"touserid\":211212,\"text\":\"Oi\"}]");
        var sut = CreateService(
            new FakeRestClient(),
            new FakePendingActions(),
            enabled: true,
            profile: Profile(new MoodleFunctionDescriptor("core_message_send_instant_messages", MoodleFunctionRisk.ControlledWrite, true)),
            currentUser: new FakeCurrentUser("moodle.write.messages"));

        var preview = await sut.PrepareAsync(
            "core_message_send_instant_messages",
            new Dictionary<string, object?> { ["messages"] = document.RootElement.Clone() },
            CancellationToken.None);

        Assert.Equal("Enviar 2 mensagem(ns) Moodle aos destinatários informados.", preview.SemanticSummary);
        Assert.Equal(2, preview.EstimatedAffectedRecords);
        Assert.Contains("message", preview.AffectedResources!);
        var change = Assert.Single(preview.Changes!, item => item.Name == "messages");
        Assert.Equal("[2 itens]", change.NewValue);
        Assert.Contains(preview.Warnings!, warning => warning.Contains("Valores anteriores", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareAsync_EstimativaDeLoteUsaQuantidadeEfetiva()
    {
        using var document = JsonDocument.Parse("[{\"assignmentid\":10},{\"assignmentid\":11},{\"assignmentid\":12}]");
        var sut = CreateService(
            new FakeRestClient(),
            new FakePendingActions(),
            enabled: true,
            profile: Profile(new MoodleFunctionDescriptor("mod_assign_save_grades", MoodleFunctionRisk.ControlledWrite, true)),
            currentUser: new FakeCurrentUser("moodle.write.assignments.grade"));

        var preview = await sut.PrepareAsync(
            "mod_assign_save_grades",
            new Dictionary<string, object?> { ["grades"] = document.RootElement.Clone() },
            CancellationToken.None);

        Assert.Equal(3, preview.EstimatedAffectedRecords);
    }

    [Fact]
    public async Task PrepareAsync_EstimativaDeCalendarioUsaQuantidadeEfetiva()
    {
        using var document = JsonDocument.Parse("[{\"courseid\":10},{\"courseid\":11}]");
        var sut = CreateService(
            new FakeRestClient(),
            new FakePendingActions(),
            enabled: true,
            profile: Profile(new MoodleFunctionDescriptor("core_calendar_create_calendar_events", MoodleFunctionRisk.ControlledWrite, true)),
            currentUser: new FakeCurrentUser("moodle.write.course_content"));

        var preview = await sut.PrepareAsync(
            "core_calendar_create_calendar_events",
            new Dictionary<string, object?> { ["events"] = document.RootElement.Clone() },
            CancellationToken.None);

        Assert.Equal(2, preview.EstimatedAffectedRecords);
    }

    [Fact]
    public async Task PrepareAsync_NaoInventaImpactoQuandoLoteNaoFoiInformado()
    {
        var sut = CreateService(
            new FakeRestClient(),
            new FakePendingActions(),
            enabled: true,
            profile: Profile(new MoodleFunctionDescriptor("mod_assign_save_grades", MoodleFunctionRisk.ControlledWrite, true)),
            currentUser: new FakeCurrentUser("moodle.write.assignments.grade"));

        var preview = await sut.PrepareAsync(
            "mod_assign_save_grades",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        Assert.Null(preview.EstimatedAffectedRecords);
        Assert.Contains(preview.Warnings!, warning => warning.Contains("quantidade de registros afetados", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmAsync_UsaTokenDoUsuarioEExecutaUmaVez()
    {
        var rest = new FakeRestClient();
        var pendingActions = new FakePendingActions();
        var auditLogs = new FakeAuditLogs();
        var confirmation = new FakeConfirmation();
        var sut = CreateService(rest, pendingActions, enabled: true, auditLogs: auditLogs, confirmation: confirmation);
        var preview = await sut.PrepareAsync(
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["assignmentid"] = 10, ["userid"] = 20, ["grade"] = 85 },
            CancellationToken.None);

        var result = await sut.ConfirmAsync(preview.PendingActionId, preview.ConfirmationText, CancellationToken.None);

        Assert.Equal("executed", result.Status);
        Assert.Equal(1, rest.Calls);
        Assert.False(rest.LastAllowServiceToken);
        Assert.Equal("moodle.write.assignments.grade", confirmation.LastRequiredScope);
        var prepared = Assert.Single(auditLogs.Logs, log => log.Status == "write_prepared");
        Assert.Equal(preview.PendingActionId, prepared.PendingActionId);
        Assert.Equal("connection", prepared.MoodleConnectionId);
        var audit = Assert.Single(auditLogs.Logs, log => log.Status == "write_executed");
        Assert.Equal(preview.PendingActionId, audit.PendingActionId);
        Assert.Equal("goias", audit.MoodleConnectionAlias);
        Assert.NotNull(audit.StartedAt);
        Assert.NotNull(audit.FinishedAt);
        using var auditResponse = JsonDocument.Parse(audit.ResponseSummaryJson);
        Assert.True(auditResponse.RootElement.TryGetProperty("durationMs", out var duration));
        Assert.True(duration.GetInt64() >= 0);
    }

    [Fact]
    public async Task ConfirmAsync_FalhaDeRedeMarcaExecucaoComoDesconhecidaENaoComoFalhaComum()
    {
        var rest = new FakeRestClient { WriteException = new HttpRequestException("connection reset") };
        var pendingActions = new FakePendingActions();
        var auditLogs = new FakeAuditLogs();
        var sut = CreateService(rest, pendingActions, enabled: true, auditLogs: auditLogs);
        var preview = await sut.PrepareAsync(
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["assignmentid"] = 10, ["userid"] = 20, ["grade"] = 85 },
            CancellationToken.None);
        pendingActions.Action!.Confirm("user", DateTimeOffset.UtcNow);

        var result = await sut.ConfirmAsync(preview.PendingActionId, preview.ConfirmationText, CancellationToken.None);

        Assert.Equal("execution_unknown", result.Status);
        Assert.Equal(PendingActionStatus.ExecutionUnknown, pendingActions.Action!.Status);
        Assert.Contains(auditLogs.Logs, log => log.Status == "write_execution_unknown");
        Assert.DoesNotContain(auditLogs.Logs, log => log.Status == "write_failed");
    }

    [Fact]
    public async Task ConfirmAsync_RecusaLocalAntesDoEnvioPermaneceFalhaDefinitiva()
    {
        var admissionRejection = new MoodleApiException(
            MoodleErrorContract.MoodleAdmissionRejected,
            "Local admission refused the write before transport.");
        var rest = new FakeRestClient { WriteException = admissionRejection };
        var pendingActions = new FakePendingActions();
        var auditLogs = new FakeAuditLogs();
        var sut = CreateService(rest, pendingActions, enabled: true, auditLogs: auditLogs);
        var preview = await sut.PrepareAsync(
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["assignmentid"] = 10, ["userid"] = 20, ["grade"] = 85 },
            CancellationToken.None);
        pendingActions.Action!.Confirm("user", DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.ConfirmAsync(
            preview.PendingActionId,
            preview.ConfirmationText,
            CancellationToken.None));

        Assert.Equal(MoodleErrorContract.MoodleAdmissionRejected, error.ErrorCode);
        Assert.Equal(1, rest.Calls);
        Assert.NotEqual(PendingActionStatus.ExecutionUnknown, pendingActions.Action!.Status);
        var failure = Assert.Single(auditLogs.Logs, log => log.Status == "write_failed");
        Assert.Equal(MoodleErrorContract.MoodleAdmissionRejected, failure.ErrorCode);
        Assert.DoesNotContain(auditLogs.Logs, log => log.Status == "write_execution_unknown");
    }

    [Fact]
    public async Task PrepareAsync_PermiteFuncaoDestrutivaSomenteNoFluxoDeConfirmacao()
    {
        var profile = Profile(new MoodleFunctionDescriptor("core_course_delete_courses", MoodleFunctionRisk.Destructive, true));
        var sut = CreateService(
            new FakeRestClient(),
            new FakePendingActions(),
            enabled: true,
            profile: profile,
            currentUser: new FakeCurrentUser("moodle.write"));

        var preview = await sut.PrepareAsync(
            "core_course_delete_courses", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal("core_course_delete_courses", preview.Function);
    }

    [Fact]
    public async Task PrepareAsync_ExigeEscopoGenericoParaFuncaoSemFamiliaEspecializada()
    {
        var profile = Profile(new MoodleFunctionDescriptor("mod_assign_set_user_flags", MoodleFunctionRisk.ControlledWrite, true));
        var sut = CreateService(new FakeRestClient(), new FakePendingActions(), enabled: true, profile: profile);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.PrepareAsync(
            "mod_assign_set_user_flags", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal("moodle_write_scope_required", error.ErrorCode);
    }

    [Fact]
    public async Task PrepareAsync_PermiteFuncaoControladaComEscopoGenerico()
    {
        var profile = Profile(new MoodleFunctionDescriptor("mod_assign_set_user_flags", MoodleFunctionRisk.ControlledWrite, true));
        var sut = CreateService(
            new FakeRestClient(),
            new FakePendingActions(),
            enabled: true,
            profile: profile,
            currentUser: new FakeCurrentUser("moodle.write"));

        var preview = await sut.PrepareAsync(
            "mod_assign_set_user_flags", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal("mod_assign_set_user_flags", preview.Function);
    }

    [Fact]
    public async Task PrepareAsync_BloqueiaEscoposVaziosMesmoParaFuncaoConhecida()
    {
        var sut = CreateService(
            new FakeRestClient(),
            new FakePendingActions(),
            enabled: true,
            currentUser: new FakeCurrentUser(string.Empty));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.PrepareAsync(
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["assignmentid"] = 10, ["userid"] = 20, ["grade"] = 85 },
            CancellationToken.None));

        Assert.Equal("moodle_write_scope_required", error.ErrorCode);
    }

    [Fact]
    public async Task PrepareAsync_ExigeEscopoDaFamiliaDeEscrita()
    {
        var user = new FakeCurrentUser("moodle.write.messages");
        var sut = CreateService(
            new FakeRestClient(),
            new FakePendingActions(),
            enabled: true,
            currentUser: user);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.PrepareAsync(
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["assignmentid"] = 10, ["userid"] = 20, ["grade"] = 85 },
            CancellationToken.None));

        Assert.Equal("moodle_write_scope_required", error.ErrorCode);
        Assert.Contains("moodle.write.assignments.grade", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAsync_AcaoJaConfirmadaAindaValidaConfirmacaoESemReexecutar()
    {
        var rest = new FakeRestClient();
        var pendingActions = new FakePendingActions();
        var confirmation = new FakeConfirmation { StatusToReturn = "already_confirmed" };
        var sut = CreateService(rest, pendingActions, enabled: true, confirmation: confirmation);
        var preview = await sut.PrepareAsync(
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["assignmentid"] = 10, ["userid"] = 20, ["grade"] = 85 },
            CancellationToken.None);
        pendingActions.Action!.Confirm("user", DateTimeOffset.UtcNow);

        var result = await sut.ConfirmAsync(preview.PendingActionId, preview.ConfirmationText, CancellationToken.None);

        Assert.Equal("already_confirmed", result.Status);
        Assert.Equal(0, rest.Calls);
        Assert.Equal(1, confirmation.Calls);
    }

    [Fact]
    public async Task PrepareAsync_RecusaQuandoFeatureFlagEstaDesabilitada()
    {
        var auditLogs = new FakeAuditLogs();
        var sut = CreateService(new FakeRestClient(), new FakePendingActions(), enabled: false, auditLogs: auditLogs);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.PrepareAsync(
            "mod_assign_save_grade", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Contains("UniversalMoodleWriteEnabled", error.Message);
        var audit = Assert.Single(auditLogs.Logs);
        Assert.Equal("write_prepare_blocked", audit.Status);
        Assert.Equal("InvalidOperationException", audit.ErrorCode);
        Assert.Equal("connection", audit.MoodleConnectionId);
    }

    [Fact]
    public async Task PrepareAsync_AceitaConteudoDeNegocioNaPrevia()
    {
        var sut = CreateService(new FakeRestClient(), new FakePendingActions(), enabled: true);

        var preview = await sut.PrepareAsync(
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["feedback"] = "conteúdo confidencial" },
            CancellationToken.None);

        Assert.Equal("mod_assign_save_grade", preview.Function);
    }

    [Fact]
    public async Task PrepareAsync_AceitaTextoDaMensagemComoConteudoDeNegocio()
    {
        using var document = JsonDocument.Parse("[{\"touserid\":211211,\"text\":\"Olá, Jefferson!\",\"textformat\":1}]");
        var pendingActions = new FakePendingActions();
        var sut = CreateService(
            new FakeRestClient(),
            pendingActions,
            enabled: true,
            profile: Profile(new MoodleFunctionDescriptor("core_message_send_instant_messages", MoodleFunctionRisk.ControlledWrite, true)),
            currentUser: new FakeCurrentUser("moodle.write.messages"));

        var preview = await sut.PrepareAsync(
            "core_message_send_instant_messages",
            new Dictionary<string, object?> { ["messages"] = document.RootElement.Clone() },
            CancellationToken.None);

        Assert.Equal("core_message_send_instant_messages", preview.Function);
        Assert.Contains("messages", preview.ParameterNames);
        using var payload = JsonDocument.Parse(pendingActions.Action!.PayloadJson);
        var message = payload.RootElement.GetProperty("Parameters").GetProperty("messages")[0];
        Assert.Equal(211211, message.GetProperty("touserid").GetInt64());
        Assert.Equal("Olá, Jefferson!", message.GetProperty("text").GetString());
        Assert.Equal(1, message.GetProperty("textformat").GetInt32());
    }

    [Fact]
    public async Task PrepareAsync_RecusaSegredoEmParametroAninhado()
    {
        var sut = CreateService(new FakeRestClient(), new FakePendingActions(), enabled: true);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.PrepareAsync(
            "mod_assign_save_grade",
            new Dictionary<string, object?>
            {
                ["users"] = new[] { new Dictionary<string, object?> { ["password"] = "segredo" } }
            },
            CancellationToken.None));

        Assert.Equal("sensitive_write_parameter_blocked", error.ErrorCode);
    }

    [Fact]
    public async Task PrepareAsync_AceitaContextIdQueNaoEConteudoSensivel()
    {
        var sut = CreateService(new FakeRestClient(), new FakePendingActions(), enabled: true);

        var preview = await sut.PrepareAsync(
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["contextid"] = 42 },
            CancellationToken.None);

        Assert.Equal("mod_assign_save_grade", preview.Function);
    }

    private static MoodleUniversalWriteService CreateService(
        FakeRestClient rest,
        FakePendingActions pendingActions,
        bool enabled,
        MoodleFunctionProfile? profile = null,
        FakeAuditLogs? auditLogs = null,
        FakeConfirmation? confirmation = null,
        ICurrentUserContext? currentUser = null) => new(
            new FakeCatalog(profile ?? Profile(new MoodleFunctionDescriptor("mod_assign_save_grade", MoodleFunctionRisk.ControlledWrite, true))),
            rest,
            new FakeCredentialsProvider(),
            pendingActions,
            confirmation ?? new FakeConfirmation(),
            pendingActions,
            auditLogs ?? new FakeAuditLogs(),
            Options.Create(new MoodleUniversalApiFeatureOptions { UniversalMoodleWriteEnabled = enabled }),
            currentUser ?? new FakeCurrentUser("moodle.write.assignments.grade"));

    private static MoodleFunctionProfile Profile(MoodleFunctionDescriptor descriptor) => new(
        "connection", "goias", "Moodle", "4.5", 7, [descriptor], DateTimeOffset.UtcNow);

    private sealed class FakeCatalog(MoodleFunctionProfile profile) : IMoodleFunctionCatalog
    {
        public Task<MoodleFunctionProfile> GetCurrentAsync(bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(profile);
    }

    private sealed class FakeCredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials("client", "connection", "goias", "https://moodle.example", "user", "password", "goias", true));
    }

    private sealed class FakeCurrentUser(string scope) : ICurrentUserContext
    {
        public string Subject => "user";
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes => [scope];
        public bool HasScope(string requestedScope) => Scopes.Contains(requestedScope, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakeRestClient : IMoodleRestClient
    {
        public int Calls { get; private set; }
        public bool LastAllowServiceToken { get; private set; } = true;
        public Exception? WriteException { get; set; }

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, true, cancellationToken);

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, bool allowServiceToken, CancellationToken cancellationToken)
        {
            Calls++;
            LastAllowServiceToken = allowServiceToken;
            using var document = JsonDocument.Parse("{\"result\":\"ok\"}");
            return Task.FromResult(document.RootElement.Clone());
        }

        public Task<JsonElement> CallWriteAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            Calls++;
            LastAllowServiceToken = false;
            if (WriteException is not null)
            {
                return Task.FromException<JsonElement>(WriteException);
            }

            using var document = JsonDocument.Parse("{\"result\":\"ok\"}");
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class FakePendingActions : IPendingActionService, IPendingMoodleActionRepository
    {
        public PendingMoodleAction? Action { get; private set; }

        public Task<PendingActionResponse> CreatePendingActionAsync(string toolName, ToolRiskLevel riskLevel, object payload, object preview, string confirmationText, TimeSpan expiresIn, long? courseId, CancellationToken cancellationToken)
        {
            Action = new PendingMoodleAction
            {
                ToolName = toolName,
                RiskLevel = riskLevel,
                CreatedBySubject = "user",
                PayloadJson = JsonSerializer.Serialize(payload),
                PreviewJson = JsonSerializer.Serialize(preview),
                ConfirmationText = confirmationText,
                ExpiresAt = DateTimeOffset.UtcNow.Add(expiresIn),
                CorrelationId = "correlation"
            };
            return Task.FromResult(new PendingActionResponse("pending_confirmation", Action.Id, toolName, riskLevel, preview, confirmationText, Action.ExpiresAt));
        }

        public Task AddAsync(PendingMoodleAction action, CancellationToken cancellationToken)
        {
            Action = action;
            return Task.CompletedTask;
        }

        public Task<PendingMoodleAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Action?.Id == id ? Action : null);

        public Task<PendingActionConfirmationClaimResult> TryConfirmWithAuditAsync(Guid id, string confirmedBySubject, DateTimeOffset confirmedAt, MoodleAuditLog confirmationAudit, CancellationToken cancellationToken)
        {
            if (Action?.Id != id || Action.Status != PendingActionStatus.PendingConfirmation)
                return Task.FromResult(new PendingActionConfirmationClaimResult(false, Action?.Status ?? PendingActionStatus.Expired, Action?.ConfirmedAt));
            Action.Confirm(confirmedBySubject, confirmedAt);
            return Task.FromResult(new PendingActionConfirmationClaimResult(true, Action.Status, Action.ConfirmedAt));
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeConfirmation : IActionConfirmationService
    {
        public int Calls { get; private set; }
        public string StatusToReturn { get; set; } = "confirmed";
        public string? LastRequiredScope { get; private set; }

        public Task<ActionConfirmationResponse> ConfirmAsync(Guid pendingActionId, string? confirmationText, string? requiredScope, CancellationToken cancellationToken)
        {
            LastRequiredScope = requiredScope;
            return Task.FromResult(CreateResponse(pendingActionId));
        }

        private ActionConfirmationResponse CreateResponse(Guid pendingActionId)
        {
            Calls++;
            return new ActionConfirmationResponse(StatusToReturn, pendingActionId, "moodle_prepare_write", ToolRiskLevel.CriticalHumanConfirmedWrite, DateTimeOffset.UtcNow, "audit");
        }
    }

    private sealed class FakeAuditLogs : IMoodleAuditLogRepository
    {
        public List<MoodleAuditLog> Logs { get; } = [];

        public Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(string correlationId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);

        public Task<int> CountByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(Guid batchJobId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);

        public Task<int> CountByBatchJobIdAsync(Guid batchJobId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
