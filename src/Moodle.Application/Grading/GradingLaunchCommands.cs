using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Auditing;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Application.PendingActions;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

public sealed record CreateGradingLaunchPreviewCommand(
    Guid BatchJobId,
    IReadOnlyList<Guid> GradingItemIds,
    bool OnlyReviewed,
    bool AllowOverwriteExisting = false,
    bool AllowPartial = false) : IRequest<CreateGradingLaunchPreviewResult>;

public sealed record CreateGradingLaunchPreviewResult(
    [property: JsonPropertyName("pendingActionId")] Guid PendingActionId,
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("readyItems")] int ReadyItems,
    [property: JsonPropertyName("blockedItems")] int BlockedItems,
    [property: JsonPropertyName("launches")] IReadOnlyList<GradingLaunchPreviewItem> Launches,
    [property: JsonPropertyName("confirmationText")] string ConfirmationText,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("expectedItems")] int ExpectedItems = 0,
    [property: JsonPropertyName("preparedItems")] int PreparedItems = 0,
    [property: JsonPropertyName("missingItems")] int MissingItems = 0,
    [property: JsonPropertyName("missingBatchCount")] int MissingBatchCount = 0,
    [property: JsonPropertyName("decisionSafe")] bool DecisionSafe = true,
    [property: JsonPropertyName("blockedReasons")] IReadOnlyDictionary<string, int>? BlockedReasons = null,
    [property: JsonPropertyName("approvalMode")] string ApprovalMode = "user_affirmative_after_preview",
    [property: JsonPropertyName("publicationState")] string PublicationState = "awaiting_approval",
    [property: JsonPropertyName("page")] int Page = 1,
    [property: JsonPropertyName("pageSize")] int PageSize = 25,
    [property: JsonPropertyName("previewItemCount")] int PreviewItemCount = 0,
    [property: JsonPropertyName("hasMore")] bool HasMore = false,
    [property: JsonPropertyName("nextAction")] string NextAction = "confirm");

public sealed record GradingLaunchPreviewItem(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("grade")] decimal? Grade,
    [property: JsonPropertyName("feedbackText")] string FeedbackText,
    [property: JsonPropertyName("contextHash")] string? ContextHash = null,
    [property: JsonPropertyName("submissionContentHash")] string? SubmissionContentHash = null,
    [property: JsonPropertyName("studentName")] string? StudentName = null,
    [property: JsonPropertyName("situation")] string Situation = "pronta_para_publicacao",
    [property: JsonPropertyName("preflightHash")] string? PreflightHash = null,
    [property: JsonPropertyName("existingGrade")] decimal? ExistingGrade = null,
    [property: JsonPropertyName("hasExistingFeedback")] bool HasExistingFeedback = false,
    [property: JsonPropertyName("assignmentName")] string? AssignmentName = null,
    [property: JsonPropertyName("maxGrade")] decimal? MaxGrade = null,
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus = "NotReviewed",
    [property: JsonPropertyName("minGrade")] decimal? MinGrade = null,
    [property: JsonPropertyName("gradingMode")] string? GradingMode = null,
    [property: JsonPropertyName("scaleSource")] string? ScaleSource = null,
    [property: JsonPropertyName("scaleVersion")] string? ScaleVersion = null,
    [property: JsonPropertyName("workflowMode")] string? WorkflowMode = null,
    [property: JsonPropertyName("workflowState")] string? WorkflowState = null,
    [property: JsonPropertyName("releaseState")] string? ReleaseState = null,
    [property: JsonPropertyName("visibilityState")] string? VisibilityState = "unknown");

public sealed record ConfirmMoodleBatchLaunchCommand(
    Guid PendingActionId,
    string? ConfirmationText,
    // Public MCP confirmation only authorizes and leaves the durable worker
    // to execute. The default remains true for direct application callers and
    // legacy tests; the presentation tool passes false explicitly.
    bool ExecuteImmediately = true) : IRequest<ConfirmMoodleBatchLaunchResult>;

public sealed record ConfirmMoodleBatchLaunchResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("pendingActionId")] Guid PendingActionId,
    [property: JsonPropertyName("sentItems")] int SentItems,
    [property: JsonPropertyName("failedItems")] int FailedItems,
    [property: JsonPropertyName("failures")] IReadOnlyList<GradingLaunchFailure> Failures,
    [property: JsonPropertyName("auditId")] string? AuditId,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string>? Warnings = null,
    [property: JsonPropertyName("batchJobId")] Guid? BatchJobId = null,
    [property: JsonPropertyName("items")] IReadOnlyList<GradingLaunchPublicationItem>? Items = null,
    [property: JsonPropertyName("retryScheduledItems")] int RetryScheduledItems = 0,
    [property: JsonPropertyName("nextAttemptAt")] DateTimeOffset? NextAttemptAt = null);

public sealed record GradingLaunchFailure(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("message")] string Message);

public sealed record GradingLaunchPublicationItem(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("assignmentName")] string? AssignmentName,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("studentName")] string? StudentName,
    [property: JsonPropertyName("publishedGrade")] decimal? PublishedGrade,
    [property: JsonPropertyName("publishedFeedback")] string? PublishedFeedback,
    [property: JsonPropertyName("commitStatus")] string CommitStatus,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("failureReason")] string? FailureReason = null,
    [property: JsonPropertyName("releaseState")] string? ReleaseState = null,
    [property: JsonPropertyName("visibilityState")] string? VisibilityState = "unknown");

public sealed record GradingLaunchPayload(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("items")] IReadOnlyList<GradingLaunchPayloadItem> Items,
    [property: JsonPropertyName("allowOverwriteExisting")] bool AllowOverwriteExisting = false,
    [property: JsonPropertyName("publicationId")] Guid? PublicationId = null,
    [property: JsonPropertyName("connectionKey")] string? ConnectionKey = null,
    [property: JsonPropertyName("contractVersion")] int ContractVersion = 0)
{
    public const int CurrentContractVersion = 2;
}

public sealed record GradingLaunchPayloadItem(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("grade")] decimal? Grade,
    [property: JsonPropertyName("feedbackText")] string FeedbackText,
    [property: JsonPropertyName("attemptNumber")] int? AttemptNumber,
    [property: JsonPropertyName("draftVersionHash")] string DraftVersionHash,
    [property: JsonPropertyName("contextHash")] string? ContextHash = null,
    [property: JsonPropertyName("submissionContentHash")] string? SubmissionContentHash = null,
    [property: JsonPropertyName("preflightHash")] string? PreflightHash = null,
    [property: JsonPropertyName("studentName")] string? StudentName = null,
    [property: JsonPropertyName("assignmentName")] string? AssignmentName = null,
    [property: JsonPropertyName("maxGrade")] decimal? MaxGrade = null,
    [property: JsonPropertyName("minGrade")] decimal? MinGrade = null,
    [property: JsonPropertyName("gradingMode")] string? GradingMode = null,
    [property: JsonPropertyName("scaleSource")] string? ScaleSource = null,
    [property: JsonPropertyName("scaleVersion")] string? ScaleVersion = null,
    // Null grade is not a sufficient transport instruction: on a graded
    // assignment Moodle interprets -1 as removing the grade. Persist the
    // reviewed operation and baseline so a feedback-only update can retain it.
    [property: JsonPropertyName("gradeOperation")] string? GradeOperation = null,
    [property: JsonPropertyName("baselineHasGrade")] bool BaselineHasGrade = false,
    [property: JsonPropertyName("baselineGrade")] decimal? BaselineGrade = null,
    [property: JsonPropertyName("workflowMode")] string? WorkflowMode = null,
    [property: JsonPropertyName("workflowState")] string? WorkflowState = null,
    [property: JsonPropertyName("releaseState")] string? ReleaseState = null,
    [property: JsonPropertyName("visibilityState")] string? VisibilityState = null);

public sealed record AssignmentExistingGrade(
    string AssignmentId,
    string StudentId,
    decimal? Grade,
    bool HasGrade,
    string? Feedback = null,
    decimal? GradeMax = null,
    long? GraderId = null,
    long? TimeModified = null,
    bool FeedbackObserved = false);

public sealed record AssignmentSubmissionAttemptStatus(
    string AssignmentId,
    string StudentId,
    int? AttemptNumber,
    string? SubmissionStatus,
    bool HasFeedback = false);

public sealed class CreateGradingLaunchPreviewCommandHandler(
    IGradingReviewRepository repository,
    IPendingActionService pendingActions,
    ICurrentUserContext currentUser,
    IMoodleAssignmentSettingsGateway settingsGateway,
    IMoodleAssignmentGradeReadGateway? gradeReadGateway = null,
    IMoodleAssignmentSubmissionStatusGateway? submissionStatusGateway = null,
    IMoodleParticipantsGateway? participantsGateway = null,
    IMoodleAssignmentSubmissionsGateway? submissionsGateway = null,
    IOptions<GradingLimitsOptions>? gradingLimits = null,
    IOptions<MoodleUniversalApiFeatureOptions>? resourceFeatures = null,
    IOptions<PendingActionOptions>? pendingActionOptions = null,
    IPendingMoodleActionRepository? pendingActionRepository = null,
    IMoodleGradebookGateway? gradebookGateway = null,
    IConnectorExecutionContext? executionContext = null,
    IMoodleConnectionSelection? connectionSelection = null,
    IMoodleBackgroundConnectionContextRestorer? backgroundConnectionContextRestorer = null)
    : IRequestHandler<CreateGradingLaunchPreviewCommand, CreateGradingLaunchPreviewResult>
{
    private const string ToolName = "criar_previa_lancamento_lote";
    private static readonly JsonSerializerOptions PreviewJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan publicationReviewExpiration =
        pendingActionOptions?.Value.ResolveExpiration(PendingActionTtlClass.Stable)
        ?? TimeSpan.FromHours(
            Math.Clamp(gradingLimits?.Value.PublicationReviewExpirationHours ?? 168, 1, 168));

    public async Task<CreateGradingLaunchPreviewResult> Handle(
        CreateGradingLaunchPreviewCommand request,
        CancellationToken cancellationToken)
    {
        var scope = await GradingBatchScopeResolver.ResolveAsync(
            repository,
            currentUser,
            request.BatchJobId,
            cancellationToken);
        var batch = scope.FirstBatch
            ?? throw new InvalidOperationException("A execucao de correcao ainda nao possui sublotes.");
        var destinationRun = scope.DestinationRun;
        if (destinationRun is not null && string.Equals(destinationRun.Destination, "csv", StringComparison.Ordinal))
        {
            throw new MoodleApiException(
                MoodleErrorContract.DestinationLocked,
                "Esta execucao ja foi direcionada para CSV; gere um novo gradingRunId para publicar no Moodle.");
        }
        var connectionKeys = scope.Batches
            .Select(ResolvePublicationConnectionKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (connectionKeys.Length > 1)
        {
            throw new InvalidOperationException("Uma execucao agregada nao pode misturar conexoes Moodle diferentes; crie uma execucao por conexao.");
        }
        var publicationConnectionKey = connectionKeys[0];
        await GradingMoodleConnectionContext.RestoreAsync(
            batch,
            executionContext,
            connectionSelection,
            backgroundConnectionContextRestorer,
            cancellationToken);
        var allItems = new List<AssistedGradingItem>();
        foreach (var child in scope.Batches)
        {
            allItems.AddRange(await LoadBatchItemsAsync(child.Id, cancellationToken));
        }
        var coverage = await GradingRunCoverageCalculator.CalculateAsync(
            repository,
            scope,
            allItems.Count,
            cancellationToken);
        var selectedIds = request.GradingItemIds.ToHashSet();
        var selected = selectedIds.Count == 0
            ? allItems.ToArray()
            : allItems.Where(item => selectedIds.Contains(item.Id)).ToArray();
        var blockedReasonByItem = new Dictionary<Guid, string>();
        var blockedReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void MarkBlocked(Guid itemId, string reason)
        {
            if (blockedReasonByItem.ContainsKey(itemId))
            {
                return;
            }

            blockedReasonByItem[itemId] = reason;
            blockedReasonCounts.TryGetValue(reason, out var current);
            blockedReasonCounts[reason] = current + 1;
        }

        foreach (var item in selected)
        {
            if (ToLaunchCandidate(item, request.OnlyReviewed) is null)
            {
                MarkBlocked(item.Id, ClassifyLaunchBlockReason(item, request.OnlyReviewed));
            }
        }
        var securityWarningsOnly = resourceFeatures?.Value.McpGradingSecurityWarningsOnly == true;
        if (scope.IsRun && selectedIds.Count == 0 && !coverage.DecisionSafe && !request.AllowPartial && !securityWarningsOnly)
        {
            blockedReasonCounts.Clear();
            blockedReasonCounts["coverage_incomplete"] = selected.Length;
            return new CreateGradingLaunchPreviewResult(
                Guid.Empty,
                request.BatchJobId,
                selected.Length,
                ReadyItems: 0,
                BlockedItems: selected.Length,
                Launches: [],
                ConfirmationText: string.Empty,
                ExpiresAt: null,
                Warnings:
                [
                    $"Cobertura incompleta da execucao: esperados {coverage.ExpectedItems}, preparados {coverage.PreparedItems}, ausentes {coverage.MissingItems} em {coverage.MissingBatchCount} sublote(s).",
                    "A previa global foi bloqueada. Corrija a cobertura ou envie allowPartial=true para autorizar conscientemente uma publicacao parcial."
                ],
                ExpectedItems: coverage.ExpectedItems,
                PreparedItems: coverage.PreparedItems,
                MissingItems: coverage.MissingItems,
                MissingBatchCount: coverage.MissingBatchCount,
                DecisionSafe: coverage.DecisionSafe,
                BlockedReasons: blockedReasonCounts,
                NextAction: "attention");
        }
        var launchable = selected
            .Select(item => ToLaunchCandidate(item, request.OnlyReviewed))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
        var duplicateTargetItemIds = launchable
            .GroupBy(candidate => (
                candidate.Item.CourseId,
                candidate.Item.AssignmentId,
                candidate.Item.MoodleUserId))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(candidate => candidate.Item.Id))
            .ToHashSet();
        var snapshotsByItem = await repository.ListLatestContextSnapshotsByItemsAsync(
            launchable.Select(candidate => candidate.Item.Id).ToArray(),
            cancellationToken);
        var restoredContextIdentity = false;
        var scaleWarnings = new List<string>();
        var contextWarnings = new List<string>();
        if (scope.IsRun && !coverage.DecisionSafe && request.AllowPartial)
        {
            contextWarnings.Add(
                $"Publicacao parcial explicitamente autorizada: esperados {coverage.ExpectedItems}, preparados {coverage.PreparedItems}, ausentes {coverage.MissingItems} em {coverage.MissingBatchCount} sublote(s).");
        }
        else if (scope.IsRun && !coverage.DecisionSafe && securityWarningsOnly)
        {
            contextWarnings.Add(
                $"Aviso tecnico de cobertura: esperados {coverage.ExpectedItems}, preparados {coverage.PreparedItems}, ausentes {coverage.MissingItems} em {coverage.MissingBatchCount} sublote(s). A previa segue com os itens preparados; os ausentes permanecem pendentes.");
        }
        var ready = new List<GradingLaunchCandidate>();
        var settingsCache = new Dictionary<(long CourseId, long AssignmentId), AssignmentSettingsSummary?>();
        var scaleCache = new Dictionary<(long CourseId, long AssignmentId), MoodleAssignmentGradeScaleResolution>();
        var currentGradebooksByTarget = new Dictionary<(long CourseId, long StudentId), CourseGradebook?>();
        var gradebookReadAtByTarget = new Dictionary<(long CourseId, long StudentId), DateTimeOffset>();
        var gradebookReadFailuresByTarget = new Dictionary<(long CourseId, long StudentId), string>();
        var previewReadMaxAge = TimeSpan.FromSeconds(
            Math.Clamp(gradingLimits?.Value.PublicationPreflightMaxAgeSeconds ?? 120, 15, 1800));
        var settingsUserExternalId = batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ?? batch.CreatedBySubject;
        foreach (var courseGroup in launchable.GroupBy(candidate => candidate.Item.CourseId))
        {
            try
            {
                var courseSettings = await settingsGateway.GetCourseAssignmentSettingsFreshAsync(
                    settingsUserExternalId,
                    courseGroup.Key.ToString(CultureInfo.InvariantCulture),
                    cancellationToken);
                foreach (var pair in courseSettings)
                {
                    if (long.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var assignmentId) &&
                        assignmentId > 0)
                    {
                        settingsCache[(courseGroup.Key, assignmentId)] = pair.Value;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Per-assignment fresh reads below provide bounded fallback
                // when a course-level response is unavailable.
            }
        }
        if (gradebookGateway is not null)
        {
            foreach (var courseGroup in launchable.GroupBy(candidate => candidate.Item.CourseId))
            {
                var candidates = courseGroup.ToArray();
                try
                {
                    var snapshot = await gradebookGateway.GetCourseGradebookAsync(
                        courseGroup.Key.ToString(CultureInfo.InvariantCulture),
                        candidates.Select(candidate => candidate.Item.MoodleUserId.ToString(CultureInfo.InvariantCulture))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        groupId: null,
                        readOptions: GradebookReadOptions.FreshBatch,
                        cancellationToken: cancellationToken);
                    foreach (var candidate in candidates)
                    {
                        var key = (candidate.Item.CourseId, candidate.Item.MoodleUserId);
                        var studentId = candidate.Item.MoodleUserId.ToString(CultureInfo.InvariantCulture);
                        if (snapshot.TryGetForStudent(studentId, out var gradebook))
                        {
                            currentGradebooksByTarget[key] = gradebook;
                            gradebookReadAtByTarget[key] = gradebook.ReadAt ?? snapshot.ReadAt ?? DateTimeOffset.UtcNow;
                        }
                        else if (snapshot.Coverage.ErrorStudentIds.Contains(studentId, StringComparer.OrdinalIgnoreCase))
                        {
                            currentGradebooksByTarget[key] = null;
                            gradebookReadFailuresByTarget[key] = "feedback_read_failed";
                        }
                        else
                        {
                            currentGradebooksByTarget[key] = null;
                            gradebookReadFailuresByTarget[key] = "feedback_read_incomplete";
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    foreach (var candidate in candidates)
                    {
                        var key = (candidate.Item.CourseId, candidate.Item.MoodleUserId);
                        currentGradebooksByTarget[key] = null;
                        gradebookReadFailuresByTarget[key] = exception.GetType().Name;
                    }
                }
            }
        }
        // Enrollment preflight is cached at course scope. The previous
        // implementation cached by (course, student), which still paged the
        // same Moodle participant list once for every student in a 10k run.
        // A course-level set turns that into one paginated read per course.
        var courseEnrollmentCache = new Dictionary<string, (IReadOnlySet<string>? StudentIds, IReadOnlyDictionary<string, string>? StudentNames, string? Error)>(StringComparer.OrdinalIgnoreCase);
        var existingGradesByTarget = new Dictionary<(long AssignmentId, long StudentId), AssignmentExistingGrade?>();
        var gradeReadFailuresByAssignment = new Dictionary<long, string>();
        if (gradeReadGateway is not null && launchable.Length > 0)
        {
            var gradeReadUserExternalId = batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ?? batch.CreatedBySubject;
            try
            {
                // Fetch grades in Moodle's bulk endpoint once per assignment
                // set, instead of issuing one remote read for every item in a
                // 10k-item run. The gateway chunks assignments according to
                // the configured Moodle limit and preserves per-assignment
                // errors for safe partial coverage.
                var gradeBatches = await GradingMoodleReadRetry.ExecuteAsync(
                    retryCancellationToken => gradeReadGateway.GetExistingGradesBatchAsync(
                        gradeReadUserExternalId,
                        launchable.Select(candidate => candidate.Item.AssignmentId.ToString(CultureInfo.InvariantCulture)).Distinct().ToArray(),
                        launchable.Select(candidate => candidate.Item.MoodleUserId.ToString(CultureInfo.InvariantCulture)).Distinct().ToArray(),
                        retryCancellationToken),
                    onRetry: null,
                    cancellationToken);
                foreach (var gradeBatch in gradeBatches)
                {
                    if (!long.TryParse(gradeBatch.AssignmentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var assignmentId))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(gradeBatch.ErrorMessage))
                    {
                        gradeReadFailuresByAssignment[assignmentId] = gradeBatch.ErrorMessage;
                        continue;
                    }

                    foreach (var entry in gradeBatch.Grades)
                    {
                        if (long.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var studentId))
                        {
                            existingGradesByTarget[(assignmentId, studentId)] = entry.Value;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                foreach (var assignmentId in launchable.Select(candidate => candidate.Item.AssignmentId).Distinct())
                {
                    gradeReadFailuresByAssignment[assignmentId] = exception.GetType().Name;
                }
            }
        }
        var bulkSubmissionStatuses = await GradingBulkSubmissionStatusReader.ReadAsync(
            submissionsGateway,
            batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ?? batch.CreatedBySubject,
            launchable.Select(candidate => candidate.Item.AssignmentId.ToString(CultureInfo.InvariantCulture)).Distinct().ToArray(),
            cancellationToken);
        foreach (var candidate in launchable)
        {
            var preparedCandidate = candidate;
            var item = candidate.Item;
            if (duplicateTargetItemIds.Contains(item.Id))
            {
                MarkBlocked(item.Id, "duplicate_publication_target");
                contextWarnings.Add(
                    $"Item {item.Id}: a seleção contém mais de uma correção para o mesmo aluno/atividade; todos os duplicados foram bloqueados.");
                continue;
            }

            if (NeedsContextIdentityRestore(item) &&
                snapshotsByItem.TryGetValue(item.Id, out var snapshotDocument))
            {
                try
                {
                    item.RestoreContextSnapshotIdentity(snapshotDocument);
                    restoredContextIdentity = true;
                }
                catch (InvalidOperationException)
                {
                    // A mensagem segura e uniforme abaixo evita expor detalhes
                    // de integridade e mantém o lançamento bloqueado.
                }
            }

            if (!HasVersionedContextIdentity(item))
            {
                MarkBlocked(item.Id, "missing_context_snapshot");
                contextWarnings.Add(
                    $"Item {item.Id}: contexto de correcao ausente ou legado; gere uma nova previa antes do lancamento.");
                continue;
            }

            if (!HasSealedSubmissionForBlockedContext(item))
            {
                contextWarnings.Add(
                    $"Item {item.Id}: aviso tecnico de seguranca — o rascunho nao foi selado com todos os anexos originais da submissao; as resourceUris originais permanecem vinculadas ao item.");
                if (!securityWarningsOnly)
                {
                    MarkBlocked(item.Id, "missing_submission_integrity");
                    continue;
                }
            }

            if (securityWarningsOnly &&
                item.SubmissionId is not null &&
                string.IsNullOrWhiteSpace(item.SubmissionContentHash))
            {
                contextWarnings.Add(
                    $"Item {item.Id}: aviso tecnico de seguranca — a integridade da submissao nao foi selada; a correcao segue para confirmacao porque o contexto e o alvo continuam vinculados ao item.");
            }

            // Resolve assignment metadata for every preview item, including
            // feedback-only items. The preview is the human decision
            // contract and must not depend on the client inferring names or
            // maximum grades from another response.
            var gradeScale = await GetKnownGradeScaleAsync(
                batch,
                item,
                settingsCache,
                scaleCache,
                currentGradebooksByTarget,
                cancellationToken);
            var assignmentSettings = settingsCache.GetValueOrDefault((item.CourseId, item.AssignmentId));
            if (assignmentSettings?.MarkingWorkflowEnabled is not false)
            {
                var workflowReason = assignmentSettings?.MarkingWorkflowEnabled == true
                    ? "marking_workflow_requires_explicit_transition"
                    : "marking_workflow_setting_unknown";
                MarkBlocked(item.Id, workflowReason);
                contextWarnings.Add(assignmentSettings?.MarkingWorkflowEnabled == true
                    ? $"Item {item.Id}: a atividade usa fluxo de avaliação. A conexão não expõe os estados que este avaliador pode definir; a publicação foi bloqueada para evitar alterar a liberação ou perder a nota."
                    : $"Item {item.Id}: o Moodle não confirmou se a atividade usa fluxo de avaliação; a publicação foi bloqueada.");
                continue;
            }

            var assignmentName = assignmentSettings?.Name;
            preparedCandidate = preparedCandidate with
            {
                StudentName = item.StudentDisplayName,
                AssignmentName = assignmentName,
                MaxGrade = gradeScale.MaximumGrade,
                MinGrade = gradeScale.MinimumGrade,
                GradingMode = gradeScale.GradingMode,
                ScaleSource = gradeScale.Source,
                ScaleVersion = gradeScale.Version,
                WorkflowMode = "disabled",
                WorkflowState = null,
                ReleaseState = "not_applicable",
                VisibilityState = "unknown"
            };

            if (gradeScale.GradingMode == "unknown")
            {
                MarkBlocked(item.Id, "missing_grade_scale");
                scaleWarnings.Add(
                    $"Item {item.Id}: escala da atividade nao foi confirmada por uma fonte Moodle exata; lancamento bloqueado.");
                continue;
            }

            if (candidate.Grade is not null)
            {
                if (gradeScale.GradingMode != "numeric" ||
                    gradeScale.MinimumGrade is null ||
                    gradeScale.MaximumGrade is null)
                {
                    MarkBlocked(item.Id, "missing_grade_scale");
                    scaleWarnings.Add(
                        $"Item {item.Id}: nota maxima da atividade nao foi confirmada; lancamento numerico bloqueado.");
                    continue;
                }

                if (candidate.Grade < gradeScale.MinimumGrade)
                {
                    MarkBlocked(item.Id, "grade_below_min");
                    scaleWarnings.Add(
                        $"Item {item.Id}: nota {FormatGrade(candidate.Grade!.Value)} esta abaixo do minimo Moodle confirmado {FormatGrade(gradeScale.MinimumGrade.Value)}.");
                    continue;
                }

                if (candidate.Grade > gradeScale.MaximumGrade)
                {
                    MarkBlocked(item.Id, "grade_exceeds_max");
                    scaleWarnings.Add(
                        $"Item {item.Id}: nota {FormatGrade(candidate.Grade!.Value)} excede nota maxima {FormatGrade(gradeScale.MaximumGrade.Value)} confirmada pelo Moodle.");
                    continue;
                }
            }

            if (gradeReadGateway is null)
            {
                MarkBlocked(item.Id, "grade_read_unavailable");
                contextWarnings.Add($"Item {item.Id}: nao foi possivel consultar a nota atual no Moodle; a publicacao foi bloqueada.");
                continue;
            }

            if (gradeReadGateway is not null)
            {
                if (gradeReadFailuresByAssignment.TryGetValue(item.AssignmentId, out var gradeReadFailure))
                {
                    MarkBlocked(item.Id, "grade_read_failed");
                    contextWarnings.Add($"Item {item.Id}: nao foi possivel consultar a nota/feedback atual no Moodle ({gradeReadFailure}); a publicacao foi bloqueada.");
                    continue;
                }

                var assignmentGrade = existingGradesByTarget.GetValueOrDefault((item.AssignmentId, item.MoodleUserId));
                if (gradebookGateway is null)
                {
                    MarkBlocked(item.Id, "feedback_read_unavailable");
                    contextWarnings.Add($"Item {item.Id}: o gradebook Moodle nao esta disponivel para confirmar o feedback atual; a publicacao foi bloqueada.");
                    continue;
                }

                var gradebookKey = (item.CourseId, item.MoodleUserId);
                currentGradebooksByTarget.TryGetValue(gradebookKey, out var currentGradebook);
                gradebookReadFailuresByTarget.TryGetValue(gradebookKey, out var gradebookFailure);
                if (currentGradebook is not null &&
                    gradebookReadAtByTarget.TryGetValue(gradebookKey, out var previewGradebookReadAt) &&
                    DateTimeOffset.UtcNow - previewGradebookReadAt > previewReadMaxAge)
                {
                    currentGradebook = null;
                    gradebookFailure = "preflight_read_expired";
                }
                if (!string.IsNullOrWhiteSpace(gradebookFailure))
                {
                    var failedRead = string.Equals(gradebookFailure, "feedback_read_failed", StringComparison.Ordinal)
                        || !string.Equals(gradebookFailure, "feedback_read_incomplete", StringComparison.Ordinal)
                        && !string.Equals(gradebookFailure, "preflight_read_expired", StringComparison.Ordinal);
                    MarkBlocked(item.Id, gradebookFailure == "preflight_read_expired"
                        ? "preflight_read_expired"
                        : failedRead ? "feedback_read_failed" : "feedback_read_incomplete");
                    contextWarnings.Add(failedRead
                        ? $"Item {item.Id}: nao foi possivel ler feedback fresco no Moodle ({gradebookFailure}); a publicacao foi bloqueada."
                        : gradebookFailure == "preflight_read_expired"
                            ? $"Item {item.Id}: a leitura fresca do gradebook venceu antes da previa terminar; a publicacao foi bloqueada."
                            : $"Item {item.Id}: o Moodle nao retornou uma linha do gradebook para este aluno; a publicacao foi bloqueada.");
                    continue;
                }

                var existingGrade = GradingPublicationGradebookProjection.Combine(
                    item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                    item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
                    assignmentGrade,
                    currentGradebook);
                if (existingGrade is null)
                {
                    MarkBlocked(item.Id, "feedback_read_incomplete");
                    contextWarnings.Add($"Item {item.Id}: o Moodle nao retornou feedback observavel para a atividade e estudante; a publicacao foi bloqueada.");
                    continue;
                }

                var gradeOperation = ResolveGradeOperation(candidate.Grade, gradeScale.GradingMode);

                if (gradeOperation == "unsupported")
                {
                    MarkBlocked(item.Id, "unsupported_grade_operation");
                    contextWarnings.Add($"Item {item.Id}: correcao sem nota em uma atividade com escala nao numerica ainda nao possui semantica de escrita validada.");
                    continue;
                }

                if (gradeOperation == "preserve_grade" && existingGrade is null)
                {
                    MarkBlocked(item.Id, "grade_baseline_unavailable");
                    contextWarnings.Add($"Item {item.Id}: nao foi possivel confirmar a nota atual que deve ser preservada ao atualizar somente o feedback.");
                    continue;
                }

                if (gradeOperation == "no_grade" && existingGrade?.HasGrade == true)
                {
                    MarkBlocked(item.Id, "existing_grade_cannot_be_removed");
                    contextWarnings.Add($"Item {item.Id}: a atividade sem nota possui uma nota atual; a publicacao nao pode remove-la por esta operacao.");
                    continue;
                }

                if (gradeOperation == "set_grade" && existingGrade?.HasGrade == true && !request.AllowOverwriteExisting)
                {
                    MarkBlocked(item.Id, "existing_grade");
                    contextWarnings.Add($"Item {item.Id}: ja existe nota no Moodle; a publicacao foi bloqueada nesta previa.");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(existingGrade?.Feedback) && !request.AllowOverwriteExisting &&
                    !string.IsNullOrWhiteSpace(candidate.FeedbackText))
                {
                    MarkBlocked(item.Id, "existing_feedback");
                    contextWarnings.Add($"Item {item.Id}: ja existe feedback no Moodle; a publicacao foi bloqueada nesta previa.");
                    continue;
                }

                preparedCandidate = preparedCandidate with
                {
                    ExistingGrade = existingGrade,
                    PreflightHash = ComputePreflightHash(batch, item, existingGrade)
                };
            }

            // The preview is the operator's complete preflight contract. The
            // confirmation path repeats these reads, but doing them here
            // prevents an obviously stale attempt or enrollment from ever
            // reaching the confirmation screen. Optional dependencies keep
            // legacy/unit-only stores compatible; production registers both.
            if ((submissionStatusGateway is not null || bulkSubmissionStatuses.Attempted) && item.AttemptNumber is not null)
            {
                AssignmentSubmissionAttemptStatus? currentStatus;
                if (bulkSubmissionStatuses.FailedAssignments.Contains(item.AssignmentId.ToString(CultureInfo.InvariantCulture)))
                {
                    MarkBlocked(item.Id, "submission_status_unavailable");
                    contextWarnings.Add($"Item {item.Id}: nao foi possivel validar a tentativa atual no Moodle (falha na consulta em lote); a publicacao foi bloqueada.");
                    continue;
                }
                if (!bulkSubmissionStatuses.TryGet(item.AssignmentId.ToString(CultureInfo.InvariantCulture), item.MoodleUserId.ToString(CultureInfo.InvariantCulture), out currentStatus))
                {
                    if (submissionStatusGateway is null || launchable.Length > GradingBulkSubmissionStatusReader.PerItemFallbackLimit)
                    {
                        MarkBlocked(item.Id, "submission_not_found");
                        contextWarnings.Add($"Item {item.Id}: a submissao atual nao foi encontrada na consulta em lote do Moodle; a publicacao foi bloqueada.");
                        continue;
                    }

                    try
                    {
                        currentStatus = await GradingMoodleReadRetry.ExecuteAsync(
                            retryCancellationToken => submissionStatusGateway.GetSubmissionStatusAsync(
                                batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ?? batch.CreatedBySubject,
                                item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                                item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
                                retryCancellationToken),
                            onRetry: null,
                            cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        MarkBlocked(item.Id, "submission_status_unavailable");
                        contextWarnings.Add($"Item {item.Id}: nao foi possivel validar a tentativa atual no Moodle ({exception.GetType().Name}); a publicacao foi bloqueada.");
                        continue;
                    }
                }

                if (currentStatus?.AttemptNumber is not null &&
                    currentStatus.AttemptNumber != item.AttemptNumber)
                {
                    MarkBlocked(item.Id, "attempt_changed");
                    contextWarnings.Add($"Item {item.Id}: a tentativa da submissao mudou no Moodle; gere uma nova previa antes do lancamento.");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(currentStatus?.SubmissionStatus) &&
                    !string.Equals(currentStatus.SubmissionStatus, "submitted", StringComparison.OrdinalIgnoreCase))
                {
                    MarkBlocked(item.Id, "submission_not_submitted");
                    contextWarnings.Add($"Item {item.Id}: a submissao atual nao esta entregue no Moodle; a publicacao foi bloqueada.");
                    continue;
                }

                if (currentStatus?.HasFeedback == true &&
                    !request.AllowOverwriteExisting &&
                    !string.IsNullOrWhiteSpace(candidate.FeedbackText) &&
                    gradeReadGateway is null)
                {
                    MarkBlocked(item.Id, "existing_feedback");
                    contextWarnings.Add($"Item {item.Id}: ja existe feedback no Moodle; a publicacao foi bloqueada nesta previa.");
                    continue;
                }
            }

            if (participantsGateway is not null)
            {
                var courseId = item.CourseId.ToString(CultureInfo.InvariantCulture);
                if (!courseEnrollmentCache.TryGetValue(courseId, out var enrollment))
                {
                    enrollment = await ResolveCourseEnrollmentPreflightAsync(
                        participantsGateway,
                        batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ?? batch.CreatedBySubject,
                        courseId,
                        cancellationToken);
                    courseEnrollmentCache[courseId] = enrollment;
                }

                if (enrollment.Error is not null)
                {
                    MarkBlocked(item.Id, "enrollment_check_failed");
                    contextWarnings.Add($"Item {item.Id}: nao foi possivel validar a matricula atual no Moodle ({enrollment.Error}); a publicacao foi bloqueada.");
                    continue;
                }

                if (enrollment.StudentIds?.Contains(
                        item.MoodleUserId.ToString(CultureInfo.InvariantCulture)) != true)
                {
                    MarkBlocked(item.Id, "student_not_enrolled");
                    contextWarnings.Add($"Item {item.Id}: o estudante nao esta matriculado no curso atual do Moodle; a publicacao foi bloqueada.");
                    continue;
                }

                var studentId = item.MoodleUserId.ToString(CultureInfo.InvariantCulture);
                preparedCandidate = preparedCandidate with
                {
                    StudentName = enrollment.StudentNames?.GetValueOrDefault(studentId) ?? preparedCandidate.StudentName
                };
            }

            ready.Add(preparedCandidate);
        }

        if (restoredContextIdentity)
        {
            await repository.SaveChangesAsync(cancellationToken);
        }

        var blocked = selected.Length - ready.Count;

        if (ready.Count == 0)
        {
            return new CreateGradingLaunchPreviewResult(
                Guid.Empty,
                request.BatchJobId,
                selected.Length,
                ReadyItems: 0,
                blocked,
                Launches: [],
                ConfirmationText: string.Empty,
                ExpiresAt: null,
                Warnings: scaleWarnings.Count > 0 || contextWarnings.Count > 0
            ? [.. contextWarnings, .. scaleWarnings]
                    : ["Nenhuma correcao salva e pronta para lancamento foi encontrada."],
                ExpectedItems: coverage.ExpectedItems,
                PreparedItems: coverage.PreparedItems,
                MissingItems: coverage.MissingItems,
                MissingBatchCount: coverage.MissingBatchCount,
                DecisionSafe: coverage.DecisionSafe,
                BlockedReasons: blockedReasonCounts);
        }

        var publicationId = Guid.NewGuid();
        var connectionKey = publicationConnectionKey;
        // Um feedback idêntico para dois estudantes da mesma atividade é um
        // forte sinal de reutilização de modelo. Bloqueamos todo o grupo para
        // impedir que um retorno genérico chegue ao Moodle; o professor pode
        // corrigir os rascunhos e gerar uma nova prévia.
        var repeatedFeedbackItemIds = ready
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.FeedbackText))
            .GroupBy(candidate => (
                candidate.Item.AssignmentId,
                Feedback: GradingFeedbackQuality.NormalizeForComparison(candidate.FeedbackText)))
            .Where(group => group.Key.Feedback.Length > 0 && group.Count() > 1)
            .SelectMany(group => group.Select(candidate => candidate.Item.Id))
            .ToHashSet();
        if (repeatedFeedbackItemIds.Count > 0)
        {
            foreach (var itemId in repeatedFeedbackItemIds)
            {
                MarkBlocked(itemId, "reused_feedback");
            }
            var repeatedGroups = ready
                .Where(candidate => repeatedFeedbackItemIds.Contains(candidate.Item.Id))
                .GroupBy(candidate => candidate.Item.AssignmentId)
                .Select(group => $"atividade {group.Key}: {group.Count()} item(ns)")
                .ToArray();
            contextWarnings.Add(
                $"Feedback reutilizado detectado ({string.Join(", ", repeatedGroups)}). " +
                "Os itens foram bloqueados; gere feedback individual com evidencias da submissao.");
            ready = ready.Where(candidate => !repeatedFeedbackItemIds.Contains(candidate.Item.Id)).ToList();
        }

        var proposedPayloadItems = ready.Select(ToPayloadItem).ToArray();
        var reusablePreview = await TryFindReusablePreviewAsync(
            scope,
            request,
            proposedPayloadItems,
            connectionKey,
            cancellationToken);
        if (reusablePreview is not null)
        {
            var savedPreview = DeserializePersistedPreview(reusablePreview.PreviewJson, proposedPayloadItems);
            if (IsReusablePreviewSnapshot(
                    savedPreview,
                    request,
                    selected.Select(item => item.Id).ToArray(),
                    proposedPayloadItems.Length,
                    coverage))
            {
                return BuildReusedPreviewResult(reusablePreview, request.BatchJobId, savedPreview!);
            }
        }

        var claimResults = await repository.TryClaimPublicationTargetsAsync(
            publicationId,
            connectionKey,
            ready.Select(candidate => new GradingPublicationClaimRequest(
                candidate.Item.Id,
                candidate.Item.AssignmentId,
                candidate.Item.MoodleUserId,
                candidate.Item.AttemptNumber ?? 0)).ToArray(),
            DateTimeOffset.UtcNow.Add(publicationReviewExpiration),
            cancellationToken);
        var busyItemIds = claimResults
            .Where(result => !result.Claimed)
            .Select(result => result.GradingItemId)
            .ToHashSet();
        if (busyItemIds.Count > 0)
        {
            foreach (var itemId in busyItemIds)
            {
                MarkBlocked(itemId, "active_publication_exists");
            }
            contextWarnings.Add($"{busyItemIds.Count} item(ns) ja possuem outra publicacao ativa para a mesma entrega/tentativa; foram bloqueado(s) nesta previa.");
            ready = ready.Where(candidate => !busyItemIds.Contains(candidate.Item.Id)).ToList();
        }

        blocked = selected.Length - ready.Count;

        if (ready.Count == 0)
        {
            if (busyItemIds.Count > 0)
            {
                var concurrentPreview = await TryFindReusablePreviewAsync(
                    scope,
                    request,
                    proposedPayloadItems,
                    connectionKey,
                    cancellationToken);
                if (concurrentPreview is not null)
                {
                    var savedPreview = DeserializePersistedPreview(concurrentPreview.PreviewJson, proposedPayloadItems);
                    if (IsReusablePreviewSnapshot(
                            savedPreview,
                            request,
                            selected.Select(item => item.Id).ToArray(),
                            proposedPayloadItems.Length,
                            coverage))
                    {
                        await repository.ReleasePublicationClaimsAsync(publicationId, CancellationToken.None);
                        return BuildReusedPreviewResult(concurrentPreview, request.BatchJobId, savedPreview!);
                    }
                }
            }

            return new CreateGradingLaunchPreviewResult(
                Guid.Empty,
                request.BatchJobId,
                selected.Length,
                ReadyItems: 0,
                BlockedItems: blocked,
                Launches: [],
                ConfirmationText: string.Empty,
                ExpiresAt: null,
                Warnings: BuildWarnings(
                    blocked,
                    scaleWarnings,
                    contextWarnings,
                    selected,
                    blockedReasonCounts),
                ExpectedItems: coverage.ExpectedItems,
                PreparedItems: coverage.PreparedItems,
                MissingItems: coverage.MissingItems,
                MissingBatchCount: coverage.MissingBatchCount,
                DecisionSafe: coverage.DecisionSafe,
                BlockedReasons: blockedReasonCounts);
        }

        if (destinationRun is not null)
        {
            // Persist the destination choice atomically so concurrent
            // requests cannot turn one run into both CSV and publication.
            if (!await repository.TrySetGradingRunDestinationAsync(
                    destinationRun.Id,
                    "publish",
                    cancellationToken))
            {
                await repository.ReleasePublicationClaimsAsync(publicationId, CancellationToken.None);
                throw new MoodleApiException(
                    MoodleErrorContract.DestinationLocked,
                    "Esta execucao foi direcionada para CSV por outra solicitacao; gere um novo gradingRunId para publicar no Moodle.");
            }
        }

        var payload = new GradingLaunchPayload(
            request.BatchJobId,
            ready.Select(ToPayloadItem).ToArray(),
            request.AllowOverwriteExisting,
            publicationId,
            connectionKey,
            GradingLaunchPayload.CurrentContractVersion);
        var previewItems = ready.Select(ToPreviewItem).ToArray();
        // Publicacao de notas usa a aprovacao da previa exibida pelo cliente;
        // o campo legado permanece vazio para compatibilidade de schema.
        var confirmationText = string.Empty;
        var warnings = BuildWarnings(blocked, scaleWarnings, contextWarnings, selected, blockedReasonCounts).ToList();
        if (request.AllowOverwriteExisting)
        {
            warnings.Add("Esta previa autoriza sobrescrever notas ou feedbacks que ja existam no Moodle.");
        }
        if (ready.Count > 0)
        {
            warnings.Add(
                "A API de publicação do Moodle pode enviar notificações de nota/feedback automaticamente; esta integração não consegue desligar esse envio pela função usada.");
        }
        PendingActionResponse pending;
        try
        {
            pending = await pendingActions.CreatePendingActionAsync(
                ToolName,
                ToolRiskLevel.CriticalHumanConfirmedWrite,
                payload,
                new
                {
                    batchJobId = request.BatchJobId,
                    totalItems = selected.Length,
                    readyItems = ready.Count,
                    blockedItems = blocked,
                    launches = previewItems,
                    warnings,
                    approvalMode = "user_affirmative_after_preview",
                    expectedItems = coverage.ExpectedItems,
                    preparedItems = coverage.PreparedItems,
                    missingItems = coverage.MissingItems,
                    missingBatchCount = coverage.MissingBatchCount,
                    decisionSafe = coverage.DecisionSafe,
                    blockedReasons = blockedReasonCounts,
                    selectedItemIds = selected.Select(item => item.Id).OrderBy(id => id).ToArray(),
                    onlyReviewed = request.OnlyReviewed,
                    allowPartial = request.AllowPartial,
                    allowOverwriteExisting = request.AllowOverwriteExisting,
                    connectionKey
                },
                confirmationText,
                publicationReviewExpiration,
                batch.CourseId,
                cancellationToken);

            // Bind the target mutex to the durable action before returning the
            // preview. If confirmation later succeeds, expiry cleanup can see
            // the Authorized action and will not release these targets during
            // a crash/restart window.
            await repository.BindPublicationClaimsAsync(
                publicationId,
                pending.PendingActionId,
                cancellationToken);
        }
        catch
        {
            await repository.ReleasePublicationClaimsAsync(publicationId, CancellationToken.None);
            throw;
        }

        return new CreateGradingLaunchPreviewResult(
            pending.PendingActionId,
            request.BatchJobId,
            selected.Length,
            ready.Count,
            blocked,
            previewItems,
            pending.ConfirmationText,
            pending.ExpiresAt,
            Warnings: warnings,
            ExpectedItems: coverage.ExpectedItems,
            PreparedItems: coverage.PreparedItems,
            MissingItems: coverage.MissingItems,
            MissingBatchCount: coverage.MissingBatchCount,
            DecisionSafe: coverage.DecisionSafe,
            BlockedReasons: blockedReasonCounts,
            ApprovalMode: "user_affirmative_after_preview");
    }

    private async Task<IReadOnlyList<AssistedGradingItem>> LoadBatchItemsAsync(
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var total = await repository.CountItemsByBatchAsync(batchId, cancellationToken);
        return await GradingItemProcessor.LoadAllBatchItemsAsync(
            repository,
            batchId,
            cancellationToken,
            Math.Max(1, total));
    }

    private async Task<PendingMoodleAction?> TryFindReusablePreviewAsync(
        GradingBatchScope scope,
        CreateGradingLaunchPreviewCommand request,
        IReadOnlyList<GradingLaunchPayloadItem> proposedItems,
        string? connectionKey,
        CancellationToken cancellationToken)
    {
        if (pendingActionRepository is null || proposedItems.Count == 0)
        {
            return null;
        }

        var scopeIds = new[] { scope.Run?.Id ?? Guid.Empty }
            .Concat(scope.Batches.Select(batch => batch.Id))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var now = DateTimeOffset.UtcNow;
        var actions = await pendingActionRepository.ListGradingPublicationsByBatchIdsAsync(
            scopeIds,
            cancellationToken);
        var currentItems = await repository.GetItemsAsync(
            proposedItems.Select(item => item.GradingItemId).Distinct().ToArray(),
            cancellationToken);
        foreach (var action in actions
                     .Where(action => string.Equals(action.CreatedBySubject, currentUser.Subject, StringComparison.Ordinal))
                     .Where(action => action.Status switch
                     {
                         PendingActionStatus.PendingConfirmation => action.ExpiresAt > now,
                         PendingActionStatus.Authorized or PendingActionStatus.Confirmed or
                         PendingActionStatus.Executing or
                         PendingActionStatus.ExecutionUnknown => true,
                         PendingActionStatus.PartiallyCompleted => action.NextExecutionAttemptAt is not null,
                         _ => false
                     })
                     .OrderByDescending(action => action.CreatedAt))
        {
            GradingLaunchPayload? existing;
            try
            {
                existing = JsonSerializer.Deserialize<GradingLaunchPayload>(action.PayloadJson, PreviewJsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (existing is null ||
                existing.AllowOverwriteExisting != request.AllowOverwriteExisting ||
                !string.Equals(existing.ConnectionKey, connectionKey, StringComparison.Ordinal) ||
                !PayloadItemsMatch(existing.Items, proposedItems, currentItems))
            {
                continue;
            }

            return action;
        }

        return null;
    }

    private static bool PayloadItemsMatch(
        IReadOnlyList<GradingLaunchPayloadItem> existingItems,
        IReadOnlyList<GradingLaunchPayloadItem> proposedItems,
        IReadOnlyDictionary<Guid, AssistedGradingItem> currentItems)
    {
        var existing = existingItems.OrderBy(item => item.GradingItemId).ToArray();
        var proposed = proposedItems.OrderBy(item => item.GradingItemId).ToArray();
        if (existing.Length != proposed.Length)
        {
            return false;
        }

        for (var index = 0; index < existing.Length; index++)
        {
            if (existing[index] == proposed[index])
            {
                continue;
            }

            var existingWithCurrentHash = existing[index] with { DraftVersionHash = proposed[index].DraftVersionHash };
            if (existingWithCurrentHash != proposed[index] ||
                !currentItems.TryGetValue(existing[index].GradingItemId, out var currentItem) ||
                !string.Equals(
                    existing[index].DraftVersionHash,
                    GradingDraftVersionHash.ComputeLegacy(currentItem),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static PersistedGradingLaunchPreview? DeserializePersistedPreview(
        string previewJson,
        IReadOnlyList<GradingLaunchPayloadItem> fallbackItems)
    {
        try
        {
            using var document = JsonDocument.Parse(previewJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("expectedItems", out var expectedItems) ||
                !root.TryGetProperty("preparedItems", out var preparedItems) ||
                !root.TryGetProperty("missingItems", out var missingItems) ||
                !root.TryGetProperty("missingBatchCount", out var missingBatchCount) ||
                !root.TryGetProperty("decisionSafe", out var decisionSafe))
            {
                return null;
            }

            var launches = root.TryGetProperty("launches", out var launchElement)
                ? launchElement.Deserialize<GradingLaunchPreviewItem[]>(PreviewJsonOptions) ?? []
                : fallbackItems.Select(ToPreviewItem).ToArray();
            var warnings = root.TryGetProperty("warnings", out var warningElement)
                ? warningElement.Deserialize<string[]>(PreviewJsonOptions) ?? []
                : [];
            var blockedReasons = root.TryGetProperty("blockedReasons", out var blockedElement)
                ? blockedElement.Deserialize<Dictionary<string, int>>(PreviewJsonOptions)
                : null;
            return new PersistedGradingLaunchPreview(
                ReadPreviewInt(root, "totalItems", fallbackItems.Count),
                ReadPreviewInt(root, "readyItems", launches.Length),
                ReadPreviewInt(root, "blockedItems", 0),
                launches,
                warnings,
                expectedItems.GetInt32(),
                preparedItems.GetInt32(),
                missingItems.GetInt32(),
                missingBatchCount.GetInt32(),
                decisionSafe.GetBoolean(),
                blockedReasons,
                root.TryGetProperty("selectedItemIds", out var selectedElement)
                    ? selectedElement.Deserialize<Guid[]>(PreviewJsonOptions) ?? []
                    : [],
                root.TryGetProperty("onlyReviewed", out var onlyReviewedElement) && onlyReviewedElement.GetBoolean(),
                root.TryGetProperty("allowPartial", out var allowPartialElement) && allowPartialElement.GetBoolean(),
                HasCoverageSummary: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ReadPreviewInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static bool IsReusablePreviewSnapshot(
        PersistedGradingLaunchPreview? savedPreview,
        CreateGradingLaunchPreviewCommand request,
        IReadOnlyCollection<Guid> selectedItemIds,
        int proposedItemCount,
        GradingRunCoverage coverage) =>
        savedPreview is not null &&
        savedPreview.HasCoverageSummary &&
        savedPreview.OnlyReviewed == request.OnlyReviewed &&
        savedPreview.AllowPartial == request.AllowPartial &&
        savedPreview.SelectedItemIds.SequenceEqual(selectedItemIds.OrderBy(id => id)) &&
        savedPreview.TotalItems == selectedItemIds.Count &&
        savedPreview.ReadyItems == proposedItemCount &&
        savedPreview.ExpectedItems == coverage.ExpectedItems &&
        savedPreview.PreparedItems == coverage.PreparedItems &&
        savedPreview.MissingItems == coverage.MissingItems &&
        savedPreview.MissingBatchCount == coverage.MissingBatchCount &&
        savedPreview.DecisionSafe == coverage.DecisionSafe;

    private static CreateGradingLaunchPreviewResult BuildReusedPreviewResult(
        PendingMoodleAction action,
        Guid batchJobId,
        PersistedGradingLaunchPreview preview) => new(
            action.Id,
            batchJobId,
            preview.TotalItems,
            preview.ReadyItems,
            preview.BlockedItems,
            preview.Launches,
            action.ConfirmationText,
            action.ExpiresAt,
            preview.Warnings,
            preview.ExpectedItems,
            preview.PreparedItems,
            preview.MissingItems,
            preview.MissingBatchCount,
            preview.DecisionSafe,
            preview.BlockedReasons,
            ApprovalMode: "user_affirmative_after_preview",
            PublicationState: ResolvePreviewPublicationState(action),
            NextAction: ResolvePreviewNextAction(action));

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
        MinGrade: item.MinGrade,
        GradingMode: item.GradingMode,
        ScaleSource: item.ScaleSource,
        ScaleVersion: item.ScaleVersion,
        WorkflowMode: item.WorkflowMode,
        WorkflowState: item.WorkflowState,
        ReleaseState: item.ReleaseState,
        VisibilityState: item.VisibilityState);

    private static string ResolvePreviewPublicationState(PendingMoodleAction action) => action.Status switch
    {
        PendingActionStatus.Authorized or PendingActionStatus.Confirmed => "queued",
        PendingActionStatus.PartiallyCompleted => action.NextExecutionAttemptAt is not null ? "retry_scheduled" : "failed",
        PendingActionStatus.Executing => "executing",
        PendingActionStatus.ExecutionUnknown => "unknown",
        _ => "awaiting_approval"
    };

    private static string ResolvePreviewNextAction(PendingMoodleAction action) => ResolvePreviewPublicationState(action) switch
    {
        "awaiting_approval" => "confirm",
        "queued" or "executing" or "retry_scheduled" => "wait",
        "succeeded" => "done",
        _ => "attention"
    };

    private sealed record PersistedGradingLaunchPreview(
        int TotalItems,
        int ReadyItems,
        int BlockedItems,
        IReadOnlyList<GradingLaunchPreviewItem> Launches,
        IReadOnlyList<string> Warnings,
        int ExpectedItems,
        int PreparedItems,
        int MissingItems,
        int MissingBatchCount,
        bool DecisionSafe,
        IReadOnlyDictionary<string, int>? BlockedReasons,
        IReadOnlyList<Guid> SelectedItemIds,
        bool OnlyReviewed,
        bool AllowPartial,
        bool HasCoverageSummary);

    private async Task<MoodleAssignmentGradeScaleResolution> GetKnownGradeScaleAsync(
        AssistedGradingBatch batch,
        AssistedGradingItem item,
        IDictionary<(long CourseId, long AssignmentId), AssignmentSettingsSummary?> settingsCache,
        IDictionary<(long CourseId, long AssignmentId), MoodleAssignmentGradeScaleResolution> scaleCache,
        IReadOnlyDictionary<(long CourseId, long StudentId), CourseGradebook?> gradebooksByTarget,
        CancellationToken cancellationToken)
    {
        var settingsKey = (item.CourseId, item.AssignmentId);
        var sourceScope = ResolvePublicationConnectionKey(batch);
        if (scaleCache.TryGetValue(settingsKey, out var cachedScale))
        {
            return cachedScale;
        }

        if (!settingsCache.TryGetValue(settingsKey, out var settings))
        {
            try
            {
                settings = await settingsGateway.GetAssignmentSettingsFreshAsync(
                    batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ?? batch.CreatedBySubject,
                    item.CourseId.ToString(CultureInfo.InvariantCulture),
                    item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                    cancellationToken);
            }
            catch
            {
                settings = null;
            }

            settingsCache[settingsKey] = settings;
        }

        var settingsResolution = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
            settings,
            item.CourseId,
            item.AssignmentId,
            sourceScope);

        MoodleAssignmentGradeScaleResolution? gradebookResolution = null;
        if (gradebookGateway is not null)
        {
            try
            {
                if (gradebooksByTarget.TryGetValue((item.CourseId, item.MoodleUserId), out var gradebook) &&
                    gradebook is not null)
                {
                    gradebookResolution = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(
                        gradebook,
                        item.CourseId,
                        item.AssignmentId,
                        sourceScope);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fresh assignment settings are the primary scale source.
                // The exact gradebook item supplements missing settings and
                // can invalidate a conflicting definition, but a transient
                // failure to read it does not erase a fresh exact definition.
            }
        }

        return scaleCache[settingsKey] = MoodleAssignmentGradeScaleResolver.ReconcileAuthoritativeSources(
            settingsResolution,
            gradebookResolution,
            item.CourseId,
            item.AssignmentId,
            sourceScope);
    }

    private static async Task<(IReadOnlySet<string>? StudentIds, IReadOnlyDictionary<string, string>? StudentNames, string? Error)> ResolveCourseEnrollmentPreflightAsync(
        IMoodleParticipantsGateway gateway,
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken)
    {
        try
        {
            var studentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var studentNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pageNumber = 1;
            while (true)
            {
                var page = await GradingMoodleReadRetry.ExecuteAsync(
                    retryCancellationToken => gateway.GetCourseParticipantsAsync(
                        userExternalId,
                        courseId,
                        ParticipantStatusFilter.All,
                        pageNumber,
                        pageSize: 500,
                        studentsOnly: false,
                        includeEmail: false,
                        groupId: null,
                        retryCancellationToken),
                    onRetry: null,
                    cancellationToken);
                foreach (var participant in page.Participants)
                {
                    if (!string.IsNullOrWhiteSpace(participant.UserId))
                    {
                        studentIds.Add(participant.UserId);
                        if (!string.IsNullOrWhiteSpace(participant.FullName))
                        {
                            studentNames[participant.UserId] = participant.FullName.Trim();
                        }
                    }
                }

                if (!page.HasMore)
                {
                    return (studentIds, studentNames, null);
                }

                pageNumber++;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, null, exception.GetType().Name);
        }
    }

    private static GradingLaunchCandidate? ToLaunchCandidate(AssistedGradingItem item, bool onlyReviewed)
    {
        if (!GradingLaunchEligibility.IsCandidate(item, onlyReviewed))
        {
            return null;
        }

        if (item.ReviewStatus == GradingReviewStatus.Reviewed)
        {
            return new GradingLaunchCandidate(item, item.FinalGrade, item.FinalFeedback!);
        }

        return new GradingLaunchCandidate(item, item.SuggestedGrade, item.DraftFeedback!);
    }

    private static IReadOnlyList<string> BuildWarnings(
        int blocked,
        IReadOnlyList<string> scaleWarnings,
        IReadOnlyList<string> contextWarnings,
        IReadOnlyList<AssistedGradingItem>? selectedItems = null,
        IReadOnlyDictionary<string, int>? blockedReasons = null)
    {
        var warnings = new List<string>(contextWarnings.Count + scaleWarnings.Count);
        warnings.AddRange(contextWarnings);
        warnings.AddRange(scaleWarnings);
        var classifiedBlocked = blockedReasons?.Values.Sum() ?? 0;
        var unclassifiedBlocked = blocked - classifiedBlocked;
        if (unclassifiedBlocked > 0)
        {
            warnings.Add($"{unclassifiedBlocked} item(ns) bloqueado(s) sem motivo detalhado disponivel.");
        }

        if (blockedReasons is { Count: > 0 })
        {
            warnings.Add(
                "Motivos dos bloqueios: " +
                string.Join(", ", blockedReasons
                    .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => $"{entry.Key}={entry.Value}")) + ".");
        }

        var recoverablePublicationFailures = selectedItems?.Count(item =>
            item.ReviewStatus == GradingReviewStatus.Reviewed &&
            item.CommitStatus == GradingCommitStatus.Failed &&
            !string.IsNullOrWhiteSpace(item.FinalFeedback)) ?? 0;
        if (recoverablePublicationFailures > 0)
        {
            warnings.Add(
                $"{recoverablePublicationFailures} item(ns) possuem uma falha de publicacao recuperavel; use requeue_failed_grading_publication_items com os gradingItemIds antes de gerar nova previa.");
        }

        return warnings;
    }

    private static string ClassifyLaunchBlockReason(
        AssistedGradingItem item,
        bool onlyReviewed)
    {
        if (item.Status == GradingItemStatus.Committed ||
            item.CommitStatus == GradingCommitStatus.Succeeded)
        {
            return "already_committed";
        }

        if (onlyReviewed && item.ReviewStatus != GradingReviewStatus.Reviewed)
        {
            return "not_reviewed";
        }

        if (item.Status == GradingItemStatus.ReadyToCommit &&
            item.CommitStatus == GradingCommitStatus.Pending &&
            string.IsNullOrWhiteSpace(item.FinalFeedback))
        {
            return "missing_feedback";
        }

        if (!onlyReviewed &&
            item.Status == GradingItemStatus.DraftReady &&
            item.CommitStatus == GradingCommitStatus.NotReady &&
            string.IsNullOrWhiteSpace(item.DraftFeedback))
        {
            return "missing_feedback";
        }

        return "invalid_item_state";
    }

    private static string FormatGrade(decimal grade)
    {
        return grade.ToString("0.####", CultureInfo.InvariantCulture);
    }


    private static GradingLaunchPayloadItem ToPayloadItem(GradingLaunchCandidate candidate)
    {
        var item = candidate.Item;
        return new GradingLaunchPayloadItem(
            item.Id,
            item.CourseId.ToString(CultureInfo.InvariantCulture),
            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
            candidate.Grade,
            candidate.FeedbackText,
            item.AttemptNumber,
            GradingDraftVersionHash.Compute(item),
            item.ContextHash,
            item.SubmissionContentHash,
            candidate.PreflightHash,
            candidate.StudentName,
            candidate.AssignmentName,
            candidate.MaxGrade,
            candidate.MinGrade,
            candidate.GradingMode,
            candidate.ScaleSource,
            candidate.ScaleVersion,
            ResolveGradeOperation(candidate.Grade, candidate.GradingMode),
            candidate.ExistingGrade?.HasGrade == true,
            candidate.ExistingGrade?.Grade,
            candidate.WorkflowMode,
            candidate.WorkflowState,
            candidate.ReleaseState,
            candidate.VisibilityState);
    }

    private static string ResolveGradeOperation(decimal? grade, string? gradingMode)
    {
        if (grade is not null)
        {
            return "set_grade";
        }

        if (string.Equals(gradingMode, "feedback_only", StringComparison.OrdinalIgnoreCase))
        {
            return "no_grade";
        }

        return string.Equals(gradingMode, "numeric", StringComparison.OrdinalIgnoreCase)
            ? "preserve_grade"
            : "unsupported";
    }

    private static GradingLaunchPreviewItem ToPreviewItem(GradingLaunchCandidate candidate)
    {
        var item = candidate.Item;
        return new GradingLaunchPreviewItem(
            item.Id,
            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
            candidate.Grade,
            candidate.FeedbackText,
            item.ContextHash,
            item.SubmissionContentHash,
            candidate.StudentName ?? item.StudentDisplayName,
            candidate.Item.ReviewStatus == GradingReviewStatus.Reviewed
                ? "revisada"
                : "rascunho_aguardando_confirmacao",
            candidate.PreflightHash,
            candidate.ExistingGrade?.Grade,
            !string.IsNullOrWhiteSpace(candidate.ExistingGrade?.Feedback),
            candidate.AssignmentName,
            candidate.MaxGrade,
            candidate.Item.ReviewStatus.ToString(),
            candidate.MinGrade,
            candidate.GradingMode,
            candidate.ScaleSource,
            candidate.ScaleVersion,
            candidate.WorkflowMode,
            candidate.WorkflowState,
            candidate.ReleaseState,
            candidate.VisibilityState);
    }

    private static string ResolvePublicationConnectionKey(AssistedGradingBatch batch) =>
        !string.IsNullOrWhiteSpace(batch.MoodleConnectionId)
            ? $"connection:{batch.MoodleConnectionId.Trim()}"
            : $"client:{(string.IsNullOrWhiteSpace(batch.ConnectorClientId) ? "default" : batch.ConnectorClientId)}" +
              $":alias:{(string.IsNullOrWhiteSpace(batch.ConnectionAlias) ? "default" : batch.ConnectionAlias)}";

    private sealed record GradingLaunchCandidate(
        AssistedGradingItem Item,
        decimal? Grade,
        string FeedbackText,
        AssignmentExistingGrade? ExistingGrade = null,
        string? PreflightHash = null,
        string? StudentName = null,
        string? AssignmentName = null,
        decimal? MaxGrade = null,
        decimal? MinGrade = null,
        string? GradingMode = null,
        string? ScaleSource = null,
        string? ScaleVersion = null,
        string? WorkflowMode = null,
        string? WorkflowState = null,
        string? ReleaseState = null,
        string? VisibilityState = null);

    private static string ComputePreflightHash(
        AssistedGradingBatch batch,
        AssistedGradingItem item,
        AssignmentExistingGrade? existingGrade) =>
        GradingPublicationGradebookProjection.ComputePreflightHash(
            ResolvePublicationConnectionKey(batch),
            ToPayloadItem(new GradingLaunchCandidate(item, item.FinalGrade, item.FinalFeedback ?? string.Empty)),
            existingGrade);

    private static bool HasVersionedContextIdentity(AssistedGradingItem item) =>
        item.ContextVersion is > 0 &&
        !string.IsNullOrWhiteSpace(item.ContextHash) &&
        !string.IsNullOrWhiteSpace(item.ContextStatus) &&
        !string.Equals(item.ContextStatus, "legacy_unversioned", StringComparison.OrdinalIgnoreCase);

    private static bool HasSealedSubmissionForBlockedContext(AssistedGradingItem item) =>
        !string.Equals(item.ContextStatus, "blocked", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(item.SubmissionContentHash);

    private static bool NeedsContextIdentityRestore(AssistedGradingItem item) =>
        item.ContextVersion is null or <= 0 ||
        string.IsNullOrWhiteSpace(item.ContextHash) ||
        string.IsNullOrWhiteSpace(item.ContextStatus) ||
        string.Equals(item.ContextStatus, "legacy_unversioned", StringComparison.OrdinalIgnoreCase);
}

public sealed class ConfirmMoodleBatchLaunchCommandHandler(
    IPendingMoodleActionRepository pendingActions,
    IGradingReviewRepository repository,
    IActionConfirmationService confirmations,
    IMoodleGradingCapabilitiesGateway capabilities,
    IMoodleAssignmentGradeReadGateway gradeReadGateway,
    IMoodleAssignmentSubmissionStatusGateway submissionStatusGateway,
    IMoodleParticipantsGateway participantsGateway,
    IMoodleAuditLogRepository auditLogs,
    IMediator mediator,
    ISubmissionContentHashResolver? submissionContentHashResolver = null,
    IOptions<MoodleUniversalApiFeatureOptions>? resourceFeatures = null,
    IMoodleCredentialsProvider? credentialsProvider = null,
    IMoodleAssignmentSubmissionsGateway? submissionsGateway = null,
    IOptions<GradingLimitsOptions>? gradingLimits = null,
    IGradingOperationTelemetry? telemetry = null,
    IMoodleAssignmentSettingsGateway? assignmentSettingsGateway = null,
    IMoodleGradebookGateway? gradebookGateway = null,
    ICurrentUserContext? currentUser = null,
    IConnectorExecutionContext? executionContext = null,
    IMoodleConnectionSelection? connectionSelection = null,
    IMoodleBackgroundConnectionContextRestorer? backgroundConnectionContextRestorer = null)
    : IRequestHandler<ConfirmMoodleBatchLaunchCommand, ConfirmMoodleBatchLaunchResult>
{
    private const string CommitToolName = "confirmar_lancamento_lote_moodle";
    private const string IndividualGradeWriteFunction = "mod_assign_save_grade";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex HtmlTagRegex = new("<[^>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly TimeSpan ExecutionLeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ExecutionLeaseRenewalInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PublicationClaimRefreshDuration = TimeSpan.FromMinutes(15);
    private const int DurableCheckpointInterval = 25;

    public async Task<ConfirmMoodleBatchLaunchResult> Handle(
        ConfirmMoodleBatchLaunchCommand request,
        CancellationToken cancellationToken)
    {
        var action = await pendingActions.GetByIdAsync(request.PendingActionId, cancellationToken)
            ?? throw new MoodleApiException(MoodleErrorContract.PendingActionNotFound, "Acao pendente nao encontrada.");
        GradingLaunchPayload? payload = null;
        if (RequiresPreviewConnectionValidation(action.Status))
        {
            payload = JsonSerializer.Deserialize<GradingLaunchPayload>(action.PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException("Payload de lancamento invalido.");

            if (action.Status == PendingActionStatus.PendingConfirmation &&
                payload.ContractVersion != GradingLaunchPayload.CurrentContractVersion)
            {
                throw new InvalidOperationException(
                    "Esta previa usa um contrato legado que não define a política de workflow. Gere uma nova previa antes de aprovar a publicação.");
            }

            // Bind the authorization decision to the same Moodle connection
            // that produced the preview. This check must happen before the
            // durable PendingConfirmation -> Authorized transition; otherwise
            // a failed connection check could leave an action that the
            // recovery worker can still publish later.
            await RestorePreviewConnectionContextAsync(
                payload,
                repository,
                currentUser,
                executionContext,
                connectionSelection,
                backgroundConnectionContextRestorer,
                cancellationToken);
            await EnsurePreviewConnectionAsync(payload, credentialsProvider, cancellationToken);

            if (action.Status == PendingActionStatus.PendingConfirmation)
            {
                var scaleMismatches = await FindGradeScaleMismatchesAsync(
                    payload,
                    action,
                    assignmentSettingsGateway,
                    gradebookGateway,
                    cancellationToken);
                if (scaleMismatches.Count > 0)
                {
                    return await BlockChangedGradeScaleAsync(
                        action,
                        payload,
                        scaleMismatches,
                        cancellationToken);
                }
            }
        }

        var confirmation = await confirmations.ConfirmAsync(
            request.PendingActionId,
            request.ConfirmationText,
            requiredScope: "moodle.write.assignments.grade",
            cancellationToken);
        if (confirmation.Status == "already_confirmed")
        {
            payload ??= JsonSerializer.Deserialize<GradingLaunchPayload>(action.PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException("Payload de lancamento invalido.");
            return new ConfirmMoodleBatchLaunchResult(
                "already_confirmed",
                request.PendingActionId,
                SentItems: 0,
                FailedItems: 0,
                Failures: [],
                confirmation.AuditId,
                BatchJobId: payload.BatchJobId);
        }

        payload ??= JsonSerializer.Deserialize<GradingLaunchPayload>(action.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Payload de lancamento invalido.");

        var durableExecution = confirmation.Status == "authorized";
        var securityWarningsOnly = resourceFeatures?.Value.McpGradingSecurityWarningsOnly == true;
        var warnings = new List<string>();
        if (durableExecution && !request.ExecuteImmediately)
        {
            // Keep the confirmation request short even for a 10k-item
            // publication. The action is already Authorized durably and the
            // publication worker will claim it on the next poll (or recover
            // it after a process restart). No Moodle write happens here.
            return new ConfirmMoodleBatchLaunchResult(
                action.Status == PendingActionStatus.PartiallyCompleted && action.NextExecutionAttemptAt is not null
                    ? "retry_scheduled"
                    : "authorized",
                request.PendingActionId,
                SentItems: 0,
                FailedItems: 0,
                Failures: [],
                confirmation.AuditId,
                BatchJobId: payload.BatchJobId,
                Items: action.Status == PendingActionStatus.PartiallyCompleted
                    ? null
                    : BuildPublicationItems(payload, forcedStatus: "authorized"),
                NextAttemptAt: action.NextExecutionAttemptAt);
        }
        GradingRun? gradingRun = null;
        var publicationClaimConflicts = new HashSet<Guid>();
        var executionOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        long executionFencingToken = 0;
        if (durableExecution)
        {
            // Reassert the target claims before taking the worker lease. This
            // is a no-op for the original preview (the same publication owns
            // those rows), and it safely reacquires only free targets when a
            // PartiallyCompleted action is retried after its claims were
            // released. A target stolen by another publication is reported as
            // a per-item conflict and is never written without a claim.
            if (payload.PublicationId is Guid publicationId)
            {
                var claimResults = await repository.TryClaimPublicationTargetsAsync(
                    publicationId,
                    payload.ConnectionKey ?? "default",
                    payload.Items.Select(item => new GradingPublicationClaimRequest(
                        item.GradingItemId,
                        long.TryParse(item.AssignmentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var assignmentId)
                            ? assignmentId
                            : 0,
                        long.TryParse(item.StudentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moodleUserId)
                            ? moodleUserId
                            : 0,
                        item.AttemptNumber ?? 0)).ToArray(),
                    DateTimeOffset.UtcNow.Add(PublicationClaimRefreshDuration),
                    cancellationToken);
                publicationClaimConflicts.UnionWith(
                    claimResults
                        .Where(result => !result.Claimed)
                        .Select(result => result.GradingItemId));
            }

            var executionClaim = await pendingActions.TryBeginExecutionAsync(
                request.PendingActionId,
                executionOwner,
                DateTimeOffset.UtcNow,
                ExecutionLeaseDuration,
                cancellationToken);
            var queueStartedAt = action.ConfirmedAt ?? action.CreatedAt;
            var queueDelayMs = Math.Max(0d, (DateTimeOffset.UtcNow - queueStartedAt).TotalMilliseconds);
            telemetry?.RecordPhase(
                "grading_publication",
                "queue",
                executionClaim.Claimed ? "started" : executionClaim.Status switch
                {
                    PendingActionStatus.ExecutionUnknown => "execution_unknown",
                    PendingActionStatus.Executing => "already_executing",
                    PendingActionStatus.PartiallyCompleted => "partial",
                    PendingActionStatus.Executed => "succeeded",
                    PendingActionStatus.Failed => "failed",
                    _ => "not_claimed"
                },
                queueDelayMs,
                itemCount: payload.Items.Count);
            if (!executionClaim.Claimed)
            {
                if (executionClaim.Status is PendingActionStatus.Executed or PendingActionStatus.Failed)
                {
                    if (payload.PublicationId is Guid completedPublicationId)
                    {
                        await repository.ReleasePublicationClaimsAsync(completedPublicationId, cancellationToken);
                    }

                    return new ConfirmMoodleBatchLaunchResult(
                        executionClaim.Status == PendingActionStatus.Executed ? "already_executed" : "already_failed",
                        request.PendingActionId,
                        SentItems: 0,
                        FailedItems: 0,
                        Failures: [],
                        confirmation.AuditId,
                        BatchJobId: payload.BatchJobId);
                }

                if (executionClaim.Status == PendingActionStatus.PartiallyCompleted &&
                    action.NextExecutionAttemptAt is { } nextAttemptAt && nextAttemptAt > DateTimeOffset.UtcNow)
                {
                    return new ConfirmMoodleBatchLaunchResult(
                        "retry_scheduled",
                        request.PendingActionId,
                        SentItems: 0,
                        FailedItems: 0,
                        Failures: [],
                        confirmation.AuditId,
                        BatchJobId: payload.BatchJobId,
                        NextAttemptAt: nextAttemptAt);
                }

                return new ConfirmMoodleBatchLaunchResult(
                    executionClaim.Status == PendingActionStatus.ExecutionUnknown
                        ? "execution_unknown"
                        : "executing",
                    request.PendingActionId,
                    SentItems: 0,
                    FailedItems: 0,
                    Failures: [],
                    confirmation.AuditId,
                    BatchJobId: payload.BatchJobId);
            }

            executionFencingToken = executionClaim.FencingToken;
            if (executionFencingToken <= 0)
            {
                throw new InvalidOperationException("O worker nao recebeu um fencing token valido para a publicacao.");
            }

            if (payload.PublicationId is Guid claimedPublicationId)
            {
                await repository.ActivatePublicationClaimsAsync(
                    claimedPublicationId,
                    request.PendingActionId,
                    executionOwner,
                    executionFencingToken,
                    cancellationToken);
            }

            var scaleMismatches = await FindGradeScaleMismatchesAsync(
                payload,
                action,
                assignmentSettingsGateway,
                gradebookGateway,
                cancellationToken);
            if (scaleMismatches.Count > 0)
            {
                return await BlockChangedGradeScaleAsync(
                    action,
                    payload,
                    scaleMismatches,
                    cancellationToken);
            }

            gradingRun = await ResolvePublicationRunAsync(payload.BatchJobId, cancellationToken);
            var directBatchForCancellation = await repository.GetBatchAsync(payload.BatchJobId, cancellationToken);
            if (gradingRun?.Status == GradingRunStatus.Cancelled ||
                directBatchForCancellation?.Status == GradingBatchStatus.Cancelled)
            {
                action.MarkCancelled("A execucao de correcao foi cancelada antes da publicacao.");
                await pendingActions.SaveChangesAsync(cancellationToken);
                if (payload.PublicationId is Guid cancelledPublicationId)
                {
                    await repository.ReleasePublicationClaimsAsync(cancelledPublicationId, cancellationToken);
                }

                return new ConfirmMoodleBatchLaunchResult(
                    "cancelled",
                    request.PendingActionId,
                    SentItems: 0,
                    FailedItems: 0,
                    Failures: [],
                    confirmation.AuditId,
                    BatchJobId: payload.BatchJobId);
            }

            if (gradingRun is not null)
            {
                gradingRun.MarkPublishing();
                await repository.SaveChangesAsync(cancellationToken);
            }

            // The repository refreshes its tracked instance after the atomic
            // claim. Reloading here also makes InMemory and PostgreSQL paths
            // observe the same durable execution state.
            action = await pendingActions.GetByIdAsync(request.PendingActionId, cancellationToken)
                ?? throw new MoodleApiException(MoodleErrorContract.PendingActionNotFound, "Acao pendente desapareceu durante a execucao.");
        }
        var sent = 0;
        var executionUnknown = false;
        var executionLeaseLost = false;
        var failures = new List<GradingLaunchFailure>();
        var userExternalId = action.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ??
            action.CreatedBySubject;
        var itemsById = await repository.GetItemsAsync(
            payload.Items.Select(item => item.GradingItemId).Distinct().ToArray(),
            cancellationToken);
        // Confirmation approves the complete preview, not only the items the
        // sequential writer reaches before a transport failure. Persist every
        // valid teacher decision before remote preflight/write work so later
        // items remain reviewable and recoverable if one write becomes unknown.
        var teacherReviewFailures = new Dictionary<Guid, string>();
        var duplicateTargetItemIds = payload.Items
            .GroupBy(item => (item.CourseId, item.AssignmentId, item.StudentId))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(item => item.GradingItemId))
            .ToHashSet();
        foreach (var payloadItem in payload.Items)
        {
            if (!itemsById.TryGetValue(payloadItem.GradingItemId, out var item) ||
                publicationClaimConflicts.Contains(payloadItem.GradingItemId) ||
                item.ReviewStatus == GradingReviewStatus.Reviewed ||
                item.CommitStatus is GradingCommitStatus.Succeeded or GradingCommitStatus.ExecutionUnknown ||
                item.CommitWriteIntentAt is not null)
            {
                continue;
            }

            try
            {
                item.ApplyTeacherReview(
                    payloadItem.Grade,
                    payloadItem.FeedbackText,
                    action.CreatedBySubject,
                    action.CreatedByMoodleUserId,
                    teacherDecision: "confirmed_for_publication");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                item.MarkCommitFailed(exception.Message, "grading_review_invalid");
                teacherReviewFailures[payloadItem.GradingItemId] = exception.Message;
            }
        }
        await repository.SaveChangesAsync(cancellationToken);

        var preflightStartedAt = Stopwatch.GetTimestamp();
        var batchWriteFeatureEnabled = durableExecution && gradingLimits?.Value.EnableBatchGradeWrite == true;
        var writeCapabilities = await GetGradeWriteCapabilitySelectionAsync(
            userExternalId,
            preferBatch: batchWriteFeatureEnabled,
            cancellationToken);
        var capabilityFailure = writeCapabilities.Failure;
        var useBatchGradeWrite = batchWriteFeatureEnabled && writeCapabilities.UseBatch;
        var individualGradeWriteAvailable = writeCapabilities.IndividualAvailable;
        if (capabilityFailure is not null)
        {
            if (!securityWarningsOnly)
            {
                telemetry?.RecordPhase(
                    "grading_publication",
                    "preflight",
                    "blocked",
                    Stopwatch.GetElapsedTime(preflightStartedAt).TotalMilliseconds,
                    itemCount: payload.Items.Count);
            }

            var capabilityWarning =
                $"Aviso tecnico de seguranca: nao foi possivel confirmar previamente a capacidade de escrita ({capabilityFailure.ErrorCode}). " +
                "A tentativa real de correcao continuara e somente uma falha efetiva sera registrada como falha.";
            if (!securityWarningsOnly)
            {
                await MarkPendingItemsFailedAsync(
                    action,
                    payload.BatchJobId,
                    payload.Items,
                    capabilityFailure.Message,
                    "commit_blocked",
                    capabilityFailure.ErrorCode,
                    failures,
                    cancellationToken);

                // A falha de catálogo/capability não deve tornar a decisão
                // revisada irrecuperável. A tentativa é encerrada como
                // parcial e pode ser reaberta quando o Moodle estiver
                // saudável novamente.
                action.MarkPartiallyCompleted(capabilityFailure.Message);
                await pendingActions.SaveChangesAsync(cancellationToken);
                if (payload.PublicationId is Guid publicationId)
                {
                    await repository.ReleasePublicationClaimsAsync(publicationId, cancellationToken);
                }
                if (gradingRun is not null)
                {
                    gradingRun.MarkPartiallyCompleted();
                    await repository.SaveChangesAsync(cancellationToken);
                }

                return new ConfirmMoodleBatchLaunchResult(
                    durableExecution ? "partial_failure" : confirmation.Status,
                    request.PendingActionId,
                    SentItems: 0,
                    failures.Count,
                    failures,
                    confirmation.AuditId,
                    [capabilityWarning]);
            }

            warnings.Add(capabilityWarning);
        }

        // Mutable Moodle state is refreshed in bounded blocks immediately
        // before those items are considered for writing.
        var existingGradesByTarget = new Dictionary<(string AssignmentId, string StudentId), AssignmentExistingGrade?>();
        var existingGradeFailuresByAssignment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Enrollment is shared by course within each block. It is cleared at
        // the next block so membership is not treated as immutable for a long
        // publication.
        var courseEnrollmentCache = new Dictionary<string, (IReadOnlySet<string>? StudentIds, string? Error)>(StringComparer.OrdinalIgnoreCase);
        var submissionStatusCache = new Dictionary<(string AssignmentId, string StudentId), SubmissionAttemptValidationResult>();
        var cachedSubmissionStatuses = new Dictionary<(string AssignmentId, string StudentId), AssignmentSubmissionAttemptStatus>();
        var cachedSubmissions = new Dictionary<(string AssignmentId, string StudentId), AssignmentSubmissionRecord>();
        var cachedFailedSubmissionAssignments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var submissionReadAtByAssignment = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var bulkSubmissionStatuses = new GradingBulkSubmissionStatusReadResult(
            cachedSubmissionStatuses,
            cachedSubmissions,
            cachedFailedSubmissionAssignments,
            Attempted: false);
        var workflowSettingsByTarget = new Dictionary<(string CourseId, string AssignmentId), AssignmentSettingsSummary?>();
        var workflowSettingsReadAtByTarget = new Dictionary<(string CourseId, string AssignmentId), DateTimeOffset>();
        var currentGradebooksByTarget = new Dictionary<(string CourseId, string StudentId), CourseGradebook?>();
        var gradebookReadAtByTarget = new Dictionary<(string CourseId, string StudentId), DateTimeOffset>();
        var gradebookReadFailuresByTarget = new Dictionary<(string CourseId, string StudentId), string>();
        var gradeReadAtByTarget = new Dictionary<(string AssignmentId, string StudentId), DateTimeOffset>();
        var submissionReadAtByTarget = new Dictionary<(string AssignmentId, string StudentId), DateTimeOffset>();
        var preflightBatchSize = Math.Clamp(gradingLimits?.Value.PublicationPreflightBatchSize ?? 5, 1, 50);
        var publicationWriteBatchSize = Math.Clamp(gradingLimits?.Value.PublicationWriteBatchSize ?? 5, 1, 5);
        var preparedBatchWrites = new List<PreparedBatchGradeWrite>();
        var preflightMaxAge = TimeSpan.FromSeconds(
            Math.Clamp(gradingLimits?.Value.PublicationPreflightMaxAgeSeconds ?? 120, 15, 1800));
        var itemsSinceCheckpoint = 0;
        var lastLeaseRenewalAt = DateTimeOffset.UtcNow;

        async Task FlushPreparedBatchWritesAsync()
        {
            if (preparedBatchWrites.Count == 0 || executionLeaseLost)
            {
                return;
            }

            foreach (var assignmentGroup in preparedBatchWrites
                         .GroupBy(candidate => (candidate.PayloadItem.CourseId, candidate.PayloadItem.AssignmentId))
                         .ToArray())
            {
                var ordered = assignmentGroup.ToArray();
                foreach (var chunk in ordered.Chunk(publicationWriteBatchSize))
                {
                    var dispatchGroupId = Guid.NewGuid();
                    var leaseRenewalAt = DateTimeOffset.UtcNow;
                    if (!await pendingActions.TryRenewExecutionLeaseAsync(
                            request.PendingActionId,
                            executionOwner,
                            executionFencingToken,
                            leaseRenewalAt,
                            ExecutionLeaseDuration,
                            cancellationToken))
                    {
                        executionLeaseLost = true;
                        preparedBatchWrites.Clear();
                        return;
                    }
                    lastLeaseRenewalAt = leaseRenewalAt;

                    foreach (var candidate in chunk)
                    {
                        if (!candidate.Item.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow))
                        {
                            throw new InvalidOperationException("Nao foi possivel preparar todas as intencoes da escrita coletiva.");
                        }
                    }

                    var attempts = chunk.Select(candidate => new GradingPublicationAttemptRecord(
                        PendingActionId: action.Id,
                        PublicationId: payload.PublicationId ?? action.Id,
                        GradingItemId: candidate.Item.Id,
                        SubmissionAttemptNumber: candidate.PayloadItem.AttemptNumber,
                        DispatchAttemptNumber: Math.Max(1, candidate.Item.CommitRetryCount + 1),
                        EventType: "write_intent_persisted",
                        ItemState: candidate.Item.CommitStatus.ToString(),
                        WorkerId: executionOwner,
                        FencingToken: executionFencingToken,
                        ErrorCode: null,
                        EvidenceKind: chunk.Length > 1 ? "local_batch_intent" : "local_intent",
                        RemoteObservedAtUtc: null,
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        DispatchGroupId: dispatchGroupId)).ToArray();

                    if (chunk.Length > 1)
                    {
                        await repository.SavePublicationBatchIntentCheckpointsAsync(attempts, cancellationToken);
                    }
                    else
                    {
                        await repository.SavePublicationCheckpointAsync("Executing", attempts[0], cancellationToken);
                    }

                    foreach (var candidate in chunk)
                    {
                        await RecordCommitAuditAsync(
                            action,
                            payload.BatchJobId,
                            candidate.PayloadItem,
                            "commit_write_intent",
                            responseSummary: new
                            {
                                candidate.Item.CommitWriteIntentAt,
                                candidate.Item.CommitRetryCount,
                                candidate.Item.CommitStatus,
                                dispatchGroupId,
                                groupedItems = chunk.Length
                            },
                            errorCode: null,
                            errorMessage: null,
                            cancellationToken);
                    }

                    var writeStartedAt = Stopwatch.GetTimestamp();
                    object? writeResult = null;
                    Exception? writeFailure = null;
                    try
                    {
                        if (chunk.Length == 1 && individualGradeWriteAvailable)
                        {
                            var candidate = chunk[0];
                            writeResult = await mediator.Send(
                                new SaveAssignmentGradeCommand(
                                    userExternalId,
                                    candidate.PayloadItem.AssignmentId,
                                    candidate.PayloadItem.StudentId,
                                    candidate.GradeToWrite,
                                    candidate.PayloadItem.FeedbackText,
                                    candidate.PayloadItem.AttemptNumber ?? -1,
                                    AddAttempt: false,
                                    ApplyToAll: false,
                                    WorkflowState: string.Empty,
                                    CourseId: candidate.PayloadItem.CourseId,
                                    PreserveGrade: candidate.PreserveGrade),
                                cancellationToken);
                        }
                        else
                        {
                            var firstCandidate = chunk[0];
                            writeResult = await mediator.Send(
                                new SaveAssignmentGradesBatchCommand(
                                    userExternalId,
                                    firstCandidate.PayloadItem.AssignmentId,
                                    firstCandidate.PayloadItem.CourseId,
                                    chunk.Select(candidate => new AssignmentGradeBatchWriteItem(
                                        candidate.PayloadItem.StudentId,
                                        candidate.GradeToWrite,
                                        candidate.PayloadItem.FeedbackText,
                                        candidate.PayloadItem.AttemptNumber ?? -1,
                                        AddAttempt: false,
                                        ApplyToAll: false,
                                        WorkflowState: string.Empty,
                                        PreserveGrade: candidate.PreserveGrade)).ToArray()),
                                cancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        writeFailure = exception;
                    }

                    telemetry?.RecordPhase(
                        "grading_publication",
                        "write",
                        writeFailure is null ? "success" : GetWriteTelemetryResult(writeFailure),
                        Stopwatch.GetElapsedTime(writeStartedAt).TotalMilliseconds,
                        itemCount: chunk.Length);

                    foreach (var candidate in chunk)
                    {
                        AssignmentExistingGrade? readBack = null;
                        Exception? readBackFailure = null;
                        try
                        {
                            readBack = await ReadCurrentPublicationGradeAsync(
                                userExternalId,
                                candidate.PayloadItem,
                                cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            readBackFailure = exception;
                        }

                        var remoteObservedAt = DateTimeOffset.UtcNow;
                        if (PublishedValuesMatch(candidate.PayloadItem, readBack))
                        {
                            candidate.Item.MarkCommitSucceeded();
                            await SavePublicationAttemptCheckpointAsync(
                                action,
                                payload,
                                candidate.PayloadItem,
                                candidate.Item,
                                claimStatus: "Succeeded",
                                eventType: "remote_readback_verified",
                                evidenceKind: "remote_readback",
                                remoteObservedAtUtc: remoteObservedAt,
                                workerId: executionOwner,
                                fencingToken: executionFencingToken,
                                cancellationToken: cancellationToken,
                                dispatchGroupId: dispatchGroupId);
                            sent++;
                        }
                        else
                        {
                            var message = writeFailure is null
                                ? "O Moodle aceitou a chamada coletiva, mas a releitura individual nao comprovou nota e feedback; o item ficou incerto e nao sera reenviado."
                                : "A chamada coletiva nao teve resultado conclusivo e a releitura individual nao comprovou nota e feedback; o item ficou incerto e nao sera reenviado.";
                            candidate.Item.MarkCommitExecutionUnknown(message, "moodle_batch_write_readback_inconclusive");
                            action.MarkExecutionUnknown(message);
                            executionUnknown = true;
                            failures.Add(new GradingLaunchFailure(candidate.PayloadItem.GradingItemId, message));
                            await SavePublicationAttemptCheckpointAsync(
                                action,
                                payload,
                                candidate.PayloadItem,
                                candidate.Item,
                                claimStatus: "ExecutionUnknown",
                                eventType: "remote_readback_inconclusive",
                                evidenceKind: readBack is null ? "remote_readback_unavailable" : "remote_readback_mismatch",
                                remoteObservedAtUtc: readBack is null ? null : remoteObservedAt,
                                workerId: executionOwner,
                                fencingToken: executionFencingToken,
                                cancellationToken: cancellationToken,
                                dispatchGroupId: dispatchGroupId);
                        }

                        await RecordCommitAuditAsync(
                            action,
                            payload.BatchJobId,
                            candidate.PayloadItem,
                            candidate.Item.CommitStatus == GradingCommitStatus.Succeeded
                                ? "commit_succeeded"
                                : "commit_execution_unknown",
                            responseSummary: new
                            {
                                writeResult,
                                writeFailureType = writeFailure?.GetType().Name,
                                candidate.Item.CommitStatus,
                                readbackObserved = readBack is not null,
                                readbackFailureType = readBackFailure?.GetType().Name,
                                dispatchGroupId
                            },
                            errorCode: candidate.Item.LastErrorCode,
                            errorMessage: candidate.Item.CommitError,
                            cancellationToken);
                    }

                    if (executionUnknown || executionLeaseLost)
                    {
                        preparedBatchWrites.Clear();
                        return;
                    }
                }
            }

            preparedBatchWrites.Clear();
        }

        for (var payloadIndex = 0; payloadIndex < payload.Items.Count; payloadIndex++)
        {
            if (useBatchGradeWrite && payloadIndex % preflightBatchSize == 0 && preparedBatchWrites.Count > 0)
            {
                await FlushPreparedBatchWritesAsync();
                if (executionUnknown || executionLeaseLost)
                {
                    break;
                }
            }

            if (payloadIndex % preflightBatchSize == 0)
            {
                var blockItems = payload.Items.Skip(payloadIndex).Take(preflightBatchSize).ToArray();
                var blockPreflightStartedAt = Stopwatch.GetTimestamp();
                existingGradesByTarget.Clear();
                existingGradeFailuresByAssignment.Clear();
                currentGradebooksByTarget.Clear();
                gradebookReadAtByTarget.Clear();
                gradebookReadFailuresByTarget.Clear();
                gradeReadAtByTarget.Clear();
                submissionReadAtByTarget.Clear();
                submissionStatusCache.Clear();
                courseEnrollmentCache.Clear();

                if (gradeReadGateway is not null && blockItems.Length > 0)
                {
                    try
                    {
                        var gradeBatches = await GradingMoodleReadRetry.ExecuteAsync(
                            retryCancellationToken => gradeReadGateway.GetExistingGradesBatchAsync(
                                userExternalId,
                                blockItems.Select(item => item.AssignmentId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                                blockItems.Select(item => item.StudentId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                                retryCancellationToken),
                            onRetry: null,
                            cancellationToken);
                        var gradesObservedAt = DateTimeOffset.UtcNow;
                        foreach (var item in blockItems)
                        {
                            gradeReadAtByTarget[(item.AssignmentId, item.StudentId)] = gradesObservedAt;
                        }
                        foreach (var gradeBatch in gradeBatches)
                        {
                            if (!string.IsNullOrWhiteSpace(gradeBatch.ErrorMessage))
                            {
                                existingGradeFailuresByAssignment[gradeBatch.AssignmentId] = gradeBatch.ErrorMessage;
                                continue;
                            }

                            foreach (var entry in gradeBatch.Grades)
                            {
                                existingGradesByTarget[(gradeBatch.AssignmentId, entry.Key)] = entry.Value;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        foreach (var assignmentId in blockItems.Select(item => item.AssignmentId).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            existingGradeFailuresByAssignment[assignmentId] = exception.GetType().Name;
                        }
                    }
                }

                var submissionsObservedAt = DateTimeOffset.UtcNow;
                var assignmentsToRefresh = blockItems
                    .Select(item => item.AssignmentId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(assignmentId =>
                        !submissionReadAtByAssignment.TryGetValue(assignmentId, out var previousReadAt) ||
                        submissionsObservedAt - previousReadAt > preflightMaxAge)
                    .ToArray();
                if (assignmentsToRefresh.Length > 0)
                {
                    var refreshSet = assignmentsToRefresh.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var key in cachedSubmissionStatuses.Keys.Where(key => refreshSet.Contains(key.AssignmentId)).ToArray())
                    {
                        cachedSubmissionStatuses.Remove(key);
                    }
                    foreach (var key in cachedSubmissions.Keys.Where(key => refreshSet.Contains(key.AssignmentId)).ToArray())
                    {
                        cachedSubmissions.Remove(key);
                    }
                    cachedFailedSubmissionAssignments.ExceptWith(refreshSet);

                    bulkSubmissionStatuses = await GradingBulkSubmissionStatusReader.ReadAsync(
                        submissionsGateway,
                        userExternalId,
                        assignmentsToRefresh,
                        cancellationToken);
                    foreach (var pair in bulkSubmissionStatuses.Statuses)
                    {
                        cachedSubmissionStatuses[pair.Key] = pair.Value;
                    }
                    foreach (var pair in bulkSubmissionStatuses.Submissions)
                    {
                        cachedSubmissions[pair.Key] = pair.Value;
                    }
                    cachedFailedSubmissionAssignments.UnionWith(bulkSubmissionStatuses.FailedAssignments);
                    foreach (var assignmentId in assignmentsToRefresh)
                    {
                        submissionReadAtByAssignment[assignmentId] = DateTimeOffset.UtcNow;
                    }
                    bulkSubmissionStatuses = new GradingBulkSubmissionStatusReadResult(
                        cachedSubmissionStatuses,
                        cachedSubmissions,
                        cachedFailedSubmissionAssignments,
                        Attempted: submissionsGateway is not null);
                }
                submissionReadAtByTarget.Clear();
                foreach (var item in blockItems)
                {
                    if (submissionReadAtByAssignment.TryGetValue(item.AssignmentId, out var assignmentReadAt))
                    {
                        submissionReadAtByTarget[(item.AssignmentId, item.StudentId)] = assignmentReadAt;
                    }
                }

                workflowSettingsByTarget.Clear();
                workflowSettingsReadAtByTarget.Clear();
                if (assignmentSettingsGateway is not null)
                {
                    foreach (var courseGroup in blockItems.GroupBy(item => item.CourseId, StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var courseSettings = await assignmentSettingsGateway.GetCourseAssignmentSettingsFreshAsync(
                                userExternalId,
                                courseGroup.Key,
                                cancellationToken);
                            var courseSettingsReadAt = DateTimeOffset.UtcNow;
                            foreach (var assignmentGroup in courseGroup.GroupBy(item => item.AssignmentId, StringComparer.OrdinalIgnoreCase))
                            {
                                if (courseSettings.TryGetValue(assignmentGroup.Key, out var settings))
                                {
                                    workflowSettingsByTarget[(courseGroup.Key, assignmentGroup.Key)] = settings;
                                    workflowSettingsReadAtByTarget[(courseGroup.Key, assignmentGroup.Key)] = courseSettingsReadAt;
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            // Exact reads below cover settings omitted by the course response.
                        }

                        foreach (var assignmentGroup in courseGroup.GroupBy(item => item.AssignmentId, StringComparer.OrdinalIgnoreCase))
                        {
                            var key = (courseGroup.Key, assignmentGroup.Key);
                            if (workflowSettingsByTarget.ContainsKey(key))
                            {
                                continue;
                            }

                            try
                            {
                                workflowSettingsByTarget[key] = await assignmentSettingsGateway.GetAssignmentSettingsFreshAsync(
                                    userExternalId,
                                    courseGroup.Key,
                                    assignmentGroup.Key,
                                    cancellationToken);
                                workflowSettingsReadAtByTarget[key] = DateTimeOffset.UtcNow;
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch
                            {
                                workflowSettingsByTarget[key] = null;
                            }
                        }
                    }
                }

                if (gradebookGateway is not null)
                {
                    foreach (var courseGroup in blockItems.GroupBy(item => item.CourseId, StringComparer.OrdinalIgnoreCase))
                    {
                        var requestedStudentIds = courseGroup
                            .Select(item => item.StudentId)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        try
                        {
                            var snapshot = await gradebookGateway.GetCourseGradebookAsync(
                                courseGroup.Key,
                                requestedStudentIds,
                                groupId: null,
                                readOptions: GradebookReadOptions.FreshBatch,
                                cancellationToken: cancellationToken);
                            foreach (var studentId in requestedStudentIds)
                            {
                                var key = (courseGroup.Key, studentId);
                                if (snapshot.TryGetForStudent(studentId, out var gradebook))
                                {
                                    currentGradebooksByTarget[key] = gradebook;
                                    gradebookReadAtByTarget[key] = gradebook.ReadAt ?? snapshot.ReadAt ?? DateTimeOffset.UtcNow;
                                }
                                else if (snapshot.Coverage.ErrorStudentIds.Contains(studentId, StringComparer.OrdinalIgnoreCase))
                                {
                                    gradebookReadFailuresByTarget[key] = "feedback_read_failed";
                                }
                                else
                                {
                                    gradebookReadFailuresByTarget[key] = "feedback_read_incomplete";
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            foreach (var studentId in requestedStudentIds)
                            {
                                gradebookReadFailuresByTarget[(courseGroup.Key, studentId)] = exception.GetType().Name;
                            }
                        }
                    }
                }

                telemetry?.RecordPhase(
                    "grading_publication",
                    "preflight_block",
                    existingGradeFailuresByAssignment.Count > 0 ||
                    bulkSubmissionStatuses.FailedAssignments.Count > 0 ||
                    gradebookReadFailuresByTarget.Count > 0
                        ? "partial"
                        : "passed",
                    Stopwatch.GetElapsedTime(blockPreflightStartedAt).TotalMilliseconds,
                    itemCount: blockItems.Length);
            }

            var payloadItem = payload.Items[payloadIndex];
            var itemPreflightStartedAt = Stopwatch.GetTimestamp();
            var itemPreflightPassed = false;
            AssistedGradingItem? itemForTelemetry = null;
            GradingCommitStatus? itemCommitStatusAtStart = null;
            var itemOutcomeCheckpointed = false;
            var remoteWriteReturned = false;
            long? writeStartedAt = null;
            try
            {
                var leaseNow = DateTimeOffset.UtcNow;
                if (durableExecution && leaseNow - lastLeaseRenewalAt >= ExecutionLeaseRenewalInterval &&
                    !await pendingActions.TryRenewExecutionLeaseAsync(
                        request.PendingActionId,
                        executionOwner,
                        executionFencingToken,
                        leaseNow,
                        ExecutionLeaseDuration,
                        cancellationToken))
                {
                    // Another worker has recovered an expired lease. Preserve
                    // its state instead of writing a terminal status from this
                    // stale request; the other worker now owns the remaining
                    // items.
                    executionLeaseLost = true;
                    break;
                }
                if (durableExecution && leaseNow - lastLeaseRenewalAt >= ExecutionLeaseRenewalInterval)
                {
                    lastLeaseRenewalAt = leaseNow;
                }

                if (!itemsById.TryGetValue(payloadItem.GradingItemId, out var item))
                {
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, "Item de correcao nao encontrado."));
                    continue;
                }
                itemForTelemetry = item;
                itemCommitStatusAtStart = item.CommitStatus;

                if (teacherReviewFailures.TryGetValue(payloadItem.GradingItemId, out var reviewFailure))
                {
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, reviewFailure));
                    await RecordCommitAuditAsync(
                        action,
                        payload.BatchJobId,
                        payloadItem,
                        "commit_blocked",
                        responseSummary: new { item.CommitStatus, item.ReviewStatus },
                        errorCode: "grading_review_invalid",
                        errorMessage: reviewFailure,
                        cancellationToken);
                    continue;
                }

                if (publicationClaimConflicts.Contains(payloadItem.GradingItemId))
                {
                    const string message = "Outra publicacao assumiu esta atividade/aluno/tentativa antes da retomada; o item nao foi enviado.";
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    await RecordCommitAuditAsync(
                        action,
                        payload.BatchJobId,
                        payloadItem,
                        "commit_blocked",
                        responseSummary: new { item.CommitStatus },
                        errorCode: "publication_target_busy",
                        errorMessage: message,
                        cancellationToken);
                    continue;
                }

            if (item.CommitStatus == GradingCommitStatus.Succeeded)
            {
                continue;
            }

            if (item.CommitWriteIntentAt is not null)
            {
                // A previous worker persisted its intent but did not persist
                // the Moodle result. A fresh read can prove that the grade
                // already landed; an absent grade is not enough to prove that
                // a request from a stale worker is no longer in flight, so
                // never resend automatically from this recovery path.
                var recovery = await ReconcileUnknownWriteAsync(
                    userExternalId,
                    payloadItem,
                    cancellationToken);
                if (recovery.Status == UnknownWriteReconciliationStatus.Applied)
                {
                    item.MarkCommitSucceeded();
                    sent++;
                    await RecordCommitAuditAsync(
                        action,
                        payload.BatchJobId,
                        payloadItem,
                        "commit_recovered_applied",
                        responseSummary: new { item.CommitStatus, recovery.Message },
                        errorCode: "moodle_write_recovered_applied",
                        errorMessage: null,
                        cancellationToken);
                    continue;
                }

                var message = recovery.Status == UnknownWriteReconciliationStatus.NotApplied
                    ? "A intencao de escrita persistida nao tem resultado. A leitura atual ainda nao mostra a nota, mas nao prova que uma requisicao antiga terminou; a publicacao foi suspensa para reconciliacao assistida."
                    : recovery.Message;
                item.MarkCommitExecutionUnknown(message, "write_intent_without_result");
                action.MarkExecutionUnknown(message);
                executionUnknown = true;
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_recovery_execution_unknown",
                    responseSummary: new { item.CommitStatus, recovery.Status },
                    errorCode: "write_intent_without_result",
                    errorMessage: message,
                    cancellationToken);
                break;
            }

            if (duplicateTargetItemIds.Contains(payloadItem.GradingItemId))
            {
                const string message = "A ação contém mais de uma correção para o mesmo aluno/atividade; o alvo foi bloqueado sem escrita.";
                item.MarkCommitFailed(message, "duplicate_publication_target");
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new { item.CommitStatus },
                    errorCode: "duplicate_publication_target",
                    errorMessage: message,
                    cancellationToken);
                continue;
            }

            if (payload.ContractVersion != GradingLaunchPayload.CurrentContractVersion ||
                !string.Equals(payloadItem.WorkflowMode, "disabled", StringComparison.Ordinal) ||
                payloadItem.WorkflowState is not null ||
                !string.Equals(payloadItem.ReleaseState, "not_applicable", StringComparison.Ordinal))
            {
                const string message = "A prévia não contém um contrato de workflow compatível. Nenhuma escrita foi enviada; gere uma nova prévia.";
                item.MarkCommitFailed(message, "grading_publication_contract_unsupported");
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new { item.CommitStatus, payload.ContractVersion, payloadItem.WorkflowMode },
                    errorCode: "grading_publication_contract_unsupported",
                    errorMessage: message,
                    cancellationToken);
                continue;
            }

            if (item.CommitStatus == GradingCommitStatus.RetryScheduled &&
                !item.BeginScheduledCommitRetry(DateTimeOffset.UtcNow))
            {
                failures.Add(new GradingLaunchFailure(
                    payloadItem.GradingItemId,
                    "A publicacao ainda aguarda a proxima tentativa agendada."));
                continue;
            }

            if (item.CommitStatus == GradingCommitStatus.Failed)
            {
                failures.Add(new GradingLaunchFailure(
                    payloadItem.GradingItemId,
                    item.CommitError ?? "A publicacao falhou e precisa de recuperacao assistida."));
                continue;
            }

            if (item.CommitStatus == GradingCommitStatus.ExecutionUnknown)
            {
                executionUnknown = true;
                failures.Add(new GradingLaunchFailure(
                    payloadItem.GradingItemId,
                    item.CommitError ?? "O resultado remoto continua desconhecido; reconcilie antes de reenviar."));
                break;
            }

            if (!HasVersionedContextIdentity(item) ||
                (!HasSealedSubmissionForBlockedContext(item) && !securityWarningsOnly))
            {
                var message = HasVersionedContextIdentity(item)
                    ? "O rascunho nao foi selado com os anexos originais da submissao. Gere um novo pacote e uma nova previa antes de lancar no Moodle."
                    : "O contexto versionado da correcao nao esta disponivel. Gere uma nova previa antes de lancar no Moodle.";
                item.MarkCommitFailed(message, "grading_context_missing");
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new { item.CommitStatus },
                    errorCode: "grading_context_missing",
                    errorMessage: message,
                    cancellationToken);
                continue;
            }

            if (!HasSealedSubmissionForBlockedContext(item))
            {
                warnings.Add(
                    $"Item {payloadItem.GradingItemId}: aviso tecnico de seguranca — o rascunho nao foi selado com todos os anexos originais da submissao; a publicacao segue porque o contexto e o alvo continuam vinculados ao item.");
            }

            if (string.IsNullOrWhiteSpace(payloadItem.ContextHash) ||
                !string.Equals(payloadItem.ContextHash, item.ContextHash, StringComparison.Ordinal))
            {
                var message = "O contexto de correcao mudou ou nao foi incluido na previa. Gere uma nova previa antes de lancar no Moodle.";
                item.MarkCommitFailed(message, "grading_context_hash_mismatch");
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new
                    {
                        item.CommitStatus,
                        expectedContextHash = payloadItem.ContextHash,
                        currentContextHash = item.ContextHash
                    },
                    errorCode: "grading_context_hash_mismatch",
                    errorMessage: message,
                    cancellationToken);
                continue;
            }

            var currentDraftVersionHash = GradingDraftVersionHash.Compute(item);
            var legacyDraftVersionHash = GradingDraftVersionHash.ComputeLegacy(item);
            if (!string.Equals(payloadItem.DraftVersionHash, currentDraftVersionHash, StringComparison.Ordinal) &&
                !string.Equals(payloadItem.DraftVersionHash, legacyDraftVersionHash, StringComparison.Ordinal))
            {
                var message = "O rascunho foi alterado depois da criacao da previa. Gere uma nova previa antes de lancar no Moodle.";
                item.MarkCommitFailed(message, "grading_draft_version_mismatch");
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new
                    {
                        item.CommitStatus,
                        expectedDraftVersionHash = payloadItem.DraftVersionHash,
                        currentDraftVersionHash
                    },
                    errorCode: "grading_draft_version_mismatch",
                    errorMessage: message,
                    cancellationToken);
                continue;
            }

            bulkSubmissionStatuses.TryGetSubmission(
                payloadItem.AssignmentId,
                payloadItem.StudentId,
                out var currentSubmission);
            if (currentSubmission is not null &&
                submissionReadAtByTarget.TryGetValue((payloadItem.AssignmentId, payloadItem.StudentId), out var submissionReadAt) &&
                DateTimeOffset.UtcNow - submissionReadAt > preflightMaxAge)
            {
                const string message = "A leitura da submissão venceu antes da validação deste item; a publicação foi suspensa.";
                item.MarkCommitFailed(message, "moodle_preflight_read_expired");
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new { item.CommitStatus, submissionReadAt },
                    errorCode: "moodle_preflight_read_expired",
                    errorMessage: message,
                    cancellationToken);
                continue;
            }
            var integrityFailure = await ValidateSubmissionIntegrityAsync(
                userExternalId,
                item,
                payloadItem,
                currentSubmission,
                cancellationToken);
            if (integrityFailure is not null)
            {
                if (!securityWarningsOnly || !IsAdvisorySecurityFailure(integrityFailure.ErrorCode))
                {
                    item.MarkCommitFailed(integrityFailure.Message, integrityFailure.ErrorCode);
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, integrityFailure.Message));
                    await RecordCommitAuditAsync(
                        action,
                        payload.BatchJobId,
                        payloadItem,
                        "commit_blocked",
                        responseSummary: new { item.CommitStatus, item.SubmissionContentHash },
                        errorCode: integrityFailure.ErrorCode,
                        errorMessage: integrityFailure.Message,
                        cancellationToken);
                    continue;
                }

                var integrityWarning =
                    $"Item {payloadItem.GradingItemId}: aviso tecnico de seguranca — {integrityFailure.Message} A escrita foi mantida porque a falha ocorreu na validacao auxiliar, nao na identificacao do alvo.";
                warnings.Add(integrityWarning);
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_warning",
                    responseSummary: new { item.CommitStatus, item.SubmissionContentHash },
                    errorCode: integrityFailure.ErrorCode,
                    errorMessage: integrityFailure.Message,
                    cancellationToken);
            }

            if (payloadItem.Grade is not null || payloadItem.PreflightHash is not null)
            {
                existingGradesByTarget.TryGetValue(
                    (payloadItem.AssignmentId, payloadItem.StudentId),
                    out var assignmentGrade);
                if (gradeReadAtByTarget.TryGetValue(
                        (payloadItem.AssignmentId, payloadItem.StudentId),
                        out var gradeReadAt) &&
                    DateTimeOffset.UtcNow - gradeReadAt > preflightMaxAge)
                {
                    assignmentGrade = null;
                }
                var gradebookTargetKey = (payloadItem.CourseId, payloadItem.StudentId);
                currentGradebooksByTarget.TryGetValue(gradebookTargetKey, out var currentGradebook);
                gradebookReadFailuresByTarget.TryGetValue(gradebookTargetKey, out var gradebookReadFailure);
                if (currentGradebook is not null &&
                    gradebookReadAtByTarget.TryGetValue(gradebookTargetKey, out var gradebookReadAt) &&
                    DateTimeOffset.UtcNow - gradebookReadAt > preflightMaxAge)
                {
                    currentGradebook = null;
                    gradebookReadFailure = "preflight_read_expired";
                }

                var projectedGrade = GradingPublicationGradebookProjection.Combine(
                    payloadItem.AssignmentId,
                    payloadItem.StudentId,
                    assignmentGrade,
                    currentGradebook);
                var existingGradeResult = new ExistingGradeValidationResult(
                    projectedGrade,
                    projectedGrade is null
                        ? new CapabilityValidationFailure(
                            string.IsNullOrWhiteSpace(gradebookReadFailure)
                                ? "O gradebook nao retornou uma observacao completa de feedback para validar a publicacao."
                                : "Nao foi possivel reler o feedback atual no Moodle antes do lancamento.",
                            string.IsNullOrWhiteSpace(gradebookReadFailure) || gradebookReadFailure == "feedback_read_incomplete"
                                ? "moodle_feedback_read_incomplete"
                                : gradebookReadFailure == "preflight_read_expired"
                                    ? "moodle_preflight_read_expired"
                                    : "moodle_feedback_read_failed")
                        : null);
                if (existingGradeResult.Failure is not null)
                {
                    item.MarkCommitFailed(existingGradeResult.Failure.Message, existingGradeResult.Failure.ErrorCode);
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, existingGradeResult.Failure.Message));
                    await RecordCommitAuditAsync(
                        action,
                        payload.BatchJobId,
                        payloadItem,
                        "commit_blocked",
                        responseSummary: new { item.CommitStatus, gradebookReadFailure },
                        errorCode: existingGradeResult.Failure.ErrorCode,
                        errorMessage: existingGradeResult.Failure.Message,
                        cancellationToken);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(payloadItem.PreflightHash))
                {
                    var currentPreflightHash = ComputePreflightHash(
                        payload.ConnectionKey ?? "default",
                        payloadItem,
                        existingGradeResult.ExistingGrade);
                    if (!string.Equals(payloadItem.PreflightHash, currentPreflightHash, StringComparison.Ordinal) &&
                        PublishedValuesMatch(payloadItem, existingGradeResult.ExistingGrade))
                    {
                        // A worker may have reached Moodle and lost the
                        // response before its item checkpoint was persisted.
                        // If the observed remote state already equals the
                        // requested values, reconcile the item locally instead
                        // of issuing a duplicate write after restart.
                        item.MarkCommitSucceeded();
                        sent++;
                        await RecordCommitAuditAsync(
                            action,
                            payload.BatchJobId,
                            payloadItem,
                            "commit_recovered_succeeded",
                            responseSummary: new { item.CommitStatus, currentPreflightHash },
                            errorCode: "moodle_write_recovered_from_remote_state",
                            errorMessage: null,
                            cancellationToken);
                        continue;
                    }

                    if (!string.Equals(payloadItem.PreflightHash, currentPreflightHash, StringComparison.Ordinal))
                    {
                        var message = "O estado atual da nota/feedback mudou desde a previa. Gere uma nova previa antes de lancar no Moodle.";
                        item.MarkCommitFailed(message, "moodle_preflight_hash_mismatch");
                        failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                        await RecordCommitAuditAsync(
                            action,
                            payload.BatchJobId,
                            payloadItem,
                            "commit_blocked",
                            responseSummary: new { item.CommitStatus, expectedPreflightHash = payloadItem.PreflightHash, currentPreflightHash },
                            errorCode: "moodle_preflight_hash_mismatch",
                            errorMessage: message,
                            cancellationToken);
                        continue;
                    }
                }

                var gradeOperation = ResolvePayloadGradeOperation(payloadItem);
                if (gradeOperation == "unsupported")
                {
                    var message = "A previa aprovada nao define uma operacao de nota compativel; gere uma nova previa antes de publicar.";
                    item.MarkCommitFailed(message, "unsupported_grade_operation");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    continue;
                }
                if (gradeOperation == "preserve_grade" && string.IsNullOrWhiteSpace(payloadItem.PreflightHash))
                {
                    var message = "A previa nao registrou uma linha de base verificavel para preservar a nota existente; gere uma nova previa.";
                    item.MarkCommitFailed(message, "grade_baseline_unavailable");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    continue;
                }

                if (gradeOperation == "no_grade" && existingGradeResult.ExistingGrade?.HasGrade == true)
                {
                    var message = "A atividade configurada sem nota possui uma nota atual; esta operacao nao pode remove-la.";
                    item.MarkCommitFailed(message, "existing_grade_cannot_be_removed");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    continue;
                }

                if (gradeOperation == "set_grade" && existingGradeResult.ExistingGrade?.HasGrade == true && !payload.AllowOverwriteExisting)
                {
                    var message = $"O Moodle ja possui nota existente para o estudante {payloadItem.StudentId} na atividade {payloadItem.AssignmentId}. Gere uma confirmacao especifica de sobrescrita antes de lancar.";
                    item.MarkCommitFailed(message, "moodle_existing_grade");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    await RecordCommitAuditAsync(
                        action,
                        payload.BatchJobId,
                        payloadItem,
                        "commit_blocked",
                        responseSummary: new
                        {
                            item.CommitStatus,
                            existingGrade = existingGradeResult.ExistingGrade.Grade
                        },
                        errorCode: "moodle_existing_grade",
                        errorMessage: message,
                        cancellationToken);
                    continue;
                }
            }

            var statusKey = (payloadItem.AssignmentId, payloadItem.StudentId);
            if (!submissionStatusCache.TryGetValue(statusKey, out var submissionAttemptResult))
            {
                var statusReadExpired = submissionReadAtByTarget.TryGetValue(statusKey, out var statusReadAt) &&
                                        DateTimeOffset.UtcNow - statusReadAt > preflightMaxAge;
                if (statusReadExpired)
                {
                    submissionAttemptResult = new SubmissionAttemptValidationResult(
                        CurrentStatus: null,
                        new CapabilityValidationFailure(
                            "A leitura da submissão venceu antes da validação deste item; gere uma nova prévia.",
                            "moodle_preflight_read_expired"));
                }
                else if (payloadItem.AttemptNumber is null)
                {
                    submissionAttemptResult = new SubmissionAttemptValidationResult(CurrentStatus: null, Failure: null);
                }
                else if (bulkSubmissionStatuses.FailedAssignments.Contains(payloadItem.AssignmentId))
                {
                    submissionAttemptResult = new SubmissionAttemptValidationResult(
                        CurrentStatus: null,
                        new CapabilityValidationFailure(
                            $"Nao foi possivel validar a tentativa atual da submissao no Moodle para a atividade {payloadItem.AssignmentId}.",
                            "moodle_submission_status_validation_failed"));
                }
                else if (bulkSubmissionStatuses.TryGet(payloadItem.AssignmentId, payloadItem.StudentId, out var bulkStatus))
                {
                    submissionAttemptResult = ValidateSubmissionAttempt(payloadItem, bulkStatus);
                }
                else if (submissionsGateway is not null || payload.Items.Count > GradingBulkSubmissionStatusReader.PerItemFallbackLimit)
                {
                    submissionAttemptResult = new SubmissionAttemptValidationResult(
                        CurrentStatus: null,
                        new CapabilityValidationFailure(
                            $"A submissao atual nao foi encontrada na consulta em lote do Moodle para o estudante {payloadItem.StudentId}.",
                            "moodle_submission_not_found"));
                }
                else
                {
                    submissionAttemptResult = await GetSubmissionAttemptValidationAsync(
                        userExternalId,
                        payloadItem,
                        cancellationToken);
                }
                submissionStatusCache[statusKey] = submissionAttemptResult;
            }
            if (submissionAttemptResult.Failure is not null)
            {
                item.MarkCommitFailed(submissionAttemptResult.Failure.Message, submissionAttemptResult.Failure.ErrorCode);
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, submissionAttemptResult.Failure.Message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new
                    {
                        item.CommitStatus,
                        payloadAttemptNumber = payloadItem.AttemptNumber,
                        currentAttemptNumber = submissionAttemptResult.CurrentStatus?.AttemptNumber,
                        currentSubmissionStatus = submissionAttemptResult.CurrentStatus?.SubmissionStatus
                    },
                    errorCode: submissionAttemptResult.Failure.ErrorCode,
                    errorMessage: submissionAttemptResult.Failure.Message,
                    cancellationToken);
                continue;
            }

            if (submissionAttemptResult.CurrentStatus?.HasFeedback == true &&
                !payload.AllowOverwriteExisting &&
                !string.IsNullOrWhiteSpace(payloadItem.FeedbackText))
            {
                var message = $"O Moodle ja possui feedback existente para o estudante {payloadItem.StudentId} na atividade {payloadItem.AssignmentId}. Gere uma confirmacao especifica de sobrescrita antes de lancar.";
                item.MarkCommitFailed(message, "moodle_existing_feedback");
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new { item.CommitStatus, hasFeedback = true },
                    errorCode: "moodle_existing_feedback",
                    errorMessage: message,
                    cancellationToken);
                continue;
            }

            if (!courseEnrollmentCache.TryGetValue(payloadItem.CourseId, out var courseEnrollment))
            {
                courseEnrollment = await ResolveCourseEnrollmentAsync(
                    userExternalId,
                    payloadItem.CourseId,
                    cancellationToken);
                courseEnrollmentCache[payloadItem.CourseId] = courseEnrollment;
            }

            var enrollmentFailure = courseEnrollment.Error is not null
                ? new CapabilityValidationFailure(
                    $"Nao foi possivel validar se o estudante {payloadItem.StudentId} esta inscrito no curso {payloadItem.CourseId} antes do lancamento: {courseEnrollment.Error}",
                    "moodle_enrollment_validation_failed")
                : courseEnrollment.StudentIds?.Contains(payloadItem.StudentId) == true
                    ? null
                    : new CapabilityValidationFailure(
                        $"O estudante {payloadItem.StudentId} nao esta inscrito no curso {payloadItem.CourseId}. Verifique se o estudante ainda pertence a turma antes de lancar a nota.",
                        "moodle_student_not_enrolled");
            if (enrollmentFailure is not null)
            {
                item.MarkCommitFailed(enrollmentFailure.Message, enrollmentFailure.ErrorCode);
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, enrollmentFailure.Message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new { item.CommitStatus },
                    errorCode: enrollmentFailure.ErrorCode,
                    errorMessage: enrollmentFailure.Message,
                    cancellationToken);
                continue;
            }

            if (!workflowSettingsByTarget.TryGetValue(
                    (payloadItem.CourseId, payloadItem.AssignmentId),
                    out var currentAssignmentSettings) ||
                currentAssignmentSettings?.MarkingWorkflowEnabled is not false ||
                !workflowSettingsReadAtByTarget.TryGetValue(
                    (payloadItem.CourseId, payloadItem.AssignmentId),
                    out var workflowSettingsReadAt) ||
                DateTimeOffset.UtcNow - workflowSettingsReadAt > preflightMaxAge)
            {
                var workflowEnabled = currentAssignmentSettings?.MarkingWorkflowEnabled == true;
                var settingsExpired = workflowSettingsReadAtByTarget.TryGetValue(
                    (payloadItem.CourseId, payloadItem.AssignmentId),
                    out var settingsObservedAt) && DateTimeOffset.UtcNow - settingsObservedAt > preflightMaxAge;
                var errorCode = settingsExpired
                    ? "moodle_preflight_read_expired"
                    : workflowEnabled
                    ? "moodle_marking_workflow_enabled"
                    : "moodle_marking_workflow_unknown";
                var message = settingsExpired
                    ? "A configuração Moodle lida antes deste bloco venceu; o item foi suspenso antes da escrita."
                    : workflowEnabled
                    ? "A atividade passou a usar fluxo de avaliação desde a prévia; a publicação foi bloqueada antes da escrita."
                    : "O Moodle não confirmou que o fluxo de avaliação está desativado; a publicação foi bloqueada antes da escrita.";
                item.MarkCommitFailed(message, errorCode);
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new { item.CommitStatus, workflowEnabled = currentAssignmentSettings?.MarkingWorkflowEnabled },
                    errorCode,
                    errorMessage: message,
                    cancellationToken);
                continue;
            }

            if (!long.TryParse(payloadItem.CourseId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentCourseId) || currentCourseId <= 0 ||
                !long.TryParse(payloadItem.AssignmentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentAssignmentId) || currentAssignmentId <= 0)
            {
                const string message = "O contrato aprovado não contém curso/atividade válidos; nenhuma escrita foi enviada.";
                item.MarkCommitFailed(message, "grading_publication_target_invalid");
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                continue;
            }

            var currentScale = MoodleAssignmentGradeScaleResolver.ReconcileAuthoritativeSources(
                MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
                    currentAssignmentSettings,
                    currentCourseId,
                    currentAssignmentId,
                    payload.ConnectionKey ?? "default"),
                currentGradebooksByTarget.TryGetValue((payloadItem.CourseId, payloadItem.StudentId), out var currentScaleGradebook)
                    ? MoodleAssignmentGradeScaleResolver.FromActivityGradebook(
                        currentScaleGradebook,
                        currentCourseId,
                        currentAssignmentId,
                        payload.ConnectionKey ?? "default")
                    : null,
                currentCourseId,
                currentAssignmentId,
                payload.ConnectionKey ?? "default");
            if (string.IsNullOrWhiteSpace(payloadItem.ScaleVersion) ||
                !string.Equals(payloadItem.ScaleVersion, currentScale.Version, StringComparison.Ordinal))
            {
                const string message = "A escala da atividade mudou desde a prévia; gere uma nova prévia antes de publicar.";
                item.MarkCommitFailed(message, "moodle_grade_scale_changed");
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_blocked",
                    responseSummary: new { item.CommitStatus, expectedScaleVersion = payloadItem.ScaleVersion, observedScaleVersion = currentScale.Version },
                    errorCode: "moodle_grade_scale_changed",
                    errorMessage: message,
                    cancellationToken);
                continue;
            }

            itemPreflightPassed = true;
            try
            {
                var gradeOperation = ResolvePayloadGradeOperation(payloadItem);
                var gradeToWrite = payloadItem.Grade;
                if (gradeOperation == "unsupported")
                {
                    const string message = "A previa aprovada nao define uma operacao de nota compativel; gere uma nova previa antes de publicar.";
                    item.MarkCommitFailed(message, "unsupported_grade_operation");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(payloadItem.PreflightHash))
                {
                    const string message = "A previa nao registrou uma linha de base fresca para validar nota e feedback antes da escrita.";
                    item.MarkCommitFailed(message, "grade_baseline_unavailable");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    continue;
                }

                AssignmentExistingGrade? currentGrade;
                try
                {
                    currentGrade = await ReadCurrentPublicationGradeAsync(
                        userExternalId,
                        payloadItem,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var message = "Nota e feedback atuais nao puderam ser relidos imediatamente antes da escrita.";
                    item.MarkCommitFailed(message, "grade_baseline_unavailable");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    await RecordCommitAuditAsync(
                        action,
                        payload.BatchJobId,
                        payloadItem,
                        "commit_blocked",
                        responseSummary: new { item.CommitStatus, exceptionType = exception.GetType().Name },
                        errorCode: "grade_baseline_unavailable",
                        errorMessage: message,
                        cancellationToken);
                    continue;
                }

                if (currentGrade is null)
                {
                    const string message = "O Moodle nao retornou uma observacao completa de feedback para o alvo; a escrita foi bloqueada.";
                    item.MarkCommitFailed(message, "moodle_feedback_read_incomplete");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    continue;
                }

                var currentHash = ComputePreflightHash(
                    payload.ConnectionKey ?? "default",
                    payloadItem,
                    currentGrade);
                if (!string.Equals(payloadItem.PreflightHash, currentHash, StringComparison.Ordinal))
                {
                    const string message = "Nota ou feedback mudou desde a previa; a publicacao foi bloqueada para revisao.";
                    item.MarkCommitFailed(message, "moodle_preflight_hash_mismatch");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    await RecordCommitAuditAsync(
                        action,
                        payload.BatchJobId,
                        payloadItem,
                        "commit_blocked",
                        responseSummary: new { item.CommitStatus },
                        errorCode: "moodle_preflight_hash_mismatch",
                        errorMessage: message,
                        cancellationToken);
                    continue;
                }

                if (gradeOperation == "preserve_grade")
                {
                    gradeToWrite = currentGrade.HasGrade ? currentGrade.Grade : null;
                    if (currentGrade.HasGrade && currentGrade.Grade is null)
                    {
                        const string message = "O Moodle indica uma nota existente, mas nao retornou seu valor; a atualizacao do feedback foi bloqueada para evitar remove-la.";
                        item.MarkCommitFailed(message, "grade_baseline_unavailable");
                        failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                        continue;
                    }
                }
                else if (gradeOperation == "no_grade" && currentGrade.HasGrade)
                {
                    const string message = "A atividade configurada sem nota possui uma nota atual; esta operacao nao pode remove-la.";
                    item.MarkCommitFailed(message, "existing_grade_cannot_be_removed");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    continue;
                }
                else if (gradeOperation == "set_grade" && currentGrade.HasGrade && !payload.AllowOverwriteExisting)
                {
                    const string message = "O Moodle ja possui nota; a previa nao autorizou sobrescrita para este alvo.";
                    item.MarkCommitFailed(message, "moodle_existing_grade");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(currentGrade.Feedback) &&
                    !payload.AllowOverwriteExisting &&
                    !string.IsNullOrWhiteSpace(payloadItem.FeedbackText))
                {
                    const string message = "O Moodle ja possui feedback; a previa nao autorizou sobrescrita para este alvo.";
                    item.MarkCommitFailed(message, "moodle_existing_feedback");
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                    continue;
                }

                // Rascunhos da IA permanecem apenas internos ate a confirmacao
                // explicita. A confirmacao e o ato de revisao/aprovacao que os
                // promove para o mesmo estado usado pelo caminho em lote legado.
                if (item.ReviewStatus != GradingReviewStatus.Reviewed)
                {
                    item.ApplyTeacherReview(
                        payloadItem.Grade,
                        payloadItem.FeedbackText,
                        action.CreatedBySubject,
                        action.CreatedByMoodleUserId,
                        teacherDecision: "confirmed_for_publication");
                }

                if (useBatchGradeWrite)
                {
                    // Collect only fully preflighted targets. The write
                    // intents are set and committed together immediately
                    // before the group leaves for Moodle.
                    preparedBatchWrites.Add(new PreparedBatchGradeWrite(
                        payloadItem,
                        item,
                        gradeToWrite,
                        PreserveGrade: gradeOperation == "preserve_grade"));
                    itemOutcomeCheckpointed = true;
                    continue;
                }

                if (!item.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow))
                {
                    throw new InvalidOperationException("Nao foi possivel persistir a intencao de escrita do item antes do envio ao Moodle.");
                }

                // Commit the durable boundary before crossing the remote
                // write. A crash after this point is recovered by reading
                // Moodle and cannot silently become a duplicate submission.
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    "commit_write_intent",
                    responseSummary: new
                    {
                        item.CommitWriteIntentAt,
                        item.CommitRetryCount,
                        item.CommitStatus
                    },
                    errorCode: null,
                    errorMessage: null,
                    cancellationToken);
                await SavePublicationAttemptCheckpointAsync(
                    action,
                    payload,
                    payloadItem,
                    item,
                    claimStatus: "Executing",
                    eventType: "write_intent_persisted",
                    evidenceKind: "local_intent",
                    remoteObservedAtUtc: null,
                    workerId: executionOwner,
                    fencingToken: executionFencingToken,
                    cancellationToken: cancellationToken);

                writeStartedAt = Stopwatch.GetTimestamp();
                var writeResult = await mediator.Send(
                    new SaveAssignmentGradeCommand(
                        userExternalId,
                        payloadItem.AssignmentId,
                        payloadItem.StudentId,
                        gradeToWrite,
                        payloadItem.FeedbackText,
                        payloadItem.AttemptNumber ?? -1,
                        AddAttempt: false,
                        ApplyToAll: false,
                        WorkflowState: string.Empty,
                        CourseId: payloadItem.CourseId,
                        PreserveGrade: gradeOperation == "preserve_grade"),
                    cancellationToken);
                remoteWriteReturned = true;
                telemetry?.RecordPhase(
                    "grading_publication",
                    "write",
                    "success",
                    Stopwatch.GetElapsedTime(writeStartedAt.Value).TotalMilliseconds,
                    itemCount: 1);

                AssignmentExistingGrade? readBack = null;
                Exception? readBackFailure = null;
                try
                {
                    readBack = await ReadCurrentPublicationGradeAsync(
                        userExternalId,
                        payloadItem,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    readBackFailure = exception;
                }

                var remoteObservedAt = DateTimeOffset.UtcNow;
                if (PublishedValuesMatch(payloadItem, readBack))
                {
                    item.MarkCommitSucceeded();
                    await SavePublicationAttemptCheckpointAsync(
                        action,
                        payload,
                        payloadItem,
                        item,
                        claimStatus: "Succeeded",
                        eventType: "remote_readback_verified",
                        evidenceKind: "remote_readback",
                        remoteObservedAtUtc: remoteObservedAt,
                        workerId: executionOwner,
                        fencingToken: executionFencingToken,
                        cancellationToken: cancellationToken);
                    itemOutcomeCheckpointed = true;
                    sent++;
                }
                else
                {
                    var message = readBackFailure is null
                        ? "A escrita foi aceita pelo Moodle, mas a releitura nao comprovou nota e feedback aprovados; o resultado ficou incerto e nao sera reenviado."
                        : "A escrita foi aceita pelo Moodle, mas nao foi possivel reler nota e feedback; o resultado ficou incerto e nao sera reenviado.";
                    item.MarkCommitExecutionUnknown(message, "moodle_write_readback_inconclusive");
                    action.MarkExecutionUnknown(message);
                    executionUnknown = true;
                    await SavePublicationAttemptCheckpointAsync(
                        action,
                        payload,
                        payloadItem,
                        item,
                        claimStatus: "ExecutionUnknown",
                        eventType: "remote_readback_inconclusive",
                        evidenceKind: readBack is null ? "remote_readback_unavailable" : "remote_readback_mismatch",
                        remoteObservedAtUtc: readBack is null ? null : remoteObservedAt,
                        workerId: executionOwner,
                        fencingToken: executionFencingToken,
                        cancellationToken: cancellationToken);
                    itemOutcomeCheckpointed = true;
                    failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
                }
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    item.CommitStatus == GradingCommitStatus.Succeeded
                        ? "commit_succeeded"
                        : "commit_execution_unknown",
                    responseSummary: new
                    {
                        writeResult,
                        item.CommitStatus,
                        readbackObserved = readBack is not null,
                        readbackFailureType = readBackFailure?.GetType().Name
                    },
                    errorCode: item.LastErrorCode,
                    errorMessage: item.CommitError,
                    cancellationToken);
                if (executionUnknown)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (writeStartedAt is long writeStartedAtTimestamp)
                {
                    telemetry?.RecordPhase(
                        "grading_publication",
                        "write",
                        GetWriteTelemetryResult(ex),
                        Stopwatch.GetElapsedTime(writeStartedAtTimestamp).TotalMilliseconds,
                        itemCount: 1);
                }

                var originalWriteErrorCode = ex is MoodleApiException writeError
                    ? writeError.ErrorCode
                    : MoodleErrorContract.Describe(ex).ErrorCode;
                // A 429 response is a definite write rejection, but still
                // reconcile before retrying so gateways that misreport or
                // proxy responses cannot cause a duplicate grade write.
                var itemExecutionUnknown = remoteWriteReturned ||
                    MoodleWriteExecutionClassifier.IsUnknown(ex) ||
                    ex is MoodleApiException { HttpStatusCode: 429 };
                if (itemExecutionUnknown)
                {
                    var reconciliation = await ReconcileUnknownWriteAsync(
                        userExternalId,
                        payloadItem,
                        cancellationToken,
                        definitiveRejection: ex is MoodleApiException { HttpStatusCode: 429 });

                    if (reconciliation.Status == UnknownWriteReconciliationStatus.Applied)
                    {
                        item.MarkCommitSucceeded();
                        sent++;
                        await RecordCommitAuditAsync(
                            action,
                            payload.BatchJobId,
                            payloadItem,
                            "commit_reconciled_succeeded",
                            responseSummary: new { item.CommitStatus, reconciliation.Message },
                            errorCode: "moodle_write_reconciled_applied",
                            errorMessage: null,
                            cancellationToken);
                        continue;
                    }

                    if (reconciliation.Status == UnknownWriteReconciliationStatus.NotApplied)
                    {
                        // An absent grade after a transport timeout does not
                        // prove the original request has stopped running. A
                        // second write could race with that request. Only an
                        // explicit HTTP rejection can enter automatic retry.
                        if (ex is not MoodleApiException { HttpStatusCode: 429 })
                        {
                            reconciliation = new UnknownWriteReconciliationResult(
                                UnknownWriteReconciliationStatus.Inconclusive,
                                "A leitura ainda nao mostra nota ou feedback, mas a requisicao sem resposta pode continuar em andamento. Reconcilie antes de reenviar.");
                        }
                    }

                    if (reconciliation.Status == UnknownWriteReconciliationStatus.NotApplied)
                    {
                        var finalMessage = reconciliation.Message;
                        const string errorCode = "moodle_rate_limited_not_applied";
                        // The rejection plus a fresh read confirms that this
                        // request was not applied, but this experiment does
                        // not automatically retry writes after HTTP 429.
                        // Recovery requires a new preview and approval.
                        item.MarkCommitFailed(finalMessage, errorCode);
                        failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, finalMessage));
                        await RecordCommitAuditAsync(
                            action,
                            payload.BatchJobId,
                            payloadItem,
                            "commit_reconciled_not_applied",
                            responseSummary: new { item.CommitStatus, reconciliation.Message },
                            errorCode,
                            errorMessage: finalMessage,
                            cancellationToken);
                        continue;
                    }

                    executionUnknown = true;
                    action.MarkExecutionUnknown(reconciliation.Message);
                    item.MarkCommitExecutionUnknown(reconciliation.Message, originalWriteErrorCode);
                }
                else
                {
                    item.MarkCommitFailed(
                        ex.Message,
                        originalWriteErrorCode);
                }
                var failureMessage = itemExecutionUnknown
                    ? item.CommitError ?? ex.Message
                    : ex.Message;
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, failureMessage));
                await RecordCommitAuditAsync(
                    action,
                    payload.BatchJobId,
                    payloadItem,
                    itemExecutionUnknown ? "commit_execution_unknown" : "commit_failed",
                    responseSummary: new { item.CommitStatus, exceptionType = ex.GetType().Name },
                    errorCode: originalWriteErrorCode,
                    errorMessage: failureMessage,
                    cancellationToken);
                if (itemExecutionUnknown)
                {
                    // Once transport leaves the remote result unknown, stop
                    // the batch. Continuing would create additional writes
                    // that cannot be reconciled as one operator decision.
                    break;
                }
            }
            }
            finally
            {
                var itemPreflightResult = itemPreflightPassed
                    ? "passed"
                    : itemForTelemetry?.CommitStatus == GradingCommitStatus.Failed
                        ? "blocked"
                        : itemForTelemetry?.CommitStatus == GradingCommitStatus.ExecutionUnknown
                            ? "execution_unknown"
                            : "skipped";
                telemetry?.RecordPhase(
                    "grading_publication",
                    "preflight_item",
                    itemPreflightResult,
                    Stopwatch.GetElapsedTime(itemPreflightStartedAt).TotalMilliseconds,
                    itemCount: 1);

                if (durableExecution &&
                    !executionLeaseLost &&
                    !itemOutcomeCheckpointed &&
                    itemForTelemetry is { } changedItem &&
                    itemCommitStatusAtStart is { } startingCommitStatus &&
                    changedItem.CommitStatus != startingCommitStatus)
                {
                    var (claimStatus, eventType, evidenceKind) = changedItem.CommitStatus switch
                    {
                        GradingCommitStatus.Succeeded => ("Succeeded", "item_recovered_or_already_satisfied", "remote_readback"),
                        GradingCommitStatus.Failed => ("Failed", "item_publication_failed", "local_or_remote_failure"),
                        GradingCommitStatus.ExecutionUnknown => ("ExecutionUnknown", "item_execution_unknown", "reconciliation_required"),
                        _ => ("Executing", "item_publication_progressed", "local_state")
                    };
                    await SavePublicationAttemptCheckpointAsync(
                        action,
                        payload,
                        payloadItem,
                        changedItem,
                        claimStatus,
                        eventType,
                        evidenceKind,
                        changedItem.CommitStatus == GradingCommitStatus.Succeeded ? DateTimeOffset.UtcNow : null,
                        executionOwner,
                        executionFencingToken,
                        cancellationToken);
                    itemOutcomeCheckpointed = true;
                }

                // A publication is a durable workflow. Checkpointing in
                // small chunks keeps restart recovery bounded while avoiding
                // one database flush per item in a 10k payload.
                if (durableExecution && !executionLeaseLost && !executionUnknown)
                {
                    itemsSinceCheckpoint++;
                    // Checkpoint in small durable chunks. The per-item
                    // statuses remain idempotent and the preflight read can
                    // reconcile a remote write after a crash, while 10k
                    // publications no longer require 30k database flushes.
                    var checkpoint = itemsSinceCheckpoint >= DurableCheckpointInterval ||
                        executionUnknown || executionLeaseLost ||
                        payloadItem == payload.Items[^1];
                    if (checkpoint)
                    {
                        // A Moodle write can outlive the action lease. Fence
                        // the durable result checkpoint after the remote call,
                        // so an expired worker cannot overwrite the recovery
                        // worker's ledger or action state.
                        var checkpointAt = DateTimeOffset.UtcNow;
                        executionLeaseLost = !await pendingActions.TryRenewExecutionLeaseAsync(
                            request.PendingActionId,
                            executionOwner,
                            executionFencingToken,
                            checkpointAt,
                            ExecutionLeaseDuration,
                            cancellationToken);
                        if (!executionLeaseLost)
                        {
                            lastLeaseRenewalAt = checkpointAt;
                            await repository.SaveChangesAsync(cancellationToken);
                            await pendingActions.SaveChangesAsync(cancellationToken);
                            await auditLogs.SaveChangesAsync(cancellationToken);
                            itemsSinceCheckpoint = 0;
                        }
                    }
                }
            }
        }

        if (useBatchGradeWrite && preparedBatchWrites.Count > 0 && !executionUnknown && !executionLeaseLost)
        {
            await FlushPreparedBatchWritesAsync();
        }

        // Do not flush tracked entities after the durable execution lease was
        // lost. Another worker may already own the action; saving this stale
        // DbContext could overwrite its item ledger or terminal action state.
        if (durableExecution && executionLeaseLost)
        {
            return new ConfirmMoodleBatchLaunchResult(
                "executing",
                request.PendingActionId,
                sent,
                failures.Count,
                failures,
                confirmation.AuditId,
                BatchJobId: payload.BatchJobId,
                Items: BuildPublicationItems(payload, itemsById, failures));
        }

        var directBatch = await repository.GetBatchAsync(payload.BatchJobId, cancellationToken);
        if (directBatch is not null)
        {
            var allItems = await GradingItemProcessor.LoadAllBatchItemsAsync(
                repository,
                directBatch.Id,
                cancellationToken);
            GradingItemProcessor.UpdateBatchCounters(directBatch, allItems);
        }
        else
        {
            var run = await repository.GetGradingRunAsync(payload.BatchJobId, cancellationToken);
            if (run is not null)
            {
                var childBatches = await repository.ListBatchesByGradingRunAsync(run.Id, cancellationToken);
                foreach (var child in childBatches)
                {
                    var allItems = await GradingItemProcessor.LoadAllBatchItemsAsync(repository, child.Id, cancellationToken);
                    GradingItemProcessor.UpdateBatchCounters(child, allItems);
                }
            }
        }

        await repository.SaveChangesAsync(cancellationToken);
        await pendingActions.SaveChangesAsync(cancellationToken);
        await auditLogs.SaveChangesAsync(cancellationToken);

        if (durableExecution && !executionLeaseLost)
        {
            var scheduledRetryItems = itemsById.Values
                .Where(item => item.CommitStatus == GradingCommitStatus.RetryScheduled && item.CommitNextAttemptAt is not null)
                .ToArray();
            var nextScheduledRetry = scheduledRetryItems
                .Select(item => item.CommitNextAttemptAt)
                .Min();
            if (executionUnknown)
            {
                action.MarkExecutionUnknown();
            }
            else if (failures.Count == 0)
            {
                action.MarkExecuted();
            }
            else
            {
                action.MarkPartiallyCompleted(
                    scheduledRetryItems.Length > 0
                        ? $"{scheduledRetryItems.Length} item(ns) aguardam retry seguro."
                        : $"{failures.Count} item(ns) nao foram publicados.",
                    nextScheduledRetry);
            }

            await pendingActions.SaveChangesAsync(cancellationToken);
            // Definite per-item failures are terminal for this attempt and
            // must not leave a perpetual target mutex. An action in
            // PartiallyCompleted can still be retried manually; successful
            // items are skipped by CommitStatus and only unresolved items are
            // attempted again. Unknown transport outcomes remain locked until
            // explicit reconciliation.
            if (!executionUnknown && scheduledRetryItems.Length == 0 && payload.PublicationId is Guid publicationId)
            {
                var claimStatuses = itemsById.Values.ToDictionary(
                    item => item.Id,
                    item => item.CommitStatus switch
                    {
                        GradingCommitStatus.Succeeded => "Succeeded",
                        GradingCommitStatus.Failed => "Failed",
                        GradingCommitStatus.ExecutionUnknown => "ExecutionUnknown",
                        // Items without a terminal ledger outcome were never
                        // committed by this publication; release their claim
                        // so a later preview can resume them independently.
                        _ => "Released"
                    });
                await repository.FinalizePublicationClaimsAsync(
                    publicationId,
                    claimStatuses,
                    cancellationToken);
            }

            if (gradingRun is not null)
            {
                if (executionUnknown)
                {
                    gradingRun.MarkPartiallyCompleted();
                }
                else if (failures.Count == 0)
                {
                    // A preview may intentionally contain only a selected
                    // page/subset of a run. Do not report the aggregate as
                    // completed while other children still have work (for
                    // example, items waiting for AI or human review).
                    if (await HasUnpublishedRunItemsAsync(gradingRun, cancellationToken))
                    {
                        gradingRun.MarkPartiallyCompleted();
                    }
                    else
                    {
                        gradingRun.MarkCompleted();
                    }
                }
                else
                {
                    gradingRun.MarkPartiallyCompleted();
                }
                await repository.SaveChangesAsync(cancellationToken);
            }
        }

        return new ConfirmMoodleBatchLaunchResult(
            executionLeaseLost
                ? "executing"
                : executionUnknown
                ? "execution_unknown"
                : durableExecution
                    ? failures.Count == 0 ? "executed" : "partial_failure"
                    : confirmation.Status,
            request.PendingActionId,
            sent,
            failures.Count,
            failures,
            confirmation.AuditId,
            warnings.Count == 0 ? [] : warnings.Distinct(StringComparer.Ordinal).ToArray(),
            BatchJobId: payload.BatchJobId,
            Items: BuildPublicationItems(payload, itemsById, failures),
            RetryScheduledItems: itemsById.Values.Count(item => item.CommitStatus == GradingCommitStatus.RetryScheduled),
            NextAttemptAt: itemsById.Values
                .Where(item => item.CommitStatus == GradingCommitStatus.RetryScheduled)
                .Select(item => item.CommitNextAttemptAt)
                .Min());
    }

    private static IReadOnlyList<GradingLaunchPublicationItem> BuildPublicationItems(
        GradingLaunchPayload payload,
        IReadOnlyDictionary<Guid, AssistedGradingItem>? itemsById = null,
        IReadOnlyList<GradingLaunchFailure>? failures = null,
        string? forcedStatus = null)
    {
        var failureByItem = (failures ?? [])
            .GroupBy(failure => failure.GradingItemId)
            .ToDictionary(group => group.Key, group => group.Last().Message);

        return payload.Items.Select(payloadItem =>
        {
            AssistedGradingItem? item = null;
            if (itemsById is not null)
            {
                itemsById.TryGetValue(payloadItem.GradingItemId, out item);
            }
            var commitStatus = item?.CommitStatus.ToString() ?? "Pending";
            var status = forcedStatus ?? commitStatus switch
            {
                nameof(GradingCommitStatus.Succeeded) => "publicado",
                nameof(GradingCommitStatus.ExecutionUnknown) => "resultado_desconhecido",
                nameof(GradingCommitStatus.RetryScheduled) => "retry_scheduled",
                nameof(GradingCommitStatus.Failed) => "falhou",
                _ => "pendente"
            };
            var isPublished = string.Equals(status, "publicado", StringComparison.Ordinal);

            return new GradingLaunchPublicationItem(
                payloadItem.GradingItemId,
                payloadItem.AssignmentId,
                payloadItem.AssignmentName,
                payloadItem.StudentId,
                item?.StudentDisplayName ?? payloadItem.StudentName,
                isPublished ? item?.FinalGrade ?? payloadItem.Grade : null,
                isPublished ? item?.FinalFeedback ?? payloadItem.FeedbackText : null,
                commitStatus,
                status,
                failureByItem.GetValueOrDefault(payloadItem.GradingItemId) ?? item?.CommitError,
                payloadItem.ReleaseState ?? "unknown",
                payloadItem.VisibilityState ?? "unknown");
        }).ToArray();
    }

    private async Task<IReadOnlySet<Guid>> FindGradeScaleMismatchesAsync(
        GradingLaunchPayload payload,
        PendingMoodleAction action,
        IMoodleAssignmentSettingsGateway? assignmentSettingsGateway,
        IMoodleGradebookGateway? gradebookGateway,
        CancellationToken cancellationToken)
    {
        var mismatches = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(payload.ConnectionKey) || assignmentSettingsGateway is null)
        {
            foreach (var item in payload.Items)
            {
                mismatches.Add(item.GradingItemId);
            }
            return mismatches;
        }

        var userExternalId = action.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ??
            action.CreatedBySubject;
        foreach (var group in payload.Items.GroupBy(item => (item.CourseId, item.AssignmentId)))
        {
            var groupItems = group.ToArray();
            if (!long.TryParse(group.Key.CourseId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var courseId) ||
                courseId <= 0 ||
                !long.TryParse(group.Key.AssignmentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var assignmentId) ||
                assignmentId <= 0)
            {
                foreach (var item in groupItems) mismatches.Add(item.GradingItemId);
                continue;
            }

            AssignmentSettingsSummary? settings;
            try
            {
                settings = await assignmentSettingsGateway.GetAssignmentSettingsFreshAsync(
                    userExternalId,
                    group.Key.CourseId,
                    group.Key.AssignmentId,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                foreach (var item in groupItems) mismatches.Add(item.GradingItemId);
                continue;
            }

            var resolution = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
                settings,
                courseId,
                assignmentId,
                payload.ConnectionKey);
            MoodleAssignmentGradeScaleResolution? gradebookResolution = null;
            if (gradebookGateway is not null)
            {
                try
                {
                    var gradebook = await gradebookGateway.GetStudentGradebookAsync(
                        group.Key.CourseId,
                        groupItems[0].StudentId,
                        GradebookReadOptions.Preview,
                        cancellationToken);
                    gradebookResolution = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(
                        gradebook,
                        courseId,
                        assignmentId,
                        payload.ConnectionKey);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Assignment settings are primary. Unknown settings still
                    // resolve to unknown and block below; a fresh settings
                    // definition remains usable if this secondary read fails.
                }
            }

            resolution = MoodleAssignmentGradeScaleResolver.ReconcileAuthoritativeSources(
                resolution,
                gradebookResolution,
                courseId,
                assignmentId,
                payload.ConnectionKey);

            if (resolution.GradingMode == "unknown")
            {
                foreach (var item in groupItems) mismatches.Add(item.GradingItemId);
                continue;
            }

            foreach (var item in groupItems)
            {
                if (string.IsNullOrWhiteSpace(item.ScaleVersion) ||
                    !string.Equals(item.ScaleVersion, resolution.Version, StringComparison.Ordinal))
                {
                    mismatches.Add(item.GradingItemId);
                }
            }
        }

        return mismatches;
    }

    private async Task<ConfirmMoodleBatchLaunchResult> BlockChangedGradeScaleAsync(
        PendingMoodleAction action,
        GradingLaunchPayload payload,
        IReadOnlySet<Guid> mismatches,
        CancellationToken cancellationToken)
    {
        const string message = "A escala da atividade mudou ou não pôde ser confirmada por leitura fresca. Gere uma nova prévia antes de publicar.";
        action.MarkFailed("grade_scale_changed_or_unavailable; new preview required");
        await pendingActions.SaveChangesAsync(cancellationToken);
        if (payload.PublicationId is Guid publicationId)
        {
            await repository.ReleasePublicationClaimsAsync(publicationId, cancellationToken);
        }

        var failures = payload.Items
            .Where(item => mismatches.Contains(item.GradingItemId))
            .Select(item => new GradingLaunchFailure(item.GradingItemId, message))
            .ToArray();
        return new ConfirmMoodleBatchLaunchResult(
            "scale_changed",
            action.Id,
            SentItems: 0,
            FailedItems: failures.Length,
            Failures: failures,
            AuditId: null,
            Warnings: [message],
            BatchJobId: payload.BatchJobId,
            Items: BuildPublicationItems(payload, forcedStatus: "bloqueada_escala_alterada"));
    }

    private static async Task EnsurePreviewConnectionAsync(
        GradingLaunchPayload payload,
        IMoodleCredentialsProvider? credentialsProvider,
        CancellationToken cancellationToken)
    {
        if (credentialsProvider is null || string.IsNullOrWhiteSpace(payload.ConnectionKey))
        {
            return;
        }

        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var expected = payload.ConnectionKey.Trim();
        var actual = expected.StartsWith("connection:", StringComparison.Ordinal)
            ? $"connection:{credentials.ConnectionId}"
            : $"client:{credentials.ClientId}:alias:{credentials.Alias}";
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A conexao Moodle selecionada mudou desde a previa. Selecione a conexao original e gere uma nova previa antes de publicar.");
        }
    }

    private static async Task RestorePreviewConnectionContextAsync(
        GradingLaunchPayload payload,
        IGradingReviewRepository repository,
        ICurrentUserContext? currentUser,
        IConnectorExecutionContext? executionContext,
        IMoodleConnectionSelection? connectionSelection,
        IMoodleBackgroundConnectionContextRestorer? backgroundConnectionContextRestorer,
        CancellationToken cancellationToken)
    {
        if (currentUser is null)
        {
            var directBatch = await repository.GetBatchAsync(payload.BatchJobId, cancellationToken);
            if (directBatch is null)
            {
                throw new InvalidOperationException(
                    "O contexto do lote de publicação não pode ser validado; nenhuma confirmação foi autorizada.");
            }

            if (!string.IsNullOrWhiteSpace(directBatch.MoodleConnectionId) ||
                !string.IsNullOrWhiteSpace(directBatch.ConnectorClientId) ||
                !string.IsNullOrWhiteSpace(directBatch.ConnectionAlias))
            {
                throw new InvalidOperationException(
                    "O contexto de usuário necessário para restaurar a conexão original do lote está indisponível.");
            }

            return;
        }

        var scope = await GradingBatchScopeResolver.ResolveAsync(
            repository,
            currentUser,
            payload.BatchJobId,
            cancellationToken);
        var batch = scope.FirstBatch
            ?? throw new InvalidOperationException("A execução de correção não possui sublotes para confirmar.");
        var connectionKeys = scope.Batches
            .Select(ResolveConfirmationConnectionKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (connectionKeys.Length > 1)
        {
            throw new InvalidOperationException(
                "Uma publicação não pode confirmar lotes associados a conexões Moodle diferentes.");
        }

        await GradingMoodleConnectionContext.RestoreAsync(
            batch,
            executionContext,
            connectionSelection,
            backgroundConnectionContextRestorer,
            cancellationToken);
    }

    private static string ResolveConfirmationConnectionKey(AssistedGradingBatch batch) =>
        !string.IsNullOrWhiteSpace(batch.MoodleConnectionId)
            ? $"connection:{batch.MoodleConnectionId.Trim()}"
            : $"client:{(string.IsNullOrWhiteSpace(batch.ConnectorClientId) ? "default" : batch.ConnectorClientId)}" +
              $":alias:{(string.IsNullOrWhiteSpace(batch.ConnectionAlias) ? "default" : batch.ConnectionAlias)}";

    private static bool RequiresPreviewConnectionValidation(PendingActionStatus status) =>
        status is PendingActionStatus.PendingConfirmation or
            PendingActionStatus.Authorized or
            PendingActionStatus.Confirmed or
            PendingActionStatus.Executing or
            PendingActionStatus.PartiallyCompleted;

    private async Task<GradeWriteCapabilitySelection> GetGradeWriteCapabilitySelectionAsync(
        string userExternalId,
        bool preferBatch,
        CancellationToken cancellationToken)
    {
        try
        {
            var catalog = await GradingMoodleReadRetry.ExecuteAsync(
                retryCancellationToken => capabilities.GetFunctionCatalogAsync(
                    userExternalId,
                    retryCancellationToken),
                onRetry: null,
                cancellationToken);
            var individualAvailable = catalog.Functions.Contains(IndividualGradeWriteFunction, StringComparer.OrdinalIgnoreCase);
            var batchAvailable = catalog.Functions.Contains("mod_assign_save_grades", StringComparer.OrdinalIgnoreCase);
            if (preferBatch && batchAvailable)
            {
                return new GradeWriteCapabilitySelection(true, individualAvailable, null);
            }

            if (individualAvailable)
            {
                return new GradeWriteCapabilitySelection(false, true, null);
            }

            return new GradeWriteCapabilitySelection(
                false,
                false,
                new CapabilityValidationFailure(
                    $"As funcoes Moodle {IndividualGradeWriteFunction} e mod_assign_save_grades nao estao disponiveis no servico autorizado.",
                    "moodle_function_unavailable"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GradeWriteCapabilitySelection(
                false,
                false,
                new CapabilityValidationFailure(
                    $"Nao foi possivel validar a funcao Moodle {IndividualGradeWriteFunction} ou mod_assign_save_grades antes do lancamento: {ex.Message}",
                    "moodle_function_validation_failed"));
        }
    }

    private static bool HasVersionedContextIdentity(AssistedGradingItem item) =>
        item.ContextVersion is > 0 &&
        !string.IsNullOrWhiteSpace(item.ContextHash) &&
        !string.IsNullOrWhiteSpace(item.ContextStatus) &&
        !string.Equals(item.ContextStatus, "legacy_unversioned", StringComparison.OrdinalIgnoreCase);

    private static bool HasSealedSubmissionForBlockedContext(AssistedGradingItem item) =>
        !string.Equals(item.ContextStatus, "blocked", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(item.SubmissionContentHash);

    private static bool IsAdvisorySecurityFailure(string errorCode) =>
        errorCode is "mcp_grading_write_disabled"
            or "submission_integrity_validation_unavailable"
            or "submission_integrity_hash_unavailable"
            or "submission_integrity_validation_failed";

    private static string ComputePreflightHash(
        string connectionKey,
        GradingLaunchPayloadItem payloadItem,
        AssignmentExistingGrade? existingGrade) =>
        GradingPublicationGradebookProjection.ComputePreflightHash(connectionKey, payloadItem, existingGrade);

    private async Task<GradingRun?> ResolvePublicationRunAsync(
        Guid requestedId,
        CancellationToken cancellationToken)
    {
        var directBatch = await repository.GetBatchAsync(requestedId, cancellationToken);
        if (directBatch?.GradingRunId is Guid runId)
        {
            return await repository.GetGradingRunAsync(runId, cancellationToken);
        }

        return await repository.GetGradingRunAsync(requestedId, cancellationToken);
    }

    private async Task<bool> HasUnpublishedRunItemsAsync(
        GradingRun run,
        CancellationToken cancellationToken)
    {
        var childBatches = await repository.ListBatchesByGradingRunAsync(run.Id, cancellationToken);
        foreach (var child in childBatches)
        {
            var childItems = await GradingItemProcessor.LoadAllBatchItemsAsync(
                repository,
                child.Id,
                cancellationToken);
            if (childItems.Any(item =>
                    item.CommitStatus != GradingCommitStatus.Succeeded &&
                    item.Status is not (GradingItemStatus.Blocked or GradingItemStatus.Failed)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PublishedValuesMatch(
        GradingLaunchPayloadItem payloadItem,
        AssignmentExistingGrade? existingGrade) =>
        GradingPublicationGradebookProjection.MatchesApprovedValues(payloadItem, existingGrade);

    private async Task SavePublicationAttemptCheckpointAsync(
        PendingMoodleAction action,
        GradingLaunchPayload payload,
        GradingLaunchPayloadItem payloadItem,
        AssistedGradingItem item,
        string claimStatus,
        string eventType,
        string evidenceKind,
        DateTimeOffset? remoteObservedAtUtc,
        string workerId,
        long fencingToken,
        CancellationToken cancellationToken,
        Guid? dispatchGroupId = null)
    {
        await repository.SavePublicationCheckpointAsync(
            claimStatus,
            new GradingPublicationAttemptRecord(
                PendingActionId: action.Id,
                PublicationId: payload.PublicationId ?? action.Id,
                GradingItemId: item.Id,
                SubmissionAttemptNumber: payloadItem.AttemptNumber,
                DispatchAttemptNumber: Math.Max(1, item.CommitRetryCount + 1),
                EventType: eventType,
                ItemState: item.CommitStatus.ToString(),
                WorkerId: workerId,
                FencingToken: fencingToken,
                ErrorCode: item.LastErrorCode,
                EvidenceKind: evidenceKind,
                RemoteObservedAtUtc: remoteObservedAtUtc,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                DispatchGroupId: dispatchGroupId),
            cancellationToken);
    }

    private async Task<AssignmentExistingGrade?> ReadCurrentPublicationGradeAsync(
        string userExternalId,
        GradingLaunchPayloadItem payloadItem,
        CancellationToken cancellationToken)
    {
        if (gradebookGateway is null)
        {
            return null;
        }

        var assignmentGrade = await gradeReadGateway.GetExistingGradeAsync(
            userExternalId,
            payloadItem.AssignmentId,
            payloadItem.StudentId,
            cancellationToken);
        var gradebook = await gradebookGateway.GetStudentGradebookAsync(
            payloadItem.CourseId,
            payloadItem.StudentId,
            GradebookReadOptions.LiveWithoutScale,
            cancellationToken);
        return GradingPublicationGradebookProjection.Combine(
            payloadItem.AssignmentId,
            payloadItem.StudentId,
            assignmentGrade,
            gradebook);
    }

    private static string ResolvePayloadGradeOperation(GradingLaunchPayloadItem payloadItem)
    {
        if (!string.IsNullOrWhiteSpace(payloadItem.GradeOperation))
        {
            return payloadItem.GradeOperation;
        }

        // Legacy approved payloads predate an explicit operation. Interpret
        // them conservatively from the sealed grading mode; an unknown mode
        // must not silently remove a grade.
        if (payloadItem.Grade is not null)
        {
            return "set_grade";
        }

        return string.Equals(payloadItem.GradingMode, "feedback_only", StringComparison.OrdinalIgnoreCase)
            ? "no_grade"
            : string.Equals(payloadItem.GradingMode, "numeric", StringComparison.OrdinalIgnoreCase)
                ? "preserve_grade"
                : "unsupported";
    }

    private async Task<CapabilityValidationFailure?> ValidateSubmissionIntegrityAsync(
        string userExternalId,
        AssistedGradingItem item,
        GradingLaunchPayloadItem payloadItem,
        AssignmentSubmissionRecord? currentSubmission,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.SubmissionContentHash))
        {
            // Lotes legados não possuem um hash de submissão. O caminho MCP
            // sempre o sela no draft e, portanto, não alcança esta exceção.
            return null;
        }

        if (resourceFeatures?.Value.McpGradingWriteEnabled != true)
        {
            return new CapabilityValidationFailure(
                "O lancamento Moodle para drafts MCP esta desabilitado pela configuracao de rollout.",
                "mcp_grading_write_disabled");
        }

        if (item.SubmissionId is not long submissionId ||
            submissionContentHashResolver is null)
        {
            return new CapabilityValidationFailure(
                "A integridade da submissao revisada nao pode ser revalidada antes do lancamento.",
                "submission_integrity_validation_unavailable");
        }

        var startedAt = Stopwatch.GetTimestamp();
        // A bulk status read already contains the metadata needed to prove
        // that the reviewed submission is still the same. Reuse that cheap
        // revision marker and avoid downloading every attachment again after
        // the teacher has confirmed a large publication. Drafts created before
        // this marker was introduced continue through the full byte hash path.
        if (currentSubmission is not null &&
            !string.IsNullOrWhiteSpace(item.SubmissionRevisionHash) &&
            SubmissionRevisionHash.TryCompute(
                currentSubmission,
                Math.Clamp(gradingLimits?.Value.MaxFilesPerSubmission ?? 10, 0, 100),
                out var currentRevisionHash) &&
            string.Equals(currentRevisionHash, item.SubmissionRevisionHash, StringComparison.OrdinalIgnoreCase))
        {
            telemetry?.RecordPhase(
                "submission_integrity",
                "revision_check",
                "skipped_download",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                itemCount: 1);
            return null;
        }

        try
        {
            // Resource IDs are local transport references and may be absent
            // for package imports or expired by publication time. Re-read the
            // current Moodle files so integrity depends on submission
            // identity and content, not on the lifetime of a local cache row.
            var current = await submissionContentHashResolver.ResolveAsync(
                userExternalId,
                payloadItem.AssignmentId,
                payloadItem.StudentId,
                submissionId,
                attachmentHashes: [],
                cancellationToken,
                currentSubmission);
            var unchanged = string.Equals(current.Hash, item.SubmissionContentHash, StringComparison.Ordinal);
            telemetry?.RecordPhase(
                "submission_integrity",
                "live_rehash",
                unchanged ? "unchanged" : "changed",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                itemCount: 1);
            return unchanged
                ? null
                : new CapabilityValidationFailure(
                    "A submissao foi alterada desde a revisao. Gere um novo draft e uma nova previa antes de lancar.",
                    "submission_changed_since_review");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            telemetry?.RecordPhase(
                "submission_integrity",
                "live_rehash",
                exception.GetType().Name,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                itemCount: 1);
            return new CapabilityValidationFailure(
                "Nao foi possivel revalidar a integridade atual da submissao antes do lancamento.",
                "submission_integrity_validation_failed");
        }
    }

    private async Task<SubmissionAttemptValidationResult> GetSubmissionAttemptValidationAsync(
        string userExternalId,
        GradingLaunchPayloadItem payloadItem,
        CancellationToken cancellationToken)
    {
        if (payloadItem.AttemptNumber is null)
        {
            return new SubmissionAttemptValidationResult(CurrentStatus: null, Failure: null);
        }

        try
        {
            var currentStatus = await GradingMoodleReadRetry.ExecuteAsync(
                retryCancellationToken => submissionStatusGateway.GetSubmissionStatusAsync(
                    userExternalId,
                    payloadItem.AssignmentId,
                    payloadItem.StudentId,
                    retryCancellationToken),
                onRetry: null,
                cancellationToken);
            if (currentStatus?.AttemptNumber is not null &&
                currentStatus.AttemptNumber != payloadItem.AttemptNumber)
            {
                return new SubmissionAttemptValidationResult(
                    currentStatus,
                    new CapabilityValidationFailure(
                        $"A tentativa da submissao mudou no Moodle para o estudante {payloadItem.StudentId} na atividade {payloadItem.AssignmentId}. Gere uma nova previa antes de lancar.",
                        "moodle_submission_attempt_mismatch"));
            }

            if (!string.IsNullOrWhiteSpace(currentStatus?.SubmissionStatus) &&
                !string.Equals(currentStatus.SubmissionStatus, "submitted", StringComparison.OrdinalIgnoreCase))
            {
                return new SubmissionAttemptValidationResult(
                    currentStatus,
                    new CapabilityValidationFailure(
                        $"A submissao atual nao esta entregue no Moodle para o estudante {payloadItem.StudentId} na atividade {payloadItem.AssignmentId}. Gere uma nova previa antes de lancar.",
                        "moodle_submission_not_submitted"));
            }

            return new SubmissionAttemptValidationResult(currentStatus, Failure: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SubmissionAttemptValidationResult(
                CurrentStatus: null,
                new CapabilityValidationFailure(
                    $"Nao foi possivel validar a tentativa atual da submissao no Moodle antes do lancamento: {ex.Message}",
                    "moodle_submission_status_validation_failed"));
        }
    }

    private async Task<(IReadOnlySet<string>? StudentIds, string? Error)> ResolveCourseEnrollmentAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken)
    {
        try
        {
            var studentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentPage = 1;
            while (true)
            {
                var page = await GetCourseParticipantsWithRetryAsync(
                    userExternalId,
                    courseId,
                    currentPage,
                    cancellationToken);
                foreach (var participant in page.Participants)
                {
                    if (!string.IsNullOrWhiteSpace(participant.UserId))
                    {
                        studentIds.Add(participant.UserId);
                    }

                }

                if (!page.HasMore)
                {
                    return (studentIds, null);
                }

                currentPage++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.GetType().Name);
        }
    }

    private static SubmissionAttemptValidationResult ValidateSubmissionAttempt(
        GradingLaunchPayloadItem payloadItem,
        AssignmentSubmissionAttemptStatus? currentStatus)
    {
        if (currentStatus?.AttemptNumber is not null &&
            currentStatus.AttemptNumber != payloadItem.AttemptNumber)
        {
            return new SubmissionAttemptValidationResult(
                currentStatus,
                new CapabilityValidationFailure(
                    $"A tentativa da submissao mudou no Moodle para o estudante {payloadItem.StudentId} na atividade {payloadItem.AssignmentId}. Gere uma nova previa antes de lancar.",
                    "moodle_submission_attempt_mismatch"));
        }

        if (!string.IsNullOrWhiteSpace(currentStatus?.SubmissionStatus) &&
            !string.Equals(currentStatus.SubmissionStatus, "submitted", StringComparison.OrdinalIgnoreCase))
        {
            return new SubmissionAttemptValidationResult(
                currentStatus,
                new CapabilityValidationFailure(
                    $"A submissao atual nao esta entregue no Moodle para o estudante {payloadItem.StudentId} na atividade {payloadItem.AssignmentId}. Gere uma nova previa antes de lancar.",
                    "moodle_submission_not_submitted"));
        }

        return new SubmissionAttemptValidationResult(currentStatus, Failure: null);
    }

    private Task<CourseParticipantsPage> GetCourseParticipantsWithRetryAsync(
        string userExternalId,
        string courseId,
        int page,
        CancellationToken cancellationToken)
    {
        return GradingMoodleReadRetry.ExecuteAsync(
            retryCancellationToken => participantsGateway.GetCourseParticipantsAsync(
                userExternalId,
                courseId,
                ParticipantStatusFilter.All,
                page,
                pageSize: 500,
                studentsOnly: false,
                includeEmail: false,
                groupId: null,
                retryCancellationToken),
            onRetry: null,
            cancellationToken);
    }

    private async Task<UnknownWriteReconciliationResult> ReconcileUnknownWriteAsync(
        string userExternalId,
        GradingLaunchPayloadItem payloadItem,
        CancellationToken cancellationToken,
        bool definitiveRejection = false)
    {
        var startedAt = Stopwatch.GetTimestamp();
        UnknownWriteReconciliationResult result;
        try
        {
            var existingGrade = await GradingMoodleReadRetry.ExecuteAsync(
                retryCancellationToken => ReadCurrentPublicationGradeAsync(
                    userExternalId,
                    payloadItem,
                    retryCancellationToken),
                onRetry: null,
                cancellationToken);

            if (definitiveRejection &&
                existingGrade is { FeedbackObserved: true, HasGrade: false } &&
                string.IsNullOrWhiteSpace(existingGrade.Feedback))
            {
                result = new UnknownWriteReconciliationResult(
                    UnknownWriteReconciliationStatus.NotApplied,
                    "A rejeicao definitiva prova que a requisicao nao foi aplicada; a leitura sem valores tambem nao encontrou o destino.");
            }
            else if (PublishedValuesMatch(payloadItem, existingGrade))
            {
                result = new UnknownWriteReconciliationResult(
                    UnknownWriteReconciliationStatus.Applied,
                    payloadItem.Grade is null
                        ? "A resposta da escrita foi perdida, mas a leitura posterior confirmou o feedback no Moodle."
                        : "A resposta da escrita foi perdida, mas a leitura posterior confirmou a nota e o feedback no Moodle.");
            }
            else
            {
                result = new UnknownWriteReconciliationResult(
                    UnknownWriteReconciliationStatus.Inconclusive,
                    "A leitura posterior nao comprova o destino aprovado nem prova que a requisicao antiga deixou de executar. Reconcilie antes de qualquer nova tentativa.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new UnknownWriteReconciliationResult(
                UnknownWriteReconciliationStatus.Inconclusive,
                $"A resposta da escrita foi perdida e nao foi possivel reconciliar o resultado no Moodle: {ex.Message}");
        }

        telemetry?.RecordPhase(
            "grading_publication",
            "reconcile",
            result.Status switch
            {
                UnknownWriteReconciliationStatus.Applied => "applied",
                UnknownWriteReconciliationStatus.NotApplied when definitiveRejection => "not_applied",
                _ => "inconclusive"
            },
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            itemCount: 1);
        return result;
    }

    private static string GetWriteTelemetryResult(Exception exception)
    {
        if (exception is MoodleApiException { HttpStatusCode: 429 })
        {
            return "http_429";
        }

        if (exception is MoodleApiException { HttpStatusCode: >= 500 and <= 599 })
        {
            return "http_5xx";
        }

        if (exception is TimeoutException ||
            MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(exception).ErrorCode) == MoodleErrorContract.RequestTimeout)
        {
            return "timeout";
        }

        if (MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(exception).ErrorCode) == MoodleErrorContract.NetworkError)
        {
            return "network_error";
        }

        return MoodleWriteExecutionClassifier.IsUnknown(exception)
            ? "execution_unknown"
            : "rejected";
    }

    private static bool FeedbackMatches(string? persistedFeedback, string expectedFeedback)
    {
        return string.Equals(
            NormalizeFeedback(persistedFeedback),
            NormalizeFeedback(expectedFeedback),
            StringComparison.Ordinal);
    }

    private static string NormalizeFeedback(string? value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty)
            .Replace('\u00A0', ' ');
        var withoutMarkup = HtmlTagRegex.Replace(decoded, " ");
        return string.Join(
            " ",
            withoutMarkup.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task MarkPendingItemsFailedAsync(
        PendingMoodleAction action,
        Guid batchJobId,
        IReadOnlyList<GradingLaunchPayloadItem> payloadItems,
        string message,
        string status,
        string errorCode,
        List<GradingLaunchFailure> failures,
        CancellationToken cancellationToken)
    {
        // Capability/preflight failures affect the whole publication. Hydrate
        // the item ledger once instead of issuing one SELECT per item in a
        // 10k-item action; audit entries remain per item for traceability.
        var itemsById = await repository.GetItemsAsync(
            payloadItems.Select(payloadItem => payloadItem.GradingItemId).Distinct().ToArray(),
            cancellationToken);
        foreach (var payloadItem in payloadItems)
        {
            if (!itemsById.TryGetValue(payloadItem.GradingItemId, out var item))
            {
                failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, "Item de correcao nao encontrado."));
                continue;
            }

            if (item.CommitStatus == GradingCommitStatus.Succeeded)
            {
                continue;
            }

            item.MarkCommitFailed(message, errorCode);
            failures.Add(new GradingLaunchFailure(payloadItem.GradingItemId, message));
            await RecordCommitAuditAsync(
                action,
                batchJobId,
                payloadItem,
                status,
                responseSummary: new { item.CommitStatus },
                errorCode,
                message,
                cancellationToken);
        }

        await repository.SaveChangesAsync(cancellationToken);
        await auditLogs.SaveChangesAsync(cancellationToken);
    }

    private Task RecordCommitAuditAsync(
        PendingMoodleAction action,
        Guid batchJobId,
        GradingLaunchPayloadItem payloadItem,
        string status,
        object responseSummary,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        return auditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = action.CorrelationId,
            BatchJobId = batchJobId,
            ToolName = CommitToolName,
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            ActorSubject = action.CreatedBySubject,
            ActorEmail = action.CreatedByEmail,
            ActorMoodleUserId = action.CreatedByMoodleUserId,
            CourseId = action.CourseId,
            MoodleFunction = IndividualGradeWriteFunction,
            RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                batchJobId,
                payloadItem.GradingItemId,
                payloadItem.CourseId,
                payloadItem.AssignmentId,
                payloadItem.StudentId,
                payloadItem.Grade,
                payloadItem.FeedbackText,
                payloadItem.AttemptNumber
            }),
            ResponseSummaryJson = AuditPayloadSanitizer.SerializeSanitized(responseSummary),
            Status = status,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        }, cancellationToken);
    }

    private sealed record ExistingGradeValidationResult(
        AssignmentExistingGrade? ExistingGrade,
        CapabilityValidationFailure? Failure);

    private sealed record GradeWriteCapabilitySelection(
        bool UseBatch,
        bool IndividualAvailable,
        CapabilityValidationFailure? Failure);

    private sealed record PreparedBatchGradeWrite(
        GradingLaunchPayloadItem PayloadItem,
        AssistedGradingItem Item,
        decimal? GradeToWrite,
        bool PreserveGrade);

    private enum UnknownWriteReconciliationStatus
    {
        Applied,
        NotApplied,
        Inconclusive
    }

    private sealed record UnknownWriteReconciliationResult(
        UnknownWriteReconciliationStatus Status,
        string Message);

    private sealed record SubmissionAttemptValidationResult(
        AssignmentSubmissionAttemptStatus? CurrentStatus,
        CapabilityValidationFailure? Failure);

    private sealed record CapabilityValidationFailure(string Message, string ErrorCode);
}

/// <summary>
/// Reads submission attempt/status data once per assignment (the concrete
/// gateway uses mod_assign_get_submissions, capped at Moodle's request limit)
/// instead of issuing one status call per student in a 10k-item run.
/// </summary>
internal sealed record GradingBulkSubmissionStatusReadResult(
    IReadOnlyDictionary<(string AssignmentId, string StudentId), AssignmentSubmissionAttemptStatus> Statuses,
    IReadOnlyDictionary<(string AssignmentId, string StudentId), AssignmentSubmissionRecord> Submissions,
    IReadOnlySet<string> FailedAssignments,
    bool Attempted)
{
    public const int PerItemFallbackLimit = 8;

    public bool TryGet(string assignmentId, string studentId, out AssignmentSubmissionAttemptStatus? status)
    {
        if (Statuses.TryGetValue((assignmentId, studentId), out var found))
        {
            status = found;
            return true;
        }

        status = null;
        return false;
    }

    public bool TryGetSubmission(
        string assignmentId,
        string studentId,
        out AssignmentSubmissionRecord? submission)
    {
        if (Submissions.TryGetValue((assignmentId, studentId), out var found))
        {
            submission = found;
            return true;
        }

        submission = null;
        return false;
    }
}

internal static class GradingBulkSubmissionStatusReader
{
    public const int PerItemFallbackLimit = GradingBulkSubmissionStatusReadResult.PerItemFallbackLimit;

    public static async Task<GradingBulkSubmissionStatusReadResult> ReadAsync(
        IMoodleAssignmentSubmissionsGateway? submissionsGateway,
        string userExternalId,
        IReadOnlyCollection<string> assignmentIds,
        CancellationToken cancellationToken)
    {
        if (submissionsGateway is null || assignmentIds.Count == 0)
        {
            return new(
                new Dictionary<(string AssignmentId, string StudentId), AssignmentSubmissionAttemptStatus>(),
                new Dictionary<(string AssignmentId, string StudentId), AssignmentSubmissionRecord>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                false);
        }

        var statuses = new Dictionary<(string AssignmentId, string StudentId), AssignmentSubmissionAttemptStatus>();
        var submissions = new Dictionary<(string AssignmentId, string StudentId), AssignmentSubmissionRecord>();
        var failedAssignments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var batches = await GradingMoodleReadRetry.ExecuteAsync(
                retryCancellationToken => submissionsGateway.GetAssignmentSubmissionsBatchAsync(
                    userExternalId,
                    assignmentIds,
                    status: null,
                    since: null,
                    before: null,
                    retryCancellationToken),
                onRetry: null,
                cancellationToken);
            foreach (var batch in batches)
            {
                if (!string.IsNullOrWhiteSpace(batch.ErrorMessage))
                {
                    failedAssignments.Add(batch.AssignmentId);
                    continue;
                }

                foreach (var submission in batch.Submissions)
                {
                    if (string.IsNullOrWhiteSpace(submission.UserId))
                    {
                        continue;
                    }

                    var key = (batch.AssignmentId, submission.UserId);
                    var candidate = new AssignmentSubmissionAttemptStatus(
                        batch.AssignmentId,
                        submission.UserId,
                        submission.AttemptNumber,
                        submission.Status,
                        HasFeedback: !string.IsNullOrWhiteSpace(submission.CurrentFeedback));
                    // Some Moodle variants return every attempt in the bulk
                    // response. Retain the newest attempt so the preflight
                    // compares the draft with the same submission selected by
                    // the item builder.
                    if (!statuses.TryGetValue(key, out var previous) ||
                        (candidate.AttemptNumber ?? -1) >= (previous.AttemptNumber ?? -1))
                    {
                        statuses[key] = candidate;
                        submissions[key] = submission;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var assignmentId in assignmentIds)
            {
                failedAssignments.Add(assignmentId);
            }
        }

        return new(statuses, submissions, failedAssignments, true);
    }
}
