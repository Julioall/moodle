using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Domain;

namespace Moodle.Infrastructure;

internal static class LocalStubMoodleData
{
    public const string CourseId = "101";
    public const string AssignmentId = "5001";

    public static CourseSummary Course => new(
        CourseId,
        "PORTAL-101",
        "PORTAL-101",
        "Portal v2 — Turma demonstrativa",
        "Portal v2 — Turma demonstrativa",
        10,
        "Demonstração local",
        DateTimeOffset.UtcNow.AddDays(-30),
        DateTimeOffset.UtcNow.AddDays(60),
        true,
        "https://moodle.local/course/view.php?id=101",
        null,
        42m,
        true,
        true,
        DateTimeOffset.UtcNow.AddDays(-2));

    public static IReadOnlyList<CourseParticipantSummary> Participants =>
    [
        Participant("2001", "Ana Souza", DateTimeOffset.UtcNow.AddDays(-90), DateTimeOffset.UtcNow.AddDays(-21)),
        Participant("2002", "Bruno Lima", DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(-2)),
        Participant("2003", "Carla Mendes", DateTimeOffset.UtcNow.AddDays(-45), DateTimeOffset.UtcNow.AddDays(-5)),
    ];

    public static IReadOnlyList<CourseGroupSummary> Groups =>
    [new("301", CourseId, "Turma demonstrativa", "PORTAL-101-A")];

    public static CourseContentsSummary Contents(
        IReadOnlyCollection<string> moduleTypes,
        bool includeHidden,
        bool onlyWithFiles)
    {
        var now = DateTimeOffset.UtcNow;
        var modules = new[]
        {
            new CourseModuleSummary(
                "cm-5001", AssignmentId, "assign", "Projeto integrador",
                "https://moodle.local/mod/assign/view.php?id=5001", true, true,
                "Entrega demonstrativa para validar pendências no Portal.", null,
                [new("Due date", now.AddDays(2))],
                [new("file", "roteiro.pdf", "/roteiro.pdf", 1024, "application/pdf", "https://moodle.local/pluginfile.php/1/roteiro.pdf", false)]),
            new CourseModuleSummary(
                "cm-5002", "5002", "quiz", "Avaliação de acompanhamento",
                "https://moodle.local/mod/quiz/view.php?id=5002", true, true,
                "Questionário de acompanhamento.", null,
                [new("Open date", now.AddDays(-3)), new("Close date", now.AddDays(10))],
                []),
            new CourseModuleSummary(
                "cm-5003", "5003", "forum", "Fórum de dúvidas",
                "https://moodle.local/mod/forum/view.php?id=5003", true, true,
                "Espaço para dúvidas da turma.", null,
                [],
                []),
        };

        var filteredModules = modules
            .Where(module => moduleTypes.Count == 0 || moduleTypes.Contains(module.ModuleType, StringComparer.OrdinalIgnoreCase))
            .Where(module => includeHidden || module.Visible != false && module.UserVisible != false)
            .Where(module => !onlyWithFiles || module.Files.Count > 0)
            .ToArray();

        return new CourseContentsSummary(
            CourseId,
            moduleTypes.ToArray(),
            includeHidden,
            onlyWithFiles,
            [new("section-1", 1, "Unidade 1 · Acompanhamento", "Conteúdos da turma demonstrativa.", true,
                filteredModules.Length, filteredModules.Length == 0, filteredModules)]);
    }

    public static IReadOnlyList<AssignmentSubmissionRecord> Submissions(string assignmentId, string? status)
    {
        if (!string.Equals(assignmentId, AssignmentId, StringComparison.Ordinal))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            new AssignmentSubmissionRecord(
                "stub-submission-1", "2001", "submitted", "notgraded", now.AddHours(-4), now.AddHours(-3), 1, 1, true,
                [new AssignmentSubmissionFile("projeto-ana.pdf", "application/pdf", 2048, "https://moodle.local/pluginfile.php/1/projeto-ana.pdf")]),
            new AssignmentSubmissionRecord("stub-submission-2", "2002", "notsubmitted", null, null, null, null, 0, false, []),
            new AssignmentSubmissionRecord("stub-submission-3", "2003", "notsubmitted", null, null, null, null, 0, false, []),
        };
        return string.IsNullOrWhiteSpace(status)
            ? rows
            : rows.Where(row => string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static CourseGradebook Gradebook(string courseId, string studentId)
    {
        var grade = studentId == "2001" ? 55m : studentId == "2002" ? 72m : 88m;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new CourseGradebook(courseId, studentId,
        [new GradebookItem(
            AssignmentId,
            "Projeto integrador",
            "mod",
            "assign",
            null,
            grade,
            $"{grade:0}",
            0m,
            100m,
            grade,
            studentId == "2001" ? "Retomar os critérios da atividade." : null,
            "html",
            now - 86400,
            now - 3600,
            "1000")]);
    }

    private static CourseParticipantSummary Participant(
        string id,
        string name,
        DateTimeOffset firstAccess,
        DateTimeOffset lastCourseAccess) => new(
        id,
        name,
        $"{id}@example.test",
        false,
        firstAccess,
        lastCourseAccess,
        lastCourseAccess,
        [new("5", "student", "Estudante")],
        [new("301", "Turma demonstrativa")]);
}

internal sealed class LocalStubMoodleCoursesGateway : IMoodleCoursesGateway
{
    public Task<IReadOnlyList<CourseHierarchyNode>> GetMyCourseHierarchyAsync(string userExternalId, CancellationToken cancellationToken)
    {
        var path = LocalStubMoodleData.Course.CategoryName ?? "Sem categoria";
        return Task.FromResult<IReadOnlyList<CourseHierarchyNode>>([new(path, path, 0, 1)]);
    }

    public Task<PagedCourses> GetMyCoursesByCategoryAsync(string userExternalId, string categoryPath, int limit, int page, CancellationToken cancellationToken)
    {
        var course = LocalStubMoodleData.Course;
        var matches = string.Equals(course.CategoryName ?? "Sem categoria", categoryPath.Trim(), StringComparison.OrdinalIgnoreCase);
        var items = matches && page == 1 ? new[] { course }.Take(Math.Max(limit, 1)).ToArray() : Array.Empty<CourseSummary>();
        return Task.FromResult(new PagedCourses(items, matches ? 1 : 0, page, Math.Max(limit, 1), CourseReadSource.Stub));
    }

    public Task<PagedCourses> GetMyCoursesAsync(string userExternalId, int limit, int page, CancellationToken cancellationToken)
    {
        var safePage = Math.Max(page, 1);
        var safeLimit = Math.Max(limit, 1);
        var items = LocalStubMoodleData.Course is { } course && safePage == 1
            ? new[] { course }.Take(safeLimit).ToArray()
            : Array.Empty<CourseSummary>();
        return Task.FromResult(new PagedCourses(items, 1, safePage, safeLimit, CourseReadSource.Stub));
    }

    public Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(string userExternalId, string query, int limit, CancellationToken cancellationToken)
    {
        var course = LocalStubMoodleData.Course;
        IReadOnlyList<CourseSummary> result = string.IsNullOrWhiteSpace(query) ||
            new[] { course.CourseId, course.FullName, course.ShortName }.Any(value => value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true)
            ? new[] { course }.Take(Math.Max(limit, 1)).ToArray()
            : [];
        return Task.FromResult(result);
    }

    public Task<CourseSummary?> GetMyCourseAsync(string userExternalId, string courseId, CancellationToken cancellationToken)
    {
        var course = LocalStubMoodleData.Course;
        var matches = course.CourseId == courseId.Trim() ||
            string.Equals(course.ShortName, courseId.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(course.IdNumber, courseId.Trim(), StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(matches ? course : null);
    }
}

internal sealed class LocalStubMoodleParticipantsGateway : IMoodleParticipantsGateway
{
    public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This legacy fixture does not support limited previews.");
    public Task<CourseParticipantsPage> GetCourseParticipantsAsync(
        string userExternalId,
        string courseId,
        ParticipantStatusFilter statusFilter,
        int page,
        int pageSize,
        bool studentsOnly,
        bool includeEmail,
        string? groupId,
        CancellationToken cancellationToken)
    {
        var safePage = Math.Max(page, 1);
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var participants = LocalStubMoodleData.Participants
            .Where(participant => statusFilter == ParticipantStatusFilter.All ||
                statusFilter == ParticipantStatusFilter.Active && participant.Suspended != true ||
                statusFilter == ParticipantStatusFilter.Suspended && participant.Suspended == true)
            .Where(participant => string.IsNullOrWhiteSpace(groupId) || participant.Groups.Any(group => group.GroupId == groupId))
            .ToArray();
        var skip = (safePage - 1) * safePageSize;
        var pageItems = participants.Skip(skip).Take(safePageSize + 1).ToArray();
        var hasMore = pageItems.Length > safePageSize;
        var data = pageItems.Take(safePageSize)
            .Select(participant => includeEmail ? participant : participant with { Email = null })
            .ToArray();
        return Task.FromResult(new CourseParticipantsPage(
            courseId, safePage, safePageSize, statusFilter, studentsOnly, includeEmail, hasMore, data,
            new ParticipantClassificationDiagnostics(data.Length, data.Length, 0, 0, false, false, ParticipantClassificationMode.RoleBased)));
    }

    public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(string userExternalId, string courseId, CancellationToken cancellationToken) =>
        Task.FromResult(LocalStubMoodleData.Groups);
}

internal sealed class LocalStubMoodleCourseContentsGateway : IMoodleCourseContentsGateway
{
    public Task<CourseContentsSummary> GetCourseContentsAsync(
        string userExternalId,
        string courseId,
        IReadOnlyCollection<string> moduleTypes,
        bool includeHidden,
        bool onlyWithFiles,
        CancellationToken cancellationToken) =>
        Task.FromResult(LocalStubMoodleData.Contents(moduleTypes, includeHidden, onlyWithFiles));
}

internal sealed class LocalStubMoodleAssignmentSubmissionsGateway : IMoodleAssignmentSubmissionsGateway
{
    public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
        string userExternalId,
        string assignmentId,
        string? status,
        DateTimeOffset? since,
        DateTimeOffset? before,
        CancellationToken cancellationToken) =>
        Task.FromResult(LocalStubMoodleData.Submissions(assignmentId, status));
}

internal sealed class LocalStubMoodleCurrentUserIdGateway : IMoodleCurrentUserIdGateway
{
    public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(1000L);
}

internal sealed class LocalStubMoodleAssignmentGradeReadGateway : IMoodleAssignmentGradeReadGateway
{
    public Task<AssignmentExistingGrade?> GetExistingGradeAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        CancellationToken cancellationToken) =>
        Task.FromResult<AssignmentExistingGrade?>(new AssignmentExistingGrade(
            assignmentId,
            studentId,
            Grade: null,
            HasGrade: false,
            Feedback: null,
            GradeMax: 100m));
}

internal sealed class LocalStubMoodleGradebookGateway : IMoodleGradebookGateway
{
    public Task<CourseGradebook> GetStudentGradebookAsync(string courseId, string studentId, CancellationToken cancellationToken) =>
        Task.FromResult(LocalStubMoodleData.Gradebook(courseId, studentId));
}
