using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Completion.Queries;

/// <summary>
/// Retorna estudantes que não acessaram o curso nos últimos N dias.
/// Usa o campo LastCourseAccessAt disponível na listagem de participantes.
/// </summary>
public sealed record StudentAccessSummary(
    string StudentId,
    string FullName,
    DateTimeOffset? LastCourseAccessAt,
    int? DaysWithoutAccess,
    bool NeverAccessed);

public sealed record GetStudentsWithoutRecentAccessResult(
    string CourseId,
    int DaysThreshold,
    int TotalStudentsAnalyzed,
    IReadOnlyList<StudentAccessSummary> Students,
    IReadOnlyList<string> SuggestedRecipientIds,
    string? Warning);

public sealed record GetStudentsWithoutRecentAccessQuery(
    string CourseId,
    int DaysWithoutAccess = 7,
    int MaxStudentsToAnalyze = 100,
    CourseParticipantsPage? PrefetchedParticipants = null) : IRequest<GetStudentsWithoutRecentAccessResult>;

public sealed class GetStudentsWithoutRecentAccessQueryHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway)
    : IRequestHandler<GetStudentsWithoutRecentAccessQuery, GetStudentsWithoutRecentAccessResult>
{
    public async Task<GetStudentsWithoutRecentAccessResult> Handle(
        GetStudentsWithoutRecentAccessQuery request,
        CancellationToken cancellationToken)
    {
        var currentUserExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();

        var maximum = request.MaxStudentsToAnalyze > 0 ? request.MaxStudentsToAnalyze : 100;
        var prefetched = request.PrefetchedParticipants;
        var participantsPage = prefetched is not null &&
                               string.Equals(prefetched.CourseId, request.CourseId, StringComparison.OrdinalIgnoreCase) &&
                               prefetched.StudentsOnly &&
                               prefetched.StatusFilter == ParticipantStatusFilter.Active
            ? prefetched with
            {
                PageSize = maximum,
                HasMore = prefetched.HasMore || prefetched.Participants.Count > maximum,
                Participants = prefetched.Participants.Take(maximum).ToArray()
            }
            : await participantsGateway.GetCourseParticipantsAsync(
                userExternalId: currentUserExternalId,
                courseId: request.CourseId,
                statusFilter: ParticipantStatusFilter.Active,
                page: 1,
                pageSize: maximum,
                studentsOnly: true,
                includeEmail: false,
                groupId: null,
                cancellationToken: cancellationToken);

        var threshold = request.DaysWithoutAccess > 0 ? request.DaysWithoutAccess : 7;
        var now = DateTimeOffset.UtcNow;

        var inactiveStudents = participantsPage.Participants
            .Select(student =>
            {
                if (!student.LastCourseAccessAt.HasValue)
                {
                    return new StudentAccessSummary(
                        StudentId: student.UserId,
                        FullName: student.FullName,
                        LastCourseAccessAt: null,
                        DaysWithoutAccess: null,
                        NeverAccessed: true);
                }

                var days = (int)(now - student.LastCourseAccessAt.Value).TotalDays;
                if (days >= threshold)
                {
                    return new StudentAccessSummary(
                        StudentId: student.UserId,
                        FullName: student.FullName,
                        LastCourseAccessAt: student.LastCourseAccessAt,
                        DaysWithoutAccess: days,
                        NeverAccessed: false);
                }

                return null;
            })
            .Where(s => s is not null)
            .Cast<StudentAccessSummary>()
            .OrderByDescending(s => s.NeverAccessed)
            .ThenByDescending(s => s.DaysWithoutAccess)
            .ToList();

        string? warning = participantsPage.HasMore
            ? "A lista de participantes foi limitada pelo maximo solicitado; a analise de acesso e parcial. Aumente MaxStudentsToAnalyze para cobrir o conjunto completo."
            : null;
        if (!participantsPage.Participants.Any(p => p.LastCourseAccessAt.HasValue))
        {
            warning = AppendWarning(warning, "O campo 'LastCourseAccessAt' pode não estar disponível para todos os estudantes. " +
                      "Verifique se o Moodle retorna dados de acesso por curso (campo lastcourseaccess).");
        }

        var suggestedRecipients = inactiveStudents.Select(s => s.StudentId).ToList();

        return new GetStudentsWithoutRecentAccessResult(
            CourseId: request.CourseId,
            DaysThreshold: threshold,
            TotalStudentsAnalyzed: participantsPage.Participants.Count,
            Students: inactiveStudents,
            SuggestedRecipientIds: suggestedRecipients,
            Warning: warning);
    }

    private static string AppendWarning(string? current, string additional) =>
        string.IsNullOrWhiteSpace(current) ? additional : $"{current} {additional}";
}
