using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

public sealed record GetGradingPublicationStatusQuery(
    Guid? PendingActionId = null,
    Guid? GradingRunId = null,
    int Page = 1,
    int PageSize = 25) : IRequest<GradingPublicationStatusResult>;

public sealed record GradingPublicationStatusItem(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("assignmentName")] string? AssignmentName,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("studentName")] string? StudentName,
    [property: JsonPropertyName("grade")] decimal? Grade,
    [property: JsonPropertyName("maxGrade")] decimal? MaxGrade,
    [property: JsonPropertyName("feedbackText")] string FeedbackText,
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus,
    [property: JsonPropertyName("situation")] string Situation,
    [property: JsonPropertyName("commitStatus")] string CommitStatus,
    [property: JsonPropertyName("publicationState")] string PublicationState,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("lastErrorCode")] string? LastErrorCode = null,
    [property: JsonPropertyName("lastErrorPhase")] string? LastErrorPhase = null,
    [property: JsonPropertyName("workflowMode")] string? WorkflowMode = null,
    [property: JsonPropertyName("workflowState")] string? WorkflowState = null,
    [property: JsonPropertyName("releaseState")] string? ReleaseState = null,
    [property: JsonPropertyName("visibilityState")] string? VisibilityState = "unknown");

public sealed record GradingPublicationCounts(
    [property: JsonPropertyName("selectedItems")] int SelectedItems,
    [property: JsonPropertyName("awaitingApproval")] int AwaitingApproval,
    [property: JsonPropertyName("queued")] int Queued,
    [property: JsonPropertyName("executing")] int Executing,
    [property: JsonPropertyName("retryScheduled")] int RetryScheduled,
    [property: JsonPropertyName("succeeded")] int Succeeded,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("unknown")] int Unknown,
    [property: JsonPropertyName("blocked")] int Blocked,
    [property: JsonPropertyName("ready")] int Ready = 0);

public sealed record GradingPublicationStatusResult(
    [property: JsonPropertyName("pendingActionId")] Guid PendingActionId,
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("publicationState")] string PublicationState,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("confirmedAt")] DateTimeOffset? ConfirmedAt,
    [property: JsonPropertyName("leaseUntil")] DateTimeOffset? LeaseUntil,
    [property: JsonPropertyName("nextAttemptAt")] DateTimeOffset? NextAttemptAt,
    [property: JsonPropertyName("attemptCount")] int AttemptCount,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("readyItems")] int ReadyItems,
    [property: JsonPropertyName("blockedItems")] int BlockedItems,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("previewItemCount")] int PreviewItemCount,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("items")] IReadOnlyList<GradingPublicationStatusItem> Items,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("relatedPendingActionIds")] IReadOnlyList<Guid> RelatedPendingActionIds,
    [property: JsonPropertyName("lastErrorCode")] string? LastErrorCode,
    [property: JsonPropertyName("asOfUtc")] DateTimeOffset AsOfUtc,
    [property: JsonPropertyName("publicationCounts")] GradingPublicationCounts PublicationCounts,
    [property: JsonPropertyName("nextAction")] string NextAction);

public sealed class GetGradingPublicationStatusQueryHandler(
    IGradingReviewRepository gradingRepository,
    IPendingMoodleActionRepository pendingActions,
    ICurrentUserContext currentUser)
    : IRequestHandler<GetGradingPublicationStatusQuery, GradingPublicationStatusResult>
{
    private const string PreviewToolName = "criar_previa_lancamento_lote";
    private const string LegacyPublicationToolName = "confirmar_lancamento_lote_moodle";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GradingPublicationStatusResult> Handle(
        GetGradingPublicationStatusQuery request,
        CancellationToken cancellationToken)
    {
        var hasActionId = request.PendingActionId is { } actionId && actionId != Guid.Empty;
        var hasRunId = request.GradingRunId is { } runId && runId != Guid.Empty;
        if (hasActionId == hasRunId)
        {
            throw new ArgumentException("Informe pendingActionId ou gradingRunId, mas nao ambos.");
        }

        PendingMoodleAction selectedAction;
        IReadOnlyList<PendingMoodleAction> relatedActions;
        if (hasActionId)
        {
            selectedAction = await pendingActions.GetByIdAsync(request.PendingActionId!.Value, cancellationToken)
                ?? throw new InvalidOperationException("Acao de publicacao nao encontrada.");
            EnsurePublicationAction(selectedAction);
            EnsureCanAccess(selectedAction);
            relatedActions = [selectedAction];
        }
        else
        {
            var scope = await GradingBatchScopeResolver.ResolveAsync(
                gradingRepository,
                currentUser,
                request.GradingRunId!.Value,
                cancellationToken);
            var scopeIds = new[] { scope.Run?.Id ?? request.GradingRunId.Value }
                .Concat(scope.Batches.Select(batch => batch.Id))
                .Distinct()
                .ToArray();
            relatedActions = (await pendingActions.ListGradingPublicationsByBatchIdsAsync(
                    scopeIds,
                    cancellationToken))
                .Where(IsPublicationAction)
                .Where(CanAccess)
                .OrderByDescending(IsActive)
                .ThenByDescending(action => action.CreatedAt)
                .ToArray();
            selectedAction = relatedActions.FirstOrDefault()
                ?? throw new InvalidOperationException("Esta execucao ainda nao possui uma previa de publicacao.");
        }

        var payload = DeserializePayload(selectedAction.PayloadJson);
        var preview = DeserializePreview(selectedAction.PreviewJson, payload);
        var itemIds = preview.Items.Select(item => item.GradingItemId).ToArray();
        var ledgerItems = await gradingRepository.GetItemsAsync(itemIds, cancellationToken);
        var publicationClaimStatuses = payload.PublicationId is Guid publicationId
            ? await gradingRepository.GetPublicationClaimStatusesAsync(publicationId, cancellationToken)
            : new Dictionary<Guid, string>();
        var now = DateTimeOffset.UtcNow;
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var allPublicationStates = preview.Items
            .Select(item => ResolveItemPublicationState(
                item,
                selectedAction,
                ledgerItems,
                publicationClaimStatuses,
                now))
            .ToArray();
        var actionState = ResolveOverallPublicationState(
            selectedAction,
            allPublicationStates,
            now);
        var pageItems = preview.Items
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => ToStatusItem(
                item,
                selectedAction,
                ledgerItems,
                publicationClaimStatuses,
                now))
            .ToArray();
        var publicationCounts = CountPublicationStates(allPublicationStates);
        var relatedIds = relatedActions
            .Select(action => action.Id)
            .Distinct()
            .ToArray();
        var warnings = preview.Warnings.ToList();
        if (relatedIds.Length > 1)
        {
            warnings.Add("Esta execucao possui mais de uma acao de publicacao; pendingActionId identifica a previa mostrada.");
        }

        return new GradingPublicationStatusResult(
            selectedAction.Id,
            payload.BatchJobId,
            selectedAction.Status.ToString(),
            actionState,
            selectedAction.CreatedAt,
            selectedAction.ExpiresAt,
            selectedAction.ConfirmedAt,
            selectedAction.ExecutionLeaseUntil,
            selectedAction.NextExecutionAttemptAt,
            selectedAction.ExecutionAttemptCount,
            preview.TotalItems,
            preview.ReadyItems,
            preview.BlockedItems,
            page,
            pageSize,
            preview.Items.Count,
            page * pageSize < preview.Items.Count,
            pageItems,
            warnings,
            relatedIds,
            ResolveActionErrorCode(selectedAction),
            now,
            publicationCounts,
            ResolveNextAction(actionState));
    }

    private void EnsureCanAccess(PendingMoodleAction action)
    {
        if (!CanAccess(action))
        {
            throw new UnauthorizedAccessException("A previa pertence a outro usuario.");
        }
    }

    private bool CanAccess(PendingMoodleAction action) =>
        string.Equals(action.CreatedBySubject, currentUser.Subject, StringComparison.Ordinal) ||
        currentUser.HasPlatformPermission("tool.pending_actions.manage");

    private static bool IsPublicationAction(PendingMoodleAction action) =>
        action.ToolName is PreviewToolName or LegacyPublicationToolName;

    private static void EnsurePublicationAction(PendingMoodleAction action)
    {
        if (!IsPublicationAction(action))
        {
            throw new InvalidOperationException("A acao informada nao e uma previa de publicacao de notas.");
        }
    }

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
                ?? throw new InvalidOperationException("O payload da previa esta vazio.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("O payload salvo da previa nao pode ser interpretado.");
        }
    }

    private static PersistedPreview DeserializePreview(string previewJson, GradingLaunchPayload payload)
    {
        try
        {
            using var document = JsonDocument.Parse(previewJson);
            var root = document.RootElement;
            var items = root.TryGetProperty("launches", out var launches)
                ? launches.Deserialize<GradingLaunchPreviewItem[]>(JsonOptions) ?? []
                : payload.Items.Select(ToPreviewItem).ToArray();
            return new PersistedPreview(
                ReadInt(root, "totalItems", items.Length),
                ReadInt(root, "readyItems", items.Length),
                ReadInt(root, "blockedItems", 0),
                items,
                root.TryGetProperty("warnings", out var warningElement)
                    ? warningElement.Deserialize<string[]>(JsonOptions) ?? []
                    : []);
        }
        catch (JsonException)
        {
            return new PersistedPreview(
                payload.Items.Count,
                payload.Items.Count,
                0,
                payload.Items.Select(ToPreviewItem).ToArray(),
                ["preview_details_rebuilt_from_legacy_payload"]);
        }
    }

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static GradingLaunchPreviewItem ToPreviewItem(GradingLaunchPayloadItem item) => new(
        item.GradingItemId,
        item.AssignmentId,
        item.StudentId,
        item.Grade,
        item.FeedbackText,
        item.ContextHash,
        item.SubmissionContentHash,
        item.StudentName,
        "pronta_para_publicacao",
        item.PreflightHash,
        AssignmentName: item.AssignmentName,
        MaxGrade: item.MaxGrade,
        ReviewStatus: "Reviewed",
        WorkflowMode: item.WorkflowMode,
        WorkflowState: item.WorkflowState,
        ReleaseState: item.ReleaseState,
        VisibilityState: item.VisibilityState);

    private static GradingPublicationStatusItem ToStatusItem(
        GradingLaunchPreviewItem preview,
        PendingMoodleAction action,
        IReadOnlyDictionary<Guid, AssistedGradingItem> ledgerItems,
        IReadOnlyDictionary<Guid, string> publicationClaimStatuses,
        DateTimeOffset now)
    {
        ledgerItems.TryGetValue(preview.GradingItemId, out var item);
        var publicationState = ResolveItemPublicationState(
            preview,
            action,
            ledgerItems,
            publicationClaimStatuses,
            now);
        var failureReason = publicationState is not ("failed" or "unknown" or "retry_scheduled")
            ? null
            : item is null || item.CommitStatus is not (GradingCommitStatus.Failed or GradingCommitStatus.ExecutionUnknown or GradingCommitStatus.RetryScheduled)
                ? "publication_item_state_inconsistent"
                : string.IsNullOrWhiteSpace(item.LastErrorCode) ? "unknown_legacy_failure" : item.LastErrorCode;
        return new GradingPublicationStatusItem(
            preview.GradingItemId,
            preview.AssignmentId,
            preview.AssignmentName,
            preview.StudentId,
            preview.StudentName ?? item?.StudentDisplayName,
            preview.Grade,
            preview.MaxGrade,
            preview.FeedbackText,
            preview.ReviewStatus,
            preview.Situation,
            item?.CommitStatus.ToString() ?? "Pending",
            publicationState,
            failureReason,
            item?.LastErrorCode,
            item?.LastErrorPhase,
            preview.WorkflowMode,
            preview.WorkflowState,
            preview.ReleaseState,
            preview.VisibilityState);
    }

    private static string ResolveItemPublicationState(
        GradingLaunchPreviewItem preview,
        PendingMoodleAction action,
        IReadOnlyDictionary<Guid, AssistedGradingItem> ledgerItems,
        IReadOnlyDictionary<Guid, string> publicationClaimStatuses,
        DateTimeOffset now)
    {
        // A durable write intent is recorded before every Moodle POST. If a
        // selected item has no intent and remains ready/pending, the worker
        // never sent it, even when the enclosing action became unknown.
        if (ledgerItems.TryGetValue(preview.GradingItemId, out var notAttemptedItem) &&
            IsReadyWithoutWriteIntent(notAttemptedItem) &&
            (action.Status == PendingActionStatus.ExecutionUnknown ||
             publicationClaimStatuses.TryGetValue(preview.GradingItemId, out var releasedClaimStatus) &&
             releasedClaimStatus == "Released"))
        {
            return "ready";
        }

        if (publicationClaimStatuses.TryGetValue(preview.GradingItemId, out var claimStatus))
        {
            return claimStatus switch
            {
                "Succeeded" when ledgerItems.TryGetValue(preview.GradingItemId, out var item) &&
                                  item.CommitStatus == GradingCommitStatus.Succeeded => "succeeded",
                "Succeeded" => "unknown",
                "Failed" => "failed",
                "ExecutionUnknown" => "unknown",
                "RetryScheduled" => "retry_scheduled",
                "Authorized" or "Executing"
                    when !IsTerminalAction(action) &&
                         ledgerItems.TryGetValue(preview.GradingItemId, out var item) &&
                         item.CommitStatus == GradingCommitStatus.Succeeded => "succeeded",
                _ => ResolvePublicationState(action, now)
            };
        }

        // A terminal item ledger is global to the correction item. Without an
        // action-scoped claim outcome it may belong to a later publication;
        // never attribute it to this action by inference.
        if (IsTerminalAction(action))
        {
            return "unknown";
        }

        return ResolvePublicationState(action, now);
    }

    private static bool IsReadyWithoutWriteIntent(AssistedGradingItem item) =>
        item.Status == GradingItemStatus.ReadyToCommit &&
        item.CommitStatus == GradingCommitStatus.Pending &&
        item.CommitWriteIntentAt is null;

    private static bool IsTerminalAction(PendingMoodleAction action) =>
        action.Status is PendingActionStatus.Executed or PendingActionStatus.Failed ||
        action.Status == PendingActionStatus.PartiallyCompleted && action.NextExecutionAttemptAt is null;

    private static string ResolveOverallPublicationState(
        PendingMoodleAction action,
        IReadOnlyList<string> itemStates,
        DateTimeOffset now)
    {
        if (itemStates.Any(state => state == "unknown")) return "unknown";
        if (itemStates.Any(state => state == "failed")) return "failed";
        if (itemStates.Count > 0 && itemStates.All(state => state == "succeeded")) return "succeeded";

        var actionState = ResolvePublicationState(action, now);
        // A terminal action row is not proof that every item was committed.
        // Legacy aggregate/item disagreement remains visible as unknown.
        if (action.Status is PendingActionStatus.Executed or PendingActionStatus.Failed ||
            action.Status == PendingActionStatus.PartiallyCompleted && action.NextExecutionAttemptAt is null)
        {
            return "unknown";
        }

        return actionState;
    }

    private static GradingPublicationCounts CountPublicationStates(IReadOnlyList<string> states) => new(
        states.Count,
        states.Count(state => state == "awaiting_approval"),
        states.Count(state => state == "queued"),
        states.Count(state => state == "executing"),
        states.Count(state => state == "retry_scheduled"),
        states.Count(state => state == "succeeded"),
        states.Count(state => state == "failed"),
        states.Count(state => state == "unknown"),
        states.Count(state => state == "blocked"),
        states.Count(state => state == "ready"));

    private static string ResolveNextAction(string publicationState) => publicationState switch
    {
        "awaiting_approval" => "confirm",
        "queued" or "executing" or "retry_scheduled" => "wait",
        "succeeded" => "done",
        "unknown" or "failed" or "blocked" => "attention",
        _ => "attention"
    };

    private static string ResolvePublicationState(PendingMoodleAction action, DateTimeOffset now) => action.Status switch
    {
        PendingActionStatus.PendingConfirmation when action.ExpiresAt <= now => "blocked",
        PendingActionStatus.PendingConfirmation => "awaiting_approval",
        PendingActionStatus.Authorized or PendingActionStatus.Confirmed => "queued",
        PendingActionStatus.PartiallyCompleted => action.NextExecutionAttemptAt is not null ? "retry_scheduled" : "failed",
        PendingActionStatus.Executing => "executing",
        PendingActionStatus.ExecutionUnknown => "unknown",
        // Aggregate terminal states are only a fallback for item rows which
        // lack a terminal ledger result. Never infer success from the action.
        PendingActionStatus.Executed or PendingActionStatus.Failed => "unknown",
        PendingActionStatus.Expired or PendingActionStatus.Cancelled => "blocked",
        _ => "blocked"
    };

    private static string? ResolveActionErrorCode(PendingMoodleAction action) => action.Status switch
    {
        PendingActionStatus.Failed => "publication_failed",
        PendingActionStatus.PartiallyCompleted when action.NextExecutionAttemptAt is not null => null,
        PendingActionStatus.PartiallyCompleted => "partial_completion",
        PendingActionStatus.ExecutionUnknown => "execution_unknown",
        _ => null
    };

    private sealed record PersistedPreview(
        int TotalItems,
        int ReadyItems,
        int BlockedItems,
        IReadOnlyList<GradingLaunchPreviewItem> Items,
        IReadOnlyList<string> Warnings);
}
