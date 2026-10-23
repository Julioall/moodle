using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Domain;

namespace Moodle.Application.Submissions.Queries;

/// <summary>
/// Resumo de uma atividade pendente (não entregue) por estudante.
/// </summary>
public sealed record PendingSubmissionItem(
    string AssignmentId,
    string AssignmentName,
    DateTimeOffset? DueDate,
    bool IsOverdue);

public sealed record AwaitingGradingItem(
    string AssignmentId,
    string AssignmentName,
    DateTimeOffset? DueDate,
    DateTimeOffset? SubmittedAt);

/// <summary>
/// A serializable, self-contained record for a delivered assignment that is
/// awaiting grading. Value tuples serialize as empty objects in System.Text.Json.
/// </summary>
public sealed record AwaitingGradingSubmission(
    string CourseId,
    string AssignmentId,
    string AssignmentName,
    string StudentId,
    string StudentName,
    DateTimeOffset? LastCourseAccessAt,
    string SubmissionStatus,
    DateTimeOffset? SubmittedAt,
    string GradingStatus);

/// <summary>Canonical submission state consumed by pending tools and reports.</summary>
public sealed record SubmissionEvaluationItem(
    string CourseId,
    string AssignmentId,
    string AssignmentName,
    string StudentId,
    SubmissionEvaluationState State,
    DateTimeOffset? SubmittedAt)
{
    /// <summary>
    /// Delivery evidence is independent from grading evidence. A delivered
    /// submission can have an unknown evaluation when grade/review coverage fails.
    /// </summary>
    public SubmissionDeliveryState DeliveryState { get; init; } = SubmissionDeliveryState.Unknown;
}

public sealed record SubmissionDeliveryCoverage(
    bool ParticipantsComplete,
    bool InventoryComplete,
    int? EligibleAssignmentCount,
    int ObservedAssignmentCount,
    bool SubmissionReadsComplete,
    bool IsComplete,
    string? ReasonCode)
{
    /// <summary>Unknown assignment deliveries for the analyzed population; null when scope is incomplete.</summary>
    public int? UnknownAssignmentsCount { get; init; }

    public static SubmissionDeliveryCoverage Unknown { get; } = new(
        ParticipantsComplete: false,
        InventoryComplete: false,
        EligibleAssignmentCount: null,
        ObservedAssignmentCount: 0,
        SubmissionReadsComplete: false,
        IsComplete: false,
        ReasonCode: "activity_coverage_unavailable");
}

/// <summary>
/// Resumo de estudante com pelo menos uma SA pendente.
/// </summary>
public sealed record StudentPendingSubmissionSummary(
    string StudentId,
    string FullName,
    DateTimeOffset? LastCourseAccessAt,
    IReadOnlyList<PendingSubmissionItem> PendingAssignments);

public sealed record GetStudentsWithPendingSubmissionsResult(
    string CourseId,
    int TotalStudentsAnalyzed,
    int DueDaysAhead,
    IReadOnlyList<StudentPendingSubmissionSummary> Students,
    IReadOnlyList<string> SuggestedRecipientIds,
    string? Warning)
{
    public IReadOnlyList<AwaitingGradingSubmission> AwaitingGrading { get; init; } = [];
    public IReadOnlyList<SubmissionEvaluationItem> Evaluations { get; init; } = [];
    public IReadOnlyList<string> GradebookNotReturnedStudentIds { get; init; } = [];
    public IReadOnlyList<string> GradebookErrorStudentIds { get; init; } = [];
    public bool IsComplete { get; init; } = true;
    public string? IncompleteReasonCode { get; init; }
    public SubmissionDeliveryCoverage DeliveryCoverage { get; init; } = SubmissionDeliveryCoverage.Unknown;
}

/// <summary>
/// Lista estudantes com SAs pendentes de entrega.
///
/// Estratégia:
/// 1. Busca todos os módulos do tipo 'assign' no curso.
/// 2. Para cada assign, obtém quem não submeteu e, quando solicitado, quem
///    entregou e ainda aguarda correção.
/// 3. Consolida por estudante.
///
/// DueDaysAhead = 0 → inclui todas as atividades sem entrega, independente do prazo.
/// DueDaysAhead > 0 → inclui apenas atividades cujo prazo está nos próximos N dias (ou já vencido).
/// ExcludeFutureActivities = true → remove atividades cuja data de abertura
/// ainda está no futuro, mesmo quando DueDaysAhead = 0.
/// Inventário elegível, modo de submissão, entregas e evidência de correção
/// são lidos ao vivo; snapshots pré-lidos não substituem essas dimensões.
/// </summary>
public sealed record GetStudentsWithPendingSubmissionsQuery(
    string CourseId,
    int DueDaysAhead = 0,
    int MaxStudentsToAnalyze = 100,
    bool IncludeAwaitingGrading = false,
    int MaxAssignmentsToAnalyze = 0,
    CourseParticipantsPage? PrefetchedParticipants = null,
    CourseGradebookSnapshot? PrefetchedGradebook = null,
    bool ExcludeFutureActivities = true) : IRequest<GetStudentsWithPendingSubmissionsResult>;

public sealed class GetStudentsWithPendingSubmissionsQueryHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleAssignmentSubmissionsGateway submissionsGateway,
    IMoodleCourseContentsGateway contentsGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMoodleAssignmentSettingsGateway assignmentSettingsGateway,
    IMoodleAssignmentGradeReadGateway? gradeReadGateway = null,
    IMoodleGradebookGateway? gradebookGateway = null)
    : IRequestHandler<GetStudentsWithPendingSubmissionsQuery, GetStudentsWithPendingSubmissionsResult>
{
    public async Task<GetStudentsWithPendingSubmissionsResult> Handle(
        GetStudentsWithPendingSubmissionsQuery request,
        CancellationToken cancellationToken)
    {
        var currentUserExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();

        // 1. Fetch active students
        var requestedStudentLimit = request.MaxStudentsToAnalyze > 0 ? request.MaxStudentsToAnalyze : 100;
        var participantsPage = IsReusableParticipants(request.PrefetchedParticipants, request.CourseId)
            ? request.PrefetchedParticipants! with
            {
                Participants = request.PrefetchedParticipants!.Participants
                    .Take(requestedStudentLimit)
                    .ToArray()
            }
            : await participantsGateway.GetCourseParticipantsAsync(
                userExternalId: currentUserExternalId,
                courseId: request.CourseId,
                statusFilter: ParticipantStatusFilter.Active,
                page: 1,
                pageSize: requestedStudentLimit,
                studentsOnly: true,
                includeEmail: false,
                groupId: null,
                cancellationToken: cancellationToken);

        var studentMap = participantsPage.Participants.ToDictionary(p => p.UserId, p => p);
        var pendingByStudent = studentMap.Keys.ToDictionary(id => id, _ => new List<PendingSubmissionItem>());
        var gradingByStudent = studentMap.Keys.ToDictionary(id => id, _ => new List<AwaitingGradingItem>());

        // 2. Fetch course contents — only assign modules
        CourseContentsSummary contents;
        try
        {
            contents = await contentsGateway.GetCourseContentsAsync(
                userExternalId: currentUserExternalId,
                courseId: request.CourseId,
                moduleTypes: ["assign"],
                includeHidden: false,
                onlyWithFiles: false,
                cancellationToken: cancellationToken);
        }
        catch
        {
            return new GetStudentsWithPendingSubmissionsResult(
                CourseId: request.CourseId,
                TotalStudentsAnalyzed: 0,
                DueDaysAhead: request.DueDaysAhead,
                Students: [],
                SuggestedRecipientIds: [],
                Warning: "Não foi possível carregar os conteúdos do curso para identificar atividades.")
            {
                IsComplete = false,
                DeliveryCoverage = SubmissionDeliveryCoverage.Unknown,
            };
        }

        var now = DateTimeOffset.UtcNow;
        var assignmentsProcessed = 0;

        // 3. Build the assignment scope first. The gateway then sends the
        // assignment IDs in batches instead of making one request per activity.
        var allAssignModules = contents.Sections
            .SelectMany(section => section.Modules)
            .Where(module => string.Equals(module.ModuleType, "assign", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var invalidAssignmentIdentity = allAssignModules.Any(module => string.IsNullOrWhiteSpace(module.InstanceId));
        var assignModules = allAssignModules
            .Where(module => !string.IsNullOrWhiteSpace(module.InstanceId))
            .GroupBy(module => module.InstanceId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var assignmentContexts = assignModules
            .Select(module => new AssignmentContext(
                module,
                FindModuleDate(module, IsDueDateLabel),
                FindModuleDate(module, IsOpenDateLabel)))
            .Where(context =>
                (!request.ExcludeFutureActivities || ActivityEligibility.IsCurrentlyOpen(context.OpenDate, now)) &&
                (request.DueDaysAhead <= 0 ||
                 !context.DueDate.HasValue ||
                 (context.DueDate.Value - now).TotalDays <= request.DueDaysAhead))
            .ToArray();

        // Uma atividade sem nota ainda pode exigir feedback. O grau da
        // atividade só define qual sinal devemos usar para saber se ela foi
        // tratada; não é motivo para removê-la da fila do tutor.
        IReadOnlyDictionary<string, AssignmentSettingsSummary>? assignmentSettings = null;
        if (assignmentContexts.Length > 0)
        {
            try
            {
                assignmentSettings = await MoodleReadRetry.ExecuteAsync(
                    ct => assignmentSettingsGateway.GetCourseAssignmentSettingsFreshAsync(
                        currentUserExternalId, request.CourseId, ct),
                    onRetry: null,
                    cancellationToken);

                // Moodle may return a successful but incomplete assignment
                // settings response. Re-read once before publishing unknown
                // inventory coverage; only an explicit setting from the
                // second live response can resolve the uncertainty.
                if (HasUnknownSubmissionMode(assignmentContexts, assignmentSettings))
                {
                    try
                    {
                        assignmentSettings = await MoodleReadRetry.ExecuteAsync(
                            ct => assignmentSettingsGateway.GetCourseAssignmentSettingsFreshAsync(
                                currentUserExternalId, request.CourseId, ct),
                            onRetry: null,
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Keep the first fresh response. Any mode still absent
                        // remains unknown below; it is never inferred as false.
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Mantém o comportamento baseado em gradingstatus se a leitura
                // das configurações não estiver disponível.
            }
        }

        assignmentSettings ??= new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal);

        var submissionModeIncomplete = false;
        var knownNoSubmissionCount = 0;
        var knownSubmissionModeAssignmentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eligibleContexts = new List<AssignmentContext>(assignmentContexts.Length);
        foreach (var context in assignmentContexts)
        {
            var setting = FindAssignmentSettings(assignmentSettings, context.Module);
            if (setting?.NoSubmissions == true)
            {
                knownNoSubmissionCount++;
                continue;
            }

            if (setting?.NoSubmissions is false)
            {
                knownSubmissionModeAssignmentIds.Add(context.Module.InstanceId!);
            }
            else
            {
                submissionModeIncomplete = true;
                continue;
            }

            eligibleContexts.Add(context);
        }

        var eligibleAssignmentCount = submissionModeIncomplete || invalidAssignmentIdentity
            ? (int?)null
            : eligibleContexts.Count;
        var assignmentAnalysisTruncated = request.MaxAssignmentsToAnalyze > 0 &&
            eligibleContexts.Count > request.MaxAssignmentsToAnalyze;
        if (assignmentAnalysisTruncated)
            eligibleContexts = eligibleContexts.Take(request.MaxAssignmentsToAnalyze).ToList();
        assignmentContexts = eligibleContexts.ToArray();

        // Grade/feedback evidence is fetched for every assignment when
        // awaiting-grading is requested. A null grade is not enough to say an
        // assignment is awaiting correction: feedback and grade timestamps
        // may prove it has already been reviewed.
        var feedbackByAssignment = new Dictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>>(StringComparer.OrdinalIgnoreCase);
        var feedbackReadyAssignments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var feedbackReadFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var submissionReadFailed = false;

        IReadOnlyList<AssignmentSubmissionsBatch> submissionBatches = [];
        var submissionFailures = new List<string>();
        if (assignmentContexts.Length > 0)
        {
            try
            {
                submissionBatches = await submissionsGateway.GetAssignmentSubmissionsBatchAsync(
                    currentUserExternalId,
                    assignmentContexts.Select(context => context.Module.InstanceId!).ToArray(),
                    request.IncludeAwaitingGrading ? null : "notsubmitted",
                    since: null,
                    before: null,
                    cancellationToken);
            }
            catch
            {
                submissionBatches = [];
                submissionReadFailed = true;
            }
        }

        var expectedSubmissionAssignmentIds = assignmentContexts
            .Select(context => context.Module.InstanceId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var submissionBatchGroups = submissionBatches
            .Where(batch => !string.IsNullOrWhiteSpace(batch.AssignmentId))
            .GroupBy(batch => batch.AssignmentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var duplicateSubmissionAssignmentIds = submissionBatchGroups
            .Where(pair => pair.Value.Length != 1)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var assignmentId in expectedSubmissionAssignmentIds)
        {
            if (!submissionBatchGroups.ContainsKey(assignmentId))
            {
                submissionFailures.Add($"{assignmentId} (assignment_batch_not_returned)");
            }
            else if (duplicateSubmissionAssignmentIds.Contains(assignmentId))
            {
                submissionFailures.Add($"{assignmentId} (assignment_batch_ambiguous)");
            }
        }

        var assignmentIdsForEvidence = assignmentContexts
            .Select(context => context.Module.InstanceId!)
            .ToArray();
        if (request.IncludeAwaitingGrading && assignmentIdsForEvidence.Length > 0)
        {
            if (gradeReadGateway is not null)
            {
                var feedbackReads = await ReadFeedbackByAssignmentAsync(
                    currentUserExternalId,
                    assignmentIdsForEvidence,
                    studentMap.Keys,
                    feedbackReadFailures,
                    cancellationToken);
                foreach (var pair in feedbackReads)
                {
                    feedbackByAssignment[pair.Key] = pair.Value;
                    feedbackReadyAssignments.Add(pair.Key);
                }
            }
            else
            {
                foreach (var assignmentId in assignmentIdsForEvidence)
                {
                    feedbackReadFailures.Add(assignmentId);
                }
            }
        }

        var gradebookRead = request.IncludeAwaitingGrading
            ? IsReusableGradebook(request.PrefetchedGradebook, request.CourseId, studentMap.Keys)
                ? new GradebookReadResult(
                    request.PrefetchedGradebook!.Gradebooks,
                    request.PrefetchedGradebook.Coverage.IsComplete,
                    request.PrefetchedGradebook.Coverage.MissingStudentIds,
                    request.PrefetchedGradebook.Coverage.ErrorStudentIds)
                : await ReadGradebooksAsync(request.CourseId, studentMap.Keys, cancellationToken)
            : null;
        var gradebooks = gradebookRead?.Gradebooks;
        var gradebooksComplete = gradebookRead?.IsComplete == true;

        var contextsByAssignmentId = assignmentContexts
            .ToDictionary(context => context.Module.InstanceId!, StringComparer.Ordinal);
        var evaluations = new List<SubmissionEvaluationItem>();
        var observedSubmissionAssignmentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in submissionBatches)
        {
            if (duplicateSubmissionAssignmentIds.Contains(batch.AssignmentId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(batch.ErrorCode))
            {
                submissionFailures.Add(
                    $"{batch.AssignmentId} ({batch.ErrorCode})");
                continue;
            }

            if (!contextsByAssignmentId.TryGetValue(batch.AssignmentId, out var context))
            {
                continue;
            }

            assignmentsProcessed++;
            observedSubmissionAssignmentIds.Add(batch.AssignmentId);
            var module = context.Module;
            var dueDate = context.DueDate;
            var isOverdue = dueDate.HasValue && dueDate.Value < now;
            var pendingItem = new PendingSubmissionItem(
                AssignmentId: module.InstanceId!,
                AssignmentName: module.Name,
                DueDate: dueDate,
                IsOverdue: isOverdue);
            var returnedUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (batch.Submissions.Any(record => string.IsNullOrWhiteSpace(record.UserId)))
            {
                submissionFailures.Add($"{batch.AssignmentId} (submission_user_identity_missing)");
                observedSubmissionAssignmentIds.Remove(batch.AssignmentId);
                continue;
            }

            foreach (var record in batch.Submissions
                         .GroupBy(record => record.UserId, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group
                             .OrderByDescending(record => record.AttemptNumber ?? -1)
                             .ThenByDescending(record => record.ModifiedAt ?? record.CreatedAt)
                             .First()))
            {
                if (!string.IsNullOrWhiteSpace(record.UserId))
                {
                    returnedUserIds.Add(record.UserId);
                }

                AssignmentExistingGrade? existingGrade = null;
                feedbackByAssignment.TryGetValue(module.InstanceId!, out var grades);
                grades?.TryGetValue(record.UserId, out existingGrade);
                CourseGradebook? gradebook = null;
                gradebooks?.TryGetValue(record.UserId, out gradebook);
                var gradebookItem = FindAssignmentGradebookItem(gradebook, module.InstanceId, module.ModuleId);
                var hasSubmission = ToSubmissionPresence(record.Status);
                if (!knownSubmissionModeAssignmentIds.Contains(module.InstanceId!) && hasSubmission == false)
                {
                    hasSubmission = null;
                }
                var state = SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
                    HasSubmission: hasSubmission,
                    GradeRaw: gradebookItem?.GradeRaw ?? (existingGrade?.HasGrade == true ? existingGrade.Grade : null),
                    GradedDateGraded: gradebookItem?.GradedDateGraded,
                    Feedback: gradebookItem?.Feedback ?? existingGrade?.Feedback ?? record.CurrentFeedback,
                        ReviewEvidenceAvailable: !request.IncludeAwaitingGrading ||
                        gradebooksComplete || feedbackReadyAssignments.Contains(module.InstanceId!),
                    GradingStatus: record.GradingStatus,
                    GraderId: existingGrade?.GraderId ?? ParseGraderId(gradebookItem?.GraderId) ?? record.CurrentGraderId,
                    GradeTimeModified: existingGrade?.TimeModified ?? gradebookItem?.GradedDateGraded ?? record.CurrentGradeTimeModified,
                    SubmissionTimeModified: record.ModifiedAt?.ToUnixTimeSeconds()));
                if (studentMap.ContainsKey(record.UserId))
                {
                    evaluations.Add(new SubmissionEvaluationItem(
                        request.CourseId, module.InstanceId!, module.Name, record.UserId, state,
                        record.ModifiedAt ?? record.CreatedAt)
                    {
                        DeliveryState = hasSubmission switch
                        {
                            true => SubmissionDeliveryState.Submitted,
                            false => SubmissionDeliveryState.NotSubmitted,
                            _ => SubmissionDeliveryState.Unknown,
                        }
                    });
                }

                if (hasSubmission == false && state == SubmissionEvaluationState.NotSubmitted)
                {
                    if (pendingByStudent.TryGetValue(record.UserId, out var pendingList))
                        pendingList.Add(pendingItem);
                }
                else if (request.IncludeAwaitingGrading && state == SubmissionEvaluationState.AwaitingGrading)
                {
                    if (gradingByStudent.TryGetValue(record.UserId, out var gradingList))
                    {
                        gradingList.Add(new AwaitingGradingItem(
                            module.InstanceId!,
                            module.Name,
                            dueDate,
                            record.ModifiedAt ?? record.CreatedAt));
                    }
                }
            }

            // A successful submission batch makes an omitted active student a
            // known NOT_SUBMITTED state. Do not apply this inference to a
            // failed or incomplete assignment read.
            foreach (var studentId in studentMap.Keys)
            {
                if (returnedUserIds.Contains(studentId) ||
                    !pendingByStudent.TryGetValue(studentId, out var pendingList) ||
                    pendingList.Any(item => string.Equals(item.AssignmentId, module.InstanceId, StringComparison.OrdinalIgnoreCase)) ||
                    evaluations.Any(item => string.Equals(item.AssignmentId, module.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.StudentId, studentId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var deliveryState = knownSubmissionModeAssignmentIds.Contains(module.InstanceId!)
                    ? SubmissionDeliveryState.NotSubmitted
                    : SubmissionDeliveryState.Unknown;
                if (deliveryState == SubmissionDeliveryState.NotSubmitted)
                {
                    pendingList.Add(pendingItem);
                }
                evaluations.Add(new SubmissionEvaluationItem(
                    request.CourseId, module.InstanceId!, module.Name, studentId,
                    deliveryState == SubmissionDeliveryState.NotSubmitted
                        ? SubmissionEvaluationState.NotSubmitted
                        : SubmissionEvaluationState.Unknown,
                    null)
                {
                    DeliveryState = deliveryState
                });
            }
        }

        // 4. Build result
        var studentsWithPending = pendingByStudent
            .Where(kv => kv.Value.Count > 0)
            .Select(kv =>
            {
                var student = studentMap[kv.Key];
                return new StudentPendingSubmissionSummary(
                    StudentId: kv.Key,
                    FullName: student.FullName,
                    LastCourseAccessAt: student.LastCourseAccessAt,
                    PendingAssignments: kv.Value);
            })
            .OrderByDescending(s => s.PendingAssignments.Count)
            .ToList();

        string? warning = null;
        if (assignModules.Count == 0)
        {
            warning = "Nenhuma atividade do tipo 'assign' foi encontrada no curso. " +
                      "Verifique se há atividades avaliativas configuradas.";
        }
        else if (assignmentContexts.Length == 0 && knownNoSubmissionCount > 0 && !submissionModeIncomplete)
        {
            warning = "As atividades encontradas não aceitam submissões e foram excluídas dos indicadores de entrega.";
        }
        else if (assignmentContexts.Length == 0 && submissionModeIncomplete)
        {
            warning = "O modo de submissão de uma ou mais atividades não pôde ser confirmado; os indicadores foram marcados como incompletos.";
        }
        else if (assignmentContexts.Length == 0)
        {
            warning = "As atividades do tipo 'assign' encontradas ficaram fora do período solicitado para esta consulta.";
        }
        else if (assignmentsProcessed == 0)
        {
            warning = "Foram encontradas atividades do tipo 'assign', mas nenhuma foi processada. " +
                      "Verifique os erros de leitura retornados pelo Moodle.";
        }
        else if (assignmentAnalysisTruncated)
        {
            warning = $"A análise foi limitada às {request.MaxAssignmentsToAnalyze} primeiras atividades avaliativas para preservar o desempenho.";
        }

        if (submissionFailures.Count > 0)
        {
            var submissionWarning = "Não foi possível ler as submissões de " +
                $"{submissionFailures.Count} atividade(s): {string.Join(", ", submissionFailures)}. " +
                "As demais atividades foram processadas normalmente.";
            warning = string.IsNullOrWhiteSpace(warning)
                ? submissionWarning
                : $"{warning} {submissionWarning}";
        }

        if (submissionReadFailed)
        {
            const string submissionWarning = "Não foi possível ler as submissões das atividades avaliativas; a contagem deste curso está incompleta.";
            warning = string.IsNullOrWhiteSpace(warning)
                ? submissionWarning
                : $"{warning} {submissionWarning}";
        }

        if (submissionModeIncomplete)
        {
            const string modeWarning = "O modo de submissão não estava disponível para todas as atividades; elas não foram contadas como pendentes.";
            warning = string.IsNullOrWhiteSpace(warning) ? modeWarning : $"{warning} {modeWarning}";
        }

        var participantsComplete = !participantsPage.HasMore;
        var inventoryComplete = !submissionModeIncomplete && !invalidAssignmentIdentity;
        var submissionReadsComplete = !submissionReadFailed && submissionFailures.Count == 0 &&
            !assignmentAnalysisTruncated && assignmentsProcessed == assignmentContexts.Length;
        var deliveryStatusesComplete = evaluations.All(item => item.DeliveryState != SubmissionDeliveryState.Unknown);
        var deliveryReasonCode = !inventoryComplete
            ? invalidAssignmentIdentity ? "assignment_identity_unknown" : "activity_submission_mode_unknown"
            : !participantsComplete ? "participant_scope_incomplete"
            : assignmentAnalysisTruncated ? "report_activity_limit"
            : !submissionReadsComplete ? "submission_read_incomplete"
            : !deliveryStatusesComplete ? "submission_status_unknown"
            : null;
        var assignmentsWithUnknownDelivery = evaluations
            .Where(item => item.DeliveryState == SubmissionDeliveryState.Unknown)
            .Select(item => item.AssignmentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deliveryCoverage = new SubmissionDeliveryCoverage(
            ParticipantsComplete: participantsComplete,
            InventoryComplete: inventoryComplete,
            EligibleAssignmentCount: inventoryComplete ? eligibleAssignmentCount : null,
            ObservedAssignmentCount: observedSubmissionAssignmentIds.Count,
            SubmissionReadsComplete: submissionReadsComplete,
            IsComplete: inventoryComplete && participantsComplete && submissionReadsComplete && deliveryStatusesComplete,
            ReasonCode: deliveryReasonCode)
        {
            UnknownAssignmentsCount = inventoryComplete && participantsComplete
                ? Math.Min(
                    eligibleAssignmentCount!.Value,
                    Math.Max(0, eligibleAssignmentCount.Value - observedSubmissionAssignmentIds.Count) +
                    assignmentsWithUnknownDelivery.Count(observedSubmissionAssignmentIds.Contains))
                : null,
        };

        var gradebookNotReturned = gradebookRead?.NotReturnedStudentIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var gradebookErrors = gradebookRead?.ErrorStudentIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (gradebookNotReturned.Length > 0 || gradebookErrors.Length > 0)
        {
            const string gradebookWarning = "A cobertura do gradebook ficou incompleta; estudantes não retornados e erros de leitura foram mantidos separados.";
            warning = string.IsNullOrWhiteSpace(warning)
                ? gradebookWarning
                : $"{warning} {gradebookWarning}";
        }

        if (feedbackReadFailures.Count > 0)
        {
            var evaluationWarning = $"Não foi possível verificar notas ou feedback em {feedbackReadFailures.Count} atividade(s); as avaliações sem evidência conclusiva permanecem desconhecidas.";
            warning = string.IsNullOrWhiteSpace(warning)
                ? evaluationWarning
                : $"{warning} {evaluationWarning}";
        }

        var suggestedRecipients = studentsWithPending.Select(s => s.StudentId).ToList();
        var awaitingGrading = gradingByStudent
            .Where(kv => kv.Value.Count > 0 && studentMap.ContainsKey(kv.Key))
            .SelectMany(kv => kv.Value.Select(item =>
            {
                var student = studentMap[kv.Key];
                return new AwaitingGradingSubmission(
                    request.CourseId,
                    item.AssignmentId,
                    item.AssignmentName,
                    kv.Key,
                    student.FullName,
                    student.LastCourseAccessAt,
                    "submitted",
                    item.SubmittedAt,
                    "awaiting_grading");
            }))
            .ToArray();

        return new GetStudentsWithPendingSubmissionsResult(
            CourseId: request.CourseId,
            TotalStudentsAnalyzed: participantsPage.Participants.Count,
            DueDaysAhead: request.DueDaysAhead,
            Students: studentsWithPending,
            SuggestedRecipientIds: suggestedRecipients,
            Warning: warning)
        {
            AwaitingGrading = awaitingGrading,
            Evaluations = evaluations,
            GradebookNotReturnedStudentIds = gradebookNotReturned,
            GradebookErrorStudentIds = gradebookErrors,
            IsComplete = deliveryCoverage.IsComplete && !submissionReadFailed && submissionFailures.Count == 0 &&
                gradebookNotReturned.Length == 0 && gradebookErrors.Length == 0 &&
                feedbackReadFailures.Count == 0 &&
                !assignmentAnalysisTruncated && !submissionModeIncomplete,
            IncompleteReasonCode = deliveryReasonCode ??
                (feedbackReadFailures.Count > 0 || gradebookNotReturned.Length > 0 || gradebookErrors.Length > 0
                    ? "submission_evaluation_incomplete"
                    : null),
            DeliveryCoverage = deliveryCoverage,
        };
    }

    private static bool? ToSubmissionPresence(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "submitted" => true,
        "new" or "draft" or "reopened" or "notsubmitted" or "not_submitted" => false,
        _ => null
    };

    private async Task<GradebookReadResult?> ReadGradebooksAsync(
        string courseId,
        IEnumerable<string> studentIds,
        CancellationToken cancellationToken)
    {
        if (gradebookGateway is null)
        {
            return null;
        }

        try
        {
            var snapshot = await gradebookGateway.GetCourseGradebookAsync(
                courseId,
                studentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                groupId: null,
                cancellationToken);
            return new GradebookReadResult(
                snapshot.Gradebooks,
                snapshot.Coverage.IsComplete,
                snapshot.Coverage.MissingStudentIds,
                snapshot.Coverage.ErrorStudentIds);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            var ids = studentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new GradebookReadResult(
                new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase),
                false,
                ids,
                ids);
        }
    }

    private static GradebookItem? FindAssignmentGradebookItem(
        CourseGradebook? gradebook,
        string? assignmentInstanceId,
        string? assignmentModuleId) =>
        gradebook?.Items.FirstOrDefault(item =>
            string.Equals(item.ItemModule, "assign", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(item.ItemInstance, assignmentInstanceId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.CourseModuleId, assignmentModuleId, StringComparison.OrdinalIgnoreCase)));

    private static long? ParseGraderId(string? value) =>
        long.TryParse(value, out var graderId) ? graderId : null;

    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>>> ReadFeedbackByAssignmentAsync(
        string userExternalId,
        IReadOnlyCollection<string> assignmentIds,
        IEnumerable<string> studentIds,
        ISet<string> failures,
        CancellationToken cancellationToken)
    {
        var students = studentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            var batches = await gradeReadGateway!.GetExistingGradesBatchAsync(
                userExternalId,
                assignmentIds,
                students,
                cancellationToken);
            foreach (var batch in batches)
            {
                if (!string.IsNullOrWhiteSpace(batch.ErrorCode))
                {
                    failures.Add(batch.AssignmentId);
                }
            }
            return batches
                .Where(batch => string.IsNullOrWhiteSpace(batch.ErrorCode))
                .ToDictionary(batch => batch.AssignmentId, batch => batch.Grades, StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            foreach (var assignmentId in assignmentIds)
            {
                failures.Add(assignmentId);
            }
            return new Dictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsForCourse(CourseGradebookSnapshot? gradebook, string courseId) =>
        gradebook is not null &&
        string.Equals(gradebook.CourseId, courseId, StringComparison.OrdinalIgnoreCase);

    private static bool IsForCourse(CourseParticipantsPage? participants, string courseId) =>
        participants is not null &&
        string.Equals(participants.CourseId, courseId, StringComparison.OrdinalIgnoreCase);

    private static bool IsReusableParticipants(
        CourseParticipantsPage? participants,
        string courseId) =>
        IsForCourse(participants, courseId) &&
        participants!.StatusFilter == ParticipantStatusFilter.Active &&
        participants.StudentsOnly &&
        !participants.HasMore &&
        participants.Participants.Count <= participants.PageSize;

    private static bool IsReusableGradebook(
        CourseGradebookSnapshot? gradebook,
        string courseId,
        IEnumerable<string> studentIds)
    {
        var requestedIds = studentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return IsForCourse(gradebook, courseId) &&
            gradebook!.Coverage.IsComplete &&
            !gradebook.Coverage.Truncated &&
            gradebook.Coverage.MissingStudentIds.Count == 0 &&
            gradebook.Coverage.ErrorStudentIds.Count == 0 &&
            gradebook.Coverage.RequestedStudentCount == requestedIds.Length &&
            gradebook.Coverage.ReturnedStudentCount == requestedIds.Length &&
            requestedIds.All(studentId => gradebook.TryGetForStudent(studentId, out _));
    }

    private static bool IsNoGradeActivity(
        CourseModuleSummary module,
        IReadOnlyDictionary<string, AssignmentSettingsSummary> settings)
    {
        if (settings.TryGetValue(module.InstanceId ?? string.Empty, out var byInstance))
        {
            return !ResolveIsGradable(byInstance);
        }

        return settings.TryGetValue(module.ModuleId ?? string.Empty, out var byModule) &&
            !ResolveIsGradable(byModule);
    }

    private static AssignmentSettingsSummary? FindAssignmentSettings(
        IReadOnlyDictionary<string, AssignmentSettingsSummary> settings,
        CourseModuleSummary module)
    {
        if (module.InstanceId is not null && settings.TryGetValue(module.InstanceId, out var byInstance))
            return byInstance;
        if (module.ModuleId is not null && settings.TryGetValue(module.ModuleId, out var byModule))
            return byModule;
        return null;
    }

    private static bool HasUnknownSubmissionMode(
        IEnumerable<AssignmentContext> contexts,
        IReadOnlyDictionary<string, AssignmentSettingsSummary> settings) =>
        contexts.Any(context => FindAssignmentSettings(settings, context.Module)?.NoSubmissions is null);

    private static bool ResolveIsGradable(AssignmentSettingsSummary settings) =>
        settings.IsGradable ?? settings.MaxGrade > 0;

    private static DateTimeOffset? FindModuleDate(
        CourseModuleSummary module,
        Func<string, bool> labelMatcher) =>
        module.Dates
            .FirstOrDefault(date => labelMatcher(date.Label))
            ?.Date;

    private static bool IsDueDateLabel(string label) =>
        label.Contains("due", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("prazo", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("entrega", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("vencimento", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("cut-off", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("cutoff", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenDateLabel(string label) =>
        label.Contains("open", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("abre", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("aberto", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("disponivel de", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("available from", StringComparison.OrdinalIgnoreCase);

    private sealed record AssignmentContext(
        CourseModuleSummary Module,
        DateTimeOffset? DueDate,
        DateTimeOffset? OpenDate);

    private sealed record GradebookReadResult(
        IReadOnlyDictionary<string, CourseGradebook> Gradebooks,
        bool IsComplete,
        IReadOnlyCollection<string> NotReturnedStudentIds,
        IReadOnlyCollection<string> ErrorStudentIds);
}
