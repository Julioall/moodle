using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Gradebook.Queries;

/// <summary>
/// Resultado agregado de estudantes com conceito abaixo do mínimo em alguma atividade.
/// Inclui público-alvo sugerido para mensagem de recuperação.
/// </summary>
public sealed record StudentBelowMinimumSummary(
    string StudentId,
    string FullName,
    DateTimeOffset? LastCourseAccessAt,
    IReadOnlyList<StudentGradeItem> BelowMinimumItems)
{
    public string GradebookStatus { get; init; } = GradebookCoverageStates.Covered;
}

public sealed record GetStudentsBelowMinGradeResult(
    string CourseId,
    decimal MinGradePercent,
    int TotalStudentsAnalyzed,
    IReadOnlyList<StudentBelowMinimumSummary> Students,
    IReadOnlyList<string> SuggestedRecipientIds,
    string? Warning)
{
    public IReadOnlyList<string> GradebookIncompleteStudentIds { get; init; } = [];
    public IReadOnlyList<StudentCourseTotalSignal> CourseTotals { get; init; } = [];
}

public sealed record StudentCourseTotalSignal(string StudentId, StudentGradeItem CourseTotal);

public sealed record GetStudentsBelowMinGradeQuery(
    string CourseId,
    decimal MinGradePercent = 60m,
    int MaxStudentsToAnalyze = 100,
    CourseGradebookSnapshot? PrefetchedGradebook = null,
    CourseParticipantsPage? PrefetchedParticipants = null) : IRequest<GetStudentsBelowMinGradeResult>;

public sealed class GetStudentsBelowMinGradeQueryHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleGradebookGateway gradebookGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway)
    : IRequestHandler<GetStudentsBelowMinGradeQuery, GetStudentsBelowMinGradeResult>
{
    public async Task<GetStudentsBelowMinGradeResult> Handle(
        GetStudentsBelowMinGradeQuery request,
        CancellationToken cancellationToken)
    {
        var currentUserExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();

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
                page: 1,
                pageSize: requestedPageSize,
                studentsOnly: true,
                includeEmail: false,
                groupId: null,
                cancellationToken: cancellationToken);

        var results = new List<StudentBelowMinimumSummary>();
        var courseTotals = new List<StudentCourseTotalSignal>();
        var warnings = new List<string>();
        var gradebookIncompleteStudentIds = new List<string>();
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
                // Individual reads remain the compatibility fallback.
            }
        }

        foreach (var student in participantsPage.Participants)
        {
            IReadOnlyList<StudentGradeItem> belowMinimumItems;
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

                var courseTotal = gradebook.Items.FirstOrDefault(GradebookMappingHelper.IsCourseTotalItem);
                if (courseTotal is not null)
                    courseTotals.Add(new(student.UserId,
                        GradebookMappingHelper.ToStudentGradeItem(courseTotal, request.MinGradePercent)));
                belowMinimumItems = gradebook.Items
                    .Where(GradebookMappingHelper.IsEvaluativeReportActivityItem)
                    .Select(i => GradebookMappingHelper.ToStudentGradeItem(i, request.MinGradePercent))
                    .Where(i => i.BelowMinimum)
                    .ToList();

                if (gradebookStatus is GradebookCoverageStates.Error or GradebookCoverageStates.NotReturned)
                {
                    gradebookIncompleteStudentIds.Add(student.UserId);
                }
            }
            catch
            {
                // Gradebook may be unavailable for this student, but keep the
                // distinction explicit in the result instead of treating it
                // as a student with no grade.
                gradebookIncompleteStudentIds.Add(student.UserId);
                continue;
            }

            if (belowMinimumItems.Count > 0)
            {
                results.Add(new StudentBelowMinimumSummary(
                    StudentId: student.UserId,
                    FullName: student.FullName,
                    LastCourseAccessAt: student.LastCourseAccessAt,
                    BelowMinimumItems: belowMinimumItems)
                {
                    GradebookStatus = gradebookStatus,
                });
            }
        }

        string? warning = null;
        if (participantsPage.Participants.Count == 0)
        {
            warning = "Nenhum estudante ativo encontrado no curso.";
        }
        else if (gradebookIncompleteStudentIds.Count > 0)
        {
            warning = $"A cobertura do gradebook está incompleta para {gradebookIncompleteStudentIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()} estudante(s); os resultados não foram tratados como 'sem nota'.";
        }

        var suggestedRecipients = results.Select(r => r.StudentId).ToList();

        return new GetStudentsBelowMinGradeResult(
            CourseId: request.CourseId,
            MinGradePercent: request.MinGradePercent,
            TotalStudentsAnalyzed: participantsPage.Participants.Count,
            Students: results,
            SuggestedRecipientIds: suggestedRecipients,
            Warning: warning)
        {
            GradebookIncompleteStudentIds = gradebookIncompleteStudentIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CourseTotals = courseTotals,
        };
    }
}
