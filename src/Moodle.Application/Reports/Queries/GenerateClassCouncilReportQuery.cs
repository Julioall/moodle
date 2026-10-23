using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;
using Moodle.Application.Submissions.Queries;
using Moodle.Domain;

namespace Moodle.Application.Reports.Queries;

// ── Result types ─────────────────────────────────────────────────────────────

/// <summary>
/// Situação de um estudante para o conselho de classe.
/// </summary>
public sealed record StudentClassCouncilRow(
    string StudentId,
    string FullName,
    DateTimeOffset? LastCourseAccessAt,
    int? DaysWithoutAccess,
    bool NeverAccessed,
    int TotalGradedItems,
    int BelowMinimumCount,
    int PendingItemsCount,
    IReadOnlyList<StudentGradeItem> BelowMinimumItems,
    string SituationFlag,   // "regular" | "attention" | "recovery_needed" | "at_risk" | "unknown"
    IReadOnlyList<string> Recommendations)
{
    public string GradebookStatus { get; init; } = GradebookCoverageStates.NotRequested;
    public bool ActivityCoverageAvailable { get; init; }
    public bool AccessCoverageAvailable { get; init; }
    public int? EligibleAssignmentCount { get; init; }
    public int? UnknownAssignmentsCount { get; init; }
    public int AwaitingGradingItemsCount { get; init; }
    public int NotSubmittedItemsCount { get; init; }
    public int ReviewedWithFeedbackItemsCount { get; init; }
    public int GradedItemsCount { get; init; }
    public StudentGradeItem? CourseTotal { get; init; }
}

public sealed record GenerateClassCouncilReportResult(
    string CourseId,
    DateTimeOffset GeneratedAt,
    int TotalStudents,
    int Regular,
    int NeedAttention,
    int NeedRecovery,
    int AtRisk,
    decimal MinGradePercent,
    IReadOnlyList<StudentClassCouncilRow> Students,
    string Disclaimer,
    string? Warning)
{
    public SubmissionDeliveryCoverage DeliveryCoverage { get; init; } = SubmissionDeliveryCoverage.Unknown;
    public int StudentsWithUnknownSituation { get; init; }
}

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Gera relatório para conselho de classe com situação pedagógica de cada estudante.
///
/// Classificações:
/// - "regular": cobertura suficiente sem indicadores negativos.
/// - "attention": 1 indicador negativo (acesso ou nota ou pendência).
/// - "recovery_needed": tem pelo menos 1 SA abaixo do mínimo (critério de recuperação paralela).
/// - "at_risk": 2 ou mais indicadores negativos simultaneamente.
///
/// IMPORTANTE: O conector não decide aprovação, reprovação ou evasão.
/// A situação é indicativa e deve ser interpretada pelo tutor e docente presencial.
/// </summary>
public sealed record GenerateClassCouncilReportQuery(
    string CourseId,
    decimal MinGradePercent = 60m,
    int InactiveDaysThreshold = 7,
    int MaxStudentsToAnalyze = 60,
    CourseGradebookSnapshot? PrefetchedGradebook = null,
    CourseParticipantsPage? PrefetchedParticipants = null) : IRequest<GenerateClassCouncilReportResult>;

public sealed class GenerateClassCouncilReportQueryHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleGradebookGateway gradebookGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMediator? mediator = null)
    : IRequestHandler<GenerateClassCouncilReportQuery, GenerateClassCouncilReportResult>
{
    private const string Disclaimer =
        "Este relatório é indicativo e não constitui decisão oficial de aprovação, reprovação ou evasão. " +
        "Os dados devem ser interpretados pelo tutor e pelo docente presencial. " +
        "Situações de recuperação paralela devem ser tratadas conforme as normas pedagógicas vigentes. " +
        "Atividades cujo nome indica recuperação não são tratadas como pendência geral.";

    public async Task<GenerateClassCouncilReportResult> Handle(
        GenerateClassCouncilReportQuery request,
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
            return new GenerateClassCouncilReportResult(
                CourseId: request.CourseId, GeneratedAt: now,
                TotalStudents: 0, Regular: 0, NeedAttention: 0, NeedRecovery: 0, AtRisk: 0,
                MinGradePercent: request.MinGradePercent,
                Students: [], Disclaimer: Disclaimer,
                Warning: "Nenhum estudante ativo encontrado no curso.");
        }

        var rows = new List<StudentClassCouncilRow>();
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

            // Access
            int? daysWithoutAccess = null;
            bool neverAccessed = student.LastCourseAccessEvidence == ParticipantAccessEvidence.Never;
            if (student.LastCourseAccessAt.HasValue)
                daysWithoutAccess = (int)(now - student.LastCourseAccessAt.Value).TotalDays;
            bool isInactive = neverAccessed || (daysWithoutAccess >= request.InactiveDaysThreshold);

            // Gradebook
            IReadOnlyList<StudentGradeItem> belowMinimumItems = [];
            StudentGradeItem? courseTotalSignal = null;
            int totalGradedItems = 0;
            int pendingItemsCount = 0;
            int awaitingGradingItemsCount = 0;
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
                // Preserve the per-student failure through GradebookStatus
                // instead of converting it into a regular empty gradebook.
            }

            if (submissionState.IsAvailable)
            {
                pendingItemsCount = submissionState.CountDeliveryFor(
                    student.UserId, SubmissionDeliveryState.NotSubmitted);
                awaitingGradingItemsCount = submissionState.AwaitingFor(student.UserId).Count;
            }

            var activityCoverageAvailable = submissionState.DeliveryCoverage.IsComplete;
            var gradebookCoverageAvailable = gradebookStatus is GradebookCoverageStates.Covered or GradebookCoverageStates.Empty;
            var accessCoverageAvailable = student.LastCourseAccessAt.HasValue ||
                student.LastCourseAccessEvidence == ParticipantAccessEvidence.Never;
            var situationCoverageAvailable = activityCoverageAvailable && gradebookCoverageAvailable && accessCoverageAvailable;

            // Classification
            bool hasLowGrade = belowMinimumItems.Count > 0;
            int negativeFactors = (isInactive ? 1 : 0) + (hasLowGrade ? 1 : 0) + (pendingItemsCount > 0 ? 1 : 0);

            var situationFlag = negativeFactors switch
            {
                0 => "regular",
                1 when hasLowGrade => "recovery_needed",
                1 => "attention",
                _ => "at_risk"
            };
            if (!situationCoverageAvailable && situationFlag == "regular")
            {
                situationFlag = "unknown";
            }

            // Recommendations
            var recommendations = BuildRecommendations(
                isInactive, hasLowGrade, pendingItemsCount, request.InactiveDaysThreshold,
                situationFlag == "unknown");

            rows.Add(new StudentClassCouncilRow(
                StudentId: student.UserId,
                FullName: student.FullName,
                LastCourseAccessAt: student.LastCourseAccessAt,
                DaysWithoutAccess: daysWithoutAccess,
                NeverAccessed: neverAccessed,
                TotalGradedItems: totalGradedItems,
                BelowMinimumCount: belowMinimumItems.Count,
                PendingItemsCount: pendingItemsCount,
                BelowMinimumItems: belowMinimumItems,
                SituationFlag: situationFlag,
                Recommendations: recommendations)
            {
                AwaitingGradingItemsCount = awaitingGradingItemsCount,
                ActivityCoverageAvailable = activityCoverageAvailable,
                EligibleAssignmentCount = submissionState.DeliveryCoverage.EligibleAssignmentCount,
                UnknownAssignmentsCount = submissionState.UnknownDeliveryCountFor(student.UserId),
                NotSubmittedItemsCount = submissionState.IsAvailable
                    ? submissionState.CountFor(student.UserId, SubmissionEvaluationState.NotSubmitted)
                    : pendingItemsCount,
                ReviewedWithFeedbackItemsCount = submissionState.IsAvailable
                    ? submissionState.CountFor(student.UserId, SubmissionEvaluationState.ReviewedWithFeedback)
                    : 0,
                GradedItemsCount = submissionState.IsAvailable
                    ? submissionState.CountFor(student.UserId, SubmissionEvaluationState.GradedNumeric)
                    : 0,
                GradebookStatus = gradebookStatus,
                CourseTotal = courseTotalSignal,
                AccessCoverageAvailable = accessCoverageAvailable,
            });
        }

        var sorted = rows
            .OrderBy(r => r.SituationFlag switch
            {
                "at_risk" => 0,
                "recovery_needed" => 1,
                "attention" => 2,
                _ => 3
            })
            .ToList();

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

        return new GenerateClassCouncilReportResult(
            CourseId: request.CourseId,
            GeneratedAt: now,
            TotalStudents: sorted.Count,
            Regular: sorted.Count(r => r.SituationFlag == "regular"),
            NeedAttention: sorted.Count(r => r.SituationFlag == "attention"),
            NeedRecovery: sorted.Count(r => r.SituationFlag == "recovery_needed"),
            AtRisk: sorted.Count(r => r.SituationFlag == "at_risk"),
            MinGradePercent: request.MinGradePercent,
            Students: sorted,
            Disclaimer: Disclaimer,
            Warning: warning)
        {
            DeliveryCoverage = submissionState.DeliveryCoverage,
            StudentsWithUnknownSituation = sorted.Count(row => row.SituationFlag == "unknown"),
        };
    }

    private static IReadOnlyList<string> BuildRecommendations(
        bool isInactive, bool hasLowGrade, int pendingCount, int inactiveDays, bool situationUnknown)
    {
        var recs = new List<string>();
        if (isInactive)
            recs.Add($"Estudante sem acesso há mais de {inactiveDays} dias. Verificar contato e engajamento.");
        if (hasLowGrade)
            recs.Add("Possui SA(s) com conceito abaixo do mínimo. Verificar elegibilidade para recuperação paralela.");
        if (pendingCount > 0)
            recs.Add($"{pendingCount} SA(s) sem nota registrada. Verificar se foram entregues ou ainda estão em aberto.");
        if (recs.Count == 0 && situationUnknown)
            recs.Add("Cobertura incompleta; a situação geral não foi classificada.");
        else if (recs.Count == 0)
            recs.Add("Situação regular. Manter acompanhamento periódico.");
        return recs;
    }
}
