using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Monitor.Queries;


public sealed record MonitorStudentRow(
    string StudentId,
    string FullName,
    bool NeverAccessed,
    DateTimeOffset? LastCourseAccessAt,
    int? DaysWithoutAccess);

public sealed record GenerateMonitorTurmaReportResult(
    string CourseId,
    DateTimeOffset GeneratedAt,
    int TotalEnrolled,
    int StudentsWhoAccessed,
    int StudentsNeverAccessed,
    int StudentsInactiveDays,
    int InactiveDaysThreshold,
    IReadOnlyList<MonitorStudentRow> NeverAccessedStudents,
    IReadOnlyList<MonitorStudentRow> InactiveStudents,
    string? Warning);


/// <summary>
/// Gera relatório administrativo da turma para uso do monitor SENAI CTM.
///
/// Diferente do relatório do tutor:
/// - NÃO consulta notas ou submissões (fora do papel do monitor).
/// - Foco em acesso ao AVA e matrícula.
/// - Linguagem administrativa, não pedagógica.
/// </summary>
public sealed record GenerateMonitorTurmaReportQuery(
    string CourseId,
    int InactiveDaysThreshold = 7,
    int MaxStudentsToAnalyze = 100) : IRequest<GenerateMonitorTurmaReportResult>;

public sealed class GenerateMonitorTurmaReportQueryHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway)
    : IRequestHandler<GenerateMonitorTurmaReportQuery, GenerateMonitorTurmaReportResult>
{
    public async Task<GenerateMonitorTurmaReportResult> Handle(
        GenerateMonitorTurmaReportQuery request,
        CancellationToken cancellationToken)
    {
        var currentUserExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();
        var now = DateTimeOffset.UtcNow;

        var participantsPage = await participantsGateway.GetCourseParticipantsAsync(
            userExternalId: currentUserExternalId,
            courseId: request.CourseId,
            statusFilter: ParticipantStatusFilter.Active,
            page: 0,
            pageSize: request.MaxStudentsToAnalyze > 0 ? request.MaxStudentsToAnalyze : 100,
            studentsOnly: true,
            includeEmail: false,
            groupId: null,
            cancellationToken: cancellationToken);

        var students = participantsPage.Participants;
        int total = students.Count;

        if (total == 0)
        {
            return new GenerateMonitorTurmaReportResult(
                CourseId: request.CourseId, GeneratedAt: now,
                TotalEnrolled: 0, StudentsWhoAccessed: 0,
                StudentsNeverAccessed: 0, StudentsInactiveDays: 0,
                InactiveDaysThreshold: request.InactiveDaysThreshold,
                NeverAccessedStudents: [], InactiveStudents: [],
                Warning: "Nenhum estudante ativo encontrado no curso. Verificar matrículas.");
        }

        var neverAccessed = new List<MonitorStudentRow>();
        var inactive = new List<MonitorStudentRow>();

        foreach (var student in students)
        {
            bool neverAccessedFlag = !student.LastCourseAccessAt.HasValue;
            int? daysWithoutAccess = student.LastCourseAccessAt.HasValue
                ? (int)(now - student.LastCourseAccessAt.Value).TotalDays
                : null;

            var row = new MonitorStudentRow(
                StudentId: student.UserId,
                FullName: student.FullName,
                NeverAccessed: neverAccessedFlag,
                LastCourseAccessAt: student.LastCourseAccessAt,
                DaysWithoutAccess: daysWithoutAccess);

            if (neverAccessedFlag)
                neverAccessed.Add(row);
            else if (daysWithoutAccess >= request.InactiveDaysThreshold)
                inactive.Add(row);
        }

        int accessed = total - neverAccessed.Count;

        var neverAccessedSorted = neverAccessed.OrderBy(r => r.FullName).ToList();
        var inactiveSorted = inactive.OrderByDescending(r => r.DaysWithoutAccess).ToList();

        string? warning = null;
        if (!students.Any(s => s.LastCourseAccessAt.HasValue))
        {
            warning = "O campo de último acesso ao curso pode não estar disponível para esta configuração de Moodle. Todos aparecem como 'nunca acessaram'.";
        }

        return new GenerateMonitorTurmaReportResult(
            CourseId: request.CourseId,
            GeneratedAt: now,
            TotalEnrolled: total,
            StudentsWhoAccessed: accessed,
            StudentsNeverAccessed: neverAccessed.Count,
            StudentsInactiveDays: inactive.Count,
            InactiveDaysThreshold: request.InactiveDaysThreshold,
            NeverAccessedStudents: neverAccessedSorted,
            InactiveStudents: inactiveSorted,
            Warning: warning);
    }
}
