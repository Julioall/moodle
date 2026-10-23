using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Courses;
using Moodle.Application.MoodleApi;
using Moodle.Application.Submissions;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

/// <summary>
/// Inicia a preparacao de correcoes pendentes em todos os cursos acessiveis.
/// Cada curso recebe um sublote proprio para preservar o escopo de processamento
/// e permitir uma exportacao CSV independente por lote.
/// </summary>
public sealed record StartPendingGradingRunCommand(
    string UserExternalId,
    int MaxCourses,
    int MaxItemsPerBatch,
    bool IncludeRubric = true,
    bool IncludeSubmissionFiles = true,
    bool IncludeCourseMaterials = false,
    string? TeacherInstructions = null,
    string Priority = "normal",
    bool UseSubmissionSnapshots = false,
    Guid? SnapshotOwnerId = null,
    string? SnapshotClientId = null,
    string? SnapshotConnectionAlias = null,
    string? CourseId = null,
    IReadOnlyList<string>? AssignmentIds = null,
    bool AllowRegradeExisting = false,
    bool IncludeAlreadyGraded = false,
    IReadOnlyList<string>? SubmissionIds = null,
    string? IdempotencyKey = null,
    string? MoodleConnectionId = null) : IRequest<StartPendingGradingRunResult>;

public sealed record RequeueBlockedGradingItemsCommand(
    Guid BatchJobId,
    IReadOnlyList<Guid> GradingItemIds) : IRequest<RequeueBlockedGradingItemsResult>;

public sealed record RequeueBlockedGradingItemsResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("requestedItems")] int RequestedItems,
    [property: JsonPropertyName("requeuedItems")] int RequeuedItems,
    [property: JsonPropertyName("alreadyQueuedItems")] int AlreadyQueuedItems,
    [property: JsonPropertyName("failedItems")] int FailedItems,
    [property: JsonPropertyName("failures")] IReadOnlyList<RequeueBlockedGradingItemFailure> Failures,
    [property: JsonPropertyName("nextStep")] string NextStep)
{
    [JsonPropertyName("processingState")]
    public string ProcessingState { get; init; } = "unchanged";

    [JsonPropertyName("nextAction")]
    public string NextAction { get; init; } = "attention";
}

public sealed record RequeueBlockedGradingItemFailure(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("message")] string Message);

public sealed record ExistingGradingCorrectionReference(
    [property: JsonPropertyName("submissionId")] long SubmissionId,
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("gradingRunId")] Guid? GradingRunId,
    [property: JsonPropertyName("itemStatus")] string ItemStatus,
    [property: JsonPropertyName("commitStatus")] string CommitStatus,
    [property: JsonPropertyName("batchStatus")] string BatchStatus,
    [property: JsonPropertyName("runStatus")] string? RunStatus);

public sealed record FindGradingCorrectionBySubmissionQuery(
    long SubmissionId,
    long? CourseId = null,
    long? AssignmentId = null,
    string? MoodleConnectionId = null,
    string? ConnectorClientId = null,
    string? ConnectionAlias = null) : IRequest<FindGradingCorrectionBySubmissionResult>;

public sealed record FindGradingCorrectionBySubmissionResult(
    [property: JsonPropertyName("submissionId")] long SubmissionId,
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("matches")] IReadOnlyList<ExistingGradingCorrectionReference> Matches,
    [property: JsonPropertyName("message")] string Message);

public sealed record StartPendingGradingRunResult(
    [property: JsonPropertyName("coursesDiscovered")] int CoursesDiscovered,
    [property: JsonPropertyName("coursesScanned")] int CoursesScanned,
    [property: JsonPropertyName("coursesWithPendingSubmissions")] int CoursesWithPendingSubmissions,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("batches")] IReadOnlyList<PendingGradingRunBatch> Batches,
    [property: JsonPropertyName("courses")] IReadOnlyList<PendingGradingRunCourse> Courses,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("nextStep")] string NextStep,
    [property: JsonPropertyName("gradingRunId")] Guid? GradingRunId = null)
{
    [JsonPropertyName("assignmentResolution")]
    public IReadOnlyList<AssignmentResolution> AssignmentResolution { get; init; } = [];

    [JsonPropertyName("existingCorrections")]
    public IReadOnlyList<ExistingGradingCorrectionReference> ExistingCorrections { get; init; } = [];
}

public sealed record AssignmentResolution(
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("requestedId")] string RequestedId,
    [property: JsonPropertyName("resolved")] bool Resolved,
    [property: JsonPropertyName("instanceId")] string? InstanceId,
    [property: JsonPropertyName("cmid")] string? Cmid,
    [property: JsonPropertyName("errorCode")] string? ErrorCode);

public sealed record PendingGradingRunBatch(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("courseName")] string CourseName,
    [property: JsonPropertyName("assignmentIds")] IReadOnlyList<string> AssignmentIds,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("blockedItems")] int BlockedItems);

public sealed record PendingGradingRunCourse(
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("courseName")] string CourseName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("batchJobId")] Guid? BatchJobId,
    [property: JsonPropertyName("message")] string? Message);

public sealed class StartPendingGradingRunCommandHandler(
    IMediator mediator,
    IMoodleCourseContentsGateway contentsGateway,
    IMoodleSnapshotStore? snapshotStore = null,
    IMoodleSnapshotSyncQueue? snapshotSyncQueue = null,
    IGradingReviewRepository? gradingRepository = null,
    ICurrentUserContext? currentUser = null,
    IMoodleAssignmentSubmissionsGateway? submissionsGateway = null)
    : IRequestHandler<StartPendingGradingRunCommand, StartPendingGradingRunResult>
{
    private const int CoursePageSize = 100;
    private const int MaxAggregateRunItems = 10_000;

    public async Task<StartPendingGradingRunResult> Handle(
        StartPendingGradingRunCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UserExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", nameof(request.UserExternalId));
        }

        if (request.MaxCourses < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaxCourses), "O limite de cursos nao pode ser negativo.");
        }

        var maxCourses = request.MaxCourses == 0 ? int.MaxValue : Math.Clamp(request.MaxCourses, 1, 1000);
        var maxItemsPerBatch = Math.Clamp(request.MaxItemsPerBatch, 1, 400);
        var includeAlreadyGraded = request.AllowRegradeExisting || request.IncludeAlreadyGraded;
        string[]? selectedSubmissionIds = null;
        if (request.SubmissionIds is not null)
        {
            selectedSubmissionIds = request.SubmissionIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => long.TryParse(id.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId) && parsedId > 0
                    ? parsedId.ToString(CultureInfo.InvariantCulture)
                    : id.Trim())
                .ToArray();
            if (selectedSubmissionIds.Length == 0 ||
                selectedSubmissionIds.Length != request.SubmissionIds.Count ||
                selectedSubmissionIds.Any(id => !long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue) || idValue <= 0) ||
                selectedSubmissionIds.Distinct(StringComparer.Ordinal).Count() != selectedSubmissionIds.Length)
            {
                throw new ArgumentException("submissionIds deve conter uma lista não vazia de IDs Moodle válidos e sem duplicatas.", nameof(request.SubmissionIds));
            }
            if (string.IsNullOrWhiteSpace(request.CourseId))
            {
                throw new ArgumentException("A seleção explícita de submissões exige courseId.", nameof(request.CourseId));
            }
            if (request.AssignmentIds is not { Count: 1 } || string.IsNullOrWhiteSpace(request.AssignmentIds[0]))
            {
                throw new ArgumentException("A seleção explícita de submissões exige exatamente um assignmentId ou cmid.", nameof(request.AssignmentIds));
            }
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Trim().Length > 128)
            {
                throw new ArgumentException("A seleção explícita de submissões exige idempotencyKey de até 128 caracteres.", nameof(request.IdempotencyKey));
            }
            if (string.IsNullOrWhiteSpace(request.MoodleConnectionId) ||
                string.IsNullOrWhiteSpace(request.SnapshotClientId))
            {
                throw new ArgumentException("A seleção explícita exige identidade confirmada do cliente e da conexão Moodle.", nameof(request.MoodleConnectionId));
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new ArgumentException("idempotencyKey só pode ser informado junto com submissionIds.", nameof(request.IdempotencyKey));
        }
        // Quando o usuário informa um curso explicitamente, a leitura de
        // entregas deve ser live. Um snapshot parcial pode não conter todas as
        // atividades e faria uma turma com pendências parecer vazia. Uma
        // reavaliação também exige leitura live para não reutilizar um snapshot
        // potencialmente desatualizado do arquivo/conteúdo que será corrigido.
        // Snapshots continuam sendo usados somente na varredura ampla normal.
        var useSnapshots = !includeAlreadyGraded &&
            string.IsNullOrWhiteSpace(request.CourseId) &&
            request.UseSubmissionSnapshots &&
            request.SnapshotOwnerId is not null &&
            !string.IsNullOrWhiteSpace(request.SnapshotClientId) &&
            !string.IsNullOrWhiteSpace(request.SnapshotConnectionAlias) &&
            snapshotStore is not null;
        var requestedCourseId = string.IsNullOrWhiteSpace(request.CourseId)
            ? null
            : request.CourseId.Trim();
        IReadOnlyList<CourseSummary> courses;
        if (requestedCourseId is not null)
        {
            var course = await mediator.Send(
                new GetCourseQuery(request.UserExternalId, requestedCourseId),
                cancellationToken);
            courses = course is null ? [] : [course];
        }
        else
        {
            courses = useSnapshots
                ? []
                : await LoadCoursesAsync(request.UserExternalId, maxCourses, cancellationToken);
        }
        var batches = new List<PendingGradingRunBatch>();
        var courseResults = new List<PendingGradingRunCourse>();
        var warnings = new List<string>();
        var assignmentResolutions = new List<AssignmentResolution>();
        var existingCorrections = new List<ExistingGradingCorrectionReference>();
        if (includeAlreadyGraded)
        {
            warnings.Add(
                "Modo de reavaliacao explicita ativo: submissaoes ja corrigidas podem ser analisadas novamente; a nota existente so sera sobrescrita mediante previa com allowOverwriteExisting=true e confirmacao explicita.");
        }

        if (requestedCourseId is not null && courses.Count == 0)
        {
            warnings.Add($"Curso {requestedCourseId} nao foi encontrado ou nao esta acessivel para o usuario atual.");
        }
        else if (useSnapshots && requestedCourseId is null)
        {
            courses = await LoadCoursesFromSnapshotAsync(request, maxCourses, warnings, cancellationToken);
        }

        IReadOnlyList<AssignmentSubmissionSummary>? selectedSubmissions = null;
        string? selectedAssignmentId = null;
        if (selectedSubmissionIds is not null)
        {
            if (courses.Count != 1)
            {
                throw new InvalidOperationException("A seleção exige exatamente um curso acessível e leitura live de submissões.");
            }

            var selectedCourse = courses[0];
            var requestedAssignmentId = request.AssignmentIds![0].Trim();
            var selectedContents = await contentsGateway.GetCourseContentsAsync(
                request.UserExternalId,
                selectedCourse.CourseId,
                moduleTypes: ["assign"],
                includeHidden: false,
                onlyWithFiles: false,
                cancellationToken);
            var selectedModule = selectedContents.Sections
                .SelectMany(section => section.Modules)
                .FirstOrDefault(module =>
                    string.Equals(module.ModuleType, "assign", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(module.InstanceId, requestedAssignmentId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(module.ModuleId, requestedAssignmentId, StringComparison.OrdinalIgnoreCase)));
            if (selectedModule is null || string.IsNullOrWhiteSpace(selectedModule.InstanceId))
            {
                throw new ArgumentException("assignmentId/cmid não identifica uma atividade assign no curso informado.", nameof(request.AssignmentIds));
            }

            selectedAssignmentId = selectedModule.InstanceId.Trim();
            var liveSubmissions = await LoadPendingSubmissionsAsync(
                request.UserExternalId,
                selectedCourse.CourseId,
                selectedAssignmentId,
                MaxAggregateRunItems,
                includeAlreadyGraded,
                cancellationToken);
            var groupedById = liveSubmissions
                .Where(submission => !string.IsNullOrWhiteSpace(submission.SubmissionId))
                .GroupBy(submission => submission.SubmissionId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var missingOrAmbiguousIds = selectedSubmissionIds
                .Where(id => !groupedById.TryGetValue(id, out var matches) || matches.Length != 1)
                .ToArray();
            if (missingOrAmbiguousIds.Length > 0)
            {
                throw new ArgumentException(
                    "Uma ou mais submissões não pertencem à atividade, não estão elegíveis ou não foram confirmadas pela leitura live. Nenhum lote foi criado.",
                    nameof(request.SubmissionIds));
            }

            var latestAttemptByUser = liveSubmissions
                .Where(submission => !string.IsNullOrWhiteSpace(submission.UserId))
                .GroupBy(submission => submission.UserId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Max(submission => submission.AttemptNumber ?? -1),
                    StringComparer.Ordinal);
            var selectedRows = selectedSubmissionIds.Select(id => groupedById[id][0]).ToArray();
            if (selectedRows.Any(submission =>
                    submission.AttemptNumber is null ||
                    !latestAttemptByUser.TryGetValue(submission.UserId, out var latestAttempt) ||
                    submission.AttemptNumber.Value != latestAttempt))
            {
                throw new ArgumentException(
                    "A seleção contém uma tentativa que não é a atual do estudante. Atualize a lista e tente novamente; nenhum lote foi criado.",
                    nameof(request.SubmissionIds));
            }

            // Canonical ordering keeps the request digest and each chunk's
            // idempotency key aligned when a client retries the same set in
            // another order.
            selectedSubmissionIds = selectedSubmissionIds
                .OrderBy(id => long.Parse(id, CultureInfo.InvariantCulture))
                .ToArray();
            selectedSubmissions = selectedSubmissionIds.Select(id => groupedById[id][0]).ToArray();
        }

        var gradingRunId = Guid.NewGuid();
        if (gradingRepository is not null)
        {
            // O ID Moodle é local à instalação e não é necessariamente o
            // subject autenticado do conector. O subject é a chave de posse
            // usada pelo resolver em todas as consultas futuras.
            var ownerSubject = string.IsNullOrWhiteSpace(currentUser?.Subject)
                ? request.UserExternalId
                : currentUser.Subject;
            var requestDigest = selectedSubmissionIds is null
                ? null
                : BuildSelectionRequestDigest(
                    ownerSubject,
                    request,
                    courses[0].CourseId,
                    selectedAssignmentId!,
                    selectedSubmissions!,
                    request.MoodleConnectionId);
            var run = GradingRun.Create(
                ownerSubject,
                createdByMoodleUserId: long.TryParse(request.UserExternalId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMoodleUserId)
                    ? parsedMoodleUserId
                    : null,
                moodleConnectionId: request.MoodleConnectionId,
                courseIdScope: request.CourseId,
                connectorClientId: request.SnapshotClientId,
                connectionAlias: request.SnapshotConnectionAlias,
                idempotencyKey: selectedSubmissionIds is null ? null : request.IdempotencyKey!.Trim(),
                requestDigest: requestDigest);
            var reservation = await gradingRepository.ReserveGradingRunAsync(run, cancellationToken);
            if (reservation.PayloadConflict)
            {
                throw new InvalidOperationException("idempotencyKey já foi usada com outra seleção ou configuração. Use uma nova chave para uma reavaliação intencional.");
            }

            gradingRunId = reservation.Run.Id;
        }
        else if (selectedSubmissionIds is not null)
        {
            throw new InvalidOperationException("A seleção explícita exige persistência idempotente de gradingRun.");
        }

        foreach (var course in courses)
        {
            if (batches.Sum(batch => batch.TotalItems) >= MaxAggregateRunItems)
            {
                AddAggregateLimitWarning(warnings);
                break;
            }

            var courseName = ResolveCourseName(course);
            if (useSnapshots)
            {
                courseResults.Add(await ProcessSnapshotCourseAsync(
                    request,
                    course,
                    courseName,
                    maxItemsPerBatch,
                    gradingRunId,
                    batches,
                    assignmentResolutions,
                    existingCorrections,
                    warnings,
                    cancellationToken));
                continue;
            }

            CourseContentsSummary contents;
            try
            {
                contents = await contentsGateway.GetCourseContentsAsync(
                    request.UserExternalId,
                    course.CourseId,
                    moduleTypes: ["assign"],
                    includeHidden: false,
                    onlyWithFiles: false,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = $"Nao foi possivel ler as atividades do curso: {ex.Message}";
                courseResults.Add(new PendingGradingRunCourse(course.CourseId, courseName, "course_read_failed", null, message));
                warnings.Add($"Curso {course.CourseId}: {message}");
                continue;
            }

            var requestedAssignmentIds = request.AssignmentIds is null
                ? null
                : request.AssignmentIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            var requestedAssignmentIdSet = requestedAssignmentIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (requestedAssignmentIds is { Length: > 0 })
            {
                foreach (var requestedId in requestedAssignmentIds)
                {
                    var resolvedModule = contents.Sections
                        .SelectMany(section => section.Modules)
                        .FirstOrDefault(module =>
                            string.Equals(module.ModuleId, requestedId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(module.InstanceId, requestedId, StringComparison.OrdinalIgnoreCase));
                    assignmentResolutions.Add(resolvedModule is null
                        ? new AssignmentResolution(
                            course.CourseId,
                            requestedId,
                            Resolved: false,
                            InstanceId: null,
                            Cmid: null,
                            ErrorCode: MoodleErrorContract.AssignmentNotFound)
                        : new AssignmentResolution(
                            course.CourseId,
                            requestedId,
                            Resolved: string.Equals(resolvedModule.ModuleType, "assign", StringComparison.OrdinalIgnoreCase),
                            InstanceId: string.Equals(resolvedModule.ModuleType, "assign", StringComparison.OrdinalIgnoreCase)
                                ? resolvedModule.InstanceId
                                : null,
                            Cmid: string.Equals(resolvedModule.ModuleType, "assign", StringComparison.OrdinalIgnoreCase)
                                ? resolvedModule.ModuleId
                                : null,
                            ErrorCode: string.Equals(resolvedModule.ModuleType, "assign", StringComparison.OrdinalIgnoreCase)
                                ? null
                                : MoodleErrorContract.AssignmentNotFound));
                }
            }
            var assignmentIds = contents.Sections
                .SelectMany(section => section.Modules)
                .Where(module =>
                    string.Equals(module.ModuleType, "assign", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(module.InstanceId) &&
                     (requestedAssignmentIdSet is null ||
                     requestedAssignmentIdSet.Contains(module.InstanceId!) ||
                     (!string.IsNullOrWhiteSpace(module.ModuleId) &&
                      requestedAssignmentIdSet.Contains(module.ModuleId!))))
                .Select(module => module.InstanceId!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (assignmentIds.Length == 0)
            {
                var message = requestedAssignmentIds is { Length: > 0 }
                    ? $"Nenhuma das tarefas solicitadas ({string.Join(", ", requestedAssignmentIds)}) foi encontrada no curso."
                    : "Nenhuma atividade avaliativa do tipo assign foi encontrada.";
                courseResults.Add(new PendingGradingRunCourse(
                    course.CourseId,
                    courseName,
                    "no_assignments",
                    null,
                    message));
                continue;
            }

            var batchesBeforeCourse = batches.Count;
            var existingCorrectionsBeforeCourse = existingCorrections.Count;
            var courseMessages = new List<string>();
            BulkPendingSubmissionRead? batchedPendingSubmissions = null;
            if (submissionsGateway is not null)
            {
                try
                {
                    batchedPendingSubmissions = await LoadPendingSubmissionsByAssignmentsAsync(
                        request.UserExternalId,
                        assignmentIds,
                        MaxAggregateRunItems - batches.Sum(batch => batch.TotalItems),
                        includeAlreadyGraded,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Preserve the legacy mediator path as a safety net for
                    // Moodle installations that reject the bulk endpoint.
                    warnings.Add($"Curso {course.CourseId}: leitura em lote das entregas falhou; usando fallback por atividade: {ex.Message}");
                }
            }

            foreach (var assignmentId in assignmentIds)
            {
                var remainingRunItemsBeforeAssignment = MaxAggregateRunItems -
                    batches.Sum(batch => batch.TotalItems);
                if (remainingRunItemsBeforeAssignment <= 0)
                {
                    AddAggregateLimitWarning(warnings);
                    break;
                }

                IReadOnlyList<AssignmentSubmissionSummary> submissions;
                try
                {
                    if (selectedSubmissions is not null &&
                        string.Equals(course.CourseId, requestedCourseId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(assignmentId, selectedAssignmentId, StringComparison.OrdinalIgnoreCase))
                    {
                        submissions = selectedSubmissions;
                    }
                    else if (batchedPendingSubmissions is not null &&
                        batchedPendingSubmissions.Successful.TryGetValue(assignmentId, out var batchedSubmissions))
                    {
                        submissions = batchedSubmissions.Take(remainingRunItemsBeforeAssignment).ToArray();
                    }
                    else
                    {
                        if (batchedPendingSubmissions?.Failed.ContainsKey(assignmentId) == true)
                        {
                            warnings.Add(
                                $"Curso {course.CourseId}: leitura em lote da atividade {assignmentId} falhou; usando fallback por atividade.");
                        }

                        submissions = await LoadPendingSubmissionsAsync(
                            request.UserExternalId,
                            course.CourseId,
                            assignmentId,
                            remainingRunItemsBeforeAssignment,
                            includeAlreadyGraded,
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var message = $"Nao foi possivel listar as entregas da tarefa {assignmentId}: {ex.Message}";
                    courseMessages.Add(message);
                    warnings.Add($"Curso {course.CourseId}: {message}");
                    continue;
                }

                foreach (var (submissionChunk, chunkIndex) in submissions
                    .Chunk(maxItemsPerBatch)
                    .Select((chunk, index) => (chunk, index)))
                {
                    var remainingRunItems = MaxAggregateRunItems - batches.Sum(batch => batch.TotalItems);
                    if (remainingRunItems <= 0)
                    {
                        AddAggregateLimitWarning(warnings);
                        break;
                    }

                    var boundedSubmissionChunk = submissionChunk
                        .Take(Math.Min(maxItemsPerBatch, remainingRunItems))
                        .ToArray();
                    var chunkSubmissionIds = selectedSubmissionIds is null
                        ? (IReadOnlyList<string>)[]
                        : boundedSubmissionChunk
                            .Select(submission => submission.SubmissionId)
                            .Where(submissionId => !string.IsNullOrWhiteSpace(submissionId))
                            .Select(submissionId => submissionId!)
                            .ToArray();
                    CreateAssistedGradingBatchResult batch;
                    try
                    {
                        batch = await mediator.Send(
                            new CreateAssistedGradingBatchCommand(
                                request.UserExternalId,
                                course.CourseId,
                                AssignmentIds: [assignmentId],
                                SubmissionIds: chunkSubmissionIds,
                                MaxItems: maxItemsPerBatch,
                                OnlyAwaitingGrading: !includeAlreadyGraded,
                                IncludeAlreadyGraded: includeAlreadyGraded,
                                IncludeRubric: request.IncludeRubric,
                                IncludeSubmissionFiles: request.IncludeSubmissionFiles,
                                IncludeCourseMaterials: request.IncludeCourseMaterials,
                                TeacherInstructions: request.TeacherInstructions,
                                Priority: request.Priority,
                                CourseDisplayName: courseName,
                                PrefetchedSubmissions: boundedSubmissionChunk,
                                IdempotencyKey: BuildBatchIdempotencyKey(
                                    gradingRunId,
                                    course.CourseId,
                                    assignmentId,
                                    chunkIndex),
                                GradingRunId: gradingRunId),
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var message = $"Nao foi possivel preparar o sublote da tarefa {assignmentId}: {ex.Message}";
                        courseMessages.Add(message);
                        warnings.Add($"Curso {course.CourseId}: {message}");
                        continue;
                    }

                    // Mesmo quando a deduplicação retorna zero itens, preserve
                    // o motivo no resultado agregado. Sem isso, uma segunda
                    // solicitação concorrente parecia simplesmente "sem
                    // pendências", escondendo que as entregas já estavam em
                    // outro lote.
                    warnings.AddRange(batch.Warnings.Select(warning => $"Curso {course.CourseId}: {warning}"));
                    existingCorrections.AddRange(batch.ExistingCorrections);
                    if (batch.BatchJobId == Guid.Empty || batch.AcceptedItems == 0)
                    {
                        continue;
                    }

                    batches.Add(new PendingGradingRunBatch(
                        batch.BatchJobId,
                        batch.CourseId,
                        courseName,
                        batch.AssignmentIds,
                        batch.AcceptedItems,
                        batch.BlockedItems));
                }
            }

            var courseBatchCount = batches.Count - batchesBeforeCourse;
            var courseExistingCorrectionCount = existingCorrections.Count - existingCorrectionsBeforeCourse;
            courseResults.Add(new PendingGradingRunCourse(
                course.CourseId,
                courseName,
                courseBatchCount > 0
                    ? "batch_created"
                    : courseMessages.Count > 0
                        ? "partial_failure"
                        : courseExistingCorrectionCount > 0
                            ? "already_queued"
                        : "no_pending_submissions",
                courseBatchCount == 1 ? batches[^1].BatchJobId : null,
                    courseBatchCount > 0
                        ? $"{courseBatchCount} sublote(s) criado(s). {string.Join(" ", courseMessages)}".Trim()
                        : courseMessages.Count > 0
                            ? string.Join(" ", courseMessages)
                            : courseExistingCorrectionCount > 0
                                ? $"{courseExistingCorrectionCount} entrega(s) já possuem correção neste fluxo ou em uma execução anterior; o trabalho existente foi preservado."
                            : "Nenhuma entrega aguardando correcao foi encontrada."));
        }

        if (gradingRepository is not null)
        {
            var run = await gradingRepository.GetGradingRunAsync(gradingRunId, cancellationToken);
            if (run is not null)
            {
                var childBatches = await gradingRepository.ListBatchesByGradingRunAsync(gradingRunId, cancellationToken);
                var firstChild = childBatches.FirstOrDefault();
                if (firstChild is not null)
                {
                    run.BindConnection(
                        firstChild.MoodleConnectionId,
                        firstChild.ConnectorClientId,
                        firstChild.ConnectionAlias);
                }
                if (childBatches.Count == 0)
                {
                    // A deduplicated/failed discovery must not leave a new
                    // usable empty run behind. The handle remains auditable,
                    // but is immediately cancelled; existingCorrections
                    // carries the prior batch/run handles when applicable.
                    run.Cancel();
                    warnings.Add("Nenhum sublote novo foi criado; a execucao vazia foi encerrada automaticamente. Consulte existingCorrections para recuperar a correcao anterior.");
                    await gradingRepository.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    // Freeze the discovery contract before exposing the run. A
                    // later aggregate read must be able to prove it still covers
                    // every declared child/item instead of treating a missing
                    // lineage row as a smaller, apparently valid execution.
                    run.SetExpectedCoverage(
                        batches.Sum(batch => batch.TotalItems),
                        batches.Count);
                    run.MarkReady();
                    await gradingRepository.SaveChangesAsync(cancellationToken);
                }
            }
        }

        return new StartPendingGradingRunResult(
            CoursesDiscovered: courses.Count,
            CoursesScanned: courseResults.Count,
            CoursesWithPendingSubmissions: courseResults.Count(course => course.Status == "batch_created"),
            TotalItems: batches.Sum(batch => batch.TotalItems),
            Batches: batches,
            Courses: courseResults,
            Warnings: warnings,
            NextStep: batches.Count == 0
                ? existingCorrections.Count > 0
                    ? "As correcoes anteriores foram preservadas. Consulte existingCorrections e retome os lotes existentes; nenhuma duplicata foi criada."
                    : "Nao ha entregas pendentes elegiveis para iniciar a correcao. Consulte os cursos com falha para ajuste manual."
                : $"Use o gradingRunId {gradingRunId} para paginar o pacote de IA, salvar os rascunhos e exportar CSV. Se o usuario pediu publicacao, gere a previa e aguarde CONFIRMAR_PUBLICACAO antes de qualquer escrita no Moodle; os batchJobIds sao detalhes internos de compatibilidade.",
            GradingRunId: gradingRunId)
        {
            AssignmentResolution = assignmentResolutions,
            ExistingCorrections = existingCorrections
                .DistinctBy(reference => reference.GradingItemId)
                .ToArray(),
        };
    }

    private async Task<IReadOnlyList<CourseSummary>> LoadCoursesFromSnapshotAsync(
        StartPendingGradingRunCommand request,
        int maxCourses,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var snapshot = await snapshotStore!.GetCoursesAsync(
            request.SnapshotOwnerId!.Value,
            request.SnapshotConnectionAlias!,
            cancellationToken);
        if (snapshot is null)
        {
            var queued = await QueueSnapshotAsync(request, MoodleSnapshotDatasets.Courses, null, force: true, cancellationToken);
            warnings.Add(
                queued
                    ? "O snapshot de cursos ainda não está disponível; a atualização foi agendada e nenhuma consulta live foi feita."
                    : "O snapshot de cursos ainda não está disponível e não foi possível agendar a atualização.");
            return [];
        }

        if (snapshot.IsStale || !snapshot.IsComplete)
        {
            var queued = await QueueSnapshotAsync(request, MoodleSnapshotDatasets.Courses, null, force: true, cancellationToken);
            warnings.Add(
                queued
                    ? "O snapshot de cursos está desatualizado ou incompleto; a atualização foi agendada."
                    : "O snapshot de cursos está desatualizado ou incompleto e não foi possível agendar a atualização.");
        }

        return snapshot.Data
            .Take(maxCourses)
            .ToArray();
    }

    private async Task<PendingGradingRunCourse> ProcessSnapshotCourseAsync(
        StartPendingGradingRunCommand request,
        CourseSummary course,
        string courseName,
        int maxItemsPerBatch,
        Guid gradingRunId,
        List<PendingGradingRunBatch> batches,
        List<AssignmentResolution> assignmentResolutions,
        List<ExistingGradingCorrectionReference> existingCorrections,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (batches.Sum(batch => batch.TotalItems) >= MaxAggregateRunItems)
        {
            AddAggregateLimitWarning(warnings);
            return new PendingGradingRunCourse(
                course.CourseId,
                courseName,
                "aggregate_limit_reached",
                null,
                "O limite agregado de 10.000 itens desta execucao ja foi atingido.");
        }

        var snapshot = await snapshotStore!.GetAsync<CourseAssignmentSubmissionsSnapshot>(
            request.SnapshotOwnerId!.Value,
            request.SnapshotConnectionAlias!,
            MoodleSnapshotDatasets.Submissions,
            course.CourseId,
            cancellationToken);
        if (snapshot is null)
        {
            var queued = await QueueSnapshotAsync(request, MoodleSnapshotDatasets.Submissions, course.CourseId, force: true, cancellationToken);
            var message = queued
                ? "O snapshot de entregas ainda não está disponível; a atualização foi agendada."
                : "O snapshot de entregas ainda não está disponível e não foi possível agendar a atualização.";
            warnings.Add($"Curso {course.CourseId}: {message}");
            return new PendingGradingRunCourse(course.CourseId, courseName, "snapshot_unavailable", null, message);
        }

        if (snapshot.IsStale || !snapshot.IsComplete)
        {
            var queued = await QueueSnapshotAsync(request, MoodleSnapshotDatasets.Submissions, course.CourseId, force: true, cancellationToken);
            warnings.Add($"Curso {course.CourseId}: o snapshot de entregas está desatualizado ou incompleto; a leitura usou apenas os dados disponíveis e agendou atualização={queued}.");
        }

        var batchesBeforeCourse = batches.Count;
        var existingCorrectionsBeforeCourse = existingCorrections.Count;
        var courseMessages = new List<string>();
        var includeAlreadyGraded = request.AllowRegradeExisting || request.IncludeAlreadyGraded;
        var requestedAssignmentIds = request.AssignmentIds is null
            ? null
            : request.AssignmentIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignments = requestedAssignmentIds is { Count: > 0 }
            ? snapshot.Data.Assignments.Where(assignment =>
                requestedAssignmentIds.Contains(assignment.AssignmentId) ||
                (!string.IsNullOrWhiteSpace(assignment.AssignmentModuleId) &&
                 requestedAssignmentIds.Contains(assignment.AssignmentModuleId)))
            : snapshot.Data.Assignments;
        var selectedAssignments = assignments.ToArray();

        if (requestedAssignmentIds is { Count: > 0 })
        {
            var selectedIds = selectedAssignments
                .SelectMany(assignment => new[] { assignment.AssignmentId, assignment.AssignmentModuleId })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in selectedAssignments)
            {
                foreach (var requestedId in requestedAssignmentIds.Where(id =>
                             string.Equals(id, assignment.AssignmentId, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(id, assignment.AssignmentModuleId, StringComparison.OrdinalIgnoreCase)))
                {
                    assignmentResolutions.Add(new AssignmentResolution(
                        course.CourseId,
                        requestedId,
                        Resolved: true,
                        InstanceId: assignment.AssignmentId,
                        Cmid: assignment.AssignmentModuleId,
                        ErrorCode: null));
                }
            }
            foreach (var requestedId in requestedAssignmentIds.Where(id => !selectedIds.Contains(id)))
            {
                assignmentResolutions.Add(new AssignmentResolution(
                    course.CourseId,
                    requestedId,
                    Resolved: false,
                    InstanceId: null,
                    Cmid: null,
                    ErrorCode: MoodleErrorContract.AssignmentNotFound));
            }
        }

        if (selectedAssignments.Length == 0)
        {
            var snapshotIncomplete = snapshot.IsStale || !snapshot.IsComplete;
            var message = requestedAssignmentIds is { Count: > 0 }
                ? snapshotIncomplete
                    ? $"A atividade solicitada ({string.Join(", ", requestedAssignmentIds)}) não pôde ser verificada porque o snapshot de entregas está incompleto."
                    : $"Nenhuma das atividades solicitadas ({string.Join(", ", requestedAssignmentIds)}) foi encontrada no snapshot."
                : snapshotIncomplete
                    ? "As atividades avaliativas não puderam ser verificadas porque o snapshot de entregas está incompleto."
                    : "Nenhuma atividade avaliativa do tipo assign está disponível no snapshot.";
            return new PendingGradingRunCourse(
                course.CourseId,
                courseName,
                snapshotIncomplete ? "partial_failure" : "no_assignments",
                null,
                message);
        }

        foreach (var assignment in selectedAssignments)
        {
            if (!assignment.IsComplete ||
                (!includeAlreadyGraded &&
                 assignment.Coverage is not null && !assignment.Coverage.NeedsGradingComplete))
            {
                var message = assignment.ErrorMessage ??
                    "Os dados da tarefa não possuem cobertura completa de participantes, submissões, configuração e notas.";
                courseMessages.Add($"Tarefa {assignment.AssignmentId}: {message}");
                warnings.Add($"Curso {course.CourseId}: tarefa {assignment.AssignmentId}: {message}");
                continue;
            }

            var submissions = assignment.Submissions
                .Where(submission => includeAlreadyGraded
                    ? IsSubmittedForRegrade(submission)
                    : submission.NeedsGrading)
                .ToArray();
            foreach (var submissionChunk in submissions.Chunk(maxItemsPerBatch))
            {
                var remainingRunItems = MaxAggregateRunItems - batches.Sum(batch => batch.TotalItems);
                if (remainingRunItems <= 0)
                {
                    AddAggregateLimitWarning(warnings);
                    break;
                }

                var boundedSubmissionChunk = submissionChunk
                    .Take(Math.Min(maxItemsPerBatch, remainingRunItems))
                    .ToArray();
                try
                {
                    var batch = await mediator.Send(
                        new CreateAssistedGradingBatchCommand(
                            request.UserExternalId,
                            course.CourseId,
                            AssignmentIds: [assignment.AssignmentId],
                            SubmissionIds: [],
                            MaxItems: maxItemsPerBatch,
                            OnlyAwaitingGrading: !includeAlreadyGraded,
                            IncludeAlreadyGraded: includeAlreadyGraded,
                            IncludeRubric: request.IncludeRubric,
                            IncludeSubmissionFiles: request.IncludeSubmissionFiles,
                            IncludeCourseMaterials: request.IncludeCourseMaterials,
                            TeacherInstructions: request.TeacherInstructions,
                            Priority: request.Priority,
                            CourseDisplayName: courseName,
                            PrefetchedSubmissions: boundedSubmissionChunk,
                            GradingRunId: gradingRunId),
                        cancellationToken);

                    warnings.AddRange(batch.Warnings.Select(warning => $"Curso {course.CourseId}: {warning}"));
                    existingCorrections.AddRange(batch.ExistingCorrections);
                    if (batch.BatchJobId == Guid.Empty || batch.AcceptedItems == 0)
                    {
                        continue;
                    }

                    batches.Add(new PendingGradingRunBatch(
                        batch.BatchJobId,
                        batch.CourseId,
                        courseName,
                        batch.AssignmentIds,
                        batch.AcceptedItems,
                        batch.BlockedItems));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var message = $"Nao foi possivel preparar o sublote da tarefa {assignment.AssignmentId}: {ex.Message}";
                    courseMessages.Add(message);
                    warnings.Add($"Curso {course.CourseId}: {message}");
                }
            }
        }

        var courseBatchCount = batches.Count - batchesBeforeCourse;
        var courseExistingCorrectionCount = existingCorrections.Count - existingCorrectionsBeforeCourse;
        return new PendingGradingRunCourse(
            course.CourseId,
            courseName,
            courseBatchCount > 0
                ? "batch_created"
                : courseMessages.Count > 0
                    ? "partial_failure"
                    : courseExistingCorrectionCount > 0
                        ? "already_queued"
                    : "no_pending_submissions",
            courseBatchCount == 1 ? batches[^1].BatchJobId : null,
                courseBatchCount > 0
                    ? $"{courseBatchCount} sublote(s) criado(s). {string.Join(" ", courseMessages)}".Trim()
                    : courseMessages.Count > 0
                        ? string.Join(" ", courseMessages)
                        : courseExistingCorrectionCount > 0
                            ? $"{courseExistingCorrectionCount} entrega(s) já possuem correção neste fluxo ou em uma execução anterior; o trabalho existente foi preservado."
                        : "Nenhuma entrega aguardando correcao foi encontrada.");
    }

    private async Task<bool> QueueSnapshotAsync(
        StartPendingGradingRunCommand request,
        string dataset,
        string? courseId,
        bool force,
        CancellationToken cancellationToken)
    {
        if (snapshotSyncQueue is null)
        {
            return false;
        }

        return await snapshotSyncQueue.EnqueueAsync(
            new MoodleSnapshotSyncRequest(
                request.SnapshotOwnerId!.Value,
                request.SnapshotClientId!,
                request.SnapshotConnectionAlias!,
                request.UserExternalId,
                force,
                dataset,
                courseId,
                Priority: 5),
            cancellationToken);
    }

    private async Task<IReadOnlyList<AssignmentSubmissionSummary>> LoadPendingSubmissionsAsync(
        string userExternalId,
        string courseId,
        string assignmentId,
        int maxItems,
        bool allowRegradeExisting,
        CancellationToken cancellationToken)
    {
        if (maxItems <= 0)
        {
            return [];
        }

        var submissions = new List<AssignmentSubmissionSummary>();
        var page = 1;
        while (true)
        {
            var response = await mediator.Send(
                new ListAssignmentSubmissionsQuery(
                    userExternalId,
                    courseId,
                    assignmentId,
                    allowRegradeExisting
                        ? AssignmentSubmissionFilter.All
                        : AssignmentSubmissionFilter.NeedsGrading,
                    page,
                    PageSize: 100,
                    Since: null,
                    Before: null,
                    IncludeLate: true,
                    IncludeUngraded: true),
                cancellationToken)
                ?? throw new InvalidOperationException("Tarefa nao encontrada para o usuario atual.");

            foreach (var submission in response.Submissions)
            {
                if (allowRegradeExisting
                    ? IsSubmittedForRegrade(submission)
                    : submission.NeedsGrading)
                {
                    submissions.Add(submission);
                    if (submissions.Count >= maxItems)
                    {
                        return submissions;
                    }
                }
            }
            if (!response.HasMore)
            {
                break;
            }

            page++;
        }

        return submissions;
    }

    private async Task<BulkPendingSubmissionRead> LoadPendingSubmissionsByAssignmentsAsync(
        string userExternalId,
        IReadOnlyCollection<string> assignmentIds,
        int maxItems,
        bool allowRegradeExisting,
        CancellationToken cancellationToken)
    {
        if (submissionsGateway is null || maxItems <= 0 || assignmentIds.Count == 0)
        {
            return new BulkPendingSubmissionRead(
                new Dictionary<string, IReadOnlyList<AssignmentSubmissionSummary>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        // A single mod_assign_get_submissions call can cover up to 50
        // activities. This removes the old N+1 chain (course, participants,
        // settings, grades and submissions) from the request path and keeps
        // a 400-item course within the MCP request budget.
        var batches = await submissionsGateway.GetAssignmentSubmissionsBatchAsync(
            userExternalId,
            assignmentIds,
            status: allowRegradeExisting ? null : "submitted",
            since: null,
            before: null,
            cancellationToken);
        var successful = new Dictionary<string, IReadOnlyList<AssignmentSubmissionSummary>>(
            StringComparer.OrdinalIgnoreCase);
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assignmentId in assignmentIds)
        {
            var batch = batches.FirstOrDefault(candidate =>
                string.Equals(candidate.AssignmentId, assignmentId, StringComparison.OrdinalIgnoreCase));
            if (batch is null)
            {
                // An omitted row has no coverage; it must never be interpreted
                // as a successful empty activity.
                failed[assignmentId] = "missing_result";
                continue;
            }

            if (!string.IsNullOrWhiteSpace(batch.ErrorCode))
            {
                failed[assignmentId] = "reported_error";
                continue;
            }

            successful[assignmentId] = batch.Submissions
                .Where(submission => allowRegradeExisting
                    ? IsSubmittedForRegrade(submission)
                    : IsPendingDirectSubmission(submission))
                .Take(maxItems)
                .Select(ToPendingSummary)
                .ToArray();
        }

        return new BulkPendingSubmissionRead(successful, failed);
    }

    private static bool IsPendingDirectSubmission(AssignmentSubmissionRecord submission)
    {
        if (string.Equals(submission.Status, "draft", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(submission.Status, "notsubmitted", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Use the same evidence-based resolver as the detailed submissions
        // query so feedback-only assignments are included when unreviewed,
        // but an existing feedback/grader timestamp is not requeued.
        return SubmissionEvaluationStateResolver.NeedsGrading(ResolveDirectState(submission));
    }

    private static bool IsSubmittedForRegrade(AssignmentSubmissionSummary submission) =>
        submission.Submitted && IsDeliveredSubmissionStatus(submission.Status);

    private static bool IsSubmittedForRegrade(AssignmentSubmissionRecord submission) =>
        IsDeliveredSubmissionStatus(submission.Status);

    private static bool IsDeliveredSubmissionStatus(string? status) =>
        string.Equals(status, "submitted", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "graded", StringComparison.OrdinalIgnoreCase);

    private static SubmissionEvaluationState ResolveDirectState(AssignmentSubmissionRecord submission) =>
        SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
            HasSubmission: ToSubmissionPresence(submission.Status),
            GradeRaw: null,
            GradedDateGraded: null,
            Feedback: submission.CurrentFeedback,
            ReviewEvidenceAvailable: IsExplicitlyUnreviewed(submission.GradingStatus),
            GradingStatus: submission.GradingStatus,
            GraderId: submission.CurrentGraderId,
            GradeTimeModified: submission.CurrentGradeTimeModified,
            SubmissionTimeModified: submission.ModifiedAt?.ToUnixTimeSeconds()));

    private static bool? ToSubmissionPresence(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "submitted" => true,
        "new" or "draft" or "reopened" or "notsubmitted" or "not_submitted" => false,
        _ => null
    };

    private static bool IsExplicitlyUnreviewed(string? gradingStatus) =>
        string.Equals(gradingStatus, "notgraded", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(gradingStatus, "needsgrading", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(gradingStatus, "notmarked", StringComparison.OrdinalIgnoreCase);

    private static AssignmentSubmissionSummary ToPendingSummary(AssignmentSubmissionRecord submission) =>
        new(
            submission.UserId,
            null,
            submission.SubmissionId,
            submission.Status,
            submission.GradingStatus,
            true,
            false,
            SubmissionEvaluationStateResolver.NeedsGrading(ResolveDirectState(submission)),
            submission.CreatedAt,
            submission.ModifiedAt,
            submission.AttemptNumber,
            submission.FileCount,
            submission.HasOnlineText,
            submission.Files,
            null,
            submission.CurrentFeedback,
            null,
            ResolveDirectState(submission),
            submission.OnlineText,
            submission.CurrentGraderId,
            submission.CurrentGradeTimeModified);

    private static string BuildBatchIdempotencyKey(
        Guid gradingRunId,
        string courseId,
        string assignmentId,
        int chunkIndex) =>
        $"pending-run:{gradingRunId:N}:course:{courseId}:assignment:{assignmentId}:chunk:{chunkIndex}";

    private static string BuildSelectionRequestDigest(
        string ownerSubject,
        StartPendingGradingRunCommand request,
        string courseId,
        string assignmentId,
        IReadOnlyList<AssignmentSubmissionSummary> selectedSubmissions,
        string? moodleConnectionId)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            OwnerSubject = ownerSubject,
            request.UserExternalId,
            MoodleConnectionId = moodleConnectionId,
            ClientId = request.SnapshotClientId,
            ConnectionAlias = request.SnapshotConnectionAlias,
            CourseId = courseId,
            AssignmentId = assignmentId,
            Submissions = selectedSubmissions
                .OrderBy(submission => submission.SubmissionId, StringComparer.Ordinal)
                .Select(submission => new { submission.SubmissionId, submission.AttemptNumber })
                .ToArray(),
            request.IncludeRubric,
            request.IncludeSubmissionFiles,
            request.IncludeCourseMaterials,
            TeacherInstructions = request.TeacherInstructions?.Trim(),
            Priority = request.Priority.Trim().ToLowerInvariant(),
            request.AllowRegradeExisting,
            request.IncludeAlreadyGraded,
            request.MaxItemsPerBatch
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private async Task<IReadOnlyList<CourseSummary>> LoadCoursesAsync(
        string userExternalId,
        int maxCourses,
        CancellationToken cancellationToken)
    {
        var courses = new List<CourseSummary>();
        var seenCourseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var page = 1;

        while (courses.Count < maxCourses)
        {
            var result = await mediator.Send(
                new ListMyCoursesQuery(userExternalId, Math.Min(CoursePageSize, maxCourses - courses.Count), page),
                cancellationToken);
            foreach (var course in result.Items)
            {
                if (seenCourseIds.Add(course.CourseId))
                {
                    courses.Add(course);
                    if (courses.Count >= maxCourses)
                    {
                        break;
                    }
                }
            }

            if (!result.HasNextPage || result.Items.Count == 0)
            {
                break;
            }

            page++;
        }

        return courses;
    }

    private static string ResolveCourseName(CourseSummary course) =>
        course.DisplayName ?? course.FullName ?? course.ShortName ?? course.CourseId;

    private static void AddAggregateLimitWarning(List<string> warnings)
    {
        const string warning = "A execucao agregada foi limitada a 10.000 itens; inicie uma nova execucao para o restante.";
        if (!warnings.Contains(warning, StringComparer.Ordinal))
        {
            warnings.Add(warning);
        }
    }

    private sealed record BulkPendingSubmissionRead(
        IReadOnlyDictionary<string, IReadOnlyList<AssignmentSubmissionSummary>> Successful,
        IReadOnlyDictionary<string, string> Failed);
}

public sealed class RequeueBlockedGradingItemsCommandHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser)
    : IRequestHandler<RequeueBlockedGradingItemsCommand, RequeueBlockedGradingItemsResult>
{
    public async Task<RequeueBlockedGradingItemsResult> Handle(
        RequeueBlockedGradingItemsCommand request,
        CancellationToken cancellationToken)
    {
        if (request.BatchJobId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(request.BatchJobId));
        }

        var requestedIds = request.GradingItemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (requestedIds.Length == 0)
        {
            throw new ArgumentException("Informe pelo menos um item para reprocessar.", nameof(request.GradingItemIds));
        }

        var scope = await GradingBatchScopeResolver.ResolveAsync(
            repository,
            currentUser,
            request.BatchJobId,
            cancellationToken);
        var scopeBatchIds = scope.Batches.Select(batch => batch.Id).ToHashSet();
        var items = await repository.GetItemsAsync(requestedIds, cancellationToken);
        var failures = new List<RequeueBlockedGradingItemFailure>();
        var requeuedItems = 0;
        var alreadyQueuedItems = 0;

        foreach (var gradingItemId in requestedIds)
        {
            if (!items.TryGetValue(gradingItemId, out var item))
            {
                failures.Add(new(gradingItemId, "Item de correcao nao encontrado."));
                continue;
            }

            if (!scopeBatchIds.Contains(item.BatchId))
            {
                failures.Add(new(gradingItemId, "Item nao pertence ao lote ou execucao informada."));
                continue;
            }

            if (item.CommitStatus == GradingCommitStatus.Succeeded ||
                item.Status == GradingItemStatus.Committed)
            {
                failures.Add(new(gradingItemId, "Item ja publicado; reprocessamento recusado."));
                continue;
            }

            if (item.FinalGrade is not null || !string.IsNullOrWhiteSpace(item.FinalFeedback))
            {
                failures.Add(new(gradingItemId, "Item possui revisao final; reprocessamento recusado para preservar a decisao humana."));
                continue;
            }

            if (item.Status is GradingItemStatus.Pending or GradingItemStatus.AwaitingAiAnalysis)
            {
                alreadyQueuedItems++;
                continue;
            }

            if (item.Status is not (GradingItemStatus.Blocked or GradingItemStatus.Failed))
            {
                failures.Add(new(gradingItemId, $"Estado {item.Status} nao permite reprocessamento seguro."));
                continue;
            }

            // A recuperação manual precisa voltar ao pipeline de preparação.
            // Marcar AwaitingAiAnalysis aqui faria o próximo passo pular a
            // ingestão/contexto e enviar para a IA um item que pode nem ter
            // sido lido pelo Moodle ainda.
            item.RequeueForProcessing("Reprocessamento manual autorizado para corrigir o bloqueio anterior.");
            requeuedItems++;
        }

        if (requeuedItems > 0)
        {
            foreach (var scopeBatch in scope.Batches)
            {
                var allItems = await GradingItemProcessor.LoadAllBatchItemsAsync(
                    repository,
                    scopeBatch.Id,
                    cancellationToken);
                GradingItemProcessor.UpdateBatchCounters(scopeBatch, allItems);
            }
            await repository.SaveChangesAsync(cancellationToken);
        }

        var failedItems = failures.Count;
        return new RequeueBlockedGradingItemsResult(
            request.BatchJobId,
            requestedIds.Length,
            requeuedItems,
            alreadyQueuedItems,
            failedItems,
            failures,
            requeuedItems > 0 || alreadyQueuedItems > 0
                ? "O item está na fila de preparação. Consulte o status; quando a análise estiver pronta, leia os recursos e salve o rascunho."
                : "Nenhum item foi reaberto; verifique as falhas retornadas.")
        {
            ProcessingState = requeuedItems > 0
                ? "queued_for_reprocessing"
                : alreadyQueuedItems > 0 ? "already_queued" : "unchanged",
            NextAction = requeuedItems > 0 || alreadyQueuedItems > 0 ? "wait" : "attention"
        };
    }
}

/// <summary>
/// Recupera falhas de publicação sem refazer a análise nem apagar a decisão
/// do professor. A prévia seguinte executará novamente todas as proteções de
/// contexto, tentativa, nota existente e feedback antes de pedir confirmação.
/// </summary>
public sealed record RequeueFailedGradingPublicationItemsCommand(
    Guid BatchJobId,
    IReadOnlyList<Guid> GradingItemIds) : IRequest<RequeueFailedGradingPublicationItemsResult>;

public sealed record RequeueFailedGradingPublicationItemsResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("requestedItems")] int RequestedItems,
    [property: JsonPropertyName("requeuedItems")] int RequeuedItems,
    [property: JsonPropertyName("alreadyQueuedItems")] int AlreadyQueuedItems,
    [property: JsonPropertyName("failedItems")] int FailedItems,
    [property: JsonPropertyName("failures")] IReadOnlyList<RequeueBlockedGradingItemFailure> Failures,
    [property: JsonPropertyName("nextStep")] string NextStep);

public sealed class RequeueFailedGradingPublicationItemsCommandHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser)
    : IRequestHandler<RequeueFailedGradingPublicationItemsCommand, RequeueFailedGradingPublicationItemsResult>
{
    public async Task<RequeueFailedGradingPublicationItemsResult> Handle(
        RequeueFailedGradingPublicationItemsCommand request,
        CancellationToken cancellationToken)
    {
        if (request.BatchJobId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(request.BatchJobId));
        }

        var requestedIds = request.GradingItemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (requestedIds.Length == 0)
        {
            throw new ArgumentException("Informe pelo menos um item para recuperar.", nameof(request.GradingItemIds));
        }

        var scope = await GradingBatchScopeResolver.ResolveAsync(
            repository,
            currentUser,
            request.BatchJobId,
            cancellationToken);
        var scopeBatchIds = scope.Batches.Select(batch => batch.Id).ToHashSet();
        var items = await repository.GetItemsAsync(requestedIds, cancellationToken);
        var failures = new List<RequeueBlockedGradingItemFailure>();
        var requeuedItems = 0;
        var alreadyQueuedItems = 0;

        foreach (var gradingItemId in requestedIds)
        {
            if (!items.TryGetValue(gradingItemId, out var item))
            {
                failures.Add(new(gradingItemId, "Item de correcao nao encontrado."));
                continue;
            }

            if (!scopeBatchIds.Contains(item.BatchId))
            {
                failures.Add(new(gradingItemId, "Item nao pertence ao lote ou execucao informada."));
                continue;
            }

            if (item.CommitStatus == GradingCommitStatus.Succeeded ||
                item.Status == GradingItemStatus.Committed)
            {
                failures.Add(new(gradingItemId, "Item ja publicado; a recuperacao foi recusada."));
                continue;
            }

            if (item.CommitStatus == GradingCommitStatus.ExecutionUnknown)
            {
                failures.Add(new(gradingItemId, "A execucao Moodle e desconhecida; reconcilie o item antes de tentar novamente."));
                continue;
            }

            if (item.Status == GradingItemStatus.ReadyToCommit &&
                item.CommitStatus == GradingCommitStatus.Pending)
            {
                alreadyQueuedItems++;
                continue;
            }

            try
            {
                item.RequeueCommitForRetry();
                requeuedItems++;
            }
            catch (InvalidOperationException ex)
            {
                failures.Add(new(gradingItemId, ex.Message));
            }
        }

        if (requeuedItems > 0)
        {
            foreach (var scopeBatch in scope.Batches)
            {
                var allItems = await GradingItemProcessor.LoadAllBatchItemsAsync(
                    repository,
                    scopeBatch.Id,
                    cancellationToken);
                GradingItemProcessor.UpdateBatchCounters(scopeBatch, allItems);
            }
            await repository.SaveChangesAsync(cancellationToken);
        }

        return new RequeueFailedGradingPublicationItemsResult(
            request.BatchJobId,
            requestedIds.Length,
            requeuedItems,
            alreadyQueuedItems,
            failures.Count,
            failures,
            requeuedItems > 0 || alreadyQueuedItems > 0
                ? "Gere uma nova previa com create_batch_grade_launch_preview e confirme somente apos revisar os avisos."
                : "Nenhum item foi reaberto; verifique as falhas retornadas.");
    }
}

public sealed class FindGradingCorrectionBySubmissionQueryHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser)
    : IRequestHandler<FindGradingCorrectionBySubmissionQuery, FindGradingCorrectionBySubmissionResult>
{
    public async Task<FindGradingCorrectionBySubmissionResult> Handle(
        FindGradingCorrectionBySubmissionQuery request,
        CancellationToken cancellationToken)
    {
        if (request.SubmissionId <= 0)
        {
            throw new ArgumentException("O submissionId deve ser positivo.", nameof(request.SubmissionId));
        }

        var matches = await repository.FindSubmissionMatchesAsync(
            request.SubmissionId,
            request.CourseId,
            request.AssignmentId,
            request.MoodleConnectionId,
            request.ConnectorClientId,
            request.ConnectionAlias,
            currentUser.Subject,
            cancellationToken);
        var references = matches
            .Select(ToReference)
            .DistinctBy(reference => reference.GradingItemId)
            .ToArray();

        return new FindGradingCorrectionBySubmissionResult(
            request.SubmissionId,
            references.Length > 0,
            references,
            references.Length > 0
                ? "Correcao anterior encontrada; use o batchJobId ou gradingRunId retornado para consultar ou cancelar o lote."
                : "Nenhuma correcao ativa do conector foi encontrada para esta submissao.");
    }

    internal static ExistingGradingCorrectionReference ToReference(GradingSubmissionMatch match) =>
        new(
            match.Identity.SubmissionId,
            match.GradingItemId,
            match.BatchJobId,
            match.GradingRunId,
            match.ItemStatus.ToString(),
            match.CommitStatus.ToString(),
            match.BatchStatus.ToString(),
            match.RunStatus?.ToString());
}

public sealed record GetPendingGradingRunReportQuery(
    IReadOnlyList<Guid> BatchJobIds) : IRequest<PendingGradingRunReportResult>;

public sealed record PendingGradingRunReportResult(
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("batchJobIds")] IReadOnlyList<Guid> BatchJobIds,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("correctedCount")] int CorrectedCount,
    [property: JsonPropertyName("notCorrectedCount")] int NotCorrectedCount,
    [property: JsonPropertyName("batches")] IReadOnlyList<PendingGradingRunBatchReport> Batches,
    [property: JsonPropertyName("correctedItems")] IReadOnlyList<PendingGradingRunItemOutcome> CorrectedItems,
    [property: JsonPropertyName("notCorrectedItems")] IReadOnlyList<PendingGradingRunItemOutcome> NotCorrectedItems,
    [property: JsonPropertyName("reportMarkdown")] string ReportMarkdown);

public sealed record PendingGradingRunBatchReport(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("correctedCount")] int CorrectedCount,
    [property: JsonPropertyName("notCorrectedCount")] int NotCorrectedCount);

public sealed record PendingGradingRunItemOutcome(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("submissionId")] string? SubmissionId,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("commitStatus")] string CommitStatus,
    [property: JsonPropertyName("grade")] decimal? Grade,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed class GetPendingGradingRunReportQueryHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser)
    : IRequestHandler<GetPendingGradingRunReportQuery, PendingGradingRunReportResult>
{
    public async Task<PendingGradingRunReportResult> Handle(
        GetPendingGradingRunReportQuery request,
        CancellationToken cancellationToken)
    {
        var batchIds = request.BatchJobIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (batchIds.Length == 0)
        {
            throw new ArgumentException("Informe pelo menos um lote de correcao valido.", nameof(request.BatchJobIds));
        }

        var corrected = new List<PendingGradingRunItemOutcome>();
        var notCorrected = new List<PendingGradingRunItemOutcome>();
        var batchReports = new List<PendingGradingRunBatchReport>();
        var expandedBatchIds = new List<Guid>();
        foreach (var requestedId in batchIds)
        {
            var directBatch = await repository.GetBatchAsync(requestedId, cancellationToken);
            if (directBatch is not null)
            {
                GradingAccessControl.EnsureCanAccessBatch(directBatch, currentUser);
                expandedBatchIds.Add(directBatch.Id);
                continue;
            }

            var scope = await GradingBatchScopeResolver.ResolveAsync(
                repository,
                currentUser,
                requestedId,
                cancellationToken);
            expandedBatchIds.AddRange(scope.Batches.Select(batch => batch.Id));
        }

        foreach (var batchId in expandedBatchIds.Distinct())
        {
            var batch = await repository.GetBatchAsync(batchId, cancellationToken)
                ?? throw new InvalidOperationException($"Lote de correcao {batchId} nao encontrado.");
            GradingAccessControl.EnsureCanAccessBatch(batch, currentUser);
            var items = await GradingItemProcessor.LoadAllBatchItemsAsync(repository, batch.Id, cancellationToken);
            var batchCorrected = 0;
            var batchNotCorrected = 0;

            foreach (var item in items)
            {
                var outcome = ToOutcome(batch, item);
                if (item.Status == GradingItemStatus.Committed || item.CommitStatus == GradingCommitStatus.Succeeded)
                {
                    corrected.Add(outcome with { Reason = null, Grade = item.FinalGrade ?? item.SuggestedGrade });
                    batchCorrected++;
                }
                else
                {
                    notCorrected.Add(outcome);
                    batchNotCorrected++;
                }
            }

            batchReports.Add(new PendingGradingRunBatchReport(
                batch.Id,
                batch.CourseId.ToString(CultureInfo.InvariantCulture),
                items.Count,
                batchCorrected,
                batchNotCorrected));
        }

        var report = new PendingGradingRunReportResult(
            DateTimeOffset.UtcNow,
            batchIds,
            corrected.Count + notCorrected.Count,
            corrected.Count,
            notCorrected.Count,
            batchReports,
            corrected.OrderBy(item => item.CourseId, StringComparer.Ordinal)
                .ThenBy(item => item.AssignmentId, StringComparer.Ordinal)
                .ThenBy(item => item.StudentId, StringComparer.Ordinal)
                .ToArray(),
            notCorrected.OrderBy(item => item.CourseId, StringComparer.Ordinal)
                .ThenBy(item => item.AssignmentId, StringComparer.Ordinal)
                .ThenBy(item => item.StudentId, StringComparer.Ordinal)
                .ToArray(),
            ReportMarkdown: string.Empty);

        return report with { ReportMarkdown = BuildReportMarkdown(report) };
    }

    private static PendingGradingRunItemOutcome ToOutcome(AssistedGradingBatch batch, AssistedGradingItem item)
    {
        var reason = item.CommitStatus == GradingCommitStatus.ExecutionUnknown
            ? "Resultado da escrita no Moodle desconhecido; reconcilie a ação antes de tentar novamente."
            : item.CommitStatus == GradingCommitStatus.Failed
            ? "Falha ao lancar no Moodle: " + Describe(item.CommitError)
            : item.Status == GradingItemStatus.Blocked
                ? "Bloqueado: " + Describe(item.DraftFeedback)
                : item.Status == GradingItemStatus.Failed
                    ? "Falha no processamento: " + Describe(item.CommitError ?? item.DraftFeedback)
                    : item.Status == GradingItemStatus.AwaitingAiAnalysis
                        ? "Aguardando analise pela IA."
                        : item.ReviewStatus != GradingReviewStatus.Reviewed
                            ? "Aguardando revisao humana do rascunho."
                            : item.CommitStatus == GradingCommitStatus.Pending
                                ? "Aguardando previa e confirmacao para lancamento no Moodle."
                                : "Aguardando processamento da correcao.";

        return new PendingGradingRunItemOutcome(
            batch.Id,
            batch.CourseId.ToString(CultureInfo.InvariantCulture),
            item.Id,
            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            item.SubmissionId?.ToString(CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
            item.Status.ToString(),
            item.CommitStatus.ToString(),
            item.FinalGrade ?? item.SuggestedGrade,
            reason);
    }

    private static string BuildReportMarkdown(PendingGradingRunReportResult report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Relatorio final de correcoes pendentes");
        builder.AppendLine();
        builder.AppendLine($"- Lotes processados: {report.BatchJobIds.Count}");
        builder.AppendLine($"- Total de entregas: {report.TotalItems}");
        builder.AppendLine($"- Corrigidas e lancadas no Moodle: {report.CorrectedCount}");
        builder.AppendLine($"- Nao corrigidas: {report.NotCorrectedCount}");
        builder.AppendLine();
        builder.AppendLine("## Corrigidas e lancadas no Moodle");
        builder.AppendLine();
        if (report.CorrectedItems.Count == 0)
        {
            builder.AppendLine("- Nenhuma entrega foi lancada no Moodle ainda.");
        }
        else
        {
            foreach (var item in report.CorrectedItems)
            {
                builder.AppendLine($"- Curso `{item.CourseId}`, tarefa `{item.AssignmentId}`, estudante `{item.StudentId}`, item `{item.GradingItemId}`, nota `{FormatGrade(item.Grade)}`.");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Nao corrigidas para ajuste manual");
        builder.AppendLine();
        if (report.NotCorrectedItems.Count == 0)
        {
            builder.AppendLine("- Nenhuma pendencia restante.");
        }
        else
        {
            foreach (var item in report.NotCorrectedItems)
            {
                builder.AppendLine($"- Curso `{item.CourseId}`, tarefa `{item.AssignmentId}`, estudante `{item.StudentId}`, item `{item.GradingItemId}`: {item.Reason}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "motivo nao informado." : value.Trim();

    private static string FormatGrade(decimal? grade) =>
        grade?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/d";
}
