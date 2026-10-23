using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;
using Moodle.Application.Submissions.Queries;
using Moodle.Domain;

namespace Moodle.Application.Reports.Queries;

// ── Result types ─────────────────────────────────────────────────────────────

public sealed record CourseOverviewResult(
    string CourseId,
    DateTimeOffset GeneratedAt,
    int TotalActiveStudents,
    int StudentsWhoAccessed,
    int StudentsNeverAccessed,
    int StudentsInactiveDays,
    int InactiveDaysThreshold,
    int? TotalGradedItems,
    decimal? AverageBelowMinimumPerStudent,
    IReadOnlyList<string> SuggestedActionsForTutor,
    string? Warning)
{
    public int StudentsAccessUnknown { get; init; }
    public bool AccessCoverageAvailable { get; init; }
    public bool ParticipantCoverageAvailable { get; init; }
}

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Gera resumo executivo do curso: participantes, acesso e indicadores gerais.
/// Não consulta gradebook individual — usa apenas dados de participants para ser rápido.
/// </summary>
public sealed record GenerateCourseOverviewQuery(
    string CourseId,
    int InactiveDaysThreshold = 7,
    int MaxStudentsToAnalyze = 100,
    CourseParticipantsPage? PrefetchedParticipants = null) : IRequest<CourseOverviewResult>;

public sealed class GenerateCourseOverviewQueryHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway)
    : IRequestHandler<GenerateCourseOverviewQuery, CourseOverviewResult>
{
    public async Task<CourseOverviewResult> Handle(
        GenerateCourseOverviewQuery request,
        CancellationToken cancellationToken)
    {
        var currentUserExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();
        var now = DateTimeOffset.UtcNow;

        var maxStudents = request.MaxStudentsToAnalyze > 0 ? request.MaxStudentsToAnalyze : 100;
        var participantPage = request.PrefetchedParticipants is { HasMore: false } cachedParticipants &&
                        string.Equals(cachedParticipants.CourseId, request.CourseId, StringComparison.OrdinalIgnoreCase)
            ? cachedParticipants
            : await participantsGateway.GetCourseParticipantsAsync(
                userExternalId: currentUserExternalId,
                courseId: request.CourseId,
                statusFilter: ParticipantStatusFilter.Active,
                page: 0,
                pageSize: maxStudents,
                studentsOnly: true,
                includeEmail: false,
                groupId: null,
                cancellationToken: cancellationToken);
        var students = participantPage.Participants.Take(maxStudents).ToArray();
        var participantCoverageAvailable = !participantPage.HasMore &&
            students.Length == participantPage.Participants.Count &&
            (participantPage.ClassificationDiagnostics?.UnclassifiedRoleCount ?? 0) == 0;
        int total = students.Length;

        if (total == 0)
        {
            return new CourseOverviewResult(
                CourseId: request.CourseId, GeneratedAt: now,
                TotalActiveStudents: 0, StudentsWhoAccessed: 0,
                StudentsNeverAccessed: 0, StudentsInactiveDays: 0,
                InactiveDaysThreshold: request.InactiveDaysThreshold,
                TotalGradedItems: null, AverageBelowMinimumPerStudent: null,
                SuggestedActionsForTutor: ["Nenhum estudante ativo encontrado. Verificar matrículas."],
                Warning: "Nenhum estudante ativo encontrado no curso. O resumo rápido não calcula métricas de nota.")
            {
                AccessCoverageAvailable = participantCoverageAvailable,
                ParticipantCoverageAvailable = participantCoverageAvailable,
            };
        }

        int neverAccessed = students.Count(s => s.LastCourseAccessEvidence == ParticipantAccessEvidence.Never);
        int accessed = students.Count(s => s.LastCourseAccessAt.HasValue);
        int unknownAccess = total - accessed - neverAccessed;
        int inactiveDays = students.Count(s =>
            s.LastCourseAccessAt.HasValue &&
            (int)(now - s.LastCourseAccessAt.Value).TotalDays >= request.InactiveDaysThreshold);

        var actions = new List<string>();
        double inactivePct = (neverAccessed + inactiveDays) * 100.0 / total;

        if (!participantCoverageAvailable)
            actions.Add("População de participantes incompleta; a situação geral não foi classificada como satisfatória.");
        else if (unknownAccess > 0)
            actions.Add($"Cobertura de acesso incompleta para {unknownAccess} estudante(s); a situação geral não foi classificada.");
        else if (inactivePct > 30)
            actions.Add($"{neverAccessed + inactiveDays} estudantes sem acesso recente ({inactivePct:F0}% da turma). Priorizar contato.");
        else if (inactivePct > 10)
            actions.Add($"{neverAccessed + inactiveDays} estudantes inativos. Enviar mensagem de acompanhamento.");
        else
            actions.Add("Acesso da turma satisfatório. Manter acompanhamento periódico.");

        if (neverAccessed > 0)
            actions.Add($"{neverAccessed} estudantes nunca acessaram o AVA. Verificar problemas de acesso ou desistência.");

        return new CourseOverviewResult(
            CourseId: request.CourseId,
            GeneratedAt: now,
            TotalActiveStudents: total,
            StudentsWhoAccessed: accessed,
            StudentsNeverAccessed: neverAccessed,
            StudentsInactiveDays: inactiveDays,
            InactiveDaysThreshold: request.InactiveDaysThreshold,
            // This fast overview intentionally does not call each student's
            // gradebook. Null means not calculated; returning zero made an
            // unavailable metric indistinguishable from a real zero.
            TotalGradedItems: null,
            AverageBelowMinimumPerStudent: null,
            SuggestedActionsForTutor: actions,
            Warning: unknownAccess > 0 || !participantCoverageAvailable
                ? $"A cobertura de acesso/população está incompleta (acesso desconhecido: {unknownAccess}); o resumo rápido não calcula métricas de nota. Use o relatório de notas do curso para esse escopo."
                : "O resumo rápido não calcula métricas de nota; use o relatório de notas do curso.")
        {
            StudentsAccessUnknown = unknownAccess,
            AccessCoverageAvailable = unknownAccess == 0 && participantCoverageAvailable,
            ParticipantCoverageAvailable = participantCoverageAvailable,
        };
    }
}

// ── Post-Execution result types ───────────────────────────────────────────────

public sealed record PostExecutionStudentRow(
    string StudentId,
    string FullName,
    bool NeverAccessed,
    int? DaysWithoutAccess,
    int TotalGradedItems,
    int BelowMinimumCount,
    int PendingCount,
    string OutcomeIndicator)  // "likely_complete" | "pending_recovery" | "at_risk" | "unknown"
{
    public string GradebookStatus { get; init; } = GradebookCoverageStates.NotRequested;
    public bool ActivityCoverageAvailable { get; init; }
    public int? EligibleAssignmentCount { get; init; }
    public int? UnknownAssignmentsCount { get; init; }
    public int AwaitingGradingCount { get; init; }
    public int NotSubmittedCount { get; init; }
    public int ReviewedWithFeedbackCount { get; init; }
    public int GradedCount { get; init; }
    public StudentGradeItem? CourseTotal { get; init; }
    public string? OutcomeReason { get; init; }
}

public sealed record GeneratePostExecutionReportResult(
    string CourseId,
    DateTimeOffset GeneratedAt,
    int TotalStudents,
    int LikelyComplete,
    int PendingRecovery,
    int AtRisk,
    int Unknown,
    decimal MinGradePercent,
    IReadOnlyList<PostExecutionStudentRow> Students,
    string Disclaimer,
    string? Warning)
{
    public SubmissionDeliveryCoverage DeliveryCoverage { get; init; } = SubmissionDeliveryCoverage.Unknown;
}

// ── Post-Execution Query ──────────────────────────────────────────────────────

/// <summary>
/// Gera relatório de pós-execução: situação provável de cada estudante ao fim do curso.
///
/// Classificações indicativas (não constituem decisão oficial):
/// - "likely_complete": acessa, notas acima do mínimo, sem pendências.
/// - "pending_recovery": notas abaixo do mínimo em pelo menos 1 SA.
/// - "at_risk": não acessa + pendências ou notas baixas.
/// - "unknown": dados insuficientes para classificar.
/// </summary>
public sealed record GeneratePostExecutionReportQuery(
    string CourseId,
    decimal MinGradePercent = 60m,
    int MaxStudentsToAnalyze = 60,
    CourseGradebookSnapshot? PrefetchedGradebook = null,
    CourseParticipantsPage? PrefetchedParticipants = null) : IRequest<GeneratePostExecutionReportResult>;

public sealed class GeneratePostExecutionReportQueryHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleGradebookGateway gradebookGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMediator? mediator = null)
    : IRequestHandler<GeneratePostExecutionReportQuery, GeneratePostExecutionReportResult>
{
    private const string Disclaimer =
        "Este relatório é indicativo e não constitui decisão oficial de conclusão, reprovação ou evasão. " +
        "Os dados são baseados na API Moodle e devem ser validados pelo tutor e pela coordenação. " +
        "Atividades cujo nome indica recuperação não são tratadas como pendência geral.";

    public async Task<GeneratePostExecutionReportResult> Handle(
        GeneratePostExecutionReportQuery request,
        CancellationToken cancellationToken)
    {
        var currentUserExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();
        var now = DateTimeOffset.UtcNow;

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
            return new GeneratePostExecutionReportResult(
                CourseId: request.CourseId, GeneratedAt: now,
                TotalStudents: 0, LikelyComplete: 0, PendingRecovery: 0, AtRisk: 0, Unknown: 0,
                MinGradePercent: request.MinGradePercent,
                Students: [], Disclaimer: Disclaimer,
                Warning: "Nenhum estudante ativo encontrado.");
        }

        var rows = new List<PostExecutionStudentRow>();
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
            cancellationToken);

        foreach (var student in participantsPage.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool neverAccessed = student.LastCourseAccessEvidence == ParticipantAccessEvidence.Never;
            int? daysWithoutAccess = student.LastCourseAccessAt.HasValue
                ? (int)(now - student.LastCourseAccessAt.Value).TotalDays
                : null;

            int totalGradedItems = 0, belowMinimumCount = 0, pendingCount = 0, awaitingGradingCount = 0;
            bool hasGradebookData = false;
            StudentGradeItem? courseTotalSignal = null;
            bool activitiesComplete = false;
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

                totalGradedItems = activityItems.Count;
                hasGradebookData = activityItems.Count > 0;
                activitiesComplete = activityItems.Count > 0 &&
                    activityItems.All(item => item.GradeRaw.HasValue);

                var gradeItems = activityItems
                    .Select(i => GradebookMappingHelper.ToStudentGradeItem(i, request.MinGradePercent))
                    .ToList();

                belowMinimumCount = gradeItems.Count(i => i.BelowMinimum);
                var courseTotal = gradebook.Items.FirstOrDefault(GradebookMappingHelper.IsCourseTotalItem);
                if (courseTotal is not null)
                {
                    courseTotalSignal = GradebookMappingHelper.ToStudentGradeItem(courseTotal, request.MinGradePercent);
                }
            }
            catch
            {
                gradebookIncompleteCount++;
                // Preserve the actual read failure through the row status.
            }

            if (submissionState.IsAvailable)
            {
                pendingCount = submissionState.CountDeliveryFor(
                    student.UserId, SubmissionDeliveryState.NotSubmitted);
                awaitingGradingCount = submissionState.AwaitingFor(student.UserId).Count;
            }

            var positiveCoverage = gradebookStatus == GradebookCoverageStates.Covered &&
                !participantsPage.HasMore &&
                (bulkGradebook is null || (bulkGradebook.Coverage.IsComplete && !bulkGradebook.Coverage.Truncated)) &&
                activitiesComplete && (submissionState.IsAvailable
                    ? submissionState.IsComplete && submissionState.DeliveryCoverage.IsComplete
                    : false);
            var outcome = DetermineOutcome(neverAccessed, belowMinimumCount, pendingCount,
                hasGradebookData, awaitingGradingCount, courseTotalSignal?.BelowMinimum == true, positiveCoverage);

            rows.Add(new PostExecutionStudentRow(
                StudentId: student.UserId,
                FullName: student.FullName,
                NeverAccessed: neverAccessed,
                DaysWithoutAccess: daysWithoutAccess,
                TotalGradedItems: totalGradedItems,
                BelowMinimumCount: belowMinimumCount,
                PendingCount: pendingCount,
                OutcomeIndicator: outcome)
            {
                AwaitingGradingCount = awaitingGradingCount,
                ActivityCoverageAvailable = submissionState.DeliveryCoverage.IsComplete,
                EligibleAssignmentCount = submissionState.DeliveryCoverage.EligibleAssignmentCount,
                UnknownAssignmentsCount = submissionState.UnknownDeliveryCountFor(student.UserId),
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
                OutcomeReason = outcome == "unknown"
                    ? courseTotalSignal?.BelowMinimum == true ? "aggregate_grade_risk"
                    : !hasGradebookData ? "no_activity_evidence"
                    : pendingCount > 0 ? "pending_submissions"
                    : awaitingGradingCount > 0 ? "awaiting_grading"
                    : "incomplete_activity_evidence"
                    : null
            });
        }

        var sorted = rows.OrderBy(r => r.OutcomeIndicator switch
        {
            "at_risk" => 0,
            "pending_recovery" => 1,
            "unknown" => 2,
            _ => 3
        }).ToList();

        var gradebookWarning = gradebookIncompleteCount > 0
            ? $"A cobertura do gradebook está incompleta para {gradebookIncompleteCount} estudante(s); estados de ausência e erro foram preservados."
            : null;
        var warning = submissionState.IsAvailable && !submissionState.IsComplete
            ? submissionState.Warning ?? "A cobertura de entregas está incompleta."
            : null;
        if (gradebookWarning is not null)
        {
            warning = string.IsNullOrWhiteSpace(warning)
                ? gradebookWarning
                : $"{warning} {gradebookWarning}";
        }

        return new GeneratePostExecutionReportResult(
            CourseId: request.CourseId,
            GeneratedAt: now,
            TotalStudents: sorted.Count,
            LikelyComplete: sorted.Count(r => r.OutcomeIndicator == "likely_complete"),
            PendingRecovery: sorted.Count(r => r.OutcomeIndicator == "pending_recovery"),
            AtRisk: sorted.Count(r => r.OutcomeIndicator == "at_risk"),
            Unknown: sorted.Count(r => r.OutcomeIndicator == "unknown"),
            MinGradePercent: request.MinGradePercent,
            Students: sorted,
            Disclaimer: Disclaimer,
            Warning: warning)
        {
            DeliveryCoverage = submissionState.DeliveryCoverage,
        };
    }

    private static string DetermineOutcome(bool neverAccessed, int belowMin, int pending, bool hasData,
        int awaitingGrading, bool aggregateRisk, bool positiveCoverage)
    {
        if (!hasData) return "unknown";
        if (neverAccessed && belowMin > 0) return "at_risk";
        if (belowMin > 0) return "pending_recovery";
        // A null grade alone does not establish that the student missed an
        // activity: it can be a submitted assignment awaiting teacher review.
        // Keep the row visible, but require a confirmed below-minimum grade
        // before recommending recovery.
        if (pending > 0 || awaitingGrading > 0 || aggregateRisk || !positiveCoverage) return "unknown";
        return "likely_complete";
    }
}
