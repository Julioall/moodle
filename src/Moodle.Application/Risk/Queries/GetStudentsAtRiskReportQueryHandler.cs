using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;
using Moodle.Domain;

namespace Moodle.Application.Risk.Queries;

public sealed class GetStudentsAtRiskReportQueryHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleGradebookGateway gradebookGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway)
    : IRequestHandler<GetStudentsAtRiskReportQuery, StudentsAtRiskReportResult>
{
    public async Task<StudentsAtRiskReportResult> Handle(GetStudentsAtRiskReportQuery request, CancellationToken cancellationToken)
    {
        var currentUserExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();

        // Fetch students (max 100 to avoid API rate limits for now)
        var requestedPageSize = request.MaxStudentsToAnalyze > 0 ? request.MaxStudentsToAnalyze : 100;
        var participantsPage = request.PrefetchedParticipants is { HasMore: false } cachedParticipants &&
            string.Equals(cachedParticipants.CourseId, request.CourseId, StringComparison.OrdinalIgnoreCase)
            ? cachedParticipants with
            {
                Participants = cachedParticipants.Participants.Take(requestedPageSize).ToArray(),
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

        var reports = new List<StudentRiskReport>();
        var gradebookFailureCount = 0;
        CourseGradebookSnapshot? bulkGradebook =
            string.Equals(request.PrefetchedGradebook?.CourseId, request.CourseId, StringComparison.OrdinalIgnoreCase)
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
                // Preserve the individual-read compatibility path below.
            }
        }

        foreach (var student in participantsPage.Participants)
        {
            var riskLevel = RiskLevel.Baixo;
            var factors = new List<string>();

            // 1. Inactivity Risk
            var inactiveDays = student.LastCourseAccessAt.HasValue
                ? (DateTimeOffset.UtcNow - student.LastCourseAccessAt.Value).TotalDays
                : (double?)null;

            if (student.LastCourseAccessEvidence == ParticipantAccessEvidence.Never)
            {
                factors.Add("Estudante nunca acessou o curso.");
                riskLevel = RiskLevel.Alto;
            }
            else if (inactiveDays.HasValue && inactiveDays.Value > request.InactivityThresholdDays)
            {
                factors.Add($"Estudante inativo por {(int)inactiveDays.Value} dias (limite: {request.InactivityThresholdDays}).");
                riskLevel = riskLevel < RiskLevel.Alto ? RiskLevel.Medio : riskLevel;
            }

            // Fetch Gradebook
            decimal? currentGrade = null;
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
                    gradebook = await gradebookGateway.GetStudentGradebookAsync(request.CourseId, student.UserId, cancellationToken);
                    gradebookStatus = gradebook.Items.Count == 0
                        ? GradebookCoverageStates.Empty
                        : GradebookCoverageStates.Covered;
                }
                else
                {
                    gradebook = new CourseGradebook(request.CourseId, student.UserId, []);
                    gradebookFailureCount++;
                    gradebookStatus = bulkGradebook.GetStudentCoverageState(student.UserId);
                }
                var courseGradeItem = gradebook.Items.FirstOrDefault(i => i.ItemType == "course");

                if (courseGradeItem != null)
                {
                    currentGrade = GradebookMappingHelper.ResolvePercentage(courseGradeItem);
                    if (currentGrade.HasValue && currentGrade.Value < request.MinGradePercentage)
                    {
                        factors.Add($"Nota atual ({currentGrade.Value:F1}%) abaixo da media minima esperada ({request.MinGradePercentage:F1}%).");
                        riskLevel = riskLevel < RiskLevel.Alto ? RiskLevel.Medio : riskLevel;
                        if (currentGrade.Value < request.MinGradePercentage - 20) // Much lower
                        {
                            riskLevel = RiskLevel.Alto;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Gradebook might be disabled or unreachable for this student
                gradebookFailureCount++;
                gradebookStatus = GradebookCoverageStates.Error;
            }

            if (gradebookStatus is GradebookCoverageStates.Error or GradebookCoverageStates.NotReturned)
            {
                factors.Add("Dados do gradebook indisponíveis para este estudante; a classificação de nota é incompleta.");
            }

            // Calculate overall risk
            if (factors.Count >= 3)
            {
                riskLevel = RiskLevel.Alto; // Aggravated risk due to multiple factors
            }

            if (riskLevel > RiskLevel.Baixo || factors.Count > 0)
            {
                reports.Add(new StudentRiskReport(
                    StudentId: student.UserId,
                    FullName: student.FullName,
                    RiskLevel: riskLevel,
                    Factors: factors,
                    LastCourseAccessAt: student.LastCourseAccessAt,
                    CurrentGrade: currentGrade,
                    // Detailed Moodle completion is not part of this report's
                    // evidence set because its dedicated endpoint is not
                    // reliable across the supported connections.
                     CompletionRate: null)
                {
                    GradebookStatus = gradebookStatus,
                });
            }
        }

        // Return ordered by RiskLevel descending, then by inactivity
        var orderedReports = reports
            .OrderByDescending(r => r.RiskLevel)
            .ThenBy(r => r.LastCourseAccessAt ?? DateTimeOffset.MinValue)
            .ToList();

        var accessUnknownCount = participantsPage.Participants.Count(student =>
            !student.LastCourseAccessAt.HasValue &&
            student.LastCourseAccessEvidence != ParticipantAccessEvidence.Never);
        return new StudentsAtRiskReportResult(
            orderedReports,
            participantsPage.Participants.Count,
            participantsPage.ClassificationDiagnostics ?? ParticipantClassificationDiagnostics.Empty,
            gradebookFailureCount)
        {
            AccessUnknownCount = accessUnknownCount,
            AccessCoverageComplete = accessUnknownCount == 0 && !participantsPage.HasMore &&
                (participantsPage.ClassificationDiagnostics?.UnclassifiedRoleCount ?? 0) == 0,
            ParticipantCoverageComplete = !participantsPage.HasMore &&
                (participantsPage.ClassificationDiagnostics?.UnclassifiedRoleCount ?? 0) == 0,
        };
    }
}
