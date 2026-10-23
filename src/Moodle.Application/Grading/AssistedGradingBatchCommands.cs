using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Auditing;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Application.Submissions;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

public sealed record CreateAssistedGradingBatchCommand(
    string UserExternalId,
    string CourseId,
    IReadOnlyList<string> AssignmentIds,
    IReadOnlyList<string> SubmissionIds,
    int MaxItems,
    bool OnlyAwaitingGrading,
    bool IncludeRubric = true,
    bool IncludeSubmissionFiles = true,
    bool IncludeCourseMaterials = false,
    string? TeacherInstructions = null,
    string Priority = "normal",
    IReadOnlyList<AssignmentSubmissionSummary>? PrefetchedSubmissions = null,
    string? IdempotencyKey = null,
    string? CourseDisplayName = null,
    string? MoodleConnectionId = null,
    Guid? GradingRunId = null,
    bool AllowRegradeExisting = false,
    bool IncludeAlreadyGraded = false) : IRequest<CreateAssistedGradingBatchResult>;

public sealed record AssistedGradingBatchDiscoveryFailure(
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("errorCode")] string ErrorCode,
    [property: JsonPropertyName("auditId")] string AuditId,
    [property: JsonPropertyName("moodleFunction")] string? MoodleFunction,
    [property: JsonPropertyName("durationMs")] long? DurationMs);

public sealed record CreateAssistedGradingBatchResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("assignmentIds")] IReadOnlyList<string> AssignmentIds,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("acceptedItems")] int AcceptedItems,
    [property: JsonPropertyName("blockedItems")] int BlockedItems,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("discoveryFailures")] IReadOnlyList<AssistedGradingBatchDiscoveryFailure>? DiscoveryFailures = null)
{
    [JsonPropertyName("existingCorrections")]
    public IReadOnlyList<ExistingGradingCorrectionReference> ExistingCorrections { get; init; } = [];
}

public sealed record GetAssistedGradingBatchStatusQuery(
    Guid BatchJobId,
    int Page,
    int PageSize) : IRequest<AssistedGradingBatchStatusResult>;

public sealed record CancelAssistedGradingBatchCommand(
    Guid BatchJobId,
    bool PurgeLocalData = false,
    string? ConfirmationText = null,
    string? Reason = null) : IRequest<CancelAssistedGradingBatchResult>;

public sealed record CancelAssistedGradingBatchResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("purgeRequested")] bool PurgeRequested = false,
    [property: JsonPropertyName("purged")] bool Purged = false,
    [property: JsonPropertyName("deletedItems")] int DeletedItems = 0,
    [property: JsonPropertyName("deletedBatches")] int DeletedBatches = 0,
    [property: JsonPropertyName("deletedRuns")] int DeletedRuns = 0,
    [property: JsonPropertyName("publishedItemsPreserved")] int PublishedItemsPreserved = 0,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string>? Warnings = null);

public sealed record AssistedGradingBatchStatusResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("processedItems")] int ProcessedItems,
    [property: JsonPropertyName("readyItems")] int ReadyItems,
    [property: JsonPropertyName("blockedItems")] int BlockedItems,
    [property: JsonPropertyName("failedItems")] int FailedItems,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("items")] IReadOnlyList<AssistedGradingBatchStatusItem> Items,
    [property: JsonPropertyName("nextReadyItems")] IReadOnlyList<AssistedGradingBatchStatusItem> NextReadyItems,
    [property: JsonPropertyName("errorsByCategory")] IReadOnlyDictionary<string, int> ErrorsByCategory,
    [property: JsonPropertyName("processingMetrics")] GradingBatchProcessingMetrics ProcessingMetrics,
    [property: JsonPropertyName("publicationActions")] IReadOnlyList<GradingPublicationActionReference>? PublicationActions = null,
    [property: JsonPropertyName("asOfUtc")] DateTimeOffset? AsOfUtc = null,
    [property: JsonPropertyName("nextAction")] string NextAction = "review",
    [property: JsonPropertyName("suggestedAction")] string? SuggestedAction = null,
    [property: JsonPropertyName("suggestedActionLimitation")] string? SuggestedActionLimitation = null);

public sealed record GradingPublicationActionReference(
    [property: JsonPropertyName("pendingActionId")] Guid PendingActionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("confirmedAt")] DateTimeOffset? ConfirmedAt,
    [property: JsonPropertyName("attemptCount")] int AttemptCount,
    [property: JsonPropertyName("nextAttemptAt")] DateTimeOffset? NextAttemptAt,
    [property: JsonPropertyName("lastErrorCode")] string? LastErrorCode);

public sealed record GradingBatchProcessingMetrics(
    [property: JsonPropertyName("progressPercent")] int ProgressPercent,
    [property: JsonPropertyName("readyPercent")] int ReadyPercent,
    [property: JsonPropertyName("blockedPercent")] int BlockedPercent,
    [property: JsonPropertyName("failedPercent")] int FailedPercent,
    [property: JsonPropertyName("pendingItems")] int PendingItems,
    [property: JsonPropertyName("canLaunch")] bool CanLaunch);

public sealed record AssistedGradingBatchStatusItem(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("submissionId")] string? SubmissionId,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus,
    [property: JsonPropertyName("commitStatus")] string CommitStatus,
    [property: JsonPropertyName("studentName")] string? StudentName = null,
    [property: JsonPropertyName("grade")] decimal? Grade = null,
    [property: JsonPropertyName("feedbackText")] string? FeedbackText = null,
    [property: JsonPropertyName("failureReason")] string? FailureReason = null,
    [property: JsonPropertyName("publicationState")] string? PublicationState = null,
    [property: JsonPropertyName("processingState")] string ProcessingState = "unknown",
    [property: JsonPropertyName("contextVersion")] int? ContextVersion = null,
    [property: JsonPropertyName("contextHash")] string? ContextHash = null,
    [property: JsonPropertyName("draftVersionHash")] string? DraftVersionHash = null,
    [property: JsonPropertyName("lastErrorCode")] string? LastErrorCode = null,
    [property: JsonPropertyName("lastErrorPhase")] string? LastErrorPhase = null);

public sealed record GetAssistedGradingCoordinationReportQuery(
    Guid BatchJobId) : IRequest<AssistedGradingCoordinationReportResult>;

public sealed record AssistedGradingCoordinationReportResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("assignmentIds")] IReadOnlyList<string> AssignmentIds,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("processedItems")] int ProcessedItems,
    [property: JsonPropertyName("readyItems")] int ReadyItems,
    [property: JsonPropertyName("blockedItems")] int BlockedItems,
    [property: JsonPropertyName("failedItems")] int FailedItems,
    [property: JsonPropertyName("executionUnknownItems")] int ExecutionUnknownItems,
    [property: JsonPropertyName("reviewedItems")] int ReviewedItems,
    [property: JsonPropertyName("pendingReviewItems")] int PendingReviewItems,
    [property: JsonPropertyName("committedItems")] int CommittedItems,
    [property: JsonPropertyName("launchPendingItems")] int LaunchPendingItems,
    [property: JsonPropertyName("lowConfidenceItems")] int LowConfidenceItems,
    [property: JsonPropertyName("averageConfidence")] decimal? AverageConfidence,
    [property: JsonPropertyName("averageSuggestedGrade")] decimal? AverageSuggestedGrade,
    [property: JsonPropertyName("averageFinalGrade")] decimal? AverageFinalGrade,
    [property: JsonPropertyName("statusCounts")] IReadOnlyDictionary<string, int> StatusCounts,
    [property: JsonPropertyName("reviewStatusCounts")] IReadOnlyDictionary<string, int> ReviewStatusCounts,
    [property: JsonPropertyName("commitStatusCounts")] IReadOnlyDictionary<string, int> CommitStatusCounts,
    [property: JsonPropertyName("attentionItems")] IReadOnlyList<AssistedGradingCoordinationAttentionItem> AttentionItems,
    [property: JsonPropertyName("criteriaNeedingReview")] IReadOnlyList<AssistedGradingCoordinationCriterionSummary> CriteriaNeedingReview,
    [property: JsonPropertyName("reportMarkdown")] string ReportMarkdown);

public sealed record AssistedGradingCoordinationAttentionItem(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("submissionId")] string? SubmissionId,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus,
    [property: JsonPropertyName("commitStatus")] string CommitStatus,
    [property: JsonPropertyName("confidence")] decimal? Confidence,
    [property: JsonPropertyName("suggestedGrade")] decimal? SuggestedGrade,
    [property: JsonPropertyName("finalGrade")] decimal? FinalGrade,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record AssistedGradingCoordinationCriterionSummary(
    [property: JsonPropertyName("criterionId")] string? CriterionId,
    [property: JsonPropertyName("criterionText")] string CriterionText,
    [property: JsonPropertyName("itemCount")] int ItemCount,
    [property: JsonPropertyName("teacherReviewRequiredItems")] int TeacherReviewRequiredItems,
    [property: JsonPropertyName("itemsWithGaps")] int ItemsWithGaps,
    [property: JsonPropertyName("averageSuggestedPoints")] decimal? AverageSuggestedPoints,
    [property: JsonPropertyName("averageMaxPoints")] decimal? AverageMaxPoints);

public sealed record GetAssistedGradingItemQuery(
    Guid GradingItemId,
    Guid? BatchJobId = null) : IRequest<AssistedGradingItemDetailResult>;

public sealed record AssistedGradingItemDetailResult(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("submissionId")] string? SubmissionId,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("attemptNumber")] int? AttemptNumber,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("suggestedGrade")] decimal? SuggestedGrade,
    [property: JsonPropertyName("finalGrade")] decimal? FinalGrade,
    [property: JsonPropertyName("confidence")] decimal? Confidence,
    [property: JsonPropertyName("draftFeedback")] string? DraftFeedback,
    [property: JsonPropertyName("finalFeedback")] string? FinalFeedback,
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus,
    [property: JsonPropertyName("commitStatus")] string CommitStatus,
    [property: JsonPropertyName("teacherDecision")] string? TeacherDecision,
    [property: JsonPropertyName("reviewNotes")] string? ReviewNotes,
    [property: JsonPropertyName("draftVersionHash")] string DraftVersionHash,
    [property: JsonPropertyName("pendingIssues")] IReadOnlyList<string> PendingIssues,
    [property: JsonPropertyName("evidence")] IReadOnlyList<AssistedGradingEvidenceResult> Evidence,
    [property: JsonPropertyName("privateNotesToTeacher")] string? PrivateNotesToTeacher = null,
    [property: JsonPropertyName("scoreNormalization")] GradingScoreNormalizationSnapshot? ScoreNormalization = null);

public sealed record AssistedGradingEvidenceResult(
    [property: JsonPropertyName("criterionId")] string? CriterionId,
    [property: JsonPropertyName("criterionText")] string CriterionText,
    [property: JsonPropertyName("maxPoints")] decimal? MaxPoints,
    [property: JsonPropertyName("suggestedPoints")] decimal? SuggestedPoints,
    [property: JsonPropertyName("evidenceText")] string? EvidenceText,
    [property: JsonPropertyName("gapsText")] string? GapsText,
    [property: JsonPropertyName("teacherReviewRequired")] bool TeacherReviewRequired);

internal static class GradingProposalNormalizationReader
{
    public static async Task<GradingScoreNormalizationSnapshot?> ReadAsync(
        IGradingProposalStore? proposalStore,
        Guid gradingItemId,
        CancellationToken cancellationToken)
    {
        if (proposalStore is null)
        {
            return null;
        }

        var document = await proposalStore.GetLatestDocumentAsync(gradingItemId, cancellationToken);
        if (document is null || string.IsNullOrWhiteSpace(document.PayloadJson))
        {
            return null;
        }

        try
        {
            using var payload = JsonDocument.Parse(document.PayloadJson);
            return payload.RootElement.ValueKind == JsonValueKind.Object &&
                payload.RootElement.TryGetProperty("ScoreNormalization", out var normalization) &&
                normalization.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<GradingScoreNormalizationSnapshot>(normalization.GetRawText())
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record UpdateAssistedGradingDraftCommand(
    Guid GradingItemId,
    decimal? FinalGrade,
    string FinalFeedback,
    string TeacherDecision,
    string? ReviewNotes,
    string ExpectedReviewStatus,
    string? ExpectedDraftVersionHash = null) : IRequest<AssistedGradingItemDetailResult>;

public sealed class GradingDraftVersionConflictException : InvalidOperationException
{
    public GradingDraftVersionConflictException(Exception? innerException = null)
        : base("O rascunho foi alterado desde a ultima leitura. Consulte o item novamente antes de sobrescrever.", innerException)
    {
    }
}

public sealed class CreateAssistedGradingBatchCommandHandler(
    IGradingReviewRepository repository,
    IMediator mediator,
    ICurrentUserContext currentUser,
    IMoodleUserResolver moodleUserResolver,
    IMoodleAuditLogRepository auditLogs,
    IGradingBatchOrchestrator orchestrator,
    IMoodleCourseContentsGateway contentsGateway,
    IMoodleAssignmentSubmissionsGateway submissionsGateway,
    IOptions<GradingLimitsOptions>? limits = null,
    IConnectorExecutionContext? executionContext = null,
    IMoodleConnectionSelection? connectionSelection = null,
    IMoodleCredentialsProvider? credentialsProvider = null,
    IMoodleAssignmentSettingsGateway? settingsGateway = null,
    IMoodleAssignmentGradeReadGateway? gradeReadGateway = null)
    : IRequestHandler<CreateAssistedGradingBatchCommand, CreateAssistedGradingBatchResult>
{
    private readonly GradingLimitsOptions _limits = limits?.Value ?? new GradingLimitsOptions();

    public async Task<CreateAssistedGradingBatchResult> Handle(
        CreateAssistedGradingBatchCommand request,
        CancellationToken cancellationToken)
    {
        _ = mediator; // Mantém compatibilidade de injeção durante a transição do agregador para a leitura direta.
        if (string.IsNullOrWhiteSpace(request.UserExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", nameof(request.UserExternalId));
        }

        if (string.IsNullOrWhiteSpace(request.CourseId))
        {
            throw new ArgumentException("O curso e obrigatorio.", nameof(request.CourseId));
        }

        var assignmentIds = request.AssignmentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (assignmentIds.Length == 0)
        {
            throw new ArgumentException("Informe pelo menos uma tarefa.", nameof(request.AssignmentIds));
        }

        if (request.PrefetchedSubmissions is not null && assignmentIds.Length != 1)
        {
            throw new ArgumentException(
                "Entregas pre-carregadas exigem exatamente uma tarefa no lote.",
                nameof(request.AssignmentIds));
        }

        var normalizedIdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? null
            : request.IdempotencyKey.Trim();
        if (normalizedIdempotencyKey is not null)
        {
            var existingBatch = await repository.GetBatchByIdempotencyKeyAsync(
                currentUser.Subject,
                normalizedIdempotencyKey,
                cancellationToken);
            if (existingBatch is not null)
            {
                return new CreateAssistedGradingBatchResult(
                    existingBatch.Id,
                    existingBatch.CourseId.ToString(CultureInfo.InvariantCulture),
                    existingBatch.AssignmentIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
                    existingBatch.TotalItems,
                    existingBatch.TotalItems,
                    BlockedItems: existingBatch.BlockedItems,
                    Status: "IdempotentReplay",
                    Warnings: ["Uma solicitacao anterior com a mesma chave ja criou este lote."]);
            }
        }

        var safeMaxItems = Math.Clamp(request.MaxItems, 1, 400);
        var effectiveIncludeAlreadyGraded = request.AllowRegradeExisting || request.IncludeAlreadyGraded;
        var selectedSubmissionIds = request.SubmissionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedItems = new List<AssistedGradingItemSeed>();
        var selectedSubmissionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? resolvedCourseId = null;
        var warnings = new List<string>();
        var discoveryFailures = new List<AssistedGradingBatchDiscoveryFailure>();
        Exception? firstDiscoveryFailure = null;

        if (request.PrefetchedSubmissions is not null)
        {
            foreach (var submission in request.PrefetchedSubmissions)
            {
                if (selectedItems.Count >= safeMaxItems ||
                    (selectedSubmissionIds.Count > 0 &&
                     (submission.SubmissionId is null || !selectedSubmissionIds.Contains(submission.SubmissionId))) ||
                    (effectiveIncludeAlreadyGraded
                        ? !IsSubmittedForRegrade(submission)
                        : request.OnlyAwaitingGrading && !submission.NeedsGrading))
                {
                    continue;
                }

                if (!selectedSubmissionKeys.Add(BuildSubmissionSelectionKey(assignmentIds[0], submission)))
                {
                    continue;
                }

                selectedItems.Add(new AssistedGradingItemSeed(
                    request.CourseId,
                    assignmentIds[0],
                    submission.SubmissionId,
                    submission.UserId,
                    submission.AttemptNumber,
                    submission.FileCount,
                    AreSubmissionFilesComplete(submission.FileCount, submission.Files),
                    submission.Files ?? [],
                    submission.FullName));
                resolvedCourseId ??= request.CourseId;
            }
        }
        else
        {
            IReadOnlyList<AssignmentSubmissionsBatch> submissionBatches;
            try
            {
                // A criação só precisa da seleção de submissões. Consultar o
                // agregador de telas aqui refazia curso, conteúdo, alunos,
                // settings e notas antes de chegar a mod_assign_get_submissions.
                // O gateway usa exatamente a função Moodle necessária e mantém
                // uma falha por atividade sem converter-a em lote vazio.
                submissionBatches = await submissionsGateway.GetAssignmentSubmissionsBatchAsync(
                    request.UserExternalId,
                    assignmentIds,
                    effectiveIncludeAlreadyGraded ? null : request.OnlyAwaitingGrading ? "submitted" : null,
                    since: null,
                    before: null,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failure = MoodleErrorContract.Describe(ex);
                foreach (var assignmentId in assignmentIds)
                {
                    AddDiscoveryFailure(assignmentId, ex, failure, discoveryFailures, warnings);
                }

                firstDiscoveryFailure = ex;
                submissionBatches = [];
            }

            var batchesByAssignment = submissionBatches
                .GroupBy(batch => batch.AssignmentId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var effectiveAssignmentIds = assignmentIds.ToDictionary(
                assignmentId => assignmentId,
                assignmentId => assignmentId,
                StringComparer.OrdinalIgnoreCase);

            // mod_assign_get_submissions only accepts the assignment instance
            // ID, while Moodle users commonly copy the course-module ID (cmid).
            // Resolve only the IDs explicitly rejected as unknown, preserving
            // the fast direct path for the instance IDs returned by listings.
            var moduleIdsToResolve = assignmentIds
                .Where(assignmentId =>
                    batchesByAssignment.TryGetValue(assignmentId, out var batch) &&
                    string.Equals(batch.ErrorCode, "assignment_not_found", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (moduleIdsToResolve.Length > 0)
            {
                try
                {
                    var contents = await contentsGateway.GetCourseContentsAsync(
                        request.UserExternalId,
                        request.CourseId,
                        CourseActivityModuleTypes.Assignments,
                        includeHidden: true,
                        onlyWithFiles: false,
                        cancellationToken);
                    var instanceByModuleId = contents.Sections
                        .SelectMany(section => section.Modules)
                        .Where(module =>
                            string.Equals(module.ModuleType, "assign", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(module.ModuleId) &&
                            !string.IsNullOrWhiteSpace(module.InstanceId))
                        .GroupBy(module => module.ModuleId!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First().InstanceId!, StringComparer.OrdinalIgnoreCase);
                    var resolvedPairs = moduleIdsToResolve
                        .Where(moduleId => instanceByModuleId.TryGetValue(moduleId, out var instanceId) &&
                                           !string.Equals(moduleId, instanceId, StringComparison.OrdinalIgnoreCase))
                        .Select(moduleId => new KeyValuePair<string, string>(moduleId, instanceByModuleId[moduleId]))
                        .ToArray();
                    if (resolvedPairs.Length > 0)
                    {
                        var resolvedBatches = await submissionsGateway.GetAssignmentSubmissionsBatchAsync(
                            request.UserExternalId,
                            resolvedPairs.Select(pair => pair.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                            effectiveIncludeAlreadyGraded ? null : request.OnlyAwaitingGrading ? "submitted" : null,
                            since: null,
                            before: null,
                            cancellationToken);
                        var resolvedByInstance = resolvedBatches
                            .GroupBy(batch => batch.AssignmentId, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
                        foreach (var pair in resolvedPairs)
                        {
                            if (resolvedByInstance.TryGetValue(pair.Value, out var resolvedBatch))
                            {
                                batchesByAssignment[pair.Key] = resolvedBatch;
                                effectiveAssignmentIds[pair.Key] = pair.Value;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Keep the original structured not-found result. A module
                    // lookup is a compatibility fallback and must not mask
                    // unrelated Moodle failures from the direct read.
                }
            }
            var noGradeAssignments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var feedbackByAssignment = new Dictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>>(StringComparer.OrdinalIgnoreCase);
            var feedbackReadFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (request.OnlyAwaitingGrading && settingsGateway is not null)
            {
                try
                {
                    var settings = await settingsGateway.GetCourseAssignmentSettingsAsync(
                        request.UserExternalId,
                        request.CourseId,
                        cancellationToken);
                    foreach (var assignmentId in assignmentIds)
                    {
                        if (settings.TryGetValue(assignmentId, out var assignmentSettings) &&
                            assignmentSettings.IsGradable == false)
                        {
                            noGradeAssignments.Add(assignmentId);
                        }
                    }
                }
                catch
                {
                    // Without authoritative configuration, preserve the
                    // existing grading-status behavior for numeric tasks.
                }
            }

            if (noGradeAssignments.Count > 0 && gradeReadGateway is not null)
            {
                using var feedbackLimiter = new SemaphoreSlim(4, 4);
                var studentIds = submissionBatches
                    .SelectMany(batch => batch.Submissions)
                    .Select(submission => submission.UserId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var feedbackTasks = noGradeAssignments.Select(async assignmentId =>
                {
                    await feedbackLimiter.WaitAsync(cancellationToken);
                    try
                    {
                        var grades = await gradeReadGateway.GetExistingGradesAsync(
                            request.UserExternalId,
                            effectiveAssignmentIds.GetValueOrDefault(assignmentId, assignmentId),
                            studentIds,
                            cancellationToken);
                        return (AssignmentId: assignmentId, Grades: grades, Success: true);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        return (AssignmentId: assignmentId,
                            Grades: (IReadOnlyDictionary<string, AssignmentExistingGrade>)new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase),
                            Success: false);
                    }
                    finally
                    {
                        feedbackLimiter.Release();
                    }
                }).ToArray();
                foreach (var feedback in await Task.WhenAll(feedbackTasks))
                {
                    if (feedback.Success)
                    {
                        feedbackByAssignment[feedback.AssignmentId] = feedback.Grades;
                    }
                    else
                    {
                        feedbackReadFailures.Add(feedback.AssignmentId);
                    }
                }
            }
            else
            {
                feedbackReadFailures.UnionWith(noGradeAssignments);
            }

            if (feedbackReadFailures.Count > 0)
            {
                warnings.Add($"Não foi possível verificar notas ou feedback em {feedbackReadFailures.Count} atividade(s); itens sem evidência conclusiva não foram incluídos na seleção.");
            }

            foreach (var assignmentId in assignmentIds)
            {
                if (!batchesByAssignment.TryGetValue(assignmentId, out var submissionBatch))
                {
                    var missing = new MoodleApiException(
                        MoodleErrorContract.ApiError,
                        "Moodle did not return this assignment in the submissions response.",
                        functionName: "mod_assign_get_submissions");
                    AddDiscoveryFailure(assignmentId, missing, MoodleErrorContract.Describe(missing), discoveryFailures, warnings);
                    firstDiscoveryFailure ??= missing;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(submissionBatch.ErrorCode))
                {
                    var failed = new MoodleApiException(
                        submissionBatch.ErrorCode,
                        submissionBatch.ErrorMessage ?? MoodleErrorContract.SafeMessage(submissionBatch.ErrorCode),
                        functionName: "mod_assign_get_submissions");
                    AddDiscoveryFailure(assignmentId, failed, MoodleErrorContract.Describe(failed), discoveryFailures, warnings);
                    firstDiscoveryFailure ??= failed;
                    continue;
                }

                var effectiveAssignmentId = effectiveAssignmentIds[assignmentId];
                resolvedCourseId ??= request.CourseId;
                foreach (var submission in submissionBatch.Submissions)
                {
                    if (selectedItems.Count >= safeMaxItems ||
                        (selectedSubmissionIds.Count > 0 && !selectedSubmissionIds.Contains(submission.SubmissionId)) ||
                        (effectiveIncludeAlreadyGraded
                            ? !IsSubmittedForRegrade(submission)
                            : request.OnlyAwaitingGrading && !NeedsGrading(
                                effectiveAssignmentIds[assignmentId],
                                submission,
                                noGradeAssignments,
                                feedbackByAssignment)))
                    {
                        continue;
                    }

                    if (!selectedSubmissionKeys.Add(BuildSubmissionSelectionKey(effectiveAssignmentId, submission)))
                    {
                        continue;
                    }

                    selectedItems.Add(new AssistedGradingItemSeed(
                        request.CourseId,
                        effectiveAssignmentId,
                        submission.SubmissionId,
                        submission.UserId,
                        submission.AttemptNumber,
                        submission.FileCount,
                        AreSubmissionFilesComplete(submission.FileCount, submission.Files),
                        submission.Files ?? []));
                }
            }
        }

        if (selectedItems.Count == 0)
        {
            if (discoveryFailures.Count > 0)
            {
                // Preserva o MoodleApiException (e consequentemente auditId,
                // funcao e duracao) para que a camada MCP responda `error`,
                // em vez de afirmar que nao ha pendencias.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstDiscoveryFailure!).Throw();
            }

            warnings.Add("Nenhuma entrega elegivel para correcao foi encontrada nas tarefas informadas.");
            return new CreateAssistedGradingBatchResult(
                Guid.Empty,
                resolvedCourseId ?? request.CourseId,
                assignmentIds,
                TotalItems: 0,
                AcceptedItems: 0,
                BlockedItems: 0,
                Status: "NoPendingSubmissions",
                Warnings: warnings,
                DiscoveryFailures: []);
        }

        if (selectedSubmissionIds.Count > 0)
        {
            var selectedIds = selectedItems
                .Select(item => item.SubmissionId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingIds = selectedSubmissionIds
                .Where(id => !selectedIds.Contains(id))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingIds.Length > 0 && selectedItems.Count < safeMaxItems)
            {
                warnings.Add($"As submissões solicitadas não foram encontradas como elegíveis: {string.Join(", ", missingIds)}.");
            }
        }

        var courseId = ParsePositiveLong(resolvedCourseId ?? request.CourseId, "courseId");
        var assignmentIdsAsLong = selectedItems.Count > 0
            ? selectedItems.Select(item => ParsePositiveLong(item.AssignmentId, "assignmentId")).Distinct().ToArray()
            : assignmentIds.Select(id => ParsePositiveLong(id, "assignmentId")).Distinct().ToArray();
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);

        // O worker de correcao executa sem HttpContext. Preserve a conexao que
        // foi resolvida para esta requisicao para que ele recupere os mesmos
        // anexos e materiais Moodle, em vez de cair em uma conexao padrao ou
        // perder a identidade do cliente.
        var connectorClientId = executionContext?.ClientId;
        var connectionAlias = connectionSelection?.Alias;
        var moodleConnectionId = request.MoodleConnectionId;
        if (credentialsProvider is not null)
        {
            var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
            connectorClientId = credentials.ClientId;
            connectionAlias = credentials.Alias;
            moodleConnectionId = credentials.ConnectionId;
        }

        // A mesma submissão pode aparecer novamente quando o professor repete
        // a solicitação, quando dois usuários disparam o curso ao mesmo tempo
        // ou quando um worker perde a resposta. No fluxo normal, reaproveite o
        // item já criado antes de abrir outro lote. Uma reavaliação explícita
        // é a exceção controlada: ela cria um novo item para preservar a
        // correção anterior e deixa a prévia de publicação comparar/sobrescrever
        // a nota antiga somente após autorização específica.
        var existingSubmissionIdentities = selectedItems
            .Where(seed => !string.IsNullOrWhiteSpace(seed.SubmissionId))
            .Select(seed => new GradingSubmissionIdentity(
                courseId,
                ParsePositiveLong(seed.AssignmentId, "assignmentId"),
                ParsePositiveLong(seed.SubmissionId!, "submissionId"),
                seed.AttemptNumber))
            .Distinct()
            .ToArray();
        var existingCorrections = new List<ExistingGradingCorrectionReference>();
        if (existingSubmissionIdentities.Length > 0 && !effectiveIncludeAlreadyGraded)
        {
            var existingMatches = await repository.ListExistingSubmissionMatchesAsync(
                existingSubmissionIdentities,
                moodleConnectionId,
                connectorClientId,
                connectionAlias,
                cancellationToken);
            var alreadyQueued = existingMatches.Count > 0
                ? existingMatches.Select(match => match.Identity).Distinct().ToArray()
                : await repository.ListExistingSubmissionIdentitiesAsync(
                existingSubmissionIdentities,
                moodleConnectionId,
                connectorClientId,
                connectionAlias,
                cancellationToken);
            existingCorrections.AddRange(existingMatches
                .Where(match => string.Equals(match.CreatedBySubject, currentUser.Subject, StringComparison.Ordinal))
                .Select(FindGradingCorrectionBySubmissionQueryHandler.ToReference)
                .DistinctBy(reference => reference.GradingItemId));
            foreach (var correction in existingCorrections)
            {
                warnings.Add(
                    $"A submissao {correction.SubmissionId} ja pertence a uma correcao anterior (batchJobId {correction.BatchJobId}, gradingRunId {correction.GradingRunId?.ToString() ?? "n/d"}). Use find_grading_correction_by_submission ou cancele esse lote antes de iniciar outro.");
            }
            if (alreadyQueued.Count > 0)
            {
                var alreadyQueuedSet = alreadyQueued.ToHashSet();
                var beforeFilterCount = selectedItems.Count;
                selectedItems.RemoveAll(seed =>
                {
                    if (string.IsNullOrWhiteSpace(seed.SubmissionId)) return false;
                    var identity = new GradingSubmissionIdentity(
                        courseId,
                        ParsePositiveLong(seed.AssignmentId, "assignmentId"),
                        ParsePositiveLong(seed.SubmissionId!, "submissionId"),
                        seed.AttemptNumber);
                    return alreadyQueuedSet.Contains(identity);
                });
                var skippedCount = beforeFilterCount - selectedItems.Count;
                if (skippedCount > 0)
                {
                    warnings.Add($"{skippedCount} entrega(s) já estavam em correção ou concluídas e foram ignoradas para evitar duplicidade.");
                }
            }
        }

        if (selectedItems.Count == 0)
        {
            return new CreateAssistedGradingBatchResult(
                Guid.Empty,
                courseId.ToString(CultureInfo.InvariantCulture),
                assignmentIds,
                TotalItems: 0,
                AcceptedItems: 0,
                BlockedItems: 0,
                Status: "AlreadyQueued",
                Warnings: warnings,
                DiscoveryFailures: discoveryFailures)
            {
                ExistingCorrections = existingCorrections,
            };
        }

        if (!request.IncludeSubmissionFiles)
        {
            warnings.Add(
                "Arquivos das entregas foram omitidos (includeSubmissionFiles=false). Não atribua nota nem feedback conclusivo quando dependerem de anexos; confirme que há evidência suficiente da submissão.");
        }

        var batch = AssistedGradingBatch.Create(
            courseId,
            assignmentIdsAsLong,
            currentUser.Subject,
            moodleUserId,
            selectedItems.Count,
            teacherInstructions: request.TeacherInstructions,
            priority: request.Priority,
            includeRubric: request.IncludeRubric,
            includeSubmissionFiles: request.IncludeSubmissionFiles,
            includeCourseMaterials: request.IncludeCourseMaterials,
            connectorClientId: connectorClientId,
            connectionAlias: connectionAlias,
            idempotencyKey: normalizedIdempotencyKey,
            courseDisplayName: request.CourseDisplayName,
            moodleConnectionId: moodleConnectionId,
            gradingRunId: request.GradingRunId);

        await repository.AddBatchAsync(batch, cancellationToken);
        var assignmentContextCache = new Dictionary<AssignmentContextCacheKey, IReadOnlyList<ContextArtifactTemplate>>();
        var submissionFilesCache = new Dictionary<string, IReadOnlyList<AssignmentSubmissionRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in selectedItems)
        {
            var item = AssistedGradingItem.Create(
                batch.Id,
                ParsePositiveLong(seed.CourseId, "courseId"),
                ParsePositiveLong(seed.AssignmentId, "assignmentId"),
                ParseNullablePositiveLong(seed.SubmissionId, "submissionId"),
                ParsePositiveLong(seed.StudentId, "studentId"),
                seed.AttemptNumber,
                seed.StudentDisplayName);

            if (item.SubmissionId is long submissionId)
            {
                item.SetIdempotencyKey(effectiveIncludeAlreadyGraded
                    ? BuildRegradeSubmissionIdempotencyKey(
                        batch.Id,
                        item.CourseId,
                        item.AssignmentId,
                        submissionId,
                        item.AttemptNumber)
                    : BuildSubmissionIdempotencyKey(
                        connectorClientId,
                        moodleConnectionId,
                        connectionAlias,
                        item.CourseId,
                        item.AssignmentId,
                        submissionId,
                        item.AttemptNumber,
                        request.GradingRunId));
            }

            await repository.AddItemAsync(item, cancellationToken);
            if (request.IncludeSubmissionFiles)
            {
                await AddSubmissionFileArtifactsAsync(
                    request.UserExternalId,
                    item.Id,
                    seed.AssignmentId,
                    seed.SubmissionId,
                    seed.StudentId,
                    seed.DeclaredFileCount,
                    seed.FilesComplete,
                    seed.Files,
                    submissionFilesCache,
                    warnings,
                    cancellationToken);
            }

            if ((request.IncludeRubric || request.IncludeCourseMaterials) && !_limits.DeferHeavyIngestion)
            {
                await AddAssignmentContextArtifactsAsync(
                    request.UserExternalId,
                    item,
                    assignmentContextCache,
                    request.IncludeRubric,
                    request.IncludeCourseMaterials,
                    warnings,
                    cancellationToken);
            }
        }

        await repository.SaveChangesAsync(cancellationToken);

        if (selectedItems.Count == safeMaxItems)
        {
            warnings.Add($"Lote limitado aos primeiros {safeMaxItems} item(ns).");
        }

        var createResult = new CreateAssistedGradingBatchResult(
            batch.Id,
            batch.CourseId.ToString(CultureInfo.InvariantCulture),
            batch.AssignmentIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
            batch.TotalItems,
            selectedItems.Count,
            BlockedItems: 0,
            discoveryFailures.Count == 0 ? batch.Status.ToString() : "PartialFailure",
            warnings,
            discoveryFailures);
        createResult = createResult with
        {
            ExistingCorrections = existingCorrections,
        };

        await auditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = $"grading-batch-{batch.Id:N}",
            BatchJobId = batch.Id,
            ToolName = "criar_lote_correcao_assistida",
            RiskLevel = ToolRiskLevel.HumanConfirmedWrite,
            ActorSubject = currentUser.Subject,
            ActorEmail = currentUser.Email,
            ActorMoodleUserId = moodleUserId,
            CourseId = batch.CourseId,
            MoodleFunction = "mod_assign_get_submissions",
            RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                request.CourseId,
                request.AssignmentIds,
                request.SubmissionIds,
                request.MaxItems,
                request.OnlyAwaitingGrading,
                request.AllowRegradeExisting,
                request.IncludeAlreadyGraded,
                effectiveIncludeAlreadyGraded
            }),
            ResponseSummaryJson = AuditPayloadSanitizer.SerializeSanitized(createResult),
            Status = "batch_created"
        }, cancellationToken);
        await auditLogs.SaveChangesAsync(cancellationToken);

        await orchestrator.EnqueueAsync(batch.Id, cancellationToken);

        return createResult;
    }

    private static long ParsePositiveLong(string value, string parameterName)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        throw new ArgumentException($"O parametro {parameterName} deve ser um identificador numerico positivo.", parameterName);
    }

    private static long? ParseNullablePositiveLong(string? value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value) ? null : ParsePositiveLong(value, parameterName);
    }

    private static bool AreSubmissionFilesComplete(
        int fileCount,
        IReadOnlyList<AssignmentSubmissionFile>? files) =>
        files is not null &&
        files.Count == fileCount &&
        files.All(file => !string.IsNullOrWhiteSpace(file.FileUrl));

    private async Task AddSubmissionFileArtifactsAsync(
        string userExternalId,
        Guid gradingItemId,
        string assignmentId,
        string? submissionId,
        string studentId,
        int declaredFileCount,
        bool filesComplete,
        IReadOnlyList<AssignmentSubmissionFile> files,
        Dictionary<string, IReadOnlyList<AssignmentSubmissionRecord>> submissionFilesCache,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var maxFiles = Math.Clamp(_limits.MaxFilesPerSubmission, 0, 100);

        var effectiveFiles = files;
        var seedFilesComplete = filesComplete && AreSubmissionFilesComplete(declaredFileCount, files);

        // A complete empty file list is meaningful. Re-fetch only when the
        // source did not provide a complete list, using one read per assignment.
        if (!seedFilesComplete && !string.IsNullOrWhiteSpace(assignmentId))
        {
            try
            {
                if (!submissionFilesCache.TryGetValue(assignmentId, out var cachedSubmissions))
                {
                    cachedSubmissions = await submissionsGateway.GetAssignmentSubmissionsAsync(
                        userExternalId,
                        assignmentId,
                        "submitted",
                        null,
                        null,
                        cancellationToken);
                    submissionFilesCache[assignmentId] = cachedSubmissions;
                }

                var match = cachedSubmissions.FirstOrDefault(s =>
                    (!string.IsNullOrWhiteSpace(submissionId) &&
                     string.Equals(s.SubmissionId, submissionId, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(s.UserId, studentId, StringComparison.OrdinalIgnoreCase));

                if (match is not null && AreSubmissionFilesComplete(match.FileCount, match.Files))
                {
                    effectiveFiles = match.Files!;
                }
                else
                {
                    effectiveFiles = [];
                    warnings.Add(
                        $"Nao foi possivel validar a lista de anexos da entrega na tarefa {assignmentId}; as referencias incompletas foram omitidas.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                effectiveFiles = [];
                warnings.Add(
                    $"Nao foi possivel validar a lista de anexos da entrega na tarefa {assignmentId}; as referencias incompletas foram omitidas.");
            }
        }

        // A correção assistida não materializa anexos. Persistimos somente a
        // referência autenticável que será convertida em MCP Resource no chat.
        foreach (var file in effectiveFiles.Take(maxFiles))
        {
            var sourceUrl = GradingArtifactSourceReference.Normalize(file.FileUrl);
            await repository.AddArtifactAsync(
                new GradingArtifact(
                    Guid.NewGuid(),
                    gradingItemId,
                    "submission_file",
                    file.Filename,
                    file.MimeType,
                    Sha256: null,
                    file.SizeBytes,
                    sourceUrl is null ? ExtractionStatus.Failed : ExtractionStatus.Pending,
                    ExtractedTextRef: null,
                    SummaryRef: sourceUrl is null ? "source_url_invalid" : "pending_resource",
                    DateTimeOffset.UtcNow,
                    sourceUrl),
                cancellationToken);
        }
    }

    private async Task AddAssignmentContextArtifactsAsync(
        string userExternalId,
        AssistedGradingItem item,
        Dictionary<AssignmentContextCacheKey, IReadOnlyList<ContextArtifactTemplate>> assignmentContextCache,
        bool includeRubric,
        bool includeCourseMaterials,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var cacheKey = new AssignmentContextCacheKey(
            item.CourseId,
            item.AssignmentId,
            includeRubric,
            includeCourseMaterials);
        if (!assignmentContextCache.TryGetValue(cacheKey, out var templates))
        {
            templates = await BuildAssignmentContextTemplatesAsync(
                userExternalId,
                item,
                includeRubric,
                includeCourseMaterials,
                warnings,
                cancellationToken);
            assignmentContextCache[cacheKey] = templates;
        }

        foreach (var template in templates)
        {
            await repository.AddArtifactAsync(template.ToArtifact(item.Id), cancellationToken);
        }
    }

    private async Task<IReadOnlyList<ContextArtifactTemplate>> BuildAssignmentContextTemplatesAsync(
        string userExternalId,
        AssistedGradingItem item,
        bool includeRubric,
        bool includeCourseMaterials,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        CourseContentsSummary contents;
        try
        {
            contents = await GradingMoodleReadRetry.ExecuteAsync(
                retryCancellationToken => contentsGateway.GetCourseContentsAsync(
                    userExternalId,
                    item.CourseId.ToString(CultureInfo.InvariantCulture),
                    moduleTypes: [],
                    includeHidden: true,
                    onlyWithFiles: false,
                    retryCancellationToken),
                (_, attempt) => warnings.Add(
                    $"Falha transitória ao escanear materiais da tarefa {item.AssignmentId}; nova tentativa {attempt}."),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = MoodleErrorContract.Describe(ex);
            warnings.Add($"Nao foi possivel escanear materiais do curso para contexto da tarefa {item.AssignmentId}: {error.ErrorCode}.");
            return [ContextDiagnosticTemplate(item, "context_fetch_failed")];
        }

        var assignmentId = item.AssignmentId.ToString(CultureInfo.InvariantCulture);
        var section = contents.Sections.FirstOrDefault(candidate =>
            candidate.Modules.Any(module => IsAssignmentModule(module, assignmentId)));
        var assignmentModule = section?.Modules.FirstOrDefault(module => IsAssignmentModule(module, assignmentId));
        if (section is null || assignmentModule is null)
        {
            warnings.Add($"A tarefa {item.AssignmentId} nao foi encontrada no conteudo do curso para recuperar contexto (context_assignment_not_found).");
            return [ContextDiagnosticTemplate(item, "context_assignment_not_found")];
        }

        var templates = new List<ContextArtifactTemplate>();
        if (includeRubric && !string.IsNullOrWhiteSpace(assignmentModule.Description))
        {
            templates.Add(new ContextArtifactTemplate(
                "assignment_context",
                assignmentModule.Name,
                "text/html",
                Sha256: null,
                SizeBytes: assignmentModule.Description.Length,
                ExtractionStatus.Succeeded,
                assignmentModule.Description,
                SummaryRef: "assignment_description"));
        }

        if (!includeCourseMaterials)
        {
            // Mesmo quando o tutor não pediu materiais auxiliares, um arquivo
            // cujo nome identifica explicitamente a atividade é parte do
            // enunciado. O Moodle frequentemente guarda esse arquivo como um
            // resource separado do assign.
            foreach (var candidate in AssignmentContextCandidateRanking.Select(
                         contents,
                         section,
                         assignmentModule,
                         maxCandidates: Math.Min(3, Math.Max(1, _limits.MaxFilesPerSubmission)),
                         includeCourseMaterials: false))
            {
                AddContextCandidateTemplate(templates, candidate, warnings);
            }

            if (templates.Count == 0)
            {
                warnings.Add(
                    $"A tarefa {item.AssignmentId} não possui descrição nem arquivo de enunciado com correspondência forte; a correção ficará bloqueada até revisão humana.");
            }

            return templates;
        }

        var selectedCandidates = AssignmentContextCandidateRanking.Select(
                     contents,
                     section,
                     assignmentModule,
                     Math.Max(1, _limits.MaxFilesPerSubmission),
                     includeCourseMaterials: true);
        foreach (var candidate in selectedCandidates)
        {
            AddContextCandidateTemplate(templates, candidate, warnings);
        }

        if (templates.Count == 0)
        {
            warnings.Add(
                $"A tarefa {item.AssignmentId} não possui descrição nem material de contexto identificável no curso; a correção ficará bloqueada até revisão humana.");
        }

        return templates;
    }

    private static void AddContextCandidateTemplate(
        ICollection<ContextArtifactTemplate> templates,
        AssignmentContextCandidateSelection candidate,
        ICollection<string> warnings)
    {
        var summaryRef =
            $"section:{candidate.Section.SectionNumber};distance:{candidate.DistanceFromAssignment};" +
            $"score:{candidate.Score.ToString("0.##", CultureInfo.InvariantCulture)};reason:{candidate.Reason}";

        if (candidate.File is null)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Module.Description))
            {
                templates.Add(new ContextArtifactTemplate(
                    "assignment_context",
                    candidate.Module.Name,
                    "text/html",
                    Sha256: null,
                    SizeBytes: candidate.Module.Description.Length,
                    ExtractionStatus.Succeeded,
                    candidate.Module.Description,
                    SummaryRef: summaryRef));
            }

            return;
        }

        var template = BuildContextFileArtifactTemplate(candidate.File, summaryRef);
        if (template is not null)
        {
            templates.Add(template);
        }
        else
        {
            warnings.Add($"Material de contexto {candidate.Title} ignorado porque não possui URL autenticável.");
        }
    }

    private static ContextArtifactTemplate? BuildContextFileArtifactTemplate(
        CourseModuleFile file,
        string? summaryRef = null)
    {
        var filename = string.IsNullOrWhiteSpace(file.FileName)
            ? "context-file"
            : file.FileName;

        var sourceUrl = GradingArtifactSourceReference.Normalize(file.FileUrl);
        return new ContextArtifactTemplate(
            "assignment_context",
            filename,
            file.MimeType,
            Sha256: null,
            SizeBytes: file.FileSize,
            sourceUrl is null ? ExtractionStatus.Failed : ExtractionStatus.Pending,
            ExtractedTextRef: null,
            SummaryRef: sourceUrl is null ? "source_url_invalid" : summaryRef ?? "pending_resource",
            SourceUrl: sourceUrl);
    }

    private static void AddDiscoveryFailure(
        string assignmentId,
        Exception exception,
        MoodleErrorDescriptor failure,
        ICollection<AssistedGradingBatchDiscoveryFailure> discoveryFailures,
        ICollection<string> warnings)
    {
        discoveryFailures.Add(new AssistedGradingBatchDiscoveryFailure(
            assignmentId,
            failure.ErrorCode,
            failure.AuditId,
            exception is MoodleApiException moodle ? moodle.FunctionName : null,
            exception is MoodleApiException api ? api.DurationMs : null));
        warnings.Add($"Nao foi possivel listar as entregas da tarefa {assignmentId} (codigo: {failure.ErrorCode}; auditId: {failure.AuditId}).");
    }

    private static bool NeedsGrading(AssignmentSubmissionRecord submission) =>
        SubmissionEvaluationStateResolver.NeedsGrading(ResolveEvaluationState(
            submission,
            reviewEvidenceAvailable: IsExplicitlyUnreviewed(submission.GradingStatus)));

    private static bool IsSubmittedForRegrade(AssignmentSubmissionSummary submission)
    {
        return submission.Submitted && IsDeliveredSubmissionStatus(submission.Status);
    }

    private static bool IsSubmittedForRegrade(AssignmentSubmissionRecord submission)
    {
        return IsDeliveredSubmissionStatus(submission.Status);
    }

    private static bool IsDeliveredSubmissionStatus(string? status) =>
        string.Equals(status, "submitted", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "graded", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsGrading(
        string assignmentId,
        AssignmentSubmissionRecord submission,
        ISet<string> noGradeAssignments,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>> feedbackByAssignment)
    {
        var feedbackReadAvailable = feedbackByAssignment.TryGetValue(assignmentId, out var grades);
        AssignmentExistingGrade? existingGrade = null;
        grades?.TryGetValue(submission.UserId, out existingGrade);
        var reviewEvidenceAvailable = noGradeAssignments.Contains(assignmentId)
            ? feedbackReadAvailable
            : IsExplicitlyUnreviewed(submission.GradingStatus);
        return SubmissionEvaluationStateResolver.NeedsGrading(ResolveEvaluationState(
            submission,
            reviewEvidenceAvailable,
            existingGrade));
    }

    private static SubmissionEvaluationState ResolveEvaluationState(
        AssignmentSubmissionRecord submission,
        bool reviewEvidenceAvailable,
        AssignmentExistingGrade? existingGrade = null) =>
        SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
            HasSubmission: ToSubmissionPresence(submission.Status),
            GradeRaw: existingGrade?.HasGrade == true ? existingGrade.Grade : null,
            GradedDateGraded: existingGrade?.TimeModified,
            Feedback: existingGrade?.Feedback ?? submission.CurrentFeedback,
            ReviewEvidenceAvailable: reviewEvidenceAvailable,
            GradingStatus: submission.GradingStatus,
            GraderId: existingGrade?.GraderId ?? submission.CurrentGraderId,
            GradeTimeModified: existingGrade?.TimeModified ?? submission.CurrentGradeTimeModified,
            SubmissionTimeModified: submission.ModifiedAt?.ToUnixTimeSeconds()));

    private static bool IsExplicitlyUnreviewed(string? gradingStatus) =>
        string.Equals(gradingStatus, "notgraded", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(gradingStatus, "needsgrading", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(gradingStatus, "notmarked", StringComparison.OrdinalIgnoreCase);

    private static bool? ToSubmissionPresence(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "submitted" => true,
        "new" or "draft" or "reopened" or "notsubmitted" or "not_submitted" => false,
        _ => null
    };

    private static string BuildSubmissionSelectionKey(
        string assignmentId,
        AssignmentSubmissionSummary submission)
    {
        var normalizedAssignmentId = assignmentId.Trim();
        if (!string.IsNullOrWhiteSpace(submission.SubmissionId))
        {
            return $"submission:{normalizedAssignmentId}:{submission.SubmissionId.Trim()}";
        }

        return $"student:{normalizedAssignmentId}:{submission.UserId.Trim()}:{submission.AttemptNumber?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
    }

    private static string BuildSubmissionSelectionKey(
        string assignmentId,
        AssignmentSubmissionRecord submission)
    {
        var normalizedAssignmentId = assignmentId.Trim();
        if (!string.IsNullOrWhiteSpace(submission.SubmissionId))
        {
            return $"submission:{normalizedAssignmentId}:{submission.SubmissionId.Trim()}";
        }

        return $"student:{normalizedAssignmentId}:{submission.UserId.Trim()}:{submission.AttemptNumber?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
    }

    private static string BuildSubmissionIdempotencyKey(
        string? connectorClientId,
        string? moodleConnectionId,
        string? connectionAlias,
        long courseId,
        long assignmentId,
        long submissionId,
        int? attemptNumber,
        Guid? gradingRunId = null)
    {
        var source = string.Join('|',
            connectorClientId?.Trim() ?? "_",
            moodleConnectionId?.Trim() ?? connectionAlias?.Trim() ?? "_",
            courseId.ToString(CultureInfo.InvariantCulture),
            assignmentId.ToString(CultureInfo.InvariantCulture),
            submissionId.ToString(CultureInfo.InvariantCulture),
            attemptNumber?.ToString(CultureInfo.InvariantCulture) ?? "-");

        // Cancelled historical items remain stored for audit and still carry
        // the legacy identity key. A new grading run must be able to create a
        // fresh item after cancellation without colliding with that unique
        // key, while retries within the same run remain idempotent.
        if (gradingRunId is Guid runId && runId != Guid.Empty)
        {
            source = $"run:{runId:N}|{source}";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static string BuildRegradeSubmissionIdempotencyKey(
        Guid batchId,
        long courseId,
        long assignmentId,
        long submissionId,
        int? attemptNumber)
    {
        var source = string.Join('|',
            "regrade",
            batchId.ToString("N"),
            courseId.ToString(CultureInfo.InvariantCulture),
            assignmentId.ToString(CultureInfo.InvariantCulture),
            submissionId.ToString(CultureInfo.InvariantCulture),
            attemptNumber?.ToString(CultureInfo.InvariantCulture) ?? "_");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static ContextArtifactTemplate ContextDiagnosticTemplate(
        AssistedGradingItem item,
        string reason) =>
        new(
            "assignment_context",
            $"assignment-{item.AssignmentId.ToString(CultureInfo.InvariantCulture)}",
            MimeType: null,
            Sha256: null,
            SizeBytes: null,
            ExtractionStatus.Failed,
            ExtractedTextRef: null,
            SummaryRef: reason);

    private static bool IsAssignmentModule(CourseModuleSummary module, string assignmentId)
    {
        return string.Equals(module.ModuleType, "assign", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(module.InstanceId, assignmentId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(module.ModuleId, assignmentId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsContextCandidateModule(CourseModuleSummary module, CourseModuleSummary assignmentModule)
    {
        if (ReferenceEquals(module, assignmentModule))
        {
            return false;
        }

        if (module.Files.Count > 0)
        {
            return true;
        }

        return module.ModuleType.Equals("resource", StringComparison.OrdinalIgnoreCase) ||
            module.ModuleType.Equals("page", StringComparison.OrdinalIgnoreCase) ||
            module.ModuleType.Equals("label", StringComparison.OrdinalIgnoreCase) ||
            module.ModuleType.Equals("folder", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record AssistedGradingItemSeed(
        string CourseId,
        string AssignmentId,
        string? SubmissionId,
        string StudentId,
        int? AttemptNumber,
        int DeclaredFileCount,
        bool FilesComplete,
        IReadOnlyList<AssignmentSubmissionFile> Files,
        string? StudentDisplayName = null);

    private sealed record AssignmentContextCacheKey(
        long CourseId,
        long AssignmentId,
        bool IncludeRubric,
        bool IncludeCourseMaterials);

    private sealed record ContextArtifactTemplate(
        string ArtifactType,
        string? Filename,
        string? MimeType,
        string? Sha256,
        long? SizeBytes,
        string ExtractionStatus,
        string? ExtractedTextRef,
        string? SummaryRef,
        string? SourceUrl = null)
    {
        public GradingArtifact ToArtifact(Guid gradingItemId)
        {
            return new GradingArtifact(
                Guid.NewGuid(),
                gradingItemId,
                ArtifactType,
                Filename,
                MimeType,
                Sha256,
                SizeBytes,
                ExtractionStatus,
                ExtractedTextRef,
                SummaryRef,
                DateTimeOffset.UtcNow,
                SourceUrl);
        }
    }
}

public sealed class GetAssistedGradingItemQueryHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser,
    IGradingProposalStore? proposalStore = null)
    : IRequestHandler<GetAssistedGradingItemQuery, AssistedGradingItemDetailResult>
{
    public async Task<AssistedGradingItemDetailResult> Handle(
        GetAssistedGradingItemQuery request,
        CancellationToken cancellationToken)
    {
        if (request.GradingItemId == Guid.Empty)
        {
            throw new ArgumentException("O item de correcao e obrigatorio.", nameof(request.GradingItemId));
        }

        var item = await repository.GetItemAsync(request.GradingItemId, cancellationToken)
            ?? throw new InvalidOperationException("Item de correcao nao encontrado.");
        var batch = await repository.GetBatchAsync(item.BatchId, cancellationToken)
            ?? throw new InvalidOperationException("Lote de correcao nao encontrado.");
        GradingAccessControl.EnsureCanAccessBatch(batch, currentUser);

        if (request.BatchJobId is Guid batchJobId && batchJobId != Guid.Empty && item.BatchId != batchJobId)
        {
            throw new InvalidOperationException("O item informado nao pertence ao lote solicitado.");
        }

        var evidence = await repository.ListEvidenceByItemAsync(item.Id, cancellationToken);
        var scoreNormalization = await GradingProposalNormalizationReader.ReadAsync(
            proposalStore,
            item.Id,
            cancellationToken);

        return new AssistedGradingItemDetailResult(
            item.Id,
            item.BatchId,
            item.CourseId.ToString(CultureInfo.InvariantCulture),
            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            item.SubmissionId?.ToString(CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
            item.AttemptNumber,
            item.Status.ToString(),
            item.SuggestedGrade,
            item.FinalGrade,
            item.Confidence,
            item.DraftFeedback,
            item.FinalFeedback,
            item.ReviewStatus.ToString(),
            item.CommitStatus.ToString(),
            item.TeacherDecision,
            item.ReviewNotes,
            GradingDraftVersionHash.Compute(item),
            BuildPendingIssues(item),
            evidence.Select(ToEvidenceResult).ToArray(),
            item.PrivateNotesToTeacher,
            scoreNormalization);
    }

    private static IReadOnlyList<string> BuildPendingIssues(AssistedGradingItem item)
    {
        var pendingIssues = new List<string>();

        if (item.ReviewStatus == GradingReviewStatus.NotReviewed)
        {
            pendingIssues.Add("Revisao humana pendente.");
        }

        if (item.Status == GradingItemStatus.DraftReady && item.Confidence is < 0.5m)
        {
            pendingIssues.Add("Baixa confianca do rascunho assistido; revise criterios, evidencias e nota sugerida antes de aprovar.");
        }

        if (string.IsNullOrWhiteSpace(item.FinalFeedback))
        {
            pendingIssues.Add("Feedback final pendente.");
        }

        if (item.CommitStatus == GradingCommitStatus.Failed && !string.IsNullOrWhiteSpace(item.CommitError))
        {
            pendingIssues.Add($"Falha no lancamento Moodle: {item.CommitError}");
        }

        if (item.Status == GradingItemStatus.Failed &&
            item.CommitStatus != GradingCommitStatus.Failed &&
            !string.IsNullOrWhiteSpace(item.DraftFeedback))
        {
            pendingIssues.Add($"Falha no processamento da correcao assistida: {item.DraftFeedback}");
        }

        return pendingIssues;
    }

    private static AssistedGradingEvidenceResult ToEvidenceResult(GradingEvidence evidence)
    {
        return new AssistedGradingEvidenceResult(
            evidence.CriterionId,
            evidence.CriterionText,
            evidence.MaxPoints,
            evidence.SuggestedPoints,
            evidence.EvidenceText,
            evidence.GapsText,
            evidence.TeacherReviewRequired);
    }
}

public sealed class UpdateAssistedGradingDraftCommandHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser,
    IMoodleUserResolver moodleUserResolver,
    IMoodleAuditLogRepository auditLogs,
    IMoodleAssignmentSettingsGateway settingsGateway,
    IGradingProposalStore? proposalStore = null)
    : IRequestHandler<UpdateAssistedGradingDraftCommand, AssistedGradingItemDetailResult>
{
    public async Task<AssistedGradingItemDetailResult> Handle(
        UpdateAssistedGradingDraftCommand request,
        CancellationToken cancellationToken)
    {
        if (request.GradingItemId == Guid.Empty)
        {
            throw new ArgumentException("O item de correcao e obrigatorio.", nameof(request.GradingItemId));
        }

        var item = await repository.GetItemAsync(request.GradingItemId, cancellationToken)
            ?? throw new InvalidOperationException("Item de correcao nao encontrado.");
        var batch = await repository.GetBatchAsync(item.BatchId, cancellationToken)
            ?? throw new InvalidOperationException("Lote de correcao nao encontrado.");
        GradingAccessControl.EnsureCanAccessBatch(batch, currentUser);

        var contextSnapshot = await repository.ListLatestContextSnapshotsByItemsAsync(
            [item.Id],
            cancellationToken);
        if (!GradingContextIdentity.EnsureVersioned(item, contextSnapshot.GetValueOrDefault(item.Id)))
        {
            throw new InvalidOperationException(
                "O contexto de correcao nao esta disponivel. Gere novamente o contexto antes de revisar o item.");
        }

        var currentDraftVersionHash = GradingDraftVersionHash.Compute(item);
        if (string.IsNullOrWhiteSpace(request.ExpectedDraftVersionHash))
        {
            if (MatchesExistingReview(item, request))
            {
                return await ToDetailResultAsync(item, cancellationToken);
            }

            throw new InvalidOperationException("Informe o hash da versao lida mais recentemente antes de revisar o item.");
        }

        if (!string.Equals(currentDraftVersionHash, request.ExpectedDraftVersionHash, StringComparison.Ordinal))
        {
            if (MatchesExistingReview(item, request))
            {
                return await ToDetailResultAsync(item, cancellationToken);
            }

            throw new InvalidOperationException("O rascunho foi alterado desde a ultima leitura. Consulte o item novamente antes de sobrescrever.");
        }

        if (!string.Equals(item.ReviewStatus.ToString(), request.ExpectedReviewStatus, StringComparison.OrdinalIgnoreCase))
        {
            if (MatchesExistingReview(item, request))
            {
                return await ToDetailResultAsync(item, cancellationToken);
            }

            throw new InvalidOperationException("O rascunho foi alterado desde a ultima leitura. Consulte o item novamente antes de sobrescrever.");
        }

        decimal? maxGrade = null;
        if (request.FinalGrade is not null)
        {
            AssignmentSettingsSummary? settings = null;
            try
            {
                settings = await settingsGateway.GetAssignmentSettingsAsync(
                    batch.CreatedBySubject,
                    item.CourseId.ToString(CultureInfo.InvariantCulture),
                    item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                    cancellationToken);
                maxGrade = settings?.MaxGrade > 0 ? settings.MaxGrade : null;
            }
            catch
            {
                maxGrade = null;
            }

            if (maxGrade is null)
            {
                if (settings?.IsGradable == false)
                {
                    throw new InvalidOperationException("Esta atividade nao possui avaliacao numerica no Moodle. Envie somente feedback e deixe finalGrade vazio.");
                }
                throw new InvalidOperationException("A escala maxima da tarefa nao pode ser confirmada; nota numerica bloqueada.");
            }
        }

        var previousReview = new
        {
            grade = item.FinalGrade ?? item.SuggestedGrade,
            feedback = item.FinalFeedback ?? item.DraftFeedback,
            teacherDecision = item.TeacherDecision,
            reviewNotes = item.ReviewNotes,
            reviewStatus = item.ReviewStatus.ToString(),
            draftVersionHash = GradingDraftVersionHash.Compute(item)
        };
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        item.ApplyTeacherReview(
            request.FinalGrade,
            request.FinalFeedback,
            currentUser.Subject,
            moodleUserId,
            request.TeacherDecision,
            request.ReviewNotes,
            maxGrade);

        var artifacts = await repository.ListArtifactsByItemAsync(item.Id, cancellationToken);
        var fileHashes = artifacts
            .Where(artifact => !string.IsNullOrWhiteSpace(artifact.Sha256))
            .Select(artifact => artifact.Sha256!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(hash => hash, StringComparer.Ordinal)
            .ToArray();
        var draftVersionHash = GradingDraftVersionHash.Compute(item);

        await auditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = $"grading-batch-{item.BatchId:N}",
            BatchJobId = item.BatchId,
            ToolName = "atualizar_rascunho_correcao",
            RiskLevel = ToolRiskLevel.HumanConfirmedWrite,
            ActorSubject = currentUser.Subject,
            ActorEmail = currentUser.Email,
            ActorMoodleUserId = moodleUserId,
            CourseId = item.CourseId,
            MoodleFunction = null,
            RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                gradingItemId = item.Id,
                batchJobId = item.BatchId,
                previousReview,
                request.FinalGrade,
                request.FinalFeedback,
                request.TeacherDecision,
                request.ReviewNotes,
                request.ExpectedReviewStatus,
                request.ExpectedDraftVersionHash,
                draftVersionHash,
                fileHashes
            }),
            ResponseSummaryJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                reviewStatus = item.ReviewStatus.ToString(),
                commitStatus = item.CommitStatus.ToString(),
                reviewedAt = item.ReviewedAt,
                finalGrade = item.FinalGrade,
                finalFeedback = item.FinalFeedback,
                teacherDecision = item.TeacherDecision,
                reviewNotes = item.ReviewNotes,
                draftVersionHash
            }),
            Status = "draft_reviewed"
        }, cancellationToken);

        await repository.SaveChangesForDraftReviewAsync(cancellationToken);

        return await ToDetailResultAsync(item, cancellationToken);
    }

    private static bool MatchesExistingReview(
        AssistedGradingItem item,
        UpdateAssistedGradingDraftCommand request)
    {
        return item.FinalGrade == request.FinalGrade &&
            string.Equals(item.FinalFeedback, request.FinalFeedback.Trim(), StringComparison.Ordinal) &&
            string.Equals(item.TeacherDecision, Normalize(request.TeacherDecision), StringComparison.Ordinal) &&
            string.Equals(item.ReviewNotes, Normalize(request.ReviewNotes), StringComparison.Ordinal);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<AssistedGradingItemDetailResult> ToDetailResultAsync(
        AssistedGradingItem item,
        CancellationToken cancellationToken)
    {
        var evidence = await repository.ListEvidenceByItemAsync(item.Id, cancellationToken);
        var draftVersionHash = GradingDraftVersionHash.Compute(item);
        var pendingIssues = BuildPendingIssues(item);
        var scoreNormalization = await GradingProposalNormalizationReader.ReadAsync(
            proposalStore,
            item.Id,
            cancellationToken);

        return new AssistedGradingItemDetailResult(
            item.Id,
            item.BatchId,
            item.CourseId.ToString(CultureInfo.InvariantCulture),
            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            item.SubmissionId?.ToString(CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
            item.AttemptNumber,
            item.Status.ToString(),
            item.SuggestedGrade,
            item.FinalGrade,
            item.Confidence,
            item.DraftFeedback,
            item.FinalFeedback,
            item.ReviewStatus.ToString(),
            item.CommitStatus.ToString(),
            item.TeacherDecision,
            item.ReviewNotes,
            draftVersionHash,
            pendingIssues,
            evidence.Select(ToEvidenceResult).ToArray(),
            item.PrivateNotesToTeacher,
            scoreNormalization);
    }

    private static IReadOnlyList<string> BuildPendingIssues(AssistedGradingItem item)
    {
        var pendingIssues = new List<string>();

        if (item.ReviewStatus == GradingReviewStatus.NotReviewed)
        {
            pendingIssues.Add("Revisao humana pendente.");
        }

        if (item.Status == GradingItemStatus.DraftReady && item.Confidence is < 0.5m)
        {
            pendingIssues.Add("Baixa confianca do rascunho assistido; revise criterios, evidencias e nota sugerida antes de aprovar.");
        }

        if (string.IsNullOrWhiteSpace(item.FinalFeedback))
        {
            pendingIssues.Add("Feedback final pendente.");
        }

        if (item.CommitStatus == GradingCommitStatus.Failed && !string.IsNullOrWhiteSpace(item.CommitError))
        {
            pendingIssues.Add($"Falha no lancamento Moodle: {item.CommitError}");
        }

        if (item.Status == GradingItemStatus.Failed &&
            item.CommitStatus != GradingCommitStatus.Failed &&
            !string.IsNullOrWhiteSpace(item.DraftFeedback))
        {
            pendingIssues.Add($"Falha no processamento da correcao assistida: {item.DraftFeedback}");
        }

        return pendingIssues;
    }

    private static AssistedGradingEvidenceResult ToEvidenceResult(GradingEvidence evidence)
    {
        return new AssistedGradingEvidenceResult(
            evidence.CriterionId,
            evidence.CriterionText,
            evidence.MaxPoints,
            evidence.SuggestedPoints,
            evidence.EvidenceText,
            evidence.GapsText,
            evidence.TeacherReviewRequired);
    }
}

public sealed class GetAssistedGradingBatchStatusQueryHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser,
    IPendingMoodleActionRepository? pendingActions = null)
    : IRequestHandler<GetAssistedGradingBatchStatusQuery, AssistedGradingBatchStatusResult>
{
    public async Task<AssistedGradingBatchStatusResult> Handle(
        GetAssistedGradingBatchStatusQuery request,
        CancellationToken cancellationToken)
    {
        var batch = await repository.GetBatchAsync(request.BatchJobId, cancellationToken);
        if (batch is null)
        {
            var scope = await GradingBatchScopeResolver.ResolveAsync(
                repository,
                currentUser,
                request.BatchJobId,
                cancellationToken);
            return await BuildRunStatusAsync(scope, request, cancellationToken);
        }
        GradingAccessControl.EnsureCanAccessBatch(batch, currentUser);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var items = await repository.ListItemsByBatchAsync(batch.Id, page, pageSize, cancellationToken);
        var totalItems = await repository.CountItemsByBatchAsync(batch.Id, cancellationToken);

        var allItems = await GradingItemProcessor.LoadAllBatchItemsAsync(
            repository,
            batch.Id,
            cancellationToken);
        var counters = CountItemStates(allItems);
        var errorsByCategory = BuildErrorCategories(allItems);
        var metrics = BuildMetrics(
            batch,
            allItems,
            processedOverride: counters.Processed,
            readyOverride: counters.Ready,
            blockedOverride: counters.Blocked,
            failedOverride: counters.Failed);
        var publicationActions = await LoadPublicationActionsAsync([batch.Id], cancellationToken);
        var publicationStates = BuildPublicationStates(allItems, publicationActions);
        var suggestedAction = ResolveSuggestedAction(
            publicationActions,
            allItemsSucceeded: batch.TotalItems > 0 && allItems.Count == batch.TotalItems &&
                               allItems.All(item => item.CommitStatus == GradingCommitStatus.Succeeded),
            hasUnknownPublication: publicationActions.Any(action => action.Status == PendingActionStatus.ExecutionUnknown) ||
                                   allItems.Any(item => item.CommitStatus == GradingCommitStatus.ExecutionUnknown) ||
                                   publicationStates.Values.Contains("unknown", StringComparer.Ordinal),
            hasPendingProcessing: allItems.Any(item => item.Status is GradingItemStatus.Pending or GradingItemStatus.Analyzing or GradingItemStatus.AwaitingAiAnalysis),
            hasDrafts: allItems.Any(item => item.Status is GradingItemStatus.DraftReady or GradingItemStatus.ReadyToCommit),
            hasUnsupportedState: allItems.Any(item => item.Status is GradingItemStatus.Blocked or GradingItemStatus.Failed));
        var nextReady = items
            .Where(item => item.Status == GradingItemStatus.DraftReady && item.ReviewStatus == GradingReviewStatus.NotReviewed)
            .Take(5)
            .Select(item => ToStatusItem(item, publicationStates))
            .ToArray();

        return new AssistedGradingBatchStatusResult(
            batch.Id,
            batch.Status.ToString(),
            batch.TotalItems,
            counters.Processed,
            counters.Ready,
            counters.Blocked,
            counters.Failed,
            page,
            pageSize,
            HasMore: page * pageSize < totalItems,
            items.Select(item => ToStatusItem(item, publicationStates)).ToArray(),
            NextReadyItems: nextReady,
            ErrorsByCategory: errorsByCategory,
            ProcessingMetrics: metrics,
            PublicationActions: publicationActions.Select(ToPublicationActionReference).ToArray(),
            AsOfUtc: DateTimeOffset.UtcNow,
            NextAction: ResolveNextAction(allItems, publicationActions, batch.TotalItems),
            SuggestedAction: suggestedAction.Action,
            SuggestedActionLimitation: suggestedAction.Limitation);
    }

    private static AssistedGradingBatchStatusItem ToStatusItem(
        AssistedGradingItem item,
        IReadOnlyDictionary<Guid, string>? publicationStates = null)
    {
        var publicationState = publicationStates?.GetValueOrDefault(item.Id) ?? ResolvePublicationState(item, null);
        var failureReason = item.CommitStatus is GradingCommitStatus.Failed or GradingCommitStatus.ExecutionUnknown or GradingCommitStatus.RetryScheduled ||
                            item.Status == GradingItemStatus.Failed
            ? string.IsNullOrWhiteSpace(item.LastErrorCode) ? "unknown_legacy_failure" : item.LastErrorCode
            : publicationState == "unknown" ? "publication_item_state_inconsistent" : null;
        return new AssistedGradingBatchStatusItem(
            item.Id,
            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            item.SubmissionId?.ToString(CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
            item.Status.ToString(),
            item.ReviewStatus.ToString(),
            item.CommitStatus.ToString(),
            item.StudentDisplayName,
            item.FinalGrade ?? item.SuggestedGrade,
            item.FinalFeedback ?? item.DraftFeedback,
            failureReason,
            publicationState,
            ResolveProcessingState(item),
            item.ContextVersion,
            item.ContextHash,
            GradingDraftVersionHash.Compute(item),
            item.LastErrorCode,
            item.LastErrorPhase);
    }

    private static string ResolveProcessingState(AssistedGradingItem item) => item.Status switch
    {
        GradingItemStatus.Pending => item.NextAttemptAt is { } nextAttempt && nextAttempt > DateTimeOffset.UtcNow
            ? "retry_scheduled"
            : "queued",
        GradingItemStatus.AwaitingAiAnalysis => "awaiting_ai_analysis",
        GradingItemStatus.DraftReady => "ready_for_review",
        GradingItemStatus.ReadyToCommit => "awaiting_publication",
        GradingItemStatus.Committed => "published",
        GradingItemStatus.Blocked => "blocked",
        GradingItemStatus.Failed => "failed",
        _ => "unknown"
    };

    private async Task<IReadOnlyList<PendingMoodleAction>> LoadPublicationActionsAsync(
        IReadOnlyCollection<Guid> scopeIds,
        CancellationToken cancellationToken)
    {
        if (pendingActions is null)
        {
            return [];
        }

        var actions = await pendingActions.ListGradingPublicationsByBatchIdsAsync(
            scopeIds,
            cancellationToken);
        return actions
            .Where(action =>
                string.Equals(action.CreatedBySubject, currentUser.Subject, StringComparison.Ordinal) ||
                currentUser.HasPlatformPermission("tool.pending_actions.manage"))
            .DistinctBy(action => action.Id)
            .OrderByDescending(action => action.CreatedAt)
            .ToArray();
    }

    private static GradingPublicationActionReference ToPublicationActionReference(PendingMoodleAction action) =>
        new(
            action.Id,
            action.Status.ToString(),
            action.ExpiresAt,
            action.ConfirmedAt,
            action.ExecutionAttemptCount,
            action.NextExecutionAttemptAt,
            action.Status switch
            {
                PendingActionStatus.Failed => "publication_failed",
                PendingActionStatus.PartiallyCompleted when action.NextExecutionAttemptAt is not null => null,
                PendingActionStatus.PartiallyCompleted => "partial_completion",
                PendingActionStatus.ExecutionUnknown => "execution_unknown",
                _ => null
            });

    private static IReadOnlyDictionary<Guid, string> BuildPublicationStates(
        IReadOnlyList<AssistedGradingItem> items,
        IReadOnlyList<PendingMoodleAction> actions)
    {
        var actionsByItem = new Dictionary<Guid, PendingMoodleAction>();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var action in actions)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<GradingLaunchPayload>(action.PayloadJson, jsonOptions);
                if (payload is null) continue;
                foreach (var payloadItem in payload.Items)
                {
                    actionsByItem.TryAdd(payloadItem.GradingItemId, action);
                }
            }
            catch (JsonException)
            {
                // O estado legado dos itens continua disponível mesmo se um
                // payload antigo não puder ser interpretado.
            }
        }

        return items.ToDictionary(
            item => item.Id,
            item => ResolvePublicationState(item, actionsByItem.GetValueOrDefault(item.Id)));
    }

    private static string ResolveActionPublicationState(PendingMoodleAction action) => action.Status switch
    {
        PendingActionStatus.PendingConfirmation => "awaiting_approval",
        PendingActionStatus.Authorized or PendingActionStatus.Confirmed => "queued",
        PendingActionStatus.Executing => "executing",
        PendingActionStatus.ExecutionUnknown => "unknown",
        PendingActionStatus.PartiallyCompleted => action.NextExecutionAttemptAt is not null ? "retry_scheduled" : "unknown",
        // A terminal aggregate action does not prove that every selected
        // item reached Moodle. Item-level Succeeded remains the only proof.
        PendingActionStatus.Executed or PendingActionStatus.Failed => "unknown",
        PendingActionStatus.Expired or PendingActionStatus.Cancelled => "blocked",
        _ => "not_selected"
    };

    private static string ResolvePublicationState(AssistedGradingItem item, PendingMoodleAction? action)
    {
        // A batch can stop after another item's write becomes ambiguous. A
        // ready item with no durable write intent was never sent to Moodle and
        // must not inherit the aggregate action's unknown state.
        if (action?.Status == PendingActionStatus.ExecutionUnknown &&
            item.Status == GradingItemStatus.ReadyToCommit &&
            item.CommitStatus == GradingCommitStatus.Pending &&
            item.CommitWriteIntentAt is null)
        {
            return "ready";
        }

        var itemState = item.CommitStatus switch
        {
            GradingCommitStatus.Succeeded => "succeeded",
            GradingCommitStatus.RetryScheduled => "retry_scheduled",
            GradingCommitStatus.ExecutionUnknown => "unknown",
            GradingCommitStatus.Failed => "failed",
            _ => action is null ? null : ResolveActionPublicationState(action)
        };
        if (itemState is not null) return itemState;
        if (item.Status == GradingItemStatus.Blocked) return "blocked";
        if (item.Status == GradingItemStatus.ReadyToCommit && item.CommitStatus == GradingCommitStatus.Pending) return "ready";
        return "not_selected";
    }

    private static GradingBatchProcessingMetrics BuildMetrics(
        AssistedGradingBatch batch,
        IReadOnlyList<AssistedGradingItem> items,
        bool? canLaunchOverride = null,
        int? processedOverride = null,
        int? readyOverride = null,
        int? blockedOverride = null,
        int? failedOverride = null)
    {
        var total = batch.TotalItems > 0 ? batch.TotalItems : 1;
        var processedItems = processedOverride ?? batch.ProcessedItems;
        var readyItems = readyOverride ?? batch.ReadyItems;
        var blockedItems = blockedOverride ?? batch.BlockedItems;
        var failedItems = failedOverride ?? batch.FailedItems;
        var progressPercent = (int)Math.Round((double)processedItems / total * 100);
        var readyPercent = (int)Math.Round((double)readyItems / total * 100);
        var blockedPercent = (int)Math.Round((double)blockedItems / total * 100);
        var failedPercent = (int)Math.Round((double)failedItems / total * 100);
        // ProcessedItems já é a união dos estados terminais; subtrair
        // bloqueados/falhos novamente produzia uma contagem pendente inflada.
        var pendingItems = Math.Max(0, batch.TotalItems - processedItems);
        var canLaunch = (canLaunchOverride ?? items.Any(item =>
                item.Status == GradingItemStatus.ReadyToCommit &&
                item.CommitStatus == GradingCommitStatus.Pending &&
                !string.IsNullOrWhiteSpace(item.FinalFeedback))) &&
            batch.Status is GradingBatchStatus.ReadyForReview or GradingBatchStatus.Processing;

        return new GradingBatchProcessingMetrics(
            progressPercent,
            readyPercent,
            blockedPercent,
            failedPercent,
            pendingItems,
            canLaunch);
    }

    private static (int Processed, int Ready, int Blocked, int Failed) CountItemStates(
        IEnumerable<AssistedGradingItem> items)
    {
        var materialized = items.ToArray();
        return (
            materialized.Count(item => item.Status is
                GradingItemStatus.ReadyToCommit or
                GradingItemStatus.Committed or
                GradingItemStatus.Blocked or
                GradingItemStatus.Failed),
            materialized.Count(item => item.Status is
                GradingItemStatus.DraftReady or
                GradingItemStatus.ReadyToCommit or
                GradingItemStatus.Committed),
            materialized.Count(item => item.Status == GradingItemStatus.Blocked),
            materialized.Count(item => item.Status == GradingItemStatus.Failed));
    }

    private static IReadOnlyDictionary<string, int> BuildErrorCategories(
        IEnumerable<AssistedGradingItem> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var category = item.Status switch
            {
                GradingItemStatus.Blocked => "blocked",
                GradingItemStatus.Failed => "failed",
                _ => null
            };
            if (category is null)
            {
                continue;
            }

            counts.TryGetValue(category, out var current);
            counts[category] = current + 1;
        }

        return counts;
    }

    private async Task<AssistedGradingBatchStatusResult> BuildRunStatusAsync(
        GradingBatchScope scope,
        GetAssistedGradingBatchStatusQuery request,
        CancellationToken cancellationToken)
    {
        if (scope.Batches.Count == 0)
        {
            var emptyPage = Math.Max(1, request.Page);
            var emptyPageSize = Math.Clamp(request.PageSize, 1, 100);
            return new AssistedGradingBatchStatusResult(
                request.BatchJobId,
                scope.Run?.Status.ToString() ?? "Preparing",
                0,
                0,
                0,
                0,
                0,
                emptyPage,
                emptyPageSize,
                false,
                [],
                [],
                new Dictionary<string, int>(),
                new GradingBatchProcessingMetrics(0, 0, 0, 0, 0, false),
                NextAction: "review",
                SuggestedAction: "get_assisted_grading_batch_status");
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var pageItems = await repository.ListItemsByGradingRunAsync(
            scope.Run!.Id,
            page,
            pageSize,
            status: null,
            cancellationToken);
        var publicationActions = await LoadPublicationActionsAsync(
            new[] { scope.Run.Id }.Concat(scope.Batches.Select(batch => batch.Id)).ToArray(),
            cancellationToken);
        var publicationStates = BuildPublicationStates(pageItems, publicationActions);
        var totalItems = await repository.CountItemsByGradingRunAsync(
            scope.Run.Id,
            status: null,
            cancellationToken);
        var nextReady = pageItems
            .Where(item => item.Status == GradingItemStatus.DraftReady && item.ReviewStatus == GradingReviewStatus.NotReviewed)
            .Take(5)
            .Select(item => ToStatusItem(item, publicationStates))
            .ToArray();
        var draftReady = await repository.CountItemsByGradingRunAsync(scope.Run.Id, GradingItemStatus.DraftReady, cancellationToken);
        var readyToCommit = await repository.CountItemsByGradingRunAsync(scope.Run.Id, GradingItemStatus.ReadyToCommit, cancellationToken);
        var committed = await repository.CountItemsByGradingRunAsync(scope.Run.Id, GradingItemStatus.Committed, cancellationToken);
        var blocked = await repository.CountItemsByGradingRunAsync(scope.Run.Id, GradingItemStatus.Blocked, cancellationToken);
        var failed = await repository.CountItemsByGradingRunAsync(scope.Run.Id, GradingItemStatus.Failed, cancellationToken);
        var processed = draftReady + readyToCommit + committed + blocked + failed;
        var ready = draftReady + readyToCommit + committed;
        var errorsByCategory = new Dictionary<string, int>();
        if (blocked > 0) errorsByCategory["blocked"] = blocked;
        if (failed > 0) errorsByCategory["failed"] = failed;
        var syntheticBatch = AssistedGradingBatch.Create(
            scope.Batches[0].CourseId,
            scope.AssignmentIds.Count == 0 ? [1L] : scope.AssignmentIds,
            scope.Run?.CreatedBySubject ?? scope.Batches[0].CreatedBySubject,
            scope.Run?.CreatedByMoodleUserId ?? scope.Batches[0].CreatedByMoodleUserId,
            totalItems);
        syntheticBatch.UpdateCounters(processed, ready, blocked, failed);
        // A process can crash after the publication action reaches a
        // terminal state but before the aggregate row is updated. Derive the
        // observable status from the item ledger in that narrow window so a
        // run does not remain indefinitely stuck at Publishing after restart.
        var runStatus = scope.Run?.Status.ToString() ?? "Ready";
        if (scope.Run?.Status == GradingRunStatus.Publishing && readyToCommit == 0)
        {
            runStatus = committed == totalItems
                ? GradingRunStatus.Completed.ToString()
                : GradingRunStatus.PartiallyCompleted.ToString();
        }
        var suggestedAction = ResolveSuggestedAction(
            publicationActions,
            allItemsSucceeded: totalItems > 0 && committed == totalItems,
            hasUnknownPublication: publicationActions.Any(action => action.Status == PendingActionStatus.ExecutionUnknown) ||
                                   pageItems.Any(item => item.CommitStatus == GradingCommitStatus.ExecutionUnknown) ||
                                   publicationStates.Values.Contains("unknown", StringComparer.Ordinal),
            hasPendingProcessing: processed < totalItems,
            hasDrafts: draftReady > 0 || readyToCommit > 0,
            hasUnsupportedState: blocked > 0 || failed > 0);
        return new AssistedGradingBatchStatusResult(
            request.BatchJobId,
            runStatus,
            totalItems,
            processed,
            ready,
            blocked,
            failed,
            page,
            pageSize,
            page * pageSize < totalItems,
            pageItems.Select(item => ToStatusItem(item, publicationStates)).ToArray(),
            nextReady,
            errorsByCategory,
            BuildMetrics(syntheticBatch, pageItems, canLaunchOverride: readyToCommit > 0),
            publicationActions.Select(ToPublicationActionReference).ToArray(),
            DateTimeOffset.UtcNow,
            ResolveNextAction(
                pageItems,
                publicationActions,
                totalItems,
                allItemsSucceeded: totalItems > 0 && committed == totalItems,
                hasReadyItems: readyToCommit > 0),
            SuggestedAction: suggestedAction.Action,
            SuggestedActionLimitation: suggestedAction.Limitation);
    }

    private static SuggestedActionRecommendation ResolveSuggestedAction(
        IReadOnlyList<PendingMoodleAction> actions,
        bool allItemsSucceeded,
        bool hasUnknownPublication,
        bool hasPendingProcessing,
        bool hasDrafts,
        bool hasUnsupportedState)
    {
        // Item-level success is the publication proof. A stale aggregate action
        // must not override a fully reconciled ledger.
        if (allItemsSucceeded)
        {
            return new SuggestedActionRecommendation("none", null);
        }

        if (hasUnknownPublication)
        {
            if (!actions.Any(action => action.Status == PendingActionStatus.ExecutionUnknown))
            {
                return new SuggestedActionRecommendation(
                    null,
                    "Há publicação desconhecida, mas nenhuma ação reconciliável está visível para este usuário. Revise o escopo/permissão antes de agir.");
            }

            return new SuggestedActionRecommendation(
                "moodle_reconcile_write",
                "Reconcilie cada item com pendingActionId, gradingItemId e resolution=auto; não crie outra prévia enquanto houver resultado remoto desconhecido.");
        }

        if (actions.Any(action => action.Status is PendingActionStatus.Authorized or PendingActionStatus.Confirmed or PendingActionStatus.Executing ||
                                  action.Status == PendingActionStatus.PartiallyCompleted && action.NextExecutionAttemptAt is not null))
        {
            return new SuggestedActionRecommendation("get_grading_publication_status", null);
        }

        if (actions.Any(action => action.Status == PendingActionStatus.PendingConfirmation && action.ExpiresAt > DateTimeOffset.UtcNow))
        {
            return new SuggestedActionRecommendation("confirm_batch_grade_launch", null);
        }

        if (hasPendingProcessing)
        {
            return new SuggestedActionRecommendation("get_assisted_grading_batch_status", null);
        }

        if (hasUnsupportedState)
        {
            return new SuggestedActionRecommendation(
                null,
                "Estado bloqueado, falho ou legado sem ação automática segura. Revise status e failureReason antes de decidir se cabe requeue.");
        }

        if (hasDrafts)
        {
            return new SuggestedActionRecommendation("create_batch_grade_launch_preview", null);
        }

        return new SuggestedActionRecommendation(
            null,
            "O ledger não corresponde a uma combinação conhecida de estados; consulte publicationActions e os itens antes de agir.");
    }

    private sealed record SuggestedActionRecommendation(string? Action, string? Limitation);

    private static string ResolveNextAction(
        IReadOnlyList<AssistedGradingItem> items,
        IReadOnlyList<PendingMoodleAction> actions,
        int totalItems,
        bool allItemsSucceeded = false,
        bool hasReadyItems = false)
    {
        if (actions.Any(action => action.Status == PendingActionStatus.PendingConfirmation && action.ExpiresAt > DateTimeOffset.UtcNow))
        {
            return "confirm";
        }

        if (actions.Any(action => action.Status is PendingActionStatus.Authorized or PendingActionStatus.Confirmed or PendingActionStatus.Executing ||
                                  action.Status == PendingActionStatus.PartiallyCompleted && action.NextExecutionAttemptAt is not null))
        {
            return "wait";
        }

        if (items.Any(item => item.Status == GradingItemStatus.Pending))
        {
            return "wait";
        }

        if (items.Any(item => item.Status == GradingItemStatus.AwaitingAiAnalysis))
        {
            return "read_resources";
        }

        if (items.Any(item => item.CommitStatus is GradingCommitStatus.Failed or GradingCommitStatus.ExecutionUnknown || item.Status == GradingItemStatus.Failed))
        {
            return "attention";
        }

        if (allItemsSucceeded || (totalItems > 0 && items.Count == totalItems && items.All(item => item.CommitStatus == GradingCommitStatus.Succeeded)))
        {
            return "done";
        }

        if (hasReadyItems || items.Any(item => item.Status == GradingItemStatus.ReadyToCommit))
        {
            return "review";
        }

        return "review";
    }
}

public sealed class GetAssistedGradingCoordinationReportQueryHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser)
    : IRequestHandler<GetAssistedGradingCoordinationReportQuery, AssistedGradingCoordinationReportResult>
{
    private const int PageSize = 100;
    private const int MaxAttentionItems = 25;
    private const int MaxCriteriaSummaries = 10;

    public async Task<AssistedGradingCoordinationReportResult> Handle(
        GetAssistedGradingCoordinationReportQuery request,
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
        var batch = scope.FirstBatch;
        if (batch is null)
        {
            var emptyReport = new AssistedGradingCoordinationReportResult(
                scope.RequestedId,
                DateTimeOffset.UtcNow,
                string.Empty,
                scope.AssignmentIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
                scope.Run?.Status.ToString() ?? "Preparing",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                null,
                null,
                new Dictionary<string, int>(),
                new Dictionary<string, int>(),
                new Dictionary<string, int>(),
                [],
                [],
                string.Empty);
            return emptyReport with { ReportMarkdown = BuildReportMarkdown(emptyReport) };
        }

        var items = new List<AssistedGradingItem>();
        foreach (var child in scope.Batches)
        {
            items.AddRange(await GradingItemProcessor.LoadAllBatchItemsAsync(repository, child.Id, cancellationToken));
        }

        // A single set-based read avoids one evidence query per item when a
        // run contains thousands of submissions.
        var evidenceByItem = await repository.ListEvidenceByItemsAsync(
            items.Select(item => item.Id).ToArray(),
            cancellationToken);

        var statusCounts = CountBy(items, item => item.Status.ToString());
        var reviewStatusCounts = CountBy(items, item => item.ReviewStatus.ToString());
        var commitStatusCounts = CountBy(items, item => item.CommitStatus.ToString());
        var attentionItems = BuildAttentionItems(items, evidenceByItem);
        var criteriaNeedingReview = BuildCriterionSummaries(evidenceByItem.Values.SelectMany(evidence => evidence));
        var reviewedItems = items.Count(item => item.ReviewStatus == GradingReviewStatus.Reviewed);
        var awaitingAiItems = items.Count(item => item.Status == GradingItemStatus.AwaitingAiAnalysis);
        var aiDraftItems = items.Count(item =>
            item.Status == GradingItemStatus.DraftReady && item.Confidence >= 0.8m);
        var pendingReviewItems = items.Count(item =>
            item.ReviewStatus != GradingReviewStatus.Reviewed &&
            item.Status is GradingItemStatus.DraftReady or GradingItemStatus.ReadyToCommit);
        var committedItems = items.Count(item =>
            item.Status == GradingItemStatus.Committed ||
            item.CommitStatus == GradingCommitStatus.Succeeded);
        var executionUnknownItems = items.Count(item => item.CommitStatus == GradingCommitStatus.ExecutionUnknown);
        var blockedPermissionItems = items.Count(item =>
            item.CommitStatus == GradingCommitStatus.Failed &&
            (item.CommitError?.Contains("moodle.write", StringComparison.OrdinalIgnoreCase) == true ||
             item.CommitError?.Contains("feature flag", StringComparison.OrdinalIgnoreCase) == true ||
             item.CommitError?.Contains("moodle_function_unavailable", StringComparison.OrdinalIgnoreCase) == true));
        var launchPendingItems = items.Count(item => item.CommitStatus == GradingCommitStatus.Pending);
        var lowConfidenceItems = items.Count(HasLowConfidence);
        var generatedAt = DateTimeOffset.UtcNow;

        var processedItems = scope.IsRun
            ? items.Count(item => item.Status is GradingItemStatus.DraftReady or GradingItemStatus.ReadyToCommit or GradingItemStatus.Committed or GradingItemStatus.Blocked or GradingItemStatus.Failed)
            : batch.ProcessedItems;
        var readyItems = scope.IsRun
            ? items.Count(item => item.Status is GradingItemStatus.DraftReady or GradingItemStatus.ReadyToCommit or GradingItemStatus.Committed)
            : batch.ReadyItems;
        var blockedItems = scope.IsRun ? items.Count(item => item.Status == GradingItemStatus.Blocked) : batch.BlockedItems;
        var failedItems = scope.IsRun ? items.Count(item => item.Status == GradingItemStatus.Failed) : batch.FailedItems;
        var report = new AssistedGradingCoordinationReportResult(
            scope.RequestedId,
            generatedAt,
            scope.CourseId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            scope.AssignmentIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
            scope.Run?.Status.ToString() ?? batch.Status.ToString(),
            scope.IsRun ? items.Count : batch.TotalItems,
            processedItems,
            readyItems,
            blockedItems,
            failedItems,
            executionUnknownItems,
            reviewedItems,
            pendingReviewItems,
            committedItems,
            launchPendingItems,
            lowConfidenceItems,
            AverageOrNull(items.Select(item => item.Confidence)),
            AverageOrNull(items.Select(item => item.SuggestedGrade)),
            AverageOrNull(items.Select(item => item.FinalGrade)),
            statusCounts,
            reviewStatusCounts,
            commitStatusCounts,
            attentionItems,
            criteriaNeedingReview,
            ReportMarkdown: string.Empty);

        return report with { ReportMarkdown = BuildReportMarkdown(report) };
    }


    private static IReadOnlyDictionary<string, int> CountBy(
        IReadOnlyList<AssistedGradingItem> items,
        Func<AssistedGradingItem, string> selector)
    {
        return items
            .GroupBy(selector, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    private static IReadOnlyList<AssistedGradingCoordinationAttentionItem> BuildAttentionItems(
        IReadOnlyList<AssistedGradingItem> items,
        IReadOnlyDictionary<Guid, IReadOnlyList<GradingEvidence>> evidenceByItem)
    {
        return items
            .Select(item => BuildAttentionItem(item, evidenceByItem))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.Item.AssignmentId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Item.StudentId, StringComparer.Ordinal)
            .Take(MaxAttentionItems)
            .Select(entry => entry.Item)
            .ToArray();
    }

    private static AttentionEntry? BuildAttentionItem(
        AssistedGradingItem item,
        IReadOnlyDictionary<Guid, IReadOnlyList<GradingEvidence>> evidenceByItem)
    {
        var reasons = new List<string>();
        var priority = 99;
        var evidence = evidenceByItem.TryGetValue(item.Id, out var itemEvidence)
            ? itemEvidence
            : [];

        if (item.CommitStatus == GradingCommitStatus.ExecutionUnknown)
        {
            reasons.Add("Resultado da escrita Moodle desconhecido; reconcilie antes de tentar novamente.");
            priority = Math.Min(priority, 0);
        }
        else if (item.CommitStatus == GradingCommitStatus.Failed)
        {
            var commitError = item.CommitError ?? item.DraftFeedback;
            if (commitError?.Contains("moodle.write", StringComparison.OrdinalIgnoreCase) == true ||
                commitError?.Contains("moodle_function_unavailable", StringComparison.OrdinalIgnoreCase) == true)
            {
                reasons.Add("Bloqueado por falta de permissao: escopo moodle.write ausente no token de servico.");
            }
            else if (commitError?.Contains("feature flag", StringComparison.OrdinalIgnoreCase) == true)
            {
                reasons.Add("Bloqueado por configuracao: feature flag de escrita de nota desabilitada.");
            }
            else
            {
                reasons.Add("Falha no lancamento Moodle: " + Shorten(commitError));
            }
            priority = Math.Min(priority, 0);
        }
        else if (item.Status == GradingItemStatus.Failed)
        {
            reasons.Add("Falha no processamento: " + Shorten(item.DraftFeedback));
            priority = Math.Min(priority, 1);
        }

        if (item.Status == GradingItemStatus.Blocked)
        {
            reasons.Add("Bloqueado: " + Shorten(item.DraftFeedback));
            priority = Math.Min(priority, 2);
        }

        if (item.Status == GradingItemStatus.AwaitingAiAnalysis)
        {
            reasons.Add("Aguardando analise pela IA. Use prepare_ai_grading_batch.");
            priority = Math.Min(priority, 3);
        }

        if (HasLowConfidence(item))
        {
            reasons.Add("Baixa confianca do rascunho assistido.");
            priority = Math.Min(priority, 3);
        }

        if (item.Status == GradingItemStatus.DraftReady && item.ReviewStatus != GradingReviewStatus.Reviewed)
        {
            reasons.Add("Revisao humana pendente.");
            priority = Math.Min(priority, 4);
        }

        if (item.CommitStatus == GradingCommitStatus.Pending)
        {
            reasons.Add("Lancamento Moodle pendente de previa/confirmacao.");
            priority = Math.Min(priority, 5);
        }

        if (evidence.Any(entry => entry.TeacherReviewRequired))
        {
            reasons.Add("Ha criterio marcado para revisao humana.");
            priority = Math.Min(priority, 6);
        }

        if (evidence.Any(entry => !string.IsNullOrWhiteSpace(entry.GapsText)))
        {
            reasons.Add("Ha lacunas registradas em criterio.");
            priority = Math.Min(priority, 7);
        }

        if (reasons.Count == 0)
        {
            return null;
        }

        var attentionItem = new AssistedGradingCoordinationAttentionItem(
            item.Id,
            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            item.SubmissionId?.ToString(CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
            item.Status.ToString(),
            item.ReviewStatus.ToString(),
            item.CommitStatus.ToString(),
            item.Confidence,
            item.SuggestedGrade,
            item.FinalGrade,
            string.Join(" ", reasons.Distinct(StringComparer.Ordinal)));

        return new AttentionEntry(priority, attentionItem);
    }

    private static IReadOnlyList<AssistedGradingCoordinationCriterionSummary> BuildCriterionSummaries(
        IEnumerable<GradingEvidence> evidence)
    {
        return evidence
            .GroupBy(evidenceItem =>
                string.IsNullOrWhiteSpace(evidenceItem.CriterionId)
                    ? NormalizeCriterionKey(evidenceItem.CriterionText)
                    : evidenceItem.CriterionId!,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToArray();
                var criterionId = entries
                    .Select(entry => entry.CriterionId)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var criterionText = entries
                    .Select(entry => entry.CriterionText)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Criterio sem texto.";
                var teacherReviewRequiredItems = entries
                    .Where(entry => entry.TeacherReviewRequired)
                    .Select(entry => entry.GradingItemId)
                    .Distinct()
                    .Count();
                var itemsWithGaps = entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.GapsText))
                    .Select(entry => entry.GradingItemId)
                    .Distinct()
                    .Count();

                return new AssistedGradingCoordinationCriterionSummary(
                    criterionId,
                    criterionText,
                    entries.Select(entry => entry.GradingItemId).Distinct().Count(),
                    teacherReviewRequiredItems,
                    itemsWithGaps,
                    AverageOrNull(entries.Select(entry => entry.SuggestedPoints)),
                    AverageOrNull(entries.Select(entry => entry.MaxPoints)));
            })
            .Where(summary => summary.TeacherReviewRequiredItems > 0 || summary.ItemsWithGaps > 0)
            .OrderByDescending(summary => summary.TeacherReviewRequiredItems)
            .ThenByDescending(summary => summary.ItemsWithGaps)
            .ThenByDescending(summary => summary.ItemCount)
            .ThenBy(summary => summary.CriterionText, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCriteriaSummaries)
            .ToArray();
    }

    private static bool HasLowConfidence(AssistedGradingItem item)
    {
        return item.Status is GradingItemStatus.DraftReady or GradingItemStatus.ReadyToCommit &&
            item.Confidence is < 0.5m;
    }

    private static decimal? AverageOrNull(IEnumerable<decimal?> values)
    {
        var concreteValues = values
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return concreteValues.Length == 0
            ? null
            : Math.Round(concreteValues.Average(), 2, MidpointRounding.AwayFromZero);
    }

    private static string BuildReportMarkdown(AssistedGradingCoordinationReportResult report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Relatorio consolidado de correcao assistida");
        builder.AppendLine();
        builder.AppendLine($"- Lote: `{report.BatchJobId}`");
        builder.AppendLine($"- Curso: `{report.CourseId}`");
        builder.AppendLine($"- Tarefas: {string.Join(", ", report.AssignmentIds.Select(id => $"`{id}`"))}");
        builder.AppendLine($"- Status: `{report.Status}`");
        builder.AppendLine($"- Gerado em UTC: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine("## Resumo");
        builder.AppendLine();
        builder.AppendLine($"- Total: {report.TotalItems}");
        builder.AppendLine($"- Processados: {report.ProcessedItems}");
        builder.AppendLine($"- Prontos para revisao: {report.ReadyItems}");
        builder.AppendLine($"- Revisados: {report.ReviewedItems}");
        builder.AppendLine($"- Revisao pendente: {report.PendingReviewItems}");
        builder.AppendLine($"- Bloqueados: {report.BlockedItems}");
        builder.AppendLine($"- Falhos: {report.FailedItems}");
        builder.AppendLine($"- Lancamento pendente: {report.LaunchPendingItems}");
        builder.AppendLine($"- Lancados no Moodle: {report.CommittedItems}");
        builder.AppendLine($"- Baixa confianca: {report.LowConfidenceItems}");
        builder.AppendLine($"- Confianca media: {FormatDecimal(report.AverageConfidence)}");
        builder.AppendLine($"- Nota sugerida media: {FormatDecimal(report.AverageSuggestedGrade)}");
        builder.AppendLine($"- Nota final media: {FormatDecimal(report.AverageFinalGrade)}");

        // --- Origem das correcoes ---
        builder.AppendLine();
        builder.AppendLine("## Origem das correcoes");
        builder.AppendLine();
        if (report.StatusCounts.TryGetValue("AwaitingAiAnalysis", out var awaitingAi) && awaitingAi > 0)
        {
            builder.AppendLine($"- Aguardando IA: {awaitingAi} — use `prepare_ai_grading_batch` para gerar nota e feedback.");
        }
        var aiGenerated = report.StatusCounts
            .Where(kv => kv.Key is "DraftReady" or "ReadyToCommit")
            .Sum(kv => kv.Value);
        if (aiGenerated > 0)
        {
            builder.AppendLine($"- Com feedback gerado (IA ou revisao): {aiGenerated}");
        }
        if (report.CommittedItems > 0)
        {
            builder.AppendLine($"- Lancados no Moodle: {report.CommittedItems}");
        }
        // Bloqueios por permissao
        var permissionBlocked = report.AttentionItems
            .Count(item => item.Reason.Contains("permissao", StringComparison.OrdinalIgnoreCase) ||
                           item.Reason.Contains("feature flag", StringComparison.OrdinalIgnoreCase));
        if (permissionBlocked > 0)
        {
            builder.AppendLine($"- Bloqueados por permissao/configuracao: {permissionBlocked}");
        }

        builder.AppendLine();
        builder.AppendLine("## Itens que exigem atencao");
        builder.AppendLine();
        if (report.AttentionItems.Count == 0)
        {
            builder.AppendLine("- Nenhum item critico identificado no lote.");
        }
        else
        {
            foreach (var item in report.AttentionItems)
            {
                builder.AppendLine(
                    $"- Estudante `{item.StudentId}`, tarefa `{item.AssignmentId}`, item `{item.GradingItemId}`: {item.Reason}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Criterios com maior necessidade de revisao");
        builder.AppendLine();
        if (report.CriteriaNeedingReview.Count == 0)
        {
            builder.AppendLine("- Nenhum criterio estruturado disponivel. Os criterios serao gerados pela IA durante a correcao.");
        }
        else
        {
            foreach (var criterion in report.CriteriaNeedingReview)
            {
                builder.AppendLine(
                    $"- {criterion.CriterionText}: {criterion.TeacherReviewRequiredItems} item(ns) exigem revisao, {criterion.ItemsWithGaps} item(ns) com lacunas.");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string Shorten(string? value, int maxLength = 180)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "motivo nao informado.";
        }

        var normalized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }

    private static string NormalizeCriterionKey(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "criterio-sem-texto"
            : value.Trim().ToUpperInvariant();
    }

    private static string FormatDecimal(decimal? value)
    {
        return value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/d";
    }

    private sealed record AttentionEntry(
        int Priority,
        AssistedGradingCoordinationAttentionItem Item);
}

public sealed class CancelAssistedGradingBatchCommandHandler(
    IGradingBatchOrchestrator orchestrator,
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser,
    IPendingMoodleActionRepository? pendingActions = null)
    : IRequestHandler<CancelAssistedGradingBatchCommand, CancelAssistedGradingBatchResult>
{
    public async Task<CancelAssistedGradingBatchResult> Handle(
        CancelAssistedGradingBatchCommand request,
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

        const string purgeConfirmation = "CANCELAR_CORRECOES_LOCAIS";
        var purgeRequested = request.PurgeLocalData;
        if (request.PurgeLocalData &&
            !string.Equals(request.ConfirmationText?.Trim(), purgeConfirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Para remover dados locais informe exatamente {purgeConfirmation} em confirmationText.");
        }

        // Cancelamento é uma operação de controle local e não precisa
        // carregar cada item, artefato ou resource para impedir novos workers.
        // A leitura completa continua sendo feita somente quando o chamador
        // pediu expurgo, pois nesse caso precisamos contar publicações e
        // escritas de resultado desconhecido antes de remover qualquer dado.
        var warnings = new List<string>();
        var publishedItems = 0;
        var executionUnknownItems = 0;
        var needsCleanupSummary = purgeRequested ||
            scope.Batches.Any(batch => batch.Status == GradingBatchStatus.Completed);
        var cleanupSummaryAvailable = !needsCleanupSummary;
        if (needsCleanupSummary)
        {
            try
            {
                var allItems = new List<AssistedGradingItem>();
                foreach (var child in scope.Batches)
                {
                    allItems.AddRange(await GradingItemProcessor.LoadAllBatchItemsAsync(
                        repository,
                        child.Id,
                        cancellationToken));
                }

                publishedItems = allItems.Count(item =>
                    item.Status == GradingItemStatus.Committed ||
                    item.CommitStatus == GradingCommitStatus.Succeeded);
                executionUnknownItems = allItems.Count(item =>
                    item.CommitStatus == GradingCommitStatus.ExecutionUnknown);
                cleanupSummaryAvailable = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Falha técnica na leitura não deve deixar workers presos,
                // mas torna o expurgo inseguro. O cancelamento prossegue e a
                // limpeza fica disponível para uma nova tentativa.
                warnings.Add(
                    $"A leitura do resumo de segurança para o expurgo falhou ({exception.GetType().Name}); " +
                    "a execução será cancelada, mas os dados locais serão preservados.");
            }
        }

        var unknownPublicationCount = 0;
        if (pendingActions is not null)
        {
            // A preview can be created with either a child batch ID or its
            // parent gradingRunId. When the caller supplies the child handle,
            // inspect both IDs so a run-level publication cannot survive the
            // cancellation of the batch that led the operator here.
            var actionLookupIds = new HashSet<Guid> { request.BatchJobId };
            if (scope.DestinationRun is not null)
            {
                actionLookupIds.Add(scope.DestinationRun.Id);
            }

            var publicationActions = new List<PendingMoodleAction>();
            foreach (var actionLookupId in actionLookupIds)
            {
                publicationActions.AddRange(
                    await pendingActions.ListGradingPublicationsByBatchIdAsync(
                        actionLookupId,
                        cancellationToken));
            }
            publicationActions = publicationActions
                .DistinctBy(action => action.Id)
                .ToList();
            var cancelledPublicationIds = new List<Guid>();
            foreach (var action in publicationActions.Where(action =>
                         scope.Batches.Any(batch =>
                             string.Equals(batch.CreatedBySubject, action.CreatedBySubject, StringComparison.Ordinal)) ||
                         scope.Run is not null &&
                         string.Equals(scope.Run.CreatedBySubject, action.CreatedBySubject, StringComparison.Ordinal)))
            {
                if (action.Status == PendingActionStatus.ExecutionUnknown)
                {
                    // Never release the claim of an unknown remote write. It
                    // remains reserved until explicit reconciliation proves
                    // whether Moodle applied the grade.
                    unknownPublicationCount++;
                    continue;
                }

                var publicationId = TryReadPublicationId(action.PayloadJson);
                action.Cancel("Publicacao cancelada junto com o lote de correcao.");
                if (publicationId is Guid id && id != Guid.Empty)
                {
                    cancelledPublicationIds.Add(id);
                }
            }

            foreach (var publicationId in cancelledPublicationIds.Distinct())
            {
                await repository.ReleasePublicationClaimsAsync(publicationId, cancellationToken);
            }

            if (publicationActions.Count > 0)
            {
                await pendingActions.SaveChangesAsync(cancellationToken);
            }
        }

        var purgeEligible = purgeRequested &&
            cleanupSummaryAvailable &&
            scope.Run?.Status != GradingRunStatus.Completed &&
            publishedItems == 0 &&
            executionUnknownItems == 0 &&
            unknownPublicationCount == 0;

        foreach (var child in scope.Batches)
        {
            if (child.Status == GradingBatchStatus.Completed)
            {
                child.CancelForRecovery();
            }
            else
            {
                await orchestrator.CancelAsync(child.Id, cancellationToken);
            }
        }

        if (scope.Run is not null)
        {
            if (scope.Run.Status == GradingRunStatus.Completed)
            {
                scope.Run.CancelForRecovery();
            }
            else
            {
                scope.Run.Cancel();
            }
            await repository.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await repository.SaveChangesAsync(cancellationToken);
        }

        if (publishedItems > 0)
        {
            warnings.Add($"{publishedItems} item(ns) ja publicado(s) foram preservados.");
        }

        if (executionUnknownItems > 0)
        {
            warnings.Add($"{executionUnknownItems} item(ns) possuem escrita Moodle de resultado desconhecido e exigem reconciliacao.");
        }

        if (unknownPublicationCount > 0)
        {
            warnings.Add($"{unknownPublicationCount} publicacao(oes) pendente(s) possuem resultado desconhecido e continuam reservadas para reconciliacao.");
        }

        GradingLocalPurgeResult? purge = null;
        if (purgeEligible)
        {
            purge = await repository.PurgeCancelledGradingDataAsync(
                scope.Batches.Select(batch => batch.Id).ToArray(),
                scope.Run?.Id,
                cancellationToken);
            if (!purge.Purged && !string.IsNullOrWhiteSpace(purge.BlockReason))
            {
                warnings.Add(purge.BlockReason);
            }
        }

        var status = scope.Run?.Status.ToString() ??
            (scope.FirstBatch?.Status.ToString() ?? GradingBatchStatus.Cancelled.ToString());
        var message = purge?.Purged == true
            ? $"Execucao cancelada e limpeza local concluida: {purge.DeletedItems} item(ns) e {purge.DeletedBatches} sublote(s) removidos. Nenhuma alteracao foi feita no Moodle."
            : scope.Run is not null
                ? $"Execucao cancelada; {scope.Batches.Count} sublote(s) foram sinalizados para cancelamento."
                : "Lote cancelado com sucesso.";

        if (purgeRequested && !purgeEligible)
        {
            warnings.Add("A limpeza física foi bloqueada porque a execução contém publicação concluída, escrita de resultado desconhecido ou já está concluída.");
        }

        if (purgeRequested && purge?.Purged != true)
        {
            message += " Os dados locais foram preservados pelos guardas de seguranca.";
        }

        return new CancelAssistedGradingBatchResult(
            scope.RequestedId,
            status,
            BuildCancellationMessage(message, unknownPublicationCount),
            PurgeRequested: purgeRequested,
            Purged: purge?.Purged == true,
            DeletedItems: purge?.DeletedItems ?? 0,
            DeletedBatches: purge?.DeletedBatches ?? 0,
            DeletedRuns: purge?.DeletedRuns ?? 0,
            PublishedItemsPreserved: publishedItems,
            Warnings: warnings);
    }

    private static Guid? TryReadPublicationId(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if ((!root.TryGetProperty("publicationId", out var value) &&
                 !root.TryGetProperty("PublicationId", out value)) ||
                value.ValueKind != JsonValueKind.String ||
                !value.TryGetGuid(out var publicationId) ||
                publicationId == Guid.Empty)
            {
                return null;
            }

            return publicationId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildCancellationMessage(string message, int unknownPublicationCount) =>
        unknownPublicationCount == 0
            ? message
            : $"{message} {unknownPublicationCount} publicacao(oes) ficou(aram) em execucao desconhecida e nao teve a claim liberada; reconcilie antes de tentar publicar novamente.";
}

// ============================================================
// Query para preparar contexto de correção para o LLM do chat
// ============================================================

public sealed record PrepareGradingContextForChatQuery(
    Guid GradingItemId,
    Guid? BatchJobId = null) : IRequest<GradingContextForChatResult>;

public sealed record GradingContextForChatResult(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("assignmentName")] string? AssignmentName,
    [property: JsonPropertyName("maxGrade")] decimal? MaxGrade,
    [property: JsonPropertyName("isGradable")] bool? IsGradable,
    [property: JsonPropertyName("gradingMode")] string GradingMode,
    [property: JsonPropertyName("submissionId")] string? SubmissionId,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("assignmentStatement")] string? AssignmentStatement,
    [property: JsonPropertyName("studentSubmission")] string? StudentSubmission,
    [property: JsonPropertyName("extractedCriteria")] string? ExtractedCriteria,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("draftFeedback")] string? DraftFeedback,
    [property: JsonPropertyName("suggestedGrade")] decimal? SuggestedGrade,
    [property: JsonPropertyName("confidence")] decimal? Confidence,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("contextHash")] string? ContextHash = null,
    [property: JsonPropertyName("resources")] IReadOnlyList<AiGradingResourceLink>? Resources = null);

public sealed class PrepareGradingContextForChatQueryHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser,
    IMoodleAssignmentSettingsGateway settingsGateway,
    IMoodleResourceGateway? resourceGateway = null,
    IOptions<MoodleUniversalApiFeatureOptions>? resourceFeatures = null,
    IAssignmentContextSelectionService? contextSelectionService = null)
    : IRequestHandler<PrepareGradingContextForChatQuery, GradingContextForChatResult>
{
    public async Task<GradingContextForChatResult> Handle(
        PrepareGradingContextForChatQuery request,
        CancellationToken cancellationToken)
    {
        if (request.GradingItemId == Guid.Empty)
        {
            throw new ArgumentException("O item de correcao e obrigatorio.", nameof(request.GradingItemId));
        }

        var item = await repository.GetItemAsync(request.GradingItemId, cancellationToken)
            ?? throw new InvalidOperationException("Item de correcao nao encontrado.");
        var batch = await repository.GetBatchAsync(item.BatchId, cancellationToken)
            ?? throw new InvalidOperationException("Lote de correcao nao encontrado.");
        GradingAccessControl.EnsureCanAccessBatch(batch, currentUser);

        if (request.BatchJobId is Guid batchJobId && batchJobId != Guid.Empty && item.BatchId != batchJobId)
        {
            throw new InvalidOperationException("O item informado nao pertence ao lote solicitado.");
        }

        var warnings = new List<string>();
        var artifacts = await repository.ListArtifactsByItemAsync(item.Id, cancellationToken);

        // A correção lê os anexos originais no chat. O texto extraído local
        // não participa deste fluxo, mesmo quando existe em artifacts antigos.
        var submissionArtifacts = artifacts
            .Where(a => a.ArtifactType == "submission_file")
            .ToArray();
        var resources = new List<AiGradingResourceLink>();
        if (resourceGateway is null || resourceFeatures?.Value.McpResourceSubmissionDeliveryEnabled != true)
        {
            throw new InvalidOperationException(
                "A correção assistida exige McpResourceSubmissionDeliveryEnabled=true e o gateway MCP Resource disponível.");
        }

        if (submissionArtifacts.Length == 0)
        {
            throw new InvalidOperationException(
                "A entrega não possui anexos originais para disponibilizar como MCP Resource.");
        }
        else
        {
            foreach (var artifact in submissionArtifacts)
            {
                if (string.IsNullOrWhiteSpace(artifact.SourceUrl) || string.IsNullOrWhiteSpace(artifact.Filename))
                {
                    throw new InvalidOperationException(
                        $"O arquivo {artifact.Filename ?? "desconhecido"} não possui referência original disponível para MCP Resource.");
                }

                var descriptor = await resourceGateway.RegisterAsync(
                    new MoodleResourceRegistration(
                        "submission_attachment",
                        artifact.Filename,
                        artifact.MimeType ?? "application/octet-stream",
                        artifact.SourceUrl,
                        item.CourseId,
                        item.AssignmentId,
                        item.SubmissionId,
                        item.MoodleUserId,
                        SizeBytes: artifact.SizeBytes,
                        Sha256: artifact.Sha256),
                    cancellationToken);
                resources.Add(new AiGradingResourceLink(
                    descriptor.Uri,
                    descriptor.Filename,
                    descriptor.MimeType,
                    descriptor.SizeBytes,
                    ResourceType: "submission",
                    ArtifactId: artifact.Id));
            }

            warnings.Add("Entrega fornecida por MCP Resource; leia os arquivos originais antes de propor a correção.");
        }

        // Materiais binários de contexto também seguem diretamente para o chat.
        // Descrições textuais vindas da API Moodle continuam como contexto
        // textual, mas nenhum arquivo de contexto é baixado ou extraído aqui.
        foreach (var artifact in artifacts.Where(a =>
                     a.ArtifactType == "assignment_context" &&
                     !string.IsNullOrWhiteSpace(a.SourceUrl) &&
                     !string.IsNullOrWhiteSpace(a.Filename)))
        {
            try
            {
                var descriptor = await resourceGateway.RegisterAsync(
                    new MoodleResourceRegistration(
                        "assignment_context_attachment",
                        artifact.Filename!,
                        artifact.MimeType ?? "application/octet-stream",
                        artifact.SourceUrl!,
                        item.CourseId,
                        item.AssignmentId,
                        SubmissionId: null,
                        StudentId: null,
                        SizeBytes: artifact.SizeBytes,
                        Sha256: artifact.Sha256),
                    cancellationToken);
                resources.Add(new AiGradingResourceLink(
                    descriptor.Uri,
                    descriptor.Filename,
                    descriptor.MimeType,
                    descriptor.SizeBytes,
                    ResourceType: "assignment_context",
                    ArtifactId: artifact.Id));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                warnings.Add($"Material de contexto {artifact.Filename} não foi disponibilizado como MCP Resource ({exception.GetType().Name}).");
            }
        }

        // Texto do enunciado da atividade
        var contextSelection = await AssignmentContextArtifactSelector.SelectAsync(
            artifacts,
            item.CourseId.ToString(CultureInfo.InvariantCulture),
            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            $"Tarefa {item.AssignmentId.ToString(CultureInfo.InvariantCulture)}",
            contextSelectionService,
            cancellationToken);
        warnings.AddRange(contextSelection.Warnings);
        var contextArtifact = contextSelection.Artifact;
        var assignmentStatement = contextArtifact?.ExtractedTextRef;
        // Prefer the official Moodle assignment name from the settings API;
        // fall back to the context artifact filename (usually the PDF name).
        var assignmentName = contextArtifact?.Filename;

        // MaxGrade + assignment name via API Moodle
        decimal? maxGrade = null;
        bool? isGradable = null;
        try
        {
            var settings = await settingsGateway.GetAssignmentSettingsAsync(
                batch.CreatedBySubject,
                item.CourseId.ToString(CultureInfo.InvariantCulture),
                item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
            isGradable = settings?.IsGradable;
            if (settings != null && settings.MaxGrade > 0)
            {
                maxGrade = settings.MaxGrade;
            }
            else if (settings?.IsGradable == false)
            {
                warnings.Add("Esta atividade nao possui avaliacao numerica no Moodle. Envie somente feedback, sem nota.");
            }
            else
            {
                warnings.Add("Nota maxima nao encontrada via API Moodle. Sugestao numerica bloqueada; sinalize a situacao no CSV para ajuste manual.");
            }

            // Override assignmentName with the real Moodle name when available
            if (!string.IsNullOrWhiteSpace(settings?.Name))
            {
                assignmentName = settings.Name;
            }
            if (string.IsNullOrWhiteSpace(assignmentStatement) && !string.IsNullOrWhiteSpace(settings?.Description))
            {
                assignmentStatement = settings.Description;
            }
        }
        catch
        {
            warnings.Add("Falha ao buscar nota maxima via API Moodle. Sugestao numerica bloqueada; sinalize a situacao no CSV para ajuste manual.");
        }

        // Critérios extraídos (se existirem do processamento anterior)
        var evidence = await repository.ListEvidenceByItemAsync(item.Id, cancellationToken);
        var extractedCriteria = evidence.Count > 0
            ? string.Join("\n", evidence.Select(e => $"- {e.CriterionText}"))
            : null;

        var gradeInstruction = maxGrade is decimal knownMax
            ? $"A nota maxima desta atividade e {knownMax} pontos. Sugira uma nota de 0 a {knownMax}."
            : isGradable == false
                ? "Esta atividade nao possui avaliacao numerica no Moodle. Produza somente feedback qualitativo e nao sugira nota."
            : "A escala de notas desta atividade nao foi confirmada. Nao sugira nem calcule nota numerica; produza somente feedback qualitativo e sinalize a situacao no CSV para ajuste manual.";
        var instructions = AiGradingPromptPolicy.AppendUntrustedEvidenceRules(
            $"Voce e um tutor educacional. Analise a entrega do aluno comparando com o enunciado da atividade. " +
            gradeInstruction + " " +
            $"Gere um feedback pedagogico em linguagem natural (paragrafos, nao listas) que: " +
            $"1) Reconheca os pontos fortes citando elementos concretos da entrega; " +
            $"2) Indique melhorias especificas quando houver lacunas; " +
            (maxGrade is not null ? "3) Sugira uma nota somente dentro da escala confirmada. " : "3) Nao inclua nota numerica. ") +
            $"O feedback deve ser adequado para colar diretamente no Moodle. " +
            $"Se a entrega pertencer claramente a outra atividade ou nao responder ao enunciado desta atividade, classifique-a como entrega_incompativel, sugira nota 0 quando houver escala numerica, e escreva um feedback especifico explicando que a atividade enviada nao corresponde a esta proposta e qual tipo de resposta deve ser enviada. Nunca descarte a entrega nem deixe o feedback vazio. " +
            $"Nao exija saudacao nominal: este contexto fornece apenas studentId, que nao e um nome. " +
            $"Para uma proposta estruturada, cite pelo menos um artifactId real de resourceType submission em evidence e descreva o arquivo ou trecho observado; use os IDs exatamente como recebidos no pacote. " +
            $"Para ZIP, importe o JSON validado com import_grading_package_results; para resources individuais, salve com save_ai_grading_batch. Se o usuario pediu CSV, use export_grading_corrections_csv; caso contrario, chame create_batch_grade_launch_preview e, se hasMore=true, recupere todas as paginas com get_grading_publication_status antes de apresentar o escopo completo. Apos uma resposta afirmativa sobre essa previa, chame confirm_batch_grade_launch somente com pendingActionId. Depois acompanhe get_grading_publication_status ate cada item ficar succeeded; authorized e retry_scheduled nao significam publicado, e retries seguros usam a mesma aprovacao sem pedir outra.");

        return new GradingContextForChatResult(
            item.Id,
            item.BatchId,
            item.CourseId.ToString(CultureInfo.InvariantCulture),
            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            assignmentName,
            maxGrade,
            isGradable,
            ResolveGradingMode(maxGrade, isGradable),
            item.SubmissionId?.ToString(CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
            assignmentStatement,
            StudentSubmission: null,
            extractedCriteria,
            item.Status.ToString(),
            item.DraftFeedback,
            item.SuggestedGrade,
            item.Confidence,
            instructions,
            warnings,
            item.ContextHash,
            resources);
    }

    private static string ResolveGradingMode(decimal? maxGrade, bool? isGradable) =>
        isGradable switch
        {
            false => "feedback_only",
            true when maxGrade is > 0 => "numeric",
            true => "scale",
            _ => "unknown"
        };
}

// ============================================================
// Query para preparar pacote IA em lote (preparar_lote_correcao_ia)
// ============================================================

public sealed record PrepareAiGradingBatchQuery(
    Guid BatchJobId,
    int Page = 1,
    int PageSize = 400) : IRequest<AiGradingBatchPackageResult>;

public sealed record AiGradingBatchPackageResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("assignmentIds")] IReadOnlyList<string> AssignmentIds,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("items")] IReadOnlyList<AiGradingBatchItemPackage> Items,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("gradingRunId")] Guid? GradingRunId = null,
    [property: JsonPropertyName("page")] int Page = 1,
    [property: JsonPropertyName("pageSize")] int PageSize = 400,
    [property: JsonPropertyName("hasMore")] bool HasMore = false,
    [property: JsonPropertyName("nextPage")] int? NextPage = null,
    [property: JsonPropertyName("expectedItems")] int ExpectedItems = 0,
    [property: JsonPropertyName("preparedItems")] int PreparedItems = 0,
    [property: JsonPropertyName("missingItems")] int MissingItems = 0,
    [property: JsonPropertyName("missingBatchCount")] int MissingBatchCount = 0,
    [property: JsonPropertyName("decisionSafe")] bool DecisionSafe = true,
    [property: JsonPropertyName("assignments")] IReadOnlyList<AiGradingAssignmentPackage>? Assignments = null,
    [property: JsonPropertyName("eligibleItems")] int EligibleItems = 0,
    [property: JsonPropertyName("itemsExcludedByStatus")] int ItemsExcludedByStatus = 0,
    [property: JsonPropertyName("itemsOnPage")] int ItemsOnPage = 0,
    [property: JsonPropertyName("recommendedTransport")] string RecommendedTransport = "individual_resources",
    [property: JsonPropertyName("recommendationReason")] string? RecommendationReason = null,
    [property: JsonPropertyName("nextAction")] string NextAction = "read_resources");

public sealed record AiGradingAssignmentPackage(
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("contextResources")] IReadOnlyList<AiGradingAssignmentContextResource> ContextResources);

public sealed record AiGradingAssignmentContextResource(
    [property: JsonPropertyName("resourceId")] string ResourceId,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("contentHash")] string? ContentHash = null);

public sealed record AiGradingBatchItemPackage(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("submissionId")] string? SubmissionId,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("assignmentName")] string? AssignmentName,
    [property: JsonPropertyName("maxGrade")] decimal? MaxGrade,
    [property: JsonPropertyName("isGradable")] bool? IsGradable,
    [property: JsonPropertyName("gradingMode")] string GradingMode,
    [property: JsonPropertyName("assignmentStatement")] string? AssignmentStatement,
    [property: JsonPropertyName("extractedCriteria")] string? ExtractedCriteria,
    [property: JsonPropertyName("extractedText")] string? ExtractedText,
    [property: JsonPropertyName("textTruncated")] bool TextTruncated,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("contextHash")] string? ContextHash = null,
    [property: JsonPropertyName("resourceDeliveryMode")] string ResourceDeliveryMode = "mcp_resource",
    [property: JsonPropertyName("resources")] IReadOnlyList<AiGradingResourceLink>? Resources = null,
    [property: JsonPropertyName("contextResourceRefs")] IReadOnlyList<string>? ContextResourceRefs = null);

public sealed record AiGradingResourceLink(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("resourceType")] string ResourceType = "submission",
    [property: JsonPropertyName("artifactId")] Guid? ArtifactId = null);

public sealed class PrepareAiGradingBatchQueryHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser,
    IMoodleAssignmentSettingsGateway settingsGateway,
    IGradingOperationTelemetry? telemetry = null,
    IMoodleResourceGateway? resourceGateway = null,
    IOptions<MoodleUniversalApiFeatureOptions>? resourceFeatures = null,
    IAssignmentContextSelectionService? contextSelectionService = null)
    : IRequestHandler<PrepareAiGradingBatchQuery, AiGradingBatchPackageResult>
{
    private const int MaxPageSize = 400;

    public async Task<AiGradingBatchPackageResult> Handle(
        PrepareAiGradingBatchQuery request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _ = settingsGateway; // Compatibilidade de DI; a escala canônica vem do snapshot local.
        if (request.BatchJobId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(request.BatchJobId));
        }

        var scope = await GradingBatchScopeResolver.ResolveAsync(
            repository,
            currentUser,
            request.BatchJobId,
            cancellationToken);
        var batch = scope.FirstBatch;
        if (batch is null)
        {
            const string emptyInstructions = "Nao ha itens elegiveis para preparar nesta execucao. Se a execucao ainda estiver em processamento, repita a chamada usando o mesmo gradingRunId quando os sublotes estiverem prontos.";
            var emptyCoverage = await GradingRunCoverageCalculator.CalculateAsync(
                repository,
                scope,
                preparedItems: 0,
                cancellationToken: cancellationToken);
            return new AiGradingBatchPackageResult(
                request.BatchJobId,
                string.Empty,
                [],
                0,
                [],
                emptyInstructions,
                ["A execucao ainda nao possui sublotes com itens elegiveis."],
                scope.Run?.Id ?? scope.DestinationRun?.Id,
                Math.Max(1, request.Page),
                Math.Clamp(request.PageSize, 1, MaxPageSize),
                false,
                null,
                ExpectedItems: emptyCoverage.ExpectedItems,
                PreparedItems: emptyCoverage.PreparedItems,
                MissingItems: emptyCoverage.MissingItems,
                MissingBatchCount: emptyCoverage.MissingBatchCount,
                DecisionSafe: emptyCoverage.DecisionSafe);
        }

        var globalWarnings = new List<string>();
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);
        IReadOnlyList<AssistedGradingItem> items;
        IReadOnlyList<AssistedGradingItem> pageItems;
        int eligibleItemCount;
        int totalScopedItems;
        if (scope.Run is not null)
        {
            // Keep pagination in SQL for aggregate runs. Materializing all
            // children here made page 25 of a 10k run read the same 10k rows
            // again, multiplying database and memory pressure for each AI
            // page request. The page itself is intentionally selected without
            // a status predicate: saving page 1 changes those items from
            // AwaitingAiAnalysis to DraftReady, and applying Skip to a
            // shrinking filtered set would otherwise skip the next 400 items
            // when the caller requests page 2.
            var stablePageItems = await repository.ListItemsByGradingRunAsync(
                scope.Run.Id,
                page,
                pageSize,
                status: null,
                cancellationToken);
            eligibleItemCount = await repository.CountItemsByGradingRunAsync(
                scope.Run.Id,
                GradingItemStatus.AwaitingAiAnalysis,
                cancellationToken);
            totalScopedItems = await repository.CountItemsByGradingRunAsync(
                scope.Run.Id,
                status: null,
                cancellationToken);
            items = stablePageItems;
            pageItems = stablePageItems
                .Where(item => item.Status == GradingItemStatus.AwaitingAiAnalysis)
                .ToArray();
        }
        else
        {
            var allScopeItems = await LoadAllScopeItemsAsync(scope.Batches, cancellationToken);
            var eligibleItems = allScopeItems
                .Where(item => item.Status == GradingItemStatus.AwaitingAiAnalysis)
                .ToArray();
            eligibleItemCount = eligibleItems.Length;
            totalScopedItems = allScopeItems.Count;
            // Keep the legacy child-batch path consistent with aggregate runs:
            // page boundaries are based on immutable creation order, not on a
            // status-filtered set that shrinks after each save operation.
            items = allScopeItems
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            pageItems = items
                .Where(item => item.Status == GradingItemStatus.AwaitingAiAnalysis)
                .ToArray();
        }
        var pageItemIds = pageItems.Select(item => item.Id).ToArray();
        var artifactsByItem = await repository.ListArtifactsByItemsAsync(pageItemIds, cancellationToken);
        var evidenceByItem = await repository.ListEvidenceByItemsAsync(pageItemIds, cancellationToken);
        var snapshotsByItem = await repository.ListLatestContextSnapshotsByItemsAsync(pageItemIds, cancellationToken);
        var packageItems = new List<AiGradingBatchItemPackage>();
        var skippedByStatus = scope.Run is not null
            ? []
            : items
            .Where(item => item.Status != GradingItemStatus.AwaitingAiAnalysis)
            .GroupBy(item => item.Status.ToString())
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Count()} {FormatSkippedStatus(group.Key)}")
            .ToArray();
        if (skippedByStatus.Length > 0)
        {
            globalWarnings.Add(
                $"Itens fora da pre-validacao da IA foram ignorados: {string.Join(", ", skippedByStatus)}.");
        }

        var itemsExcludedByStatus = Math.Max(0, totalScopedItems - eligibleItemCount);
        if (scope.Run is not null && itemsExcludedByStatus > 0)
        {
            globalWarnings.Add(
                $"A execucao possui {itemsExcludedByStatus} item(ns) ja fora da fila de analise da IA; eles nao foram repetidos neste pacote. Use preparedItems/expectedItems para validar a cobertura total.");
        }

        if (pageItems.Count > 0 &&
            (resourceGateway is null || resourceFeatures?.Value.McpResourceSubmissionDeliveryEnabled != true))
        {
            throw new InvalidOperationException(
                "A correção assistida exige McpResourceSubmissionDeliveryEnabled=true e o gateway MCP Resource disponível.");
        }

        var submissionRegistrationEntries = new List<(Guid ItemId, Guid ArtifactId, MoodleResourceRegistration Registration)>();
        var contextRegistrationEntries = new List<(long AssignmentId, string ResourceId, string Filename, string? ContentHash, MoodleResourceRegistration Registration)>();
        foreach (var item in pageItems)
        {
            var artifacts = artifactsByItem.GetValueOrDefault(item.Id, []);
            var submissionArtifacts = artifacts
                .Where(artifact => artifact.ArtifactType == "submission_file")
                .ToArray();
            if (submissionArtifacts.Length == 0 || submissionArtifacts.Any(artifact =>
                    string.IsNullOrWhiteSpace(artifact.SourceUrl) ||
                    string.IsNullOrWhiteSpace(artifact.Filename)))
            {
                throw new InvalidOperationException(
                    $"A entrega do item {item.Id} não possui referências originais completas para MCP Resource.");
            }

            submissionRegistrationEntries.AddRange(submissionArtifacts.Select(artifact =>
                (item.Id, artifact.Id, new MoodleResourceRegistration(
                    "submission_attachment",
                    artifact.Filename!,
                    artifact.MimeType ?? "application/octet-stream",
                    artifact.SourceUrl!,
                    item.CourseId,
                    item.AssignmentId,
                    item.SubmissionId,
                    item.MoodleUserId,
                    SizeBytes: artifact.SizeBytes,
                    Sha256: artifact.Sha256))));

            contextRegistrationEntries.AddRange(artifacts
                .Where(artifact => artifact.ArtifactType == "assignment_context" &&
                                   !string.IsNullOrWhiteSpace(artifact.SourceUrl) &&
                                   !string.IsNullOrWhiteSpace(artifact.Filename))
                .Select(artifact => (
                    item.AssignmentId,
                    BuildAssignmentContextResourceId(item.CourseId, item.AssignmentId, artifact),
                    artifact.Filename!,
                    artifact.Sha256,
                    new MoodleResourceRegistration(
                        "assignment_context_attachment",
                        artifact.Filename!,
                        artifact.MimeType ?? "application/octet-stream",
                        artifact.SourceUrl!,
                        item.CourseId,
                        item.AssignmentId,
                        SubmissionId: null,
                        StudentId: null,
                        SizeBytes: artifact.SizeBytes,
                        Sha256: artifact.Sha256))));
        }

        var resourceLinksByItem = new Dictionary<Guid, IReadOnlyList<AiGradingResourceLink>>();
        if (submissionRegistrationEntries.Count > 0)
        {
            var descriptors = await resourceGateway!.RegisterManyAsync(
                submissionRegistrationEntries.Select(entry => entry.Registration).ToArray(),
                cancellationToken);
            if (descriptors.Count != submissionRegistrationEntries.Count)
            {
                throw new InvalidOperationException("O gateway MCP Resource retornou uma quantidade inconsistente de anexos registrados.");
            }

            resourceLinksByItem = submissionRegistrationEntries
                .Select((entry, index) => (entry.ItemId, Link: new AiGradingResourceLink(
                    descriptors[index].Uri,
                    descriptors[index].Filename,
                    descriptors[index].MimeType,
                    descriptors[index].SizeBytes,
                    ResourceType: "submission",
                    ArtifactId: entry.ArtifactId)))
                .GroupBy(entry => entry.ItemId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<AiGradingResourceLink>)group.Select(entry => entry.Link).ToArray());
        }

        // Assignment context is shared by all students in one activity. It is
        // registered and serialized once per stable context identity, while
        // each item only carries compact references to that assignment-level
        // material. Submission resources remain individual and strict.
        var assignmentContexts = new List<AiGradingAssignmentPackage>();
        var contextResourceRefsByAssignment = new Dictionary<long, IReadOnlyList<string>>();
        var uniqueContextEntries = contextRegistrationEntries
            .GroupBy(entry => entry.ResourceId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (uniqueContextEntries.Length > 0)
        {
            try
            {
                var descriptors = await resourceGateway!.RegisterManyAsync(
                    uniqueContextEntries.Select(entry => entry.Registration).ToArray(),
                    cancellationToken);
                if (descriptors.Count != uniqueContextEntries.Length)
                {
                    throw new InvalidOperationException("O gateway MCP Resource retornou uma quantidade inconsistente de materiais de contexto registrados.");
                }

                assignmentContexts = uniqueContextEntries
                    .Select((entry, index) => (entry.AssignmentId, Resource: new AiGradingAssignmentContextResource(
                        entry.ResourceId,
                        descriptors[index].Uri,
                        descriptors[index].Filename,
                        descriptors[index].MimeType,
                        descriptors[index].SizeBytes,
                        descriptors[index].Sha256 ?? entry.ContentHash)))
                    .GroupBy(entry => entry.AssignmentId)
                    .OrderBy(group => group.Key)
                    .Select(group => new AiGradingAssignmentPackage(
                        group.Key.ToString(CultureInfo.InvariantCulture),
                        group.Select(entry => entry.Resource).ToArray()))
                    .ToList();
                contextResourceRefsByAssignment = assignmentContexts.ToDictionary(
                    assignment => long.Parse(assignment.AssignmentId, CultureInfo.InvariantCulture),
                    assignment => (IReadOnlyList<string>)assignment.ContextResources
                        .Select(resource => resource.ResourceId)
                        .ToArray());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                foreach (var assignment in uniqueContextEntries
                    .GroupBy(entry => entry.AssignmentId)
                    .OrderBy(group => group.Key))
                {
                    var files = string.Join(", ", assignment
                        .Select(entry => entry.Filename)
                        .Distinct(StringComparer.Ordinal));
                    globalWarnings.Add(
                        $"Atividade {assignment.Key}: materiais de contexto ({files}) não foram disponibilizados como MCP Resource ({exception.GetType().Name}).");
                }
            }
        }

        foreach (var item in pageItems)
        {
            var itemWarnings = new List<string>();
            IReadOnlyList<GradingArtifact> artifacts = artifactsByItem.GetValueOrDefault(item.Id, []);
            var resourceLinks = resourceLinksByItem.GetValueOrDefault(item.Id, []).ToList();
            var contextResourceRefs = contextResourceRefsByAssignment.GetValueOrDefault(item.AssignmentId, []);

            itemWarnings.Add("Entrega fornecida por MCP Resource; leia os arquivos originais antes de propor a correção.");

            // Escala e nome vêm do snapshot canônico publicado pelo worker.
            // O nome participa do ranking para que SAP-XX específico vença
            // cronogramas e planos genéricos mesmo quando estes têm mais texto.
            var snapshot = snapshotsByItem.GetValueOrDefault(item.Id);
            var snapshotData = ParseSnapshotDisplay(snapshot?.PayloadJson);

            // Contexto da atividade
            var contextSelection = await AssignmentContextArtifactSelector.SelectAsync(
                artifacts,
                item.CourseId.ToString(CultureInfo.InvariantCulture),
                item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                snapshotData.ActivityName ?? $"Tarefa {item.AssignmentId.ToString(CultureInfo.InvariantCulture)}",
                contextSelectionService,
                cancellationToken);
            itemWarnings.AddRange(contextSelection.Warnings);
            var contextArtifact = contextSelection.Artifact;
            var assignmentStatement = contextArtifact?.ExtractedTextRef;
            var assignmentName = contextArtifact?.Filename;

            // Critérios (evidências do processamento anterior, se existirem)
            var evidence = evidenceByItem.GetValueOrDefault(item.Id, []);
            var extractedCriteria = evidence.Count > 0
                ? string.Join("\n", evidence.Select(e => $"- {e.CriterionText}"))
                : null;

            // Escala, nome e enunciado vêm do snapshot canônico publicado pelo
            // worker. A preparação da IA nunca reconstrói contexto no Moodle.
            assignmentName = snapshotData.ActivityName ?? assignmentName;
            assignmentStatement ??= snapshotData.AssignmentStatement;
            var maxGrade = snapshotData.MaxGrade;
            var isGradable = snapshotData.IsGradable;
            itemWarnings.AddRange(snapshotData.Warnings);

            if (maxGrade is null)
            {
                itemWarnings.Add(isGradable == false
                    ? "Atividade sem avaliacao numerica no Moodle. Gere somente feedback, sem nota."
                    : "Nota maxima nao encontrada via API Moodle. Sugestao numerica bloqueada; sinalize a situacao no CSV para ajuste manual.");
            }

            packageItems.Add(new AiGradingBatchItemPackage(
                item.Id,
                item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                item.SubmissionId?.ToString(CultureInfo.InvariantCulture),
                item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
                assignmentName,
                maxGrade,
                isGradable,
                snapshotData.GradingMode,
                assignmentStatement,
                extractedCriteria,
                ExtractedText: null,
                TextTruncated: false,
                itemWarnings,
                item.ContextHash,
                ResourceDeliveryMode: "mcp_resource",
                Resources: resourceLinks,
                ContextResourceRefs: contextResourceRefs));
        }

        if (packageItems.Count == 0)
        {
            globalWarnings.Add("Nenhum item apto para analise pela IA foi encontrado no lote.");
        }

        var instructions = AiGradingPromptPolicy.AppendUntrustedEvidenceRules(
            "Voce e um tutor educacional. Para cada aluno no pacote, analise a entrega comparando com o enunciado e criterios da atividade. " +
            "Gere um feedback curto e direto (maximo 6 paragrafos) seguindo esta estrutura exata:\n" +
            "1) SAUDACAO: use uma saudacao neutra. Este pacote fornece apenas studentId; nunca trate esse identificador como nome e nunca invente ou derive um nome a partir dele.\n" +
            "2) RECONHECIMENTO: agradeca a entrega em uma frase curta.\n" +
            "3) PONTO POSITIVO: destaque algo concreto que o aluno fez bem na entrega.\n" +
            "4) MELHORIAS: indique de forma clara e respeitosa os pontos que precisam ser revistos ou aprofundados. Seja especifico sobre o que ajustar, sem frases genericas como 'esta errado'. Se houver lacuna, sugira que o aluno revise os conceitos relacionados no material de apoio do curso, sem citar nomes de aulas ou documentos especificos.\n" +
            "5) INCENTIVO: finalize com uma mensagem motivadora curta.\n" +
            "6) ENCERRAMENTO: encerre com 'Em caso de duvidas, estou a disposicao.'\n\n" +
            "Regras de estilo:\n" +
            "- Use linguagem clara, respeitosa e objetiva.\n" +
            "- Tom acolhedor e profissional. Sem julgamentos pessoais, ironias ou comparacoes.\n" +
            "- Escreva em paragrafos (nao use listas com marcadores).\n" +
            "- O feedback inteiro deve ter entre 80 e 200 palavras.\n" +
            "- Cada feedback deve ser individual: inclua pelo menos uma observacao factual da submissao deste aluno e uma lacuna ou proximo passo relacionado a essa evidencia. Nunca copie o mesmo feedback para alunos diferentes.\n" +
            "- Se nao houver evidencia suficiente da submissao para individualizar o retorno, nao invente detalhes: marque a proposta para revisao humana e nao a trate como pronta para publicacao.\n" +
            "- Atribua nota numerica somente quando maxGrade estiver informado; caso contrario, nao inclua nota.\n" +
            "- Uma entrega claramente de outra atividade deve receber nota 0 (quando a escala existir) e feedback individual explicando a incompatibilidade e o proximo passo do aluno; isso e um rascunho para revisao humana, nao um bloqueio tecnico.\n" +
            "- Os materiais compartilhados da atividade ficam em assignments[].contextResources. Cada item informa em contextResourceRefs quais resourceIds deve consultar. Identifique automaticamente o enunciado priorizando o arquivo cujo nome e numero correspondam a atividade; ignore materiais genericos, folhas de resposta e arquivos de outra atividade. Se nenhum contexto corresponder com seguranca, nao invente criterios nem nota e sinalize revisao humana.\n" +
            "- Rota eficiente de arquivos: use eligibleItems do resultado. Com mais de um item e cliente que abre ZIP, chame prepare_grading_packages uma vez para o gradingRunId (preferido) ou batchJobId, em packageMode='per_activity'. Leia cada pacote assim que ficar pronto; consulte get_grading_package_status respeitando retryAfterSeconds, com 5 s padrao, maximo 30 s e orcamento de espera de 120 s. Ao esgotar o orcamento, use resources individuais somente para itens ainda sem evidencia. Importe resultados de cada parte por import_grading_package_results usando packageId, manifestHash e resultsJson conforme formato-resposta.schema.json. Cubra cada gradingItemId uma vez. Se uma parte estiver indisponivel, incompleta, acima do limite ou nao puder ser aberta no cliente, leia resources individuais somente para seus itens ainda sem rascunho e salve-os com save_ai_grading_batch. Com um item ou cliente sem suporte a ZIP, use resources individuais desde o inicio.\n" +
            "- Para cada proposta, cite pelo menos um artifactId real de resourceType 'submission' em proposal.evidence[].artifactId e, se possivel, a URI original correspondente. Nunca invente UUIDs; ao usar ZIP, confira o manifesto e associe os artifactIds ao gradingItemId correto.\n" +
            "- No fallback individual, ao chamar save_ai_grading_batch, preencha proposal.resourceUris com todas e somente as URIs originais de submission resources do item. Nunca use a URI do ZIP nesse campo nem inclua contextResources; estes podem ser citados somente em proposal.evidence[].resourceUri. Para resultado lido do ZIP, use import_grading_package_results conforme o schema.\n" +
            "O feedback deve ser adequado para colar diretamente no Moodle. " +
            "Use import_grading_package_results para resultados de ZIP e save_ai_grading_batch para o fallback individual. Se o usuario pediu CSV, chame export_grading_corrections_csv; caso contrario, chame create_batch_grade_launch_preview e recupere todas as paginas da previa por get_grading_publication_status antes de mostra-la. Depois da aprovacao afirmativa do usuario, confirme somente com pendingActionId e acompanhe get_grading_publication_status ate succeeded; authorized e retry_scheduled nao significam publicado, e retries seguros nao pedem nova aprovacao.");

        // The traversal cursor is the stable all-item page, while
        // eligibleItemCount remains the number of items that still need AI.
        // This avoids skipping work when saving a previous page changes item
        // statuses between requests.
        var hasMore = totalScopedItems > page * pageSize;
        var coverage = await GradingRunCoverageCalculator.CalculateAsync(
            repository,
            scope,
            totalScopedItems,
            cancellationToken);
        if (!coverage.DecisionSafe)
        {
            globalWarnings.Add(
                $"Cobertura incompleta da execucao: esperados {coverage.ExpectedItems}, preparados {coverage.PreparedItems}, ausentes {coverage.MissingItems} em {coverage.MissingBatchCount} sublote(s). Nao use esta pagina para publicacao global sem allowPartial=true.");
        }
        var result = new AiGradingBatchPackageResult(
            scope.RequestedId,
            scope.CourseId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            scope.AssignmentIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
            eligibleItemCount,
            packageItems,
            instructions,
            globalWarnings,
            scope.Run?.Id ?? scope.DestinationRun?.Id,
            page,
            pageSize,
            hasMore,
            hasMore ? page + 1 : null,
            ExpectedItems: coverage.ExpectedItems,
            PreparedItems: coverage.PreparedItems,
            MissingItems: coverage.MissingItems,
            MissingBatchCount: coverage.MissingBatchCount,
            DecisionSafe: coverage.DecisionSafe,
            Assignments: assignmentContexts,
            EligibleItems: eligibleItemCount,
            ItemsExcludedByStatus: itemsExcludedByStatus,
            ItemsOnPage: packageItems.Count,
            RecommendedTransport: eligibleItemCount > 1 ? "package_zip" : eligibleItemCount == 1 ? "individual_resources" : "none",
            RecommendationReason: eligibleItemCount > 1 ? "multiple_eligible_items" : eligibleItemCount == 1 ? "single_eligible_item" : "no_eligible_items",
            NextAction: !coverage.DecisionSafe
                ? "resolve_coverage"
                : eligibleItemCount > 1 ? "prepare_packages" : eligibleItemCount == 1 ? "read_resources" : "review");
        telemetry?.RecordPhase(
            "grading",
            "package",
            "success",
            stopwatch.Elapsed.TotalMilliseconds,
            queryCount: 5,
            itemCount: packageItems.Count);
        return result;
    }

    private static string BuildAssignmentContextResourceId(
        long courseId,
        long assignmentId,
        GradingArtifact artifact)
    {
        var identity = string.Join(
            "\n",
            courseId.ToString(CultureInfo.InvariantCulture),
            assignmentId.ToString(CultureInfo.InvariantCulture),
            artifact.SourceUrl?.Trim() ?? string.Empty,
            artifact.Filename?.Trim() ?? string.Empty,
            artifact.MimeType?.Trim() ?? string.Empty,
            artifact.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            artifact.Sha256?.Trim() ?? string.Empty);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"ctx-{hash[..24]}";
    }

    private async Task<IReadOnlyList<AssistedGradingItem>> LoadAllScopeItemsAsync(
        IReadOnlyList<AssistedGradingBatch> batches,
        CancellationToken cancellationToken)
    {
        var result = new List<AssistedGradingItem>();
        foreach (var child in batches)
        {
            var items = await GradingItemProcessor.LoadAllBatchItemsAsync(repository, child.Id, cancellationToken);
            result.AddRange(items);
        }

        return result
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToArray();
    }

    private static string FormatSkippedStatus(string status) => status switch
    {
        "Blocked" => "bloqueado(s)",
        "Failed" => "falho(s)",
        "Pending" => "aguardando processamento",
        "DraftReady" => "com rascunho pronto para CSV",
        "ReadyToCommit" => "pronto para CSV",
        "Committed" => "ja lancado(s) no Moodle",
        _ => status
    };

    private static SnapshotDisplayData ParseSnapshotDisplay(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new SnapshotDisplayData(null, null, null, null, "unknown", ["Snapshot de contexto ainda não publicado; escala não confirmada."]);
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            var activityName = GetString(root, "ActivityName");
            var statement = GetString(root, "AssignmentStatement");
            decimal? maxGrade = null;
            bool? isGradable = null;
            var gradingMode = "unknown";
            if (TryGetProperty(root, "GradingScale", out var scale) && scale.ValueKind == JsonValueKind.Object)
            {
                maxGrade = GetDecimal(scale, "MaximumGrade");
                gradingMode = GetString(scale, "GradingMode")?.Trim().ToLowerInvariant() switch
                {
                    "numeric" => "numeric",
                    "scale" => "scale",
                    "feedback_only" => "feedback_only",
                    _ when maxGrade is > 0m => "numeric",
                    _ when !string.IsNullOrWhiteSpace(GetString(scale, "Name")) ||
                           !string.IsNullOrWhiteSpace(GetString(scale, "Description")) => "scale",
                    _ => "unknown"
                };
                isGradable = gradingMode switch
                {
                    "feedback_only" => false,
                    "numeric" or "scale" => true,
                    _ => null
                };
            }

            var warnings = GetStringArray(root, "Warnings").Concat(GetStringArray(root, "Blockers"))
                .Distinct(StringComparer.Ordinal).ToArray();
            return new SnapshotDisplayData(activityName, statement, maxGrade, isGradable, gradingMode, warnings);
        }
        catch (JsonException)
        {
            return new SnapshotDisplayData(null, null, null, null, "unknown", ["Snapshot de contexto inválido; escala não confirmada."]);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? GetDecimal(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDecimal(out var number)
            ? number
            : null;

    private static IEnumerable<string> GetStringArray(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(item => !string.IsNullOrWhiteSpace(item))
            : [];

    private sealed record SnapshotDisplayData(
        string? ActivityName,
        string? AssignmentStatement,
        decimal? MaxGrade,
        bool? IsGradable,
        string GradingMode,
        IReadOnlyList<string> Warnings);
}

// ============================================================
// Command para salvar correções geradas pela IA (salvar_correcoes_ia_lote)
// ============================================================

public sealed record SaveAiGradingBatchCommand(
    Guid BatchJobId,
    IReadOnlyList<AiGradingItemInput> Items,
    bool TrustedPackageImport = false,
    IReadOnlyDictionary<Guid, string>? ExpectedDraftVersionHashes = null) : IRequest<SaveAiGradingBatchResult>;

public sealed record AiGradingItemInput(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    // Mantido apenas para compatibilidade com clientes legados. O nome não é
    // fonte de identidade nem é persistido: o item usa somente MoodleUserId.
    [property: JsonPropertyName("nome")] string? Nome,
    [property: JsonPropertyName("nota")] decimal? Nota,
    [property: JsonPropertyName("feedback")] string Feedback,
    [property: JsonPropertyName("proposal")] AiGradingProposalInput? Proposal = null);

public sealed record SaveAiGradingBatchResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("savedItems")] int SavedItems,
    [property: JsonPropertyName("skippedItems")] int SkippedItems,
    [property: JsonPropertyName("failedItems")] int FailedItems,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("nextStep")] string NextStep,
    [property: JsonPropertyName("updatedItems")] IReadOnlyList<SaveAiGradingBatchItemResult>? UpdatedItems = null);

public sealed record SaveAiGradingBatchItemResult(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("draftVersionHash")] string DraftVersionHash,
    [property: JsonPropertyName("suggestedGrade")] decimal? SuggestedGrade,
    [property: JsonPropertyName("draftFeedback")] string? DraftFeedback,
    [property: JsonPropertyName("scoreNormalization")] GradingScoreNormalizationSnapshot? ScoreNormalization = null);

public sealed class SaveAiGradingBatchCommandHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser,
    IMoodleUserResolver moodleUserResolver,
    IMoodleAuditLogRepository auditLogs,
    IMoodleAssignmentSettingsGateway settingsGateway,
    IGradingProposalStore? proposalStore = null,
    IGradingOperationTelemetry? telemetry = null,
    IMoodleResourceRepository? resourceRepository = null,
    ISubmissionContentHashResolver? submissionContentHashResolver = null,
    IOptions<MoodleUniversalApiFeatureOptions>? resourceFeatures = null)
    : IRequestHandler<SaveAiGradingBatchCommand, SaveAiGradingBatchResult>
{
    // O contrato atual de salvar_correcoes_ia_lote é legado: não transporta
    // evidências, cobertura nem confiança calculada. Portanto, ele nunca pode
    // promover uma nota para alta confiança por presunção. A revisão humana
    // continua obrigatória e a confiança é explicitamente zero até que uma
    // proposta versionada seja integrada ao pipeline.
    private const decimal LegacyAiProposalConfidence = 0m;

    public async Task<SaveAiGradingBatchResult> Handle(
        SaveAiGradingBatchCommand request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (request.BatchJobId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(request.BatchJobId));
        }

        if (request.Items.Count == 0)
        {
            throw new ArgumentException("Informe pelo menos um item.", nameof(request.Items));
        }

        var scope = await GradingBatchScopeResolver.ResolveAsync(
            repository,
            currentUser,
            request.BatchJobId,
            cancellationToken);
        var batch = scope.FirstBatch
            ?? throw new InvalidOperationException("A execucao de correcao ainda nao possui sublotes.");
        var scopeBatchIds = scope.Batches.Select(child => child.Id).ToHashSet();

        _ = settingsGateway; // Compatibilidade de DI; a escala vem do snapshot local.
        // Um resultado vindo do pacote já passou pela validação de propriedade,
        // hash, escala e evidência no importador. Não faça uma chamada ao Moodle
        // apenas para preencher a auditoria: o lote preserva o identificador do
        // professor quando disponível e o rascunho local deve continuar
        // importável durante uma indisponibilidade externa.
        var moodleUserId = request.TrustedPackageImport
            ? string.Equals(batch.CreatedBySubject, currentUser.Subject, StringComparison.Ordinal)
                ? batch.CreatedByMoodleUserId
                : null
            : await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        var warnings = new List<string>();
        var savedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;
        var proposalsToPublish = new List<AiGradingProposal>();
        var updatedItems = new List<SaveAiGradingBatchItemResult>();
        var securityWarningsOnly = resourceFeatures?.Value.McpGradingSecurityWarningsOnly == true;
        var snapshotsByItem = await repository.ListLatestContextSnapshotsByItemsAsync(
            request.Items.Select(input => input.GradingItemId).Where(id => id != Guid.Empty).Distinct().ToArray(),
            cancellationToken);
        var artifactsByItem = await repository.ListArtifactsByItemsAsync(
            request.Items.Select(input => input.GradingItemId).Where(id => id != Guid.Empty).Distinct().ToArray(),
            cancellationToken);
        var legacySettingsCache = new Dictionary<(long CourseId, long AssignmentId), AssignmentSettingsSummary?>();
        var touchedBatchIds = new HashSet<Guid>();
        var requestedItemIds = request.Items
            .Select(input => input.GradingItemId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var expectedDraftVersionHashes = request.ExpectedDraftVersionHashes is null
            ? new Dictionary<Guid, string>()
            : request.ExpectedDraftVersionHashes
                .Where(entry => entry.Key != Guid.Empty && !string.IsNullOrWhiteSpace(entry.Value))
                .ToDictionary(entry => entry.Key, entry => entry.Value.Trim(), EqualityComparer<Guid>.Default);
        if (expectedDraftVersionHashes.Count > 0)
        {
            await repository.ReloadItemsAsync(requestedItemIds, cancellationToken);
        }

        var itemsById = await repository.GetItemsAsync(requestedItemIds, cancellationToken);
        var nextProposalVersions = proposalStore is null
            ? new Dictionary<Guid, int>()
            : new Dictionary<Guid, int>(await proposalStore.GetNextVersionsAsync(itemsById.Keys.ToArray(), cancellationToken));
        var repeatedFeedbackItemIds = request.Items
            .Where(input => itemsById.ContainsKey(input.GradingItemId))
            .Select(input =>
            {
                var feedback = string.IsNullOrWhiteSpace(input.Proposal?.Feedback)
                    ? input.Feedback
                    : input.Proposal!.Feedback;
                return new
                {
                    input.GradingItemId,
                    AssignmentId = itemsById[input.GradingItemId].AssignmentId,
                    StudentId = itemsById[input.GradingItemId].MoodleUserId,
                    FeedbackKey = GradingFeedbackQuality.NormalizeForComparison(feedback)
                };
            })
            .Where(entry => entry.FeedbackKey.Length > 0)
            .GroupBy(entry => (entry.AssignmentId, entry.FeedbackKey))
            .Where(group => group.Select(entry => entry.StudentId).Distinct().Count() > 1)
            .SelectMany(group => group.Select(entry => entry.GradingItemId))
            .ToHashSet();
        if (repeatedFeedbackItemIds.Count > 0)
        {
            warnings.Add(
                $"{repeatedFeedbackItemIds.Count} item(ns) ignorado(s): feedback reutilizado entre estudantes da mesma atividade. " +
                "Gere um retorno individual com evidencia da submissao.");
        }
        var resourcesById = new Dictionary<string, MoodleResource>(StringComparer.Ordinal);
        if (resourceRepository is not null)
        {
            // Validate all proposal resource references from one indexed
            // lookup before entering the item loop. The previous per-URI
            // FindAsync path multiplied database round trips across a 10k
            // run, especially when each submission had several attachments.
            var requestedResourceIds = request.Items
                .SelectMany(input => (input.Proposal?.ResourceUris ?? [])
                    .Concat((input.Proposal?.Evidence ?? [])
                        .Select(evidence => evidence.ResourceUri)
                        .Where(uri => !string.IsNullOrWhiteSpace(uri))
                        .Select(uri => uri!)))
                .Where(uri => !string.IsNullOrWhiteSpace(uri) && MoodleResourceUri.TryParse(uri, out _))
                .Select(uri => MoodleResourceUri.TryParse(uri!, out var resourceId) ? resourceId : null)
                .Where(resourceId => !string.IsNullOrWhiteSpace(resourceId))
                .Select(resourceId => resourceId!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (requestedResourceIds.Length > 0)
            {
                try
                {
                    foreach (var entry in await resourceRepository.FindManyAsync(
                                 requestedResourceIds,
                                 cancellationToken))
                    {
                        resourcesById[entry.Key] = entry.Value;
                    }

                    // ZIP child resources point to a parent attachment. Load
                    // those parents in one second indexed query as well.
                    var parentIds = resourcesById.Values
                        .Select(resource => resource.ParentResourceId)
                        .Where(parentId => !string.IsNullOrWhiteSpace(parentId))
                        .Select(parentId => parentId!)
                        .Distinct(StringComparer.Ordinal)
                        .Where(parentId => !resourcesById.ContainsKey(parentId))
                        .ToArray();
                    if (parentIds.Length > 0)
                    {
                        foreach (var entry in await resourceRepository.FindManyAsync(parentIds, cancellationToken))
                        {
                            resourcesById[entry.Key] = entry.Value;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Preserve per-item partial-failure semantics. The loop
                    // below falls back to FindAsync only if this bulk read
                    // itself failed, which is an exceptional path.
                    resourcesById.Clear();
                }
            }
        }

        foreach (var input in request.Items)
        {
            try
            {
                if (input.GradingItemId == Guid.Empty)
                {
                    warnings.Add($"Item ignorado: gradingItemId vazio.");
                    skippedCount++;
                    continue;
                }

                if (input.Proposal is null && string.IsNullOrWhiteSpace(input.Feedback))
                {
                    warnings.Add($"Item {input.GradingItemId} ignorado: feedback vazio.");
                    skippedCount++;
                    continue;
                }

                if (input.Nota is < 0 || input.Proposal?.SuggestedGrade is < 0)
                {
                    warnings.Add($"Item {input.GradingItemId} ignorado: nota negativa nao e permitida.");
                    skippedCount++;
                    continue;
                }

                if (!itemsById.TryGetValue(input.GradingItemId, out var item))
                {
                    warnings.Add($"Item {input.GradingItemId} nao encontrado.");
                    failedCount++;
                    continue;
                }

                if (repeatedFeedbackItemIds.Contains(input.GradingItemId))
                {
                    skippedCount++;
                    continue;
                }

                if (input.Proposal is not null && !HasStructuredStudentEvidence(
                        input.Proposal,
                        artifactsByItem.GetValueOrDefault(input.GradingItemId, [])))
                {
                    warnings.Add(
                        $"Item {input.GradingItemId} ignorado: a proposta nao possui evidencia estruturada da submissao. " +
                        "Inclua evidencia por criterio ou uma referencia de recurso antes de salvar.");
                    skippedCount++;
                    continue;
                }

                if (input.Proposal is not null && !EvidenceArtifactsBelongToItem(
                        input.Proposal,
                        artifactsByItem.GetValueOrDefault(input.GradingItemId, [])))
                {
                    warnings.Add(
                        $"Item {input.GradingItemId} ignorado: a proposta referencia artifactId que nao pertence ao pacote desta entrega. " +
                        "Use os artifactId expostos em prepare_ai_grading_batch.");
                    skippedCount++;
                    continue;
                }

                if (!scopeBatchIds.Contains(item.BatchId))
                {
                    warnings.Add($"Item {input.GradingItemId} nao pertence ao lote ou execucao {request.BatchJobId}.");
                    failedCount++;
                    continue;
                }

                touchedBatchIds.Add(item.BatchId);

                // Itens bloqueados ou falhos permanecem no relatorio manual;
                // um rascunho da IA nao pode reabrir uma submissao sem leitura valida.
                if (item.Status is GradingItemStatus.Blocked or GradingItemStatus.Failed)
                {
                    warnings.Add($"Item {input.GradingItemId} ignorado: status {item.Status} ({item.DraftFeedback ?? item.CommitError ?? "sem detalhe"}).");
                    skippedCount++;
                    continue;
                }

                // Pular itens que já foram revisados ou commitados
                if (item.Status is GradingItemStatus.ReadyToCommit or GradingItemStatus.Committed)
                {
                    warnings.Add($"Item {input.GradingItemId} ja foi revisado/commitado. Ignorado.");
                    skippedCount++;
                    continue;
                }

                if (item.Status is not (GradingItemStatus.AwaitingAiAnalysis or GradingItemStatus.DraftReady))
                {
                    warnings.Add($"Item {input.GradingItemId} ignorado: aguardando pre-validacao antes da analise pela IA.");
                    skippedCount++;
                    continue;
                }

                if (expectedDraftVersionHashes.TryGetValue(item.Id, out var expectedDraftVersionHash) &&
                    !string.Equals(
                        GradingDraftVersionHash.Compute(item),
                        expectedDraftVersionHash,
                        StringComparison.Ordinal))
                {
                    var currentDraftVersionHash = GradingDraftVersionHash.Compute(item);
                    warnings.Add(
                        $"Item {item.Id} ignorado: a versao do rascunho mudou desde a leitura do pacote; " +
                        "a revisao existente foi preservada.");
                    skippedCount++;
                    updatedItems.Add(new SaveAiGradingBatchItemResult(
                        item.Id,
                        "conflict",
                        currentDraftVersionHash,
                        item.SuggestedGrade,
                        item.DraftFeedback));
                    continue;
                }

                if (input.Proposal is not null &&
                    string.IsNullOrWhiteSpace(input.Proposal.Feedback) &&
                    string.IsNullOrWhiteSpace(input.Feedback) &&
                    string.IsNullOrWhiteSpace(item.DraftFeedback))
                {
                    warnings.Add($"Item {input.GradingItemId} ignorado: feedback da proposta vazio.");
                    skippedCount++;
                    continue;
                }

                var requestedGrade = input.Proposal?.SuggestedGrade ?? input.Nota;
                var contextSnapshotDocument = snapshotsByItem.GetValueOrDefault(item.Id);
                var contextSnapshot = TryReadContextSnapshot(contextSnapshotDocument?.PayloadJson);
                var maxGrade = contextSnapshot?.GradingScale?.MaximumGrade ??
                    TryReadMaxGrade(contextSnapshotDocument?.PayloadJson);
                var hasFormalRubricPoints = (input.Proposal?.Criteria ?? []).Any(criterion =>
                    string.Equals(
                        AiGradingCriterionSource.Normalize(criterion.Source),
                        AiGradingCriterionSource.FormalRubric,
                        StringComparison.Ordinal));
                if (maxGrade is null && requestedGrade is not null &&
                    !hasFormalRubricPoints && !snapshotsByItem.ContainsKey(item.Id))
                {
                    // Compatibilidade para lotes legados que ainda não têm
                    // snapshot. Lotes novos nunca chegam a este fallback.
                    var settingsKey = (item.CourseId, item.AssignmentId);
                    if (!legacySettingsCache.TryGetValue(settingsKey, out var legacySettings))
                    {
                        legacySettings = await settingsGateway.GetAssignmentSettingsAsync(
                            batch.CreatedBySubject,
                            item.CourseId.ToString(CultureInfo.InvariantCulture),
                            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                            cancellationToken);
                        legacySettingsCache[settingsKey] = legacySettings;
                    }
                    maxGrade = legacySettings?.MaxGrade > 0 ? legacySettings.MaxGrade : null;
                    if (maxGrade is not null)
                    {
                        warnings.Add($"Item {input.GradingItemId}: escala obtida pelo fallback legado; publique um snapshot para eliminar a leitura Moodle.");
                    }
                }
                if (requestedGrade is not null && !hasFormalRubricPoints)
                {
                    if (maxGrade is null)
                    {
                        warnings.Add($"Item {input.GradingItemId} ignorado: escala maxima nao confirmada; nota numerica bloqueada.");
                        skippedCount++;
                        continue;
                    }

                    if (requestedGrade > maxGrade)
                    {
                        warnings.Add($"Item {input.GradingItemId} ignorado: nota excede a escala maxima confirmada.");
                        skippedCount++;
                        continue;
                    }
                }

                AiGradingProposal? proposal = null;
                var itemSecurityWarnings = new List<string>();
                if (input.Proposal is not null)
                {
                    if (input.Proposal.Version <= 0)
                    {
                        warnings.Add($"Item {input.GradingItemId} ignorado: versao da proposta invalida.");
                        skippedCount++;
                        continue;
                    }

                    if (input.Nota is not null && input.Nota != input.Proposal.SuggestedGrade)
                    {
                        warnings.Add($"Item {input.GradingItemId} ignorado: nota legada diverge da proposta versionada.");
                        skippedCount++;
                        continue;
                    }

                    var proposalVersion = proposalStore is null
                        ? input.Proposal.Version
                        : nextProposalVersions.GetValueOrDefault(item.Id, input.Proposal.Version);
                    string? submissionContentHash = null;
                    var packageEvidenceSource = request.TrustedPackageImport &&
                        string.Equals(
                            input.Proposal.Source,
                            "grading_package",
                            StringComparison.OrdinalIgnoreCase);
                    if (packageEvidenceSource)
                    {
                        var packageHash = input.Proposal.SubmissionContentHash?.Trim().ToLowerInvariant();
                        var itemHash = item.SubmissionContentHash?.Trim().ToLowerInvariant();
                        if (!string.Equals(packageHash, itemHash, StringComparison.Ordinal) ||
                            packageHash is not null && !IsSha256(packageHash))
                        {
                            warnings.Add($"Item {input.GradingItemId} ignorado: submissionContentHash do pacote nao corresponde a entrega atual.");
                            skippedCount++;
                            continue;
                        }

                        submissionContentHash = packageHash;
                    }
                    if (resourceFeatures?.Value.McpGradingDraftEnabled == true && !packageEvidenceSource)
                    {
                        if (resourceRepository is null)
                            throw new InvalidOperationException("A validacao dos resources do draft MCP nao esta configurada.");

                        var resourceUris = (input.Proposal.ResourceUris ?? [])
                            .Where(uri => !string.IsNullOrWhiteSpace(uri))
                            .Select(uri => uri!)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                        if (resourceUris.Length == 0)
                            throw new InvalidOperationException("O draft MCP exige as URIs de todos os anexos originais da submissao em proposal.resourceUris.");

                        var resources = new Dictionary<string, MoodleResource>(StringComparer.Ordinal);
                        foreach (var uri in resourceUris)
                        {
                            if (!MoodleResourceUri.TryParse(uri, out var resourceId))
                                throw new InvalidOperationException("A proposta aponta para uma URI de resource invalida.");
                            var resource = resourcesById.GetValueOrDefault(resourceId);
                            if (resource is null)
                            {
                                resource = await resourceRepository.FindAsync(resourceId, cancellationToken);
                            }
                            if (resource is null || resource.IsExpired(DateTimeOffset.UtcNow) || resource.SubmissionId != item.SubmissionId)
                                throw new InvalidOperationException("A proposta referencia um resource invalido da submissao.");
                            if (!string.IsNullOrWhiteSpace(resource.ParentResourceId))
                            {
                                resource = resourcesById.GetValueOrDefault(resource.ParentResourceId) ??
                                    await resourceRepository.FindAsync(resource.ParentResourceId, cancellationToken) ??
                                    throw new InvalidOperationException("O resource ZIP pai nao esta mais disponivel.");
                            }
                            if (!string.Equals(resource.ResourceType, "submission_attachment", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("proposal.resourceUris deve conter somente os anexos originais da submissao.");
                            resources[resource.ResourceId] = resource;
                        }

                        var unverifiedResources = resources.Values
                            .Where(resource => string.IsNullOrWhiteSpace(resource.Sha256))
                            .ToArray();
                        if (unverifiedResources.Length > 0)
                        {
                            var message =
                                $"Item {input.GradingItemId}: aviso tecnico de seguranca — {unverifiedResources.Length} resource(s) ainda nao possuem hash validado. " +
                                "O rascunho foi salvo com as resourceUris originais; a selagem de integridade ficou pendente.";
                            if (!securityWarningsOnly)
                                throw new InvalidOperationException("Todos os resources da proposta devem ser lidos e validados antes de salvar o draft.");

                            warnings.Add(message);
                            itemSecurityWarnings.Add(message);
                        }
                        else if (item.SubmissionId is not long submissionId || moodleUserId is null)
                        {
                            var message =
                                $"Item {input.GradingItemId}: aviso tecnico de seguranca — a submissao nao pode ser selada porque sua identidade Moodle nao foi resolvida. " +
                                "O rascunho foi salvo para revisao.";
                            if (!securityWarningsOnly)
                                throw new InvalidOperationException("A submissao Moodle nao esta identificada para selar o draft.");

                            warnings.Add(message);
                            itemSecurityWarnings.Add(message);
                        }
                        else if (submissionContentHashResolver is null)
                        {
                            var message =
                                $"Item {input.GradingItemId}: aviso tecnico de seguranca — o resolvedor de integridade nao esta disponivel. " +
                                "O rascunho foi salvo para revisao.";
                            if (!securityWarningsOnly)
                                throw new InvalidOperationException("A integridade de draft MCP nao esta configurada.");

                            warnings.Add(message);
                            itemSecurityWarnings.Add(message);
                        }
                        else
                        {
                            try
                            {
                                var integrity = await submissionContentHashResolver.ResolveAsync(
                                    moodleUserId.Value.ToString(CultureInfo.InvariantCulture),
                                    item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                                    item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
                                    submissionId,
                                    resources.Values.Select(resource => resource.Sha256!).ToArray(),
                                    cancellationToken);
                                if (resources.Count != integrity.FileCount)
                                    throw new InvalidOperationException("O draft MCP deve vincular todos os anexos originais da submissao.");

                                submissionContentHash = integrity.Hash;
                                item.RecordSubmissionIntegrity(
                                    submissionContentHash,
                                    resources.Keys.ToArray(),
                                    integrity.RevisionHash);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                var message =
                                    $"Item {input.GradingItemId}: aviso tecnico de seguranca — a selagem da submissao nao foi concluida ({exception.GetType().Name}). " +
                                    "O rascunho foi salvo com as resourceUris originais para revisao.";
                                if (!securityWarningsOnly)
                                    throw;

                                warnings.Add(message);
                                itemSecurityWarnings.Add(message);
                            }
                        }
                    }

                            if (resourceRepository is not null)
                            {
                        // URIs de evidência são sempre verificadas; o ID opaco
                        // não é uma autorização. Elas podem apontar para o
                        // anexo da submissão ou para material de contexto, mas
                        // nunca participam do selo da submissão.
                        foreach (var evidence in input.Proposal.Evidence ?? [])
                        {
                            if (string.IsNullOrWhiteSpace(evidence.ResourceUri)) continue;
                            if (!MoodleResourceUri.TryParse(evidence.ResourceUri, out var resourceId)) throw new InvalidOperationException("A evidencia aponta para uma URI de resource invalida.");
                            var resource = resourcesById.GetValueOrDefault(resourceId);
                            if (resource is null)
                            {
                                resource = await resourceRepository.FindAsync(resourceId, cancellationToken);
                            }
                            if (resource is null || !IsEvidenceResourceForItem(resource, item))
                                throw new InvalidOperationException("A evidencia nao esta vinculada a um resource valido da entrega ou do enunciado desta atividade.");
                        }
                    }
                    proposal = AiGradingProposalFactory.Create(
                        item,
                        input.Proposal,
                        maxGrade,
                        proposalVersion,
                        input.Feedback,
                        submissionContentHash,
                        contextSnapshot);
                }
                else if (proposalStore is not null)
                {
                    var legacyVersion = nextProposalVersions.GetValueOrDefault(item.Id, 1);
                    proposalsToPublish.Add(AiGradingProposal.FromLegacy(
                        item.Id,
                        item.BatchId,
                        legacyVersion,
                        item.ContextHash,
                        input.Nota,
                        input.Feedback));
                }

                var privateNotes = proposal is null && input.Nota is null
                        ? "Proposta IA legada sem evidencias ou confianca calculada; escala numerica nao confirmada. Revise a escala no CSV antes de qualquer uso posterior."
                        : proposal is null
                            ? "Proposta IA legada sem evidencias ou confianca calculada. Revise o resultado no CSV antes de qualquer uso posterior."
                            : proposal.ReviewRequired
                                ? $"Proposta IA versionada ({proposal.ProposalHash[..12]}) requer revisao manual no CSV: {string.Join(", ", proposal.UncertaintyReasons)}."
                                : $"Proposta IA versionada ({proposal.ProposalHash[..12]}) requer revisao manual no CSV antes de qualquer uso posterior.";
                if (itemSecurityWarnings.Count > 0)
                {
                    privateNotes = $"{privateNotes} Avisos tecnicos nao bloqueantes: {string.Join(" ", itemSecurityWarnings)}";
                }

                item.SetDraft(
                    suggestedGrade: proposal?.SuggestedGrade ?? input.Nota,
                    confidence: proposal?.Confidence ?? LegacyAiProposalConfidence,
                    draftFeedback: proposal?.Feedback ?? input.Feedback,
                    privateNotesToTeacher: privateNotes,
                    maxGrade: maxGrade);

                // Só persiste a proposta versionada depois que o item local
                // foi aceito como rascunho. Assim uma falha de validação do
                // item não deixa uma proposta órfã no store de auditoria.
                if (proposalStore is not null && proposal is not null)
                {
                    proposalsToPublish.Add(proposal);
                }

                savedCount++;
                updatedItems.Add(new SaveAiGradingBatchItemResult(
                    item.Id,
                    item.Status.ToString(),
                    GradingDraftVersionHash.Compute(item),
                    item.SuggestedGrade,
                    item.DraftFeedback,
                    proposal?.ScoreNormalization));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"Falha ao salvar item {input.GradingItemId}: {ex.Message}");
                failedCount++;
            }
        }

        if (proposalStore is not null)
        {
            await proposalStore.PublishManyAsync(proposalsToPublish, cancellationToken);
        }

        // Atualizar contadores somente nos sublotes autorizados que aparecem
        // nos itens do pedido; o mesmo comando pode salvar uma página de run.
        foreach (var child in scope.Batches.Where(child => touchedBatchIds.Contains(child.Id)))
        {
            var allItems = await GradingItemProcessor.LoadAllBatchItemsAsync(repository, child.Id, cancellationToken);
            GradingItemProcessor.UpdateBatchCounters(child, allItems);
        }
        await repository.SaveChangesAsync(cancellationToken);

        await auditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = $"grading-batch-{request.BatchJobId:N}",
            BatchJobId = request.BatchJobId,
            ToolName = "salvar_correcoes_ia_lote",
            RiskLevel = ToolRiskLevel.DraftOnly,
            ActorSubject = currentUser.Subject,
            ActorEmail = currentUser.Email,
            ActorMoodleUserId = moodleUserId,
            CourseId = batch.CourseId,
            MoodleFunction = null,
            RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                batchJobId = request.BatchJobId,
                itemCount = request.Items.Count
            }),
            ResponseSummaryJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                savedCount,
                skippedCount,
                failedCount
            }),
            Status = "ai_drafts_saved"
        }, cancellationToken);
        await auditLogs.SaveChangesAsync(cancellationToken);

        var result = new SaveAiGradingBatchResult(
            request.BatchJobId,
            savedCount,
            skippedCount,
            failedCount,
            request.Items.Count,
            warnings,
            NextStep: savedCount > 0
                ? "Correcoes salvas internamente. Para CSV externo, chame export_grading_corrections_csv com o mesmo batchJobId ou gradingRunId. Para publicar no Moodle, chame create_batch_grade_launch_preview, revise a previa e siga quando o usuario aprovar; para varios itens, prefira o fluxo de pacotes ZIP e import_grading_package_results."
                : "Nenhum item foi salvo. Verifique os avisos.",
            updatedItems);
        telemetry?.RecordPhase(
            "grading",
            "save",
            failedCount > 0 ? "partial_failure" : "success",
            stopwatch.Elapsed.TotalMilliseconds,
            queryCount: 5,
            itemCount: savedCount);
        return result;
    }

    private static bool HasStructuredStudentEvidence(
        AiGradingProposalInput proposal,
        IReadOnlyList<GradingArtifact> artifacts)
    {
        var submissionReferences = (proposal.Evidence ?? [])
            .Any(evidence =>
                evidence.ArtifactId != Guid.Empty ||
                !string.IsNullOrWhiteSpace(evidence.ResourceUri));
        var criterionArtifactReferences = (proposal.Criteria ?? [])
            .Any(criterion => (criterion.ArtifactIds ?? []).Any(id => id != Guid.Empty));

        if (submissionReferences || criterionArtifactReferences)
        {
            return true;
        }

        // Compatibilidade com lotes legados que não persistiram o catálogo de
        // artifacts. Em lotes novos, EvidenceText/GapsText isolados não são
        // suficientes: a proposta precisa apontar para o arquivo observado.
        return artifacts.Count == 0 && (proposal.Criteria ?? [])
            .Any(criterion =>
                !string.IsNullOrWhiteSpace(criterion.EvidenceText) ||
                !string.IsNullOrWhiteSpace(criterion.GapsText));
    }

    private static bool EvidenceArtifactsBelongToItem(
        AiGradingProposalInput proposal,
        IReadOnlyList<GradingArtifact> artifacts)
    {
        // Lotes legados podem não ter persistido os artifacts locais embora
        // ainda tenham resources MCP válidos. Nesse caso a URI continua sendo
        // a prova verificável e não há catálogo local contra o qual comparar.
        if (artifacts.Count == 0)
        {
            return true;
        }

        var knownArtifactIds = artifacts.Select(artifact => artifact.Id).ToHashSet();
        var referencedArtifactIds = (proposal.Criteria ?? [])
            .SelectMany(criterion => criterion.ArtifactIds ?? [])
            .Concat((proposal.Evidence ?? [])
                .Where(evidence => evidence.ArtifactId != Guid.Empty)
                .Select(evidence => evidence.ArtifactId))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        return referencedArtifactIds.Length == 0 || referencedArtifactIds.All(knownArtifactIds.Contains);
    }

    private static bool IsEvidenceResourceForItem(MoodleResource resource, AssistedGradingItem item)
    {
        if (resource.IsExpired(DateTimeOffset.UtcNow))
        {
            return false;
        }

        if (string.Equals(resource.ResourceType, "submission_attachment", StringComparison.OrdinalIgnoreCase))
        {
            return resource.SubmissionId == item.SubmissionId;
        }

        if (string.Equals(resource.ResourceType, "assignment_context_attachment", StringComparison.OrdinalIgnoreCase))
        {
            var assignmentMatches = resource.AssignmentId is null || resource.AssignmentId == item.AssignmentId;
            var courseMatches = resource.CourseId is null || resource.CourseId == item.CourseId;
            var itemScopedToSameSubmission = resource.SubmissionId is not null && resource.SubmissionId == item.SubmissionId;
            return assignmentMatches && courseMatches && (resource.SubmissionId is null || itemScopedToSameSubmission);
        }

        return false;
    }

    private static decimal? TryReadMaxGrade(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("GradingScale", out var scale) &&
                !document.RootElement.TryGetProperty("gradingScale", out scale)) return null;
            if (scale.ValueKind != JsonValueKind.Object) return null;
            if (!scale.TryGetProperty("MaximumGrade", out var value) &&
                !scale.TryGetProperty("maximumGrade", out value)) return null;
            return value.ValueKind == JsonValueKind.Number &&
                value.TryGetDecimal(out var max) &&
                max > 0
                    ? max
                    : null;
        }
        catch (JsonException) { return null; }
    }

    private static GradingContextSnapshot? TryReadContextSnapshot(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            return GradingContextSnapshot.DeserializeCanonicalPayload(payloadJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length == 64 &&
        value.Trim().All(Uri.IsHexDigit);
}
