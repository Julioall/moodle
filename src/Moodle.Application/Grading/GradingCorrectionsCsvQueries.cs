using System.Globalization;
using System.Text.Json.Serialization;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

public sealed record GetGradingCorrectionsCsvQuery(
    Guid BatchJobId) : IRequest<GradingCorrectionsCsvResult>;

public sealed record GradingCorrectionsCsvResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("generatedItems")] int GeneratedItems,
    [property: JsonPropertyName("pendingItems")] int PendingItems,
    [property: JsonPropertyName("blockedItems")] int BlockedItems,
    [property: JsonPropertyName("rows")] IReadOnlyList<GradingCorrectionsCsvRow> Rows);

public sealed record GradingCorrectionsCsvRow(
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("nota")] decimal? Nota,
    [property: JsonPropertyName("feedback")] string? Feedback,
    [property: JsonPropertyName("situacao")] string Situacao);

public sealed class GetGradingCorrectionsCsvQueryHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser)
    : IRequestHandler<GetGradingCorrectionsCsvQuery, GradingCorrectionsCsvResult>
{
    public async Task<GradingCorrectionsCsvResult> Handle(
        GetGradingCorrectionsCsvQuery request,
        CancellationToken cancellationToken)
    {
        if (request.BatchJobId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(request.BatchJobId));
        }

        var scope = await GradingBatchScopeResolver.ResolveAsync(
            repository,
            currentUser,
            request.BatchJobId,
            cancellationToken);
        if (scope.DestinationRun is not null &&
            !await repository.TrySetGradingRunDestinationAsync(
                scope.DestinationRun.Id,
                "csv",
                cancellationToken))
        {
            throw new MoodleApiException(
                MoodleErrorContract.DestinationLocked,
                "Esta execucao ja foi direcionada para publicacao no Moodle; gere um novo gradingRunId para exportar CSV.");
        }
        var items = new List<AssistedGradingItem>();
        foreach (var batch in scope.Batches)
        {
            items.AddRange(await GradingItemProcessor.LoadAllBatchItemsAsync(
                repository,
                batch.Id,
                cancellationToken));
        }
        var rows = items
            .Select(ToRow)
            .ToArray();

        return new GradingCorrectionsCsvResult(
            request.BatchJobId,
            DateTimeOffset.UtcNow,
            rows.Length,
            rows.Count(row => row.Situacao == "gerado"),
            rows.Count(row => row.Situacao is "aguardando_geracao" or "processando" or "pendente"),
            rows.Count(row => row.Situacao is "bloqueado" or "falha" or "falha_envio_moodle"),
            rows);
    }

    private static GradingCorrectionsCsvRow ToRow(AssistedGradingItem item)
    {
        var name = string.IsNullOrWhiteSpace(item.StudentDisplayName)
            ? item.MoodleUserId.ToString(CultureInfo.InvariantCulture)
            : item.StudentDisplayName.Trim();

        return new GradingCorrectionsCsvRow(
            name,
            item.FinalGrade ?? item.SuggestedGrade,
            string.IsNullOrWhiteSpace(item.FinalFeedback) ? item.DraftFeedback : item.FinalFeedback,
            ResolveSituation(item));
    }

    private static string ResolveSituation(AssistedGradingItem item) =>
        item.Status switch
        {
            GradingItemStatus.DraftReady or GradingItemStatus.ReadyToCommit => "gerado",
            GradingItemStatus.Committed => "enviado_moodle",
            GradingItemStatus.Blocked => "bloqueado",
            GradingItemStatus.Failed when item.CommitStatus is GradingCommitStatus.Failed or GradingCommitStatus.ExecutionUnknown => "falha_envio_moodle",
            GradingItemStatus.Failed => "falha",
            GradingItemStatus.AwaitingAiAnalysis => "aguardando_geracao",
            GradingItemStatus.Analyzing => "processando",
            _ => "pendente"
        };
}
