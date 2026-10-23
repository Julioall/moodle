using System.Text.Json;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Risk.Queries;
using Moodle.Domain;
using Moodle.Presentation.Tools.Risk;

namespace Moodle.Application.Tests.Tools.Risk;

public sealed class MoodleRiskAnalysisToolsTests
{
    [Fact]
    public async Task Alerta_quando_relatorio_usa_fallback_de_participantes()
    {
        var result = await CreateTool(CreateResult(
            reports: [CreateReport()],
            analyzed: 1,
            diagnostics: new ParticipantClassificationDiagnostics(
                1, 0, 1, 0, true, true, ParticipantClassificationMode.Fallback)))
            .GerarRelatorioRiscoEstudantesAsync("10");

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("ok", structured.GetProperty("status").GetString());
        Assert.Equal(1, structured.GetProperty("data").GetArrayLength());
        Assert.Contains(structured.GetProperty("warnings").EnumerateArray(), warning =>
            warning.GetString()!.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Alerta_quando_curso_nao_retorna_participantes()
    {
        var result = await CreateTool(CreateResult()).GerarRelatorioRiscoEstudantesAsync("10");

        var warnings = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("warnings");
        Assert.Contains(warnings.EnumerateArray(), warning =>
            warning.GetString()!.Contains("participantes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Explica_lista_vazia_apos_analisar_participantes()
    {
        var completeResult = CreateResult(analyzed: 2) with
        {
            AccessCoverageComplete = true,
            ParticipantCoverageComplete = true
        };
        var result = await CreateTool(completeResult)
            .GerarRelatorioRiscoEstudantesAsync("10");

        var warnings = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("warnings");
        Assert.Contains(warnings.EnumerateArray(), warning =>
            warning.GetString()!.Contains("nenhum fator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Alerta_quando_notas_estao_parcialmente_indisponiveis()
    {
        var result = await CreateTool(CreateResult(
                reports: [CreateReport()],
                analyzed: 1,
                gradebookFailures: 1))
            .GerarRelatorioRiscoEstudantesAsync("10");

        var warnings = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("warnings");
        Assert.Contains(warnings.EnumerateArray(), warning =>
            warning.GetString()!.Contains("notas", StringComparison.OrdinalIgnoreCase));
    }

    private static MoodleRiskAnalysisTools CreateTool(StudentsAtRiskReportResult result) =>
        new(new FakeMediator(result), new FakeSelection(), new FakeUserResolver());

    private static StudentsAtRiskReportResult CreateResult(
        IReadOnlyList<StudentRiskReport>? reports = null,
        int analyzed = 0,
        ParticipantClassificationDiagnostics? diagnostics = null,
        int gradebookFailures = 0) =>
        new(
            reports ?? [],
            analyzed,
            diagnostics ?? ParticipantClassificationDiagnostics.Empty,
            gradebookFailures);

    private static StudentRiskReport CreateReport() =>
        new("123", "Aluno", RiskLevel.Alto, ["Inatividade"], null, null, null);

    private sealed class FakeSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class FakeUserResolver : IMoodleUserResolver
    {
        public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult<long?>(42);
    }

    private sealed class FakeMediator(StudentsAtRiskReportResult result) : IMediator
    {
        public Task<TResponse> Send<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)result);

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(result);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Publish<TNotification>(
            TNotification notification,
            CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(
            object request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<object?>();
    }
}
