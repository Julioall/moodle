using System.Text.Json;
using MediatR;
using ModelContextProtocol.Protocol;
using Moodle.Application.Grading;
using Moodle.Application.Grading.Packages;
using Moodle.Application.Tools;
using Moodle.Domain.Grading;
using Moodle.Presentation.Tools;
using Moodle.Presentation.Tools.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingFailurePhaseTests
{
    [Fact]
    public void MarkAnalysisFailed_PersisteCodigoEFaseSemUsarMensagemComoCausa()
    {
        var batch = AssistedGradingBatch.Create(10, [101L], "owner", null, 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkProcessingStage(GradingProcessingStage.Context);

        item.MarkAnalysisFailed(
            "Falha operacional com mensagem segura.",
            "unexpected_connector_error",
            GradingFailurePhase.Context);

        Assert.Equal("unexpected_connector_error", item.LastErrorCode);
        Assert.Equal(GradingFailurePhase.Context, item.LastErrorPhase);
    }

    [Fact]
    public void MarkAnalysisFailed_UsaUnknownParaFaseLegadaOuInvalida()
    {
        var batch = AssistedGradingBatch.Create(10, [101L], "owner", null, 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);

        item.MarkAnalysisFailed("Falha simulada.", errorPhase: "exception message");

        Assert.Equal("unknown_legacy_failure", item.LastErrorCode);
        Assert.Equal(GradingFailurePhase.Unknown, item.LastErrorPhase);
    }

    [Fact]
    public void ToolResponseSerializaFaseEProximaAcaoAditivamente()
    {
        var response = new ToolResponse<object>(
            "error",
            null,
            [],
            null,
            DateTimeOffset.UnixEpoch,
            ErrorCode: "unexpected_connector_error",
            Message: "Falha simulada.",
            ErrorPhase: GradingFailurePhase.Query,
            NextAction: "retry_status_read",
            Code: "GRADING_STATUS_QUERY_FAILED",
            CauseCode: "unexpected_connector_error",
            Retryable: true);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));

        Assert.Equal("query", json.RootElement.GetProperty("phase").GetString());
        Assert.Equal("retry_status_read", json.RootElement.GetProperty("nextAction").GetString());
        Assert.Equal("GRADING_STATUS_QUERY_FAILED", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("unexpected_connector_error", json.RootElement.GetProperty("causeCode").GetString());
        Assert.True(json.RootElement.GetProperty("retryable").GetBoolean());
    }

    [Theory]
    [InlineData("query", "GRADING_STATUS_QUERY_FAILED")]
    [InlineData("acquisition", "GRADING_SUBMISSION_ACQUISITION_FAILED")]
    [InlineData("context", "GRADING_CONTEXT_BUILD_FAILED")]
    [InlineData("package", "GRADING_PACKAGE_MATERIALIZATION_FAILED")]
    [InlineData("analysis", "GRADING_ANALYSIS_FAILED")]
    [InlineData("save", "GRADING_DRAFT_SAVE_FAILED")]
    [InlineData("preview", "GRADING_PUBLICATION_PREVIEW_FAILED")]
    [InlineData("publication", "GRADING_PUBLICATION_FAILED")]
    public void ToolError_UsesStableOperationCodeForEveryGradingPhase(string phase, string expectedCode)
    {
        var result = ToolResultHelper.Error<object>(
            "Falha sintetica.",
            errorCode: "synthetic_dependency_error",
            errorPhase: phase,
            nextAction: "inspect_synthetic_state");

        var json = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(phase, json.GetProperty("phase").GetString());
        Assert.Equal(expectedCode, json.GetProperty("code").GetString());
        Assert.Equal("inspect_synthetic_state", json.GetProperty("nextAction").GetString());
        Assert.NotEqual("Falha sintetica.", json.GetProperty("causeCode").GetString());
    }

    [Fact]
    public async Task PublicationStatusQueryFailure_IsNotReportedAsPublicationFailure()
    {
        var tools = new MoodleGradingTools(
            new FailingMediator(new InvalidOperationException("synthetic status query failure")),
            null!,
            null!);

        var result = await tools.ConsultarStatusPublicacaoCorrecaoAsync(
            pendingActionId: Guid.NewGuid(),
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsError);
        var json = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("query", json.GetProperty("phase").GetString());
        Assert.Equal("GRADING_STATUS_QUERY_FAILED", json.GetProperty("code").GetString());
        Assert.Equal("retry_status_read", json.GetProperty("nextAction").GetString());
        Assert.NotEqual("GRADING_PUBLICATION_FAILED", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DraftSaveFailure_ExposesSavePhaseAndDoesNotSuggestPublicationRetry()
    {
        var tools = new MoodleGradingTools(
            new FailingMediator(new InvalidOperationException("synthetic save failure")),
            null!,
            null!);

        var result = await tools.SalvarCorrecoesIaLoteAsync(
            Guid.NewGuid(),
            [new AiGradingItemInput(Guid.NewGuid(), Nome: null, Nota: 5m, Feedback: "synthetic")],
            CancellationToken.None);

        Assert.True(result.IsError);
        var json = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("save", json.GetProperty("phase").GetString());
        Assert.Equal("GRADING_DRAFT_SAVE_FAILED", json.GetProperty("code").GetString());
        Assert.Equal("review_input_and_retry_save", json.GetProperty("nextAction").GetString());
        Assert.False(json.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task PackageMaterializationFailure_ExposesPackagePhase()
    {
        var tools = new MoodleGradingPackageTools(
            new FailingMediator(new InvalidOperationException("synthetic package materialization failure")));

        var result = await tools.PrepararPacotesCorrecaoAsync(
            Guid.NewGuid(),
            cancellationToken: CancellationToken.None);

        AssertErrorEnvelope(
            result,
            phase: "package",
            code: "GRADING_PACKAGE_MATERIALIZATION_FAILED",
            nextAction: "check_package_status_before_retry");
    }

    [Fact]
    public async Task PreviewFailure_ExposesPreviewPhaseAndIsNotTreatedAsPublication()
    {
        var tools = new MoodleGradingTools(
            new FailingMediator(new InvalidOperationException("synthetic preview failure")),
            null!,
            null!);

        var result = await tools.CriarPreviaLancamentoLoteAsync(Guid.NewGuid());

        AssertErrorEnvelope(
            result,
            phase: "preview",
            code: "GRADING_PUBLICATION_PREVIEW_FAILED",
            nextAction: "review_and_retry_preview");
        var json = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.False(json.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task PublicationFailure_ExposesPublicationPhaseAndStatusRecovery()
    {
        var tools = new MoodleGradingTools(
            new FailingMediator(new InvalidOperationException("synthetic publication failure")),
            null!,
            null!);

        var result = await tools.ConfirmarLancamentoLoteMoodleAsync(
            Guid.NewGuid(),
            cancellationToken: CancellationToken.None);

        AssertErrorEnvelope(
            result,
            phase: "publication",
            code: "GRADING_PUBLICATION_FAILED",
            nextAction: "consult_publication_status");
        var json = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.False(json.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task LegacyBatchStatusQueryFailure_ExposesQueryPhase()
    {
        var tools = new MoodleGradingTools(
            new FailingMediator(new InvalidOperationException("synthetic batch status query failure")),
            null!,
            null!);

        var result = await tools.ConsultarStatusLoteCorrecaoAsync(
            Guid.NewGuid(),
            cancellationToken: CancellationToken.None);

        AssertErrorEnvelope(
            result,
            phase: "query",
            code: "GRADING_STATUS_QUERY_FAILED",
            nextAction: "retry_status_read");
    }

    [Fact]
    public async Task AiBatchPreparationFailure_ExposesPackagePhase()
    {
        var tools = new MoodleGradingTools(
            new FailingMediator(new InvalidOperationException("synthetic package query failure")),
            null!,
            null!);

        var result = await tools.PrepararLoteCorrecaoIaAsync(
            Guid.NewGuid(),
            cancellationToken: CancellationToken.None);

        AssertErrorEnvelope(
            result,
            phase: "package",
            code: "GRADING_PACKAGE_MATERIALIZATION_FAILED",
            nextAction: "check_package_status_before_retry");
    }

    [Theory]
    [InlineData(GradingProcessingStage.Ingestion, GradingFailurePhase.Acquisition)]
    [InlineData(GradingProcessingStage.Context, GradingFailurePhase.Context)]
    [InlineData(GradingProcessingStage.Analysis, GradingFailurePhase.Analysis)]
    public void WorkerStageMapsToSpecificStoredFailurePhase(string stage, string expectedPhase)
    {
        Assert.Equal(expectedPhase, GradingFailurePhase.FromProcessingStage(stage));
    }

    private static void AssertErrorEnvelope(
        CallToolResult result,
        string phase,
        string code,
        string nextAction)
    {
        Assert.True(result.IsError);
        var json = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(phase, json.GetProperty("phase").GetString());
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal(nextAction, json.GetProperty("nextAction").GetString());
    }

    private sealed class FailingMediator(Exception failure) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromException<TResponse>(failure);

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromException<object?>(failure);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.FromException(failure);

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<object?>();
    }
}
