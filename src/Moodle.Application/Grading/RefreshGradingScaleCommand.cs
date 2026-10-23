using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Auditing;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

public sealed record GradingScaleRefreshItemExpectation(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("expectedContextVersion")] int ExpectedContextVersion,
    [property: JsonPropertyName("expectedContextHash")] string ExpectedContextHash,
    [property: JsonPropertyName("expectedDraftVersionHash")] string ExpectedDraftVersionHash);

public sealed record RefreshGradingScaleCommand(
    Guid BatchJobId,
    long AssignmentId,
    IReadOnlyList<GradingScaleRefreshItemExpectation> Items) : IRequest<RefreshGradingScaleResult>;

public sealed record GradingScaleRefreshItemResult(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("previousContextVersion")] int PreviousContextVersion,
    [property: JsonPropertyName("contextVersion")] int ContextVersion,
    [property: JsonPropertyName("previousContextHash")] string PreviousContextHash,
    [property: JsonPropertyName("contextHash")] string ContextHash,
    [property: JsonPropertyName("reviewWasReset")] bool ReviewWasReset);

public sealed record RefreshGradingScaleResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("gradingMode")] string GradingMode,
    [property: JsonPropertyName("minimumGrade")] decimal? MinimumGrade,
    [property: JsonPropertyName("maximumGrade")] decimal? MaximumGrade,
    [property: JsonPropertyName("scaleSource")] string ScaleSource,
    [property: JsonPropertyName("scaleVersion")] string ScaleVersion,
    [property: JsonPropertyName("refreshedItems")] IReadOnlyList<GradingScaleRefreshItemResult> RefreshedItems,
    [property: JsonPropertyName("unchangedItems")] IReadOnlyList<Guid> UnchangedItems,
    [property: JsonPropertyName("invalidatedPreviewActionIds")] IReadOnlyList<Guid> InvalidatedPreviewActionIds,
    [property: JsonPropertyName("nextAction")] string NextAction);

public sealed class RefreshGradingScaleCommandHandler(
    IGradingReviewRepository gradingRepository,
    IGradingContextSnapshotStore contextSnapshots,
    IMoodleAssignmentSettingsGateway assignmentSettings,
    IMoodleGradebookGateway gradebook,
    IPendingMoodleActionRepository pendingActions,
    IMoodleAuditLogRepository auditLogs,
    ICurrentUserContext currentUser,
    IConnectorExecutionContext? executionContext = null,
    IMoodleConnectionSelection? connectionSelection = null,
    IMoodleBackgroundConnectionContextRestorer? backgroundConnectionContextRestorer = null)
    : IRequestHandler<RefreshGradingScaleCommand, RefreshGradingScaleResult>
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumItems = 400;

    public async Task<RefreshGradingScaleResult> Handle(
        RefreshGradingScaleCommand request,
        CancellationToken cancellationToken)
    {
        if (request.BatchJobId == Guid.Empty || request.AssignmentId <= 0)
        {
            throw new ArgumentException("Informe um lote e assignmentId validos.");
        }

        if (request.Items is null || request.Items.Count is < 1 or > MaximumItems)
        {
            throw new ArgumentException($"Informe de 1 a {MaximumItems} itens explicitamente selecionados.");
        }

        var expectations = request.Items.ToArray();
        if (expectations.Any(item => item.GradingItemId == Guid.Empty ||
                                     item.ExpectedContextVersion < 1 ||
                                     !IsSha256(item.ExpectedContextHash) ||
                                     !IsSha256(item.ExpectedDraftVersionHash)) ||
            expectations.Select(item => item.GradingItemId).Distinct().Count() != expectations.Length)
        {
            throw new ArgumentException("Cada item deve informar ID, versao/hash de contexto e hash do rascunho lidos; IDs duplicados nao sao aceitos.");
        }

        var scope = await GradingBatchScopeResolver.ResolveAsync(
            gradingRepository,
            currentUser,
            request.BatchJobId,
            cancellationToken);
        if (scope.Batches.Count == 0)
        {
            throw new InvalidOperationException("A execucao ainda nao possui sublotes para atualizar.");
        }

        var itemsById = await gradingRepository.GetItemsAsync(
            expectations.Select(item => item.GradingItemId).ToArray(),
            cancellationToken);
        var batchIds = scope.Batches.Select(batch => batch.Id).ToHashSet();
        if (itemsById.Count != expectations.Length ||
            expectations.Any(expectation => !itemsById.TryGetValue(expectation.GradingItemId, out var item) ||
                                            !batchIds.Contains(item.BatchId) ||
                                            item.AssignmentId != request.AssignmentId))
        {
            throw new InvalidOperationException("Todos os itens devem pertencer ao escopo autorizado e a mesma atividade informada.");
        }

        var selectedItems = expectations.Select(expectation => itemsById[expectation.GradingItemId]).ToArray();
        var selectedBatches = scope.Batches
            .Where(batch => selectedItems.Any(item => item.BatchId == batch.Id))
            .ToArray();
        if (selectedItems.Select(item => item.CourseId).Distinct().Count() != 1 ||
            selectedBatches.Select(MoodleAssignmentGradeScaleResolver.ConnectionScope)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1)
        {
            throw new InvalidOperationException("O refresh precisa ficar em um unico curso e uma unica conexao Moodle.");
        }

        var courseId = selectedItems[0].CourseId;
        var batch = selectedBatches[0];
        if (selectedBatches.Any(candidate => candidate.CourseId != courseId ||
                                             !candidate.AssignmentIds.Contains(request.AssignmentId)))
        {
            throw new InvalidOperationException("A atividade nao pertence a todos os sublotes selecionados.");
        }

        var snapshotsByItem = await LoadAndValidateSnapshotsAsync(
            expectations,
            selectedItems,
            cancellationToken);
        await GradingMoodleConnectionContext.RestoreAsync(
            batch,
            executionContext,
            connectionSelection,
            backgroundConnectionContextRestorer,
            cancellationToken);
        var settings = await ReadFreshAssignmentSettingsAsync(batch, courseId, request.AssignmentId, cancellationToken);
        var connectionScope = MoodleAssignmentGradeScaleResolver.ConnectionScope(batch);
        MoodleAssignmentGradeScaleResolution? gradebookScale = null;
        try
        {
            var currentGradebook = await gradebook.GetStudentGradebookAsync(
                courseId.ToString(CultureInfo.InvariantCulture),
                selectedItems[0].MoodleUserId.ToString(CultureInfo.InvariantCulture),
                GradebookReadOptions.Preview,
                cancellationToken);
            gradebookScale = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(
                currentGradebook,
                courseId,
                request.AssignmentId,
                connectionScope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fresh assignment settings are authoritative for the assign
            // activity. The exact gradebook item is a supplemental source:
            // when available it can reveal a conflict, but its transport
            // failure must not discard a fresh settings definition.
        }

        var currentScale = MoodleAssignmentGradeScaleResolver.ReconcileAuthoritativeSources(
            MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(settings, courseId, request.AssignmentId, connectionScope),
            gradebookScale,
            courseId,
            request.AssignmentId,
            connectionScope);
        if (currentScale.GradingMode == "unknown" ||
            currentScale.Source == "conflict" ||
            string.IsNullOrWhiteSpace(currentScale.Version))
        {
            throw new InvalidOperationException(
                "A escala atual esta ausente, ambigua ou conflitante; nenhum item foi alterado.");
        }

        var changedItems = new List<(AssistedGradingItem Item, GradingContextSnapshot Snapshot, GradingScaleRefreshItemExpectation Expected)>();
        var unchangedItems = new List<Guid>();
        foreach (var expectation in expectations)
        {
            var item = itemsById[expectation.GradingItemId];
            var snapshot = snapshotsByItem[item.Id];
            if (string.Equals(snapshot.GradingScale?.Version, currentScale.Version, StringComparison.Ordinal))
            {
                unchangedItems.Add(item.Id);
            }
            else
            {
                changedItems.Add((item, snapshot, expectation));
            }
        }

        if (changedItems.Count == 0)
        {
            return BuildResult(request, currentScale, [], unchangedItems, [], status: "already_current");
        }

        // Avoid cancelling a pending preview when a selected item is already
        // known to be published, uncertain, or actively leased. Repeat these
        // checks after cancellation to close the race with concurrent work.
        foreach (var entry in changedItems)
        {
            EnsureItemCanRefresh(entry.Item);
        }

        var invalidatedPreviewIds = await CancelPendingPreviewsForChangedItemsAsync(
            scope,
            changedItems.Select(entry => entry.Item.Id).ToHashSet(),
            cancellationToken);

        // Re-read after cancelling pending previews. This catches a review,
        // package import, worker claim, or another refresh that completed while
        // the Moodle scale was being read.
        await gradingRepository.ReloadItemsAsync(selectedItems.Select(item => item.Id).ToArray(), cancellationToken);
        itemsById = await gradingRepository.GetItemsAsync(selectedItems.Select(item => item.Id).ToArray(), cancellationToken);
        snapshotsByItem = await LoadAndValidateSnapshotsAsync(expectations, itemsById.Values.ToArray(), cancellationToken);

        foreach (var entry in changedItems)
        {
            var latest = itemsById[entry.Item.Id];
            EnsureExpectedVersions(latest, snapshotsByItem[latest.Id], entry.Expected);
            EnsureItemCanRefresh(latest);
        }

        var scaleSnapshot = new GradingScaleSnapshot(
            currentScale.MaximumGrade,
            Name: null,
            Description: null,
            currentScale.GradingMode,
            currentScale.MinimumGrade,
            currentScale.Source,
            currentScale.Version,
            currentScale.ScaleId);
        var refreshed = new List<GradingScaleRefreshItemResult>(changedItems.Count);
        var auditCorrelationId = Guid.NewGuid().ToString("N");
        foreach (var entry in changedItems)
        {
            var item = itemsById[entry.Item.Id];
            var oldSnapshot = snapshotsByItem[item.Id];
            var nextSnapshot = oldSnapshot.WithGradingScale(scaleSnapshot);
            var previousReview = new
            {
                grade = item.FinalGrade ?? item.SuggestedGrade,
                feedback = item.FinalFeedback ?? item.DraftFeedback,
                teacherDecision = item.TeacherDecision,
                reviewNotes = item.ReviewNotes,
                reviewStatus = item.ReviewStatus.ToString(),
                draftVersionHash = GradingDraftVersionHash.Compute(item)
            };

            item.InvalidateDraftForScaleRefresh("Escala Moodle alterada; revisar novamente antes de qualquer publicacao.");
            item.RecordContextSnapshot(nextSnapshot);
            await contextSnapshots.PublishAsync(nextSnapshot, cancellationToken);
            await auditLogs.AddAsync(new MoodleAuditLog
            {
                CorrelationId = auditCorrelationId,
                BatchJobId = item.BatchId,
                ToolName = "refresh_grading_scale",
                RiskLevel = ToolRiskLevel.DraftOnly,
                ActorSubject = currentUser.Subject,
                ActorEmail = currentUser.Email,
                CourseId = item.CourseId,
                MoodleConnectionId = batch.MoodleConnectionId,
                MoodleConnectionAlias = batch.ConnectionAlias,
                RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
                {
                    item.Id,
                    item.AssignmentId,
                    previousContextVersion = oldSnapshot.Version,
                    previousContextHash = oldSnapshot.ContextHash,
                    currentScale.Version,
                    previousReview
                }),
                ResponseSummaryJson = AuditPayloadSanitizer.SerializeSanitized(new
                {
                    contextVersion = nextSnapshot.Version,
                    contextHash = nextSnapshot.ContextHash,
                    currentScale.GradingMode,
                    currentScale.MinimumGrade,
                    currentScale.MaximumGrade,
                    currentScale.Source,
                    currentScale.Version,
                    reviewReset = true,
                    invalidatedPreviewActionIds = invalidatedPreviewIds
                }),
                Status = "grading_scale_refreshed"
            }, cancellationToken);

            refreshed.Add(new GradingScaleRefreshItemResult(
                item.Id,
                oldSnapshot.Version,
                nextSnapshot.Version,
                oldSnapshot.ContextHash,
                nextSnapshot.ContextHash,
                previousReview.reviewStatus != GradingReviewStatus.NotReviewed.ToString()));
        }

        await gradingRepository.SaveChangesForGradingContextRefreshAsync(cancellationToken);
        return BuildResult(request, currentScale, refreshed, unchangedItems, invalidatedPreviewIds, status: "refreshed");
    }

    private async Task<IReadOnlyDictionary<Guid, GradingContextSnapshot>> LoadAndValidateSnapshotsAsync(
        IReadOnlyList<GradingScaleRefreshItemExpectation> expectations,
        IReadOnlyCollection<AssistedGradingItem> items,
        CancellationToken cancellationToken)
    {
        var documents = await gradingRepository.ListLatestContextSnapshotsByItemsAsync(
            expectations.Select(item => item.GradingItemId).ToArray(),
            cancellationToken);
        var itemsById = items.ToDictionary(item => item.Id);
        var expectedById = expectations.ToDictionary(item => item.GradingItemId);
        var snapshots = new Dictionary<Guid, GradingContextSnapshot>();
        foreach (var expected in expectations)
        {
            var item = itemsById[expected.GradingItemId];
            if (!documents.TryGetValue(item.Id, out var document))
            {
                throw new InvalidOperationException("O item nao possui snapshot canonico; gere um novo contexto antes de atualizar a escala.");
            }

            GradingContextSnapshot snapshot;
            try
            {
                snapshot = GradingContextSnapshot.DeserializeCanonicalPayload(document.PayloadJson);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "O snapshot canonico e legado ou invalido para refresh de escala; gere um novo contexto antes de tentar novamente.",
                    exception);
            }

            if (document.Version != snapshot.Version ||
                !string.Equals(document.ContextHash, snapshot.ContextHash, StringComparison.Ordinal) ||
                item.ContextVersion != document.Version ||
                !string.Equals(item.ContextHash, document.ContextHash, StringComparison.Ordinal))
            {
                throw new GradingDraftVersionConflictException();
            }

            EnsureExpectedVersions(item, snapshot, expectedById[item.Id]);
            snapshots[item.Id] = snapshot;
        }

        return snapshots;
    }

    private static void EnsureExpectedVersions(
        AssistedGradingItem item,
        GradingContextSnapshot snapshot,
        GradingScaleRefreshItemExpectation expected)
    {
        if (item.ContextVersion != expected.ExpectedContextVersion ||
            snapshot.Version != expected.ExpectedContextVersion ||
            !string.Equals(item.ContextHash, expected.ExpectedContextHash, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ContextHash, expected.ExpectedContextHash, StringComparison.Ordinal) ||
            !string.Equals(GradingDraftVersionHash.Compute(item), expected.ExpectedDraftVersionHash, StringComparison.Ordinal))
        {
            throw new GradingDraftVersionConflictException();
        }
    }

    private async Task<IReadOnlyList<Guid>> CancelPendingPreviewsForChangedItemsAsync(
        GradingBatchScope scope,
        IReadOnlySet<Guid> changedItemIds,
        CancellationToken cancellationToken)
    {
        var scopeIds = scope.Batches
            .Select(batch => batch.Id)
            .Append(scope.RequestedId);
        if (scope.Run is not null)
        {
            scopeIds = scopeIds.Append(scope.Run.Id);
        }

        var actionScopeIds = scopeIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        var actions = await pendingActions.ListGradingPublicationsByBatchIdsAsync(actionScopeIds, cancellationToken);
        var invalidated = new List<Guid>();
        foreach (var action in actions.Where(action =>
                     action.ToolName is "criar_previa_lancamento_lote" or "confirmar_lancamento_lote_moodle"))
        {
            GradingLaunchPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<GradingLaunchPayload>(action.PayloadJson, PayloadJsonOptions)
                    ?? throw new JsonException("Payload vazio.");
            }
            catch (JsonException exception)
            {
                if (action.Status == PendingActionStatus.PendingConfirmation || IsPublicationInFlight(action.Status))
                {
                    throw new InvalidOperationException("Existe uma acao de publicacao ativa com payload ilegivel; o refresh foi bloqueado por seguranca.", exception);
                }

                continue;
            }

            if (!payload.Items.Any(item => changedItemIds.Contains(item.GradingItemId)))
            {
                continue;
            }

            if (action.Status == PendingActionStatus.PendingConfirmation)
            {
                var cancelled = await pendingActions.TryCancelPendingGradingPublicationAsync(
                    action.Id,
                    payload.PublicationId,
                    "Prévia invalidada porque a escala Moodle do item foi atualizada.",
                    cancellationToken);
                if (!cancelled)
                {
                    throw new GradingDraftVersionConflictException();
                }

                invalidated.Add(action.Id);
                continue;
            }

            if (IsPublicationInFlight(action.Status) || action.Status == PendingActionStatus.Executed)
            {
                throw new InvalidOperationException("Um item selecionado esta reservado ou publicado por uma acao Moodle; reconcilie ou aguarde antes do refresh.");
            }
        }

        return invalidated;
    }

    private async Task<AssignmentSettingsSummary?> ReadFreshAssignmentSettingsAsync(
        AssistedGradingBatch batch,
        long courseId,
        long assignmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await assignmentSettings.GetAssignmentSettingsFreshAsync(
                batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ?? batch.CreatedBySubject,
                courseId.ToString(CultureInfo.InvariantCulture),
                assignmentId.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Nao foi possivel ler a definicao Moodle atual sem cache; nenhum item foi alterado.",
                exception);
        }
    }

    private static void EnsureItemCanRefresh(AssistedGradingItem item)
    {
        if (item.CommitStatus is GradingCommitStatus.Succeeded or GradingCommitStatus.ExecutionUnknown ||
            item.CommitWriteIntentAt is not null ||
            item.Status == GradingItemStatus.Committed)
        {
            throw new InvalidOperationException("Item publicado ou com resultado Moodle incerto nao pode ser atualizado por refresh de escala.");
        }

        if (item.LeaseOwner is not null && item.LeaseUntil > DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("O item esta em processamento por um worker; aguarde a conclusao antes do refresh.");
        }
    }

    private static bool IsPublicationInFlight(PendingActionStatus status) => status is
        PendingActionStatus.PendingConfirmation or
        PendingActionStatus.Confirmed or
        PendingActionStatus.Authorized or
        PendingActionStatus.Executing or
        PendingActionStatus.ExecutionUnknown or
        PendingActionStatus.PartiallyCompleted;

    private static RefreshGradingScaleResult BuildResult(
        RefreshGradingScaleCommand request,
        MoodleAssignmentGradeScaleResolution scale,
        IReadOnlyList<GradingScaleRefreshItemResult> refreshed,
        IReadOnlyList<Guid> unchanged,
        IReadOnlyList<Guid> invalidatedPreviewIds,
        string status) =>
        new(
            request.BatchJobId,
            request.AssignmentId.ToString(CultureInfo.InvariantCulture),
            status,
            scale.GradingMode,
            scale.MinimumGrade,
            scale.MaximumGrade,
            scale.Source,
            scale.Version,
            refreshed,
            unchanged,
            invalidatedPreviewIds,
            refreshed.Count == 0 ? "none" : "review_and_prepare_new_preview");

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => Uri.IsHexDigit(character));
}
