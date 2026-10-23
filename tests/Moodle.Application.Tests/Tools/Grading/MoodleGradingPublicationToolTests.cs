using System.Text.Json;
using MediatR;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using Moodle.Application.Grading;
using Moodle.Application.Tools;
using Moodle.Presentation.Tools.Grading;

namespace Moodle.Application.Tests.Tools.Grading;

public sealed class MoodleGradingPublicationToolTests
{
    [Fact]
    public async Task CreatePreview_RetornaPrimeiraPaginaEReferenciaRecuperavelSemTruncarAListaEstruturada()
    {
        var pendingActionId = Guid.NewGuid();
        var batchJobId = Guid.NewGuid();
        var launches = Enumerable.Range(1, 30)
            .Select(index => new GradingLaunchPreviewItem(
                Guid.NewGuid(),
                AssignmentId: "501",
                StudentId: index.ToString(),
                Grade: 8m,
                FeedbackText: $"Feedback completo do item {index}.",
                StudentName: $"Estudante {index}",
                AssignmentName: "Atividade 01",
                MaxGrade: 10m,
                ReviewStatus: "Reviewed"))
            .ToArray();
        var mediator = new FakeMediator(new CreateGradingLaunchPreviewResult(
            pendingActionId,
            batchJobId,
            TotalItems: 30,
            ReadyItems: 30,
            BlockedItems: 0,
            Launches: launches,
            ConfirmationText: string.Empty,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15),
            Warnings: []));
        var tools = new MoodleGradingTools(mediator, null!, null!);

        var result = await tools.CriarPreviaLancamentoLoteAsync(batchJobId);

        Assert.False(result.IsError);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        Assert.Equal(pendingActionId, data.GetProperty("pendingActionId").GetGuid());
        Assert.Equal(1, data.GetProperty("page").GetInt32());
        Assert.Equal(25, data.GetProperty("pageSize").GetInt32());
        Assert.Equal(30, data.GetProperty("previewItemCount").GetInt32());
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.Equal("confirm", data.GetProperty("nextAction").GetString());
        Assert.Equal(25, data.GetProperty("launches").GetArrayLength());
        Assert.Equal("Feedback completo do item 25.", data.GetProperty("launches")[24].GetProperty("feedbackText").GetString());

        var narration = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("get_grading_publication_status", narration, StringComparison.Ordinal);
        Assert.Contains("page=2", narration, StringComparison.Ordinal);
        Assert.Contains("\"pendingActionId\"", narration, StringComparison.Ordinal);
        Assert.Contains("\"feedbackText\"", narration, StringComparison.Ordinal);
        Assert.DoesNotContain("Feedback completo do item 30.", narration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatePreview_BloqueioPorPublicacaoAtivaOrientaRecuperacaoDaAcao()
    {
        var batchJobId = Guid.NewGuid();
        var mediator = new FakeMediator(new CreateGradingLaunchPreviewResult(
            Guid.Empty,
            batchJobId,
            TotalItems: 2,
            ReadyItems: 0,
            BlockedItems: 2,
            Launches: [],
            ConfirmationText: string.Empty,
            ExpiresAt: null,
            Warnings: ["Publicacao ativa existente."],
            BlockedReasons: new Dictionary<string, int> { ["active_publication_exists"] = 2 }));
        var tools = new MoodleGradingTools(mediator, null!, null!);

        var result = await tools.CriarPreviaLancamentoLoteAsync(batchJobId);

        var narration = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("get_assisted_grading_batch_status", narration, StringComparison.Ordinal);
        Assert.Contains("get_grading_publication_status", narration, StringComparison.Ordinal);
        Assert.DoesNotContain("Nenhum item pronto", narration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicationStatus_ByRunShowsReferencesForEveryAction()
    {
        var runId = Guid.NewGuid();
        var completedActionId = Guid.NewGuid();
        var activeActionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var publicationStatus = new GradingPublicationStatusResult(
            PendingActionId: activeActionId,
            BatchJobId: runId,
            Status: "ExecutionUnknown",
            PublicationState: "unknown",
            CreatedAt: now.AddMinutes(-3),
            ExpiresAt: now.AddMinutes(12),
            ConfirmedAt: now.AddMinutes(-2),
            LeaseUntil: null,
            NextAttemptAt: null,
            AttemptCount: 1,
            TotalItems: 2,
            ReadyItems: 2,
            BlockedItems: 0,
            Page: 1,
            PageSize: 25,
            PreviewItemCount: 2,
            HasMore: false,
            Items: [],
            Warnings: ["Ação ainda não reconciliada."],
            RelatedPendingActionIds: [activeActionId, completedActionId],
            LastErrorCode: "moodle_network_error",
            AsOfUtc: now,
            PublicationCounts: new GradingPublicationCounts(
                SelectedItems: 2,
                AwaitingApproval: 0,
                Queued: 0,
                Executing: 0,
                RetryScheduled: 0,
                Succeeded: 0,
                Failed: 0,
                Unknown: 1,
                Blocked: 0,
                Ready: 1),
            NextAction: "reconcile");
        var tools = new MoodleGradingTools(new FakeMediator(publicationStatus), null!, null!);

        var result = await tools.ConsultarStatusPublicacaoCorrecaoAsync(gradingRunId: runId);

        Assert.False(result.IsError);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var relatedIds = structured.GetProperty("data").GetProperty("relatedPendingActionIds");
        Assert.Equal(2, relatedIds.GetArrayLength());
        Assert.Contains(completedActionId, relatedIds.EnumerateArray().Select(value => value.GetGuid()));
        Assert.Contains(activeActionId, relatedIds.EnumerateArray().Select(value => value.GetGuid()));
        var narration = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains(completedActionId.ToString(), narration, StringComparison.Ordinal);
        Assert.Contains(activeActionId.ToString(), narration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulatedHostBlock_BeforeServerKeepsSameActionForLaterApprovalWithoutPosting()
    {
        var pendingActionId = Guid.NewGuid();
        var mediator = new FakeMediator(new ConfirmMoodleBatchLaunchResult(
            "authorized",
            pendingActionId,
            SentItems: 0,
            FailedItems: 0,
            Failures: [],
            AuditId: "synthetic-audit"));
        var tools = new MoodleGradingTools(mediator, null!, null!);
        var hostBlockedBeforeServer = true;
        CallToolResult? firstResult = null;

        // The host's approval interstitial blocks before forwarding the tool call.
        if (!hostBlockedBeforeServer)
        {
            firstResult = await tools.ConfirmarLancamentoLoteMoodleAsync(pendingActionId);
        }

        Assert.Null(firstResult);
        Assert.Equal(0, mediator.CallCount);

        // A later approval forwards the exact same pendingActionId.
        hostBlockedBeforeServer = false;
        Assert.False(hostBlockedBeforeServer);
        var approvedResult = await tools.ConfirmarLancamentoLoteMoodleAsync(pendingActionId);

        Assert.False(approvedResult.IsError);
        Assert.Equal(1, mediator.CallCount);
        var command = Assert.IsType<ConfirmMoodleBatchLaunchCommand>(mediator.LastRequest);
        Assert.Equal(pendingActionId, command.PendingActionId);
        Assert.False(command.ExecuteImmediately);
        var json = Assert.IsType<JsonElement>(approvedResult.StructuredContent);
        Assert.Equal("authorized", json.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(0, json.GetProperty("data").GetProperty("sentItems").GetInt32());
    }

    [Fact]
    public async Task PublicationPlan_IsReadOnlyAndReturnsPagedActivityGroupsAndActiveReferences()
    {
        var batchJobId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var plan = new GradingPublicationPlanResult(
            BatchJobId: batchJobId,
            AsOfUtc: DateTimeOffset.UtcNow,
            RecommendedMaxItems: 20,
            TotalGroups: 2,
            Page: 2,
            PageSize: 1,
            HasMore: false,
            Groups: [new GradingPublicationPlanGroup("501", 2, [Guid.NewGuid(), Guid.NewGuid()])],
            Warnings: ["Este plano e um retrato local; a previa revalida estado remoto e permissoes."],
            ActiveActions: [new GradingPublicationActiveActionReference(
                actionId, batchJobId, "ExecutionUnknown", DateTimeOffset.UtcNow.AddMinutes(-1))]);
        var mediator = new FakeMediator(plan);
        var tools = new MoodleGradingTools(mediator, null!, null!);
        var method = typeof(MoodleGradingTools).GetMethod(nameof(MoodleGradingTools.PlanejarPublicacaoCorrecaoAsync));
        var toolMetadata = Assert.IsType<McpServerToolAttribute>(method!.GetCustomAttributes(typeof(McpServerToolAttribute), false).Single());

        var result = await tools.PlanejarPublicacaoCorrecaoAsync(batchJobId, page: 2, pageSize: 1);

        Assert.False(result.IsError);
        Assert.Equal("plan_grading_publication", toolMetadata.Name);
        Assert.True(toolMetadata.ReadOnly);
        Assert.False(toolMetadata.Destructive);
        Assert.Equal(typeof(ToolResponse<GradingPublicationPlanResult>), toolMetadata.OutputSchemaType);
        var query = Assert.IsType<GetGradingPublicationPlanQuery>(mediator.LastRequest);
        Assert.Equal(batchJobId, query.BatchJobId);
        Assert.Equal(2, query.Page);
        Assert.Equal(1, query.PageSize);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        Assert.Equal(batchJobId, data.GetProperty("batchJobId").GetGuid());
        Assert.Equal(2, data.GetProperty("totalGroups").GetInt32());
        Assert.Equal("501", data.GetProperty("groups")[0].GetProperty("assignmentId").GetString());
        Assert.Equal(actionId, data.GetProperty("activeActions")[0].GetProperty("pendingActionId").GetGuid());
        var narration = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("create_batch_grade_launch_preview", narration, StringComparison.Ordinal);
        Assert.Contains(actionId.ToString(), narration, StringComparison.Ordinal);
        Assert.Contains("nao autoriza", narration, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeMediator(object response) : IMediator
    {
        public object? LastRequest { get; private set; }

        public int CallCount { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            if (request is GetGradingPublicationPlanQuery && response is GradingPublicationPlanResult plan)
            {
                return Task.FromResult((TResponse)(object)plan);
            }

            if (request is CreateGradingLaunchPreviewCommand && response is CreateGradingLaunchPreviewResult preview)
            {
                return Task.FromResult((TResponse)(object)preview);
            }

            if (request is GetGradingPublicationStatusQuery && response is GradingPublicationStatusResult publicationStatus)
            {
                return Task.FromResult((TResponse)(object)publicationStatus);
            }

            if (request is ConfirmMoodleBatchLaunchCommand && response is ConfirmMoodleBatchLaunchResult confirmation)
            {
                return Task.FromResult((TResponse)(object)confirmation);
            }

            throw new NotSupportedException(request.GetType().Name);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(request.GetType().Name);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(
            object request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
