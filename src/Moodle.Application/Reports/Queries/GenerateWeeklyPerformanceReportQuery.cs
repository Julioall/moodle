using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;
using Moodle.Application.Submissions.Queries;
using Moodle.Domain;

namespace Moodle.Application.Reports.Queries;

// ── Result types ─────────────────────────────────────────────────────────────

/// <summary>
/// Linha de desempenho semanal de um estudante.
/// </summary>
public sealed record StudentWeeklyPerformanceRow(
    string StudentId,
    string FullName,
    DateTimeOffset? LastCourseAccessAt,
    int? DaysWithoutAccess,
    bool NeverAccessed,
    int? TotalAssignments,
    int SubmittedCount,
    int PendingCount,
    int BelowMinimumCount,
    IReadOnlyList<StudentGradeItem> BelowMinimumItems,
    IReadOnlyList<string> PendingAssignmentNames,
    string AttentionLevel)  // "ok" | "attention" | "risk" | "unknown"
{
    public string GradebookStatus { get; init; } = GradebookCoverageStates.NotRequested;
    public bool AccessCoverageAvailable { get; init; }
    public bool GradebookCoverageAvailable { get; init; }
    public bool ActivityCoverageAvailable { get; init; }
    public bool EvaluationCoverageAvailable { get; init; }
    public bool SituationCoverageAvailable { get; init; }
    public int? EligibleAssignmentCount { get; init; }
    public int? UnknownAssignmentsCount { get; init; }
    public int AwaitingGradingCount { get; init; }
    public IReadOnlyList<string> AwaitingGradingAssignmentNames { get; init; } = [];
    public int NotSubmittedCount { get; init; }
    public int ReviewedWithFeedbackCount { get; init; }
    public int GradedCount { get; init; }
    public StudentGradeItem? CourseTotal { get; init; }
}

public sealed record GenerateWeeklyPerformanceReportResult(
    string CourseId,
    DateTimeOffset GeneratedAt,
    int TotalStudents,
    int StudentsWithAttention,
    int StudentsAtRisk,
    decimal MinGradePercent,
    int InactiveDaysThreshold,
    IReadOnlyList<StudentWeeklyPerformanceRow> Students,
    IReadOnlyList<string> SuggestedRecipientIdsForAccess,
    IReadOnlyList<string> SuggestedRecipientIdsForGrade,
    IReadOnlyList<string> SuggestedRecipientIdsForPending,
    string? Warning)
{
    public string? ActivityCoverageReason { get; init; }
    public string? EvaluationCoverageReason { get; init; }
    public int StudentsWithUnknownSituation { get; init; }
    public SubmissionDeliveryCoverage DeliveryCoverage { get; init; } = SubmissionDeliveryCoverage.Unknown;
}

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Gera relatório semanal consolidado de desempenho da turma.
/// Cruza acesso, entregas pendentes e notas abaixo do mínimo por estudante.
///
/// Estratégia:
/// 1. Busca lista de estudantes ativos.
/// 2. Para cada estudante, coleta o boletim (notas por SA).
/// 3. Cruza com dados de acesso (LastCourseAccessAt) da listagem de participantes.
/// 4. Calcula nível de atenção: "ok", "attention" (1 indicador), "risk" (2+ indicadores).
///
/// Limitações declaradas:
/// - Uma SA só entra como pendente quando não há nota nem metadado de envio;
///   entregas aguardando correção são expostas separadamente.
/// - Dados de acesso dependem do campo lastcourseaccess da API Moodle.
/// - O gradebook é lido em modo coletivo quando a capability e os limites permitem;
///   instalações incompatíveis usam fallback individual limitado.
/// </summary>
public sealed record GenerateWeeklyPerformanceReportQuery(
    string CourseId,
    decimal MinGradePercent = 60m,
    int InactiveDaysThreshold = 7,
    int MaxStudentsToAnalyze = 60,
    CourseGradebookSnapshot? PrefetchedGradebook = null,
    CourseParticipantsPage? PrefetchedParticipants = null,
    int MaxAssignmentsToAnalyze = 0) : IRequest<GenerateWeeklyPerformanceReportResult>;

public sealed class GenerateWeeklyPerformanceReportQueryHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleGradebookGateway gradebookGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMediator? mediator = null)
    : IRequestHandler<GenerateWeeklyPerformanceReportQuery, GenerateWeeklyPerformanceReportResult>
{
    private const string LimitationMessage =
        "Relatório gerado com base nos dados disponíveis na API Moodle. " +
        "Notas sem lançamento aparecem como 'sem nota'; entregas identificadas como aguardando correção não entram como pendência. " +
        "Acesso ao AVA depende do campo lastcourseaccess disponível no Moodle. " +
        "Atividades cujo nome indica recuperação não são tratadas como pendência geral. " +
        "Para turmas grandes o relatório pode ser limitado pela capability bulk, orçamento de payload ou fallback individual.";

    public async Task<GenerateWeeklyPerformanceReportResult> Handle(
        GenerateWeeklyPerformanceReportQuery request,
        CancellationToken cancellationToken)
    {
        var currentUserExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();
        var now = DateTimeOffset.UtcNow;

        // 1. Fetch active students
        var requestedPageSize = request.MaxStudentsToAnalyze > 0 ? request.MaxStudentsToAnalyze : 60;
        var participantsPage = CourseSubmissionReportReadContext.CanReuseParticipants(
                request.PrefetchedParticipants,
                request.CourseId)
            ? request.PrefetchedParticipants! with
            {
                Participants = request.PrefetchedParticipants!.Participants.Take(requestedPageSize).ToArray(),
            }
            : await participantsGateway.GetCourseParticipantsAsync(
                userExternalId: currentUserExternalId,
                courseId: request.CourseId,
                statusFilter: ParticipantStatusFilter.Active,
                page: 0,
                pageSize: requestedPageSize,
                studentsOnly: true,
                includeEmail: false,
                groupId: null,
                cancellationToken: cancellationToken);

        if (participantsPage.Participants.Count == 0)
        {
            return new GenerateWeeklyPerformanceReportResult(
                CourseId: request.CourseId,
                GeneratedAt: now,
                TotalStudents: 0,
                StudentsWithAttention: 0,
                StudentsAtRisk: 0,
                MinGradePercent: request.MinGradePercent,
                InactiveDaysThreshold: request.InactiveDaysThreshold,
                Students: [],
                SuggestedRecipientIdsForAccess: [],
                SuggestedRecipientIdsForGrade: [],
                SuggestedRecipientIdsForPending: [],
                Warning: "Nenhum estudante ativo encontrado no curso.");
        }

        var rows = new List<StudentWeeklyPerformanceRow>();
        var recipientsForAccess = new List<string>();
        var recipientsForGrade = new List<string>();
        var recipientsForPending = new List<string>();
        var gradebookIncompleteCount = 0;
        CourseGradebookSnapshot? bulkGradebook =
            CourseSubmissionReportReadContext.CanReuseGradebook(
                request.PrefetchedGradebook,
                request.CourseId,
                participantsPage.Participants)
                ? request.PrefetchedGradebook
                : null;
        if (bulkGradebook is null)
        {
            try
            {
                bulkGradebook = await gradebookGateway.GetCourseGradebookAsync(
                    request.CourseId,
                    participantsPage.Participants.Select(student => student.UserId).ToArray(),
                    groupId: null,
                    cancellationToken);
            }
            catch
            {
                // Individual reads remain the compatibility fallback.
            }
        }
        var submissionState = await CourseSubmissionReportState.LoadAsync(
            mediator,
            request.CourseId,
            request.MaxStudentsToAnalyze > 0 ? request.MaxStudentsToAnalyze : 60,
            new CourseSubmissionReportReadContext(
                Participants: participantsPage,
                Gradebook: bulkGradebook),
            cancellationToken,
            request.MaxAssignmentsToAnalyze);

        // 2. Per-student analysis
        foreach (var student in participantsPage.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // -- Access analysis
            int? daysWithoutAccess = null;
            bool neverAccessed = false;
            bool isInactive = false;

            var accessCoverageAvailable = student.LastCourseAccessAt.HasValue ||
                student.LastCourseAccessEvidence == ParticipantAccessEvidence.Never;
            if (student.LastCourseAccessEvidence == ParticipantAccessEvidence.Never)
            {
                neverAccessed = true;
                isInactive = true;
            }
            else if (student.LastCourseAccessAt.HasValue)
            {
                daysWithoutAccess = Math.Max(0, (int)Math.Floor((now - student.LastCourseAccessAt.Value).TotalDays));
                isInactive = daysWithoutAccess >= request.InactiveDaysThreshold;
            }

            // -- Gradebook analysis (best effort)
            IReadOnlyList<StudentGradeItem> belowMinimumItems = [];
            StudentGradeItem? courseTotalSignal = null;
            int? totalAssignments = null;
            int pendingCount = 0;
            int submittedCount = 0;
            var pendingNames = new List<string>();
            var awaitingGradingNames = new List<string>();
            var gradebookStatus = GradebookCoverageStates.Error;

            try
            {
                CourseGradebook gradebook;
                if (bulkGradebook?.TryGetForStudent(student.UserId, out var bulkStudentGradebook) == true)
                {
                    gradebook = bulkStudentGradebook;
                    gradebookStatus = bulkGradebook.GetStudentCoverageState(student.UserId);
                }
                else if (bulkGradebook is null || bulkGradebook.Coverage.SourceMode == "bulk")
                {
                    gradebook = await gradebookGateway.GetStudentGradebookAsync(
                        request.CourseId, student.UserId, cancellationToken);
                    gradebookStatus = gradebook.Items.Count == 0
                        ? GradebookCoverageStates.Empty
                        : GradebookCoverageStates.Covered;
                }
                else
                {
                    gradebook = new CourseGradebook(request.CourseId, student.UserId, []);
                    gradebookStatus = bulkGradebook.GetStudentCoverageState(student.UserId);
                }

                if (gradebookStatus is GradebookCoverageStates.Error or GradebookCoverageStates.NotReturned)
                {
                    gradebookIncompleteCount++;
                }

                var activityItems = gradebook.Items
                    .Where(GradebookMappingHelper.IsEvaluativeReportActivityItem)
                    .ToList();

                var gradeItems = activityItems
                    .Select(i => GradebookMappingHelper.ToStudentGradeItem(i, request.MinGradePercent))
                    .ToList();

                belowMinimumItems = gradeItems.Where(i => i.BelowMinimum).ToList();

                var courseTotal = gradebook.Items.FirstOrDefault(GradebookMappingHelper.IsCourseTotalItem);
                if (courseTotal is not null)
                {
                    courseTotalSignal = GradebookMappingHelper.ToStudentGradeItem(courseTotal, request.MinGradePercent);
                }

            }
            catch
            {
                gradebookIncompleteCount++;
                // Gradebook unavailable for this student — continue with
                // partial data while preserving the state in the row.
            }

            if (submissionState.IsAvailable)
            {
                var awaiting = submissionState.AwaitingFor(student.UserId);
                totalAssignments = submissionState.DeliveryCoverage.EligibleAssignmentCount;
                pendingCount = submissionState.CountDeliveryFor(
                    student.UserId, SubmissionDeliveryState.NotSubmitted);
                submittedCount = submissionState.CountDeliveryFor(
                    student.UserId, SubmissionDeliveryState.Submitted);
                pendingNames = submissionState.PendingFor(student.UserId)
                    .Select(item => item.AssignmentName).ToList();
                awaitingGradingNames = awaiting.Select(item => item.AssignmentName).ToList();
            }
            else
            {
                totalAssignments = null;
                pendingCount = 0;
                submittedCount = 0;
                pendingNames = [];
                awaitingGradingNames = [];
            }

            var unknownAssignmentsCount = submissionState.UnknownDeliveryCountFor(student.UserId);

            // -- Attention level calculation
            var gradebookCoverageAvailable = gradebookStatus is GradebookCoverageStates.Covered or GradebookCoverageStates.Empty;
            var activityCoverageAvailable = submissionState.DeliveryCoverage.IsComplete;
            var evaluationCoverageAvailable = submissionState.IsAvailable && submissionState.IsComplete;
            var situationCoverageAvailable = accessCoverageAvailable && gradebookCoverageAvailable &&
                activityCoverageAvailable && evaluationCoverageAvailable;
            var riskFactors = (isInactive ? 1 : 0) + (belowMinimumItems.Count > 0 ? 1 : 0) + (pendingCount > 0 ? 1 : 0);
            var attentionLevel = riskFactors switch
            {
                0 => "ok",
                1 => "attention",
                _ => "risk"
            };
            if (!situationCoverageAvailable && attentionLevel == "ok")
            {
                attentionLevel = "unknown";
            }

            rows.Add(new StudentWeeklyPerformanceRow(
                StudentId: student.UserId,
                FullName: student.FullName,
                LastCourseAccessAt: student.LastCourseAccessAt,
                DaysWithoutAccess: daysWithoutAccess,
                NeverAccessed: neverAccessed,
                TotalAssignments: totalAssignments,
                SubmittedCount: submittedCount,
                PendingCount: pendingCount,
                BelowMinimumCount: belowMinimumItems.Count,
                BelowMinimumItems: belowMinimumItems,
                PendingAssignmentNames: pendingNames,
                AttentionLevel: attentionLevel)
            {
                AwaitingGradingCount = awaitingGradingNames.Count,
                AwaitingGradingAssignmentNames = awaitingGradingNames,
                NotSubmittedCount = submissionState.IsAvailable
                    ? submissionState.CountFor(student.UserId, SubmissionEvaluationState.NotSubmitted)
                    : pendingCount,
                ReviewedWithFeedbackCount = submissionState.IsAvailable
                    ? submissionState.CountFor(student.UserId, SubmissionEvaluationState.ReviewedWithFeedback)
                    : 0,
                GradedCount = submissionState.IsAvailable
                    ? submissionState.CountFor(student.UserId, SubmissionEvaluationState.GradedNumeric)
                    : 0,
                GradebookStatus = gradebookStatus,
                CourseTotal = courseTotalSignal,
                AccessCoverageAvailable = accessCoverageAvailable,
                GradebookCoverageAvailable = gradebookCoverageAvailable,
                ActivityCoverageAvailable = activityCoverageAvailable,
                EvaluationCoverageAvailable = evaluationCoverageAvailable,
                EligibleAssignmentCount = submissionState.DeliveryCoverage.EligibleAssignmentCount,
                UnknownAssignmentsCount = unknownAssignmentsCount,
                SituationCoverageAvailable = situationCoverageAvailable
            });

            if (isInactive) recipientsForAccess.Add(student.UserId);
            if (belowMinimumItems.Count > 0) recipientsForGrade.Add(student.UserId);
            if (pendingCount > 0) recipientsForPending.Add(student.UserId);
        }

        // Sort: risk first, then attention, then ok
        var sorted = rows
            .OrderBy(r => r.AttentionLevel switch { "risk" => 0, "attention" => 1, _ => 2 })
            .ThenByDescending(r => r.BelowMinimumCount + r.PendingCount)
            .ToList();

        int studentsAtRisk = sorted.Count(r => r.AttentionLevel == "risk");
        int studentsWithAttention = sorted.Count(r => r.AttentionLevel == "attention");
        int studentsWithUnknownSituation = sorted.Count(r => r.AttentionLevel == "unknown");

        var gradebookWarning = gradebookIncompleteCount > 0
            ? $"A cobertura do gradebook está incompleta para {gradebookIncompleteCount} estudante(s); estados de ausência e erro foram preservados."
            : null;
        var warning = submissionState.DeliveryCoverage.IsComplete && submissionState.IsComplete
            ? LimitationMessage
            : $"{LimitationMessage} {submissionState.Warning ?? "A cobertura de entrega ou avaliação está incompleta; contagens desconhecidas foram preservadas."}";
        if (gradebookWarning is not null)
        {
            warning = $"{warning} {gradebookWarning}";
        }

        return new GenerateWeeklyPerformanceReportResult(
            CourseId: request.CourseId,
            GeneratedAt: now,
            TotalStudents: sorted.Count,
            StudentsWithAttention: studentsWithAttention,
            StudentsAtRisk: studentsAtRisk,
            MinGradePercent: request.MinGradePercent,
            InactiveDaysThreshold: request.InactiveDaysThreshold,
            Students: sorted,
            SuggestedRecipientIdsForAccess: recipientsForAccess,
            SuggestedRecipientIdsForGrade: recipientsForGrade,
            SuggestedRecipientIdsForPending: recipientsForPending,
            Warning: warning)
        {
            ActivityCoverageReason = submissionState.DeliveryCoverage.ReasonCode ??
                (submissionState.DeliveryCoverage.IsComplete ? null : "activity_coverage_incomplete"),
            EvaluationCoverageReason = submissionState.IsComplete
                ? null
                : submissionState.IncompleteReasonCode ?? "submission_evaluation_incomplete",
            StudentsWithUnknownSituation = studentsWithUnknownSituation,
            DeliveryCoverage = submissionState.DeliveryCoverage
        };
    }
}
