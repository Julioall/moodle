using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

public sealed record GetGradingPublicationPlanQuery(
    Guid BatchJobId,
    int Page = 1,
    int PageSize = 25) : IRequest<GradingPublicationPlanResult>;

public sealed record GradingPublicationPlanGroup(
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("groupIndex")] int GroupIndex,
    [property: JsonPropertyName("gradingItemIds")] IReadOnlyList<Guid> GradingItemIds);

public sealed record GradingPublicationActiveActionReference(
    [property: JsonPropertyName("pendingActionId")] Guid PendingActionId,
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public sealed record GradingPublicationPlanResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("asOfUtc")] DateTimeOffset AsOfUtc,
    [property: JsonPropertyName("recommendedMaxItems")] int RecommendedMaxItems,
    [property: JsonPropertyName("totalGroups")] int TotalGroups,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("groups")] IReadOnlyList<GradingPublicationPlanGroup> Groups,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("activeActions")] IReadOnlyList<GradingPublicationActiveActionReference> ActiveActions);

public sealed class GetGradingPublicationPlanQueryHandler(
    IGradingReviewRepository gradingRepository,
    IPendingMoodleActionRepository pendingActions,
    ICurrentUserContext currentUser,
    IOptions<GradingLimitsOptions> gradingLimits)
    : IRequestHandler<GetGradingPublicationPlanQuery, GradingPublicationPlanResult>
{
    private const int MaximumGroupsPerPage = 25;
    private const int ReadPageSize = 400;
    private const string PreviewToolName = "criar_previa_lancamento_lote";
    private const string LegacyPreviewToolName = "confirmar_lancamento_lote_moodle";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly int recommendedMaxItems = ValidateRecommendedMaxItems(gradingLimits.Value.PublicationRecommendedMaxItems);

    public async Task<GradingPublicationPlanResult> Handle(
        GetGradingPublicationPlanQuery request,
        CancellationToken cancellationToken)
    {
        var scope = await GradingBatchScopeResolver.ResolveAsync(
            gradingRepository,
            currentUser,
            request.BatchJobId,
            cancellationToken);
        if (scope.Batches.Count == 0)
        {
            throw new InvalidOperationException("A execucao de correcao ainda nao possui sublotes.");
        }

        if (scope.DestinationRun is not null &&
            string.Equals(scope.DestinationRun.Destination, "csv", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Esta execucao ja foi direcionada para CSV; gere um novo gradingRunId para planejar a publicacao no Moodle.");
        }

        var connectionKeys = scope.Batches
            .Select(GradingBatchScopeResolver.ResolveConnectionKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (connectionKeys.Length > 1)
        {
            throw new InvalidOperationException("Uma execucao agregada nao pode misturar conexoes Moodle diferentes; crie uma execucao por conexao.");
        }

        var items = await LoadScopeItemsAsync(scope, cancellationToken);
        var scopeIds = new[] { request.BatchJobId, scope.Run?.Id, scope.DestinationRun?.Id }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Concat(scope.Batches.Select(batch => batch.Id))
            .Distinct()
            .ToArray();
        var actions = await pendingActions.ListGradingPublicationsByBatchIdsAsync(scopeIds, cancellationToken);
        var itemIds = items.Select(item => item.Id).ToHashSet();
        var activeActionTargets = new HashSet<Guid>();
        var accessibleActiveActions = new List<GradingPublicationActiveActionReference>();
        var activeActionCount = 0;

        foreach (var action in actions
                     .Where(IsPublicationAction)
                     .Where(IsActive)
                     .OrderByDescending(action => action.CreatedAt)
                     .ThenBy(action => action.Id))
        {
            var payload = DeserializePayload(action.PayloadJson);
            var targetsInScope = payload.Items
                .Select(item => item.GradingItemId)
                .Where(itemId => itemIds.Contains(itemId))
                .Distinct()
                .ToArray();
            if (targetsInScope.Length == 0)
            {
                continue;
            }

            activeActionCount++;
            activeActionTargets.UnionWith(targetsInScope);
            if (CanAccess(action))
            {
                accessibleActiveActions.Add(new GradingPublicationActiveActionReference(
                    action.Id,
                    payload.BatchJobId,
                    action.Status.ToString(),
                    action.CreatedAt));
            }
        }

        var coverage = scope.Run is null
            ? null
            : await GradingRunCoverageCalculator.CalculateAsync(
                gradingRepository,
                scope,
                items.Count,
                cancellationToken);
        var eligibleItems = (coverage is null || coverage.DecisionSafe
                ? items
                : [])
            .Where(item => GradingLaunchEligibility.IsCandidate(item, onlyReviewed: false))
            .Where(item => !activeActionTargets.Contains(item.Id))
            .ToArray();
        var allGroups = GradingPublicationPlanGrouping.CreateGroups(eligibleItems, recommendedMaxItems);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumGroupsPerPage);
        var offset = (long)(page - 1) * pageSize;
        GradingPublicationPlanGroup[] pageGroups = offset >= allGroups.Count
            ? []
            : allGroups.Skip((int)offset).Take(pageSize).ToArray();
        var hasMore = offset + pageGroups.Length < allGroups.Count;
        var warnings = new List<string>
        {
            "Este plano e um retrato local. A previa revalida no Moodle versao, estado remoto, tentativa, permissoes e claims; planejar nao garante elegibilidade nem autoriza escrita.",
            "A separacao por atividade e operacional. O grupo maximo recomendado e uma hipotese de homologacao, nao um limite comprovado do Moodle."
        };
        if (activeActionCount > 0)
        {
            warnings.Add($"{activeActionCount} acao(oes) ativa(s) ja reserva(m) itens deste escopo; os alvos foram excluidos dos grupos. Acompanhe as referencias acessiveis em activeActions.");
        }
        if (coverage is { DecisionSafe: false })
        {
            warnings.Add($"Cobertura incompleta do gradingRun: esperados {coverage.ExpectedItems}, disponiveis {coverage.PreparedItems}, ausentes {coverage.MissingItems} em {coverage.MissingBatchCount} sublote(s). Nenhum grupo novo foi recomendado; resolva a cobertura antes de planejar uma publicacao global.");
        }
        if (eligibleItems.Length == 0)
        {
            warnings.Add("Nao ha itens elegiveis para novos grupos neste retrato local.");
        }

        return new GradingPublicationPlanResult(
            request.BatchJobId,
            DateTimeOffset.UtcNow,
            recommendedMaxItems,
            allGroups.Count,
            page,
            pageSize,
            hasMore,
            pageGroups,
            warnings,
            accessibleActiveActions);
    }

    private async Task<IReadOnlyList<AssistedGradingItem>> LoadScopeItemsAsync(
        GradingBatchScope scope,
        CancellationToken cancellationToken)
    {
        var items = new List<AssistedGradingItem>();
        if (scope.Run is not null)
        {
            for (var page = 1; ; page++)
            {
                var pageItems = await gradingRepository.ListItemsByGradingRunAsync(
                    scope.Run.Id,
                    page,
                    ReadPageSize,
                    status: null,
                    cancellationToken);
                items.AddRange(pageItems);
                if (pageItems.Count < ReadPageSize)
                {
                    break;
                }
            }

            return items;
        }

        foreach (var batch in scope.Batches)
        {
            for (var page = 1; ; page++)
            {
                var pageItems = await gradingRepository.ListItemsByBatchAsync(
                    batch.Id,
                    page,
                    ReadPageSize,
                    cancellationToken);
                items.AddRange(pageItems);
                if (pageItems.Count < ReadPageSize)
                {
                    break;
                }
            }
        }

        return items;
    }

    private bool CanAccess(PendingMoodleAction action) =>
        string.Equals(action.CreatedBySubject, currentUser.Subject, StringComparison.Ordinal) ||
        currentUser.HasPlatformPermission("tool.pending_actions.manage");

    private static bool IsPublicationAction(PendingMoodleAction action) =>
        action.ToolName is PreviewToolName or LegacyPreviewToolName;

    private static bool IsActive(PendingMoodleAction action) => action.Status switch
    {
        PendingActionStatus.PendingConfirmation or PendingActionStatus.Authorized or
        PendingActionStatus.Confirmed or PendingActionStatus.Executing or
        PendingActionStatus.ExecutionUnknown => true,
        PendingActionStatus.PartiallyCompleted => action.NextExecutionAttemptAt is not null,
        _ => false
    };

    private static GradingLaunchPayload DeserializePayload(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<GradingLaunchPayload>(payloadJson, JsonOptions)
                ?? throw new InvalidOperationException("O payload de uma acao de publicacao ativa esta vazio; nao e seguro planejar novos grupos.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("O payload de uma acao de publicacao ativa esta invalido; nao e seguro planejar novos grupos.", exception);
        }
    }

    private static int ValidateRecommendedMaxItems(int value)
    {
        if (value is < 1 or > 100)
        {
            throw new OptionsValidationException(
                GradingLimitsOptions.SectionName,
                typeof(GradingLimitsOptions),
                [$"{nameof(GradingLimitsOptions.PublicationRecommendedMaxItems)} deve ficar entre 1 e 100."]);
        }

        return value;
    }
}

internal static class GradingPublicationPlanGrouping
{
    public static IReadOnlyList<GradingPublicationPlanGroup> CreateGroups(
        IEnumerable<AssistedGradingItem> eligibleItems,
        int maxItems)
    {
        if (maxItems is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "O tamanho do grupo deve ficar entre 1 e 100.");
        }

        return eligibleItems
            .OrderBy(item => item.AssignmentId)
            .ThenBy(item => item.Id)
            .GroupBy(item => item.AssignmentId)
            .SelectMany(assignmentItems => assignmentItems
                .Chunk(maxItems)
                .Select((chunk, index) => new GradingPublicationPlanGroup(
                    assignmentItems.Key.ToString(CultureInfo.InvariantCulture),
                    index + 1,
                    chunk.Select(item => item.Id).ToArray())))
            .ToArray();
    }
}

internal static class GradingLaunchEligibility
{
    public static bool IsCandidate(AssistedGradingItem item, bool onlyReviewed)
    {
        if (item.CommitWriteIntentAt is not null)
        {
            return false;
        }

        if (item.ReviewStatus == GradingReviewStatus.Reviewed &&
            item.Status == GradingItemStatus.ReadyToCommit &&
            item.CommitStatus == GradingCommitStatus.Pending &&
            !string.IsNullOrWhiteSpace(item.FinalFeedback))
        {
            return true;
        }

        return !onlyReviewed &&
               item.Status == GradingItemStatus.DraftReady &&
               item.CommitStatus == GradingCommitStatus.NotReady &&
               !string.IsNullOrWhiteSpace(item.DraftFeedback);
    }
}
