using Moodle.Domain;
using Moodle.Application.Risk.Queries;

namespace Moodle.Presentation;

public sealed record AppPendingDto(
    string ConnectionRef,
    string CourseId,
    string StudentId,
    string? ActivityId,
    string StudentName,
    string ActivityName,
    string Type,
    string Level,
    IReadOnlyList<string> Factors,
    DateTimeOffset? DueAt,
    decimal? Grade,
    DateTimeOffset? LastAccessAt,
    string? MoodleUrl,
    bool CanGrade = false,
    bool CanWrite = false)
{
    public AppPendingDto(
        string connectionRef, string courseId, string studentId, string? activityId, string studentName,
        string activityName, string type, string level, IReadOnlyList<string> factors,
        DateTimeOffset? dueAt, decimal? grade, DateTimeOffset? lastAccessAt,
        string? ignored, string? moodleUrl)
        : this(connectionRef, courseId, studentId, activityId, studentName, activityName, type, level, factors,
            dueAt, grade, lastAccessAt, moodleUrl, false, false) { }

}

public sealed record AppPendingSourceRow(
    string StudentId,
    string StudentName,
    DateTimeOffset? LastAccessAt,
    string? ActivityId,
    string ActivityName,
    string Type,
    DateTimeOffset? DueAt,
    bool IsOverdue,
    bool NeedsGrading,
    decimal? Grade = null,
    string? MoodleUrl = null);

public sealed record AppPendingAccessRow(
    string StudentId,
    string StudentName,
    DateTimeOffset? LastAccessAt);

public static class AppPendingContractMapper
{
    public static IReadOnlyList<AppPendingDto> Build(
        string connectionRef,
        string courseId,
        IEnumerable<AppPendingSourceRow> submissionRows,
        IEnumerable<AppPendingAccessRow> accessRows,
        DateTimeOffset generatedAt)
    {
        var result = new List<AppPendingDto>();
        foreach (var row in submissionRows)
        {
            var factors = new List<string>();
            if (row.Type == "pending_submission") factors.Add("Atividade não entregue.");
            if (row.IsOverdue) factors.Add("Prazo da atividade já expirou.");
            if (row.NeedsGrading) factors.Add("Entrega aguardando correção no Moodle.");
            if (row.DueAt is not null && row.DueAt >= generatedAt && row.DueAt <= generatedAt.AddDays(7))
                factors.Add($"Prazo próximo: {row.DueAt:O}.");

            result.Add(new AppPendingDto(
                connectionRef, courseId, row.StudentId, row.ActivityId, row.StudentName,
                row.ActivityName, NormalizeType(row.Type, row.NeedsGrading), MapLevel(row.Type, row.IsOverdue, row.NeedsGrading),
                factors.Distinct(StringComparer.Ordinal).ToArray(), row.DueAt, row.Grade,
                row.LastAccessAt, row.MoodleUrl));
        }

        foreach (var row in accessRows)
        {
            var factors = row.LastAccessAt is null
                ? new[] { "Estudante nunca acessou o curso." }
                : new[] { $"Sem acesso ao curso há {(int)Math.Max(0, (generatedAt - row.LastAccessAt.Value).TotalDays)} dias." };
            result.Add(new AppPendingDto(
                connectionRef, courseId, row.StudentId, null, row.StudentName,
                "Acesso ao curso", "no_recent_access", "attention", factors, null, null,
                row.LastAccessAt, null));
        }

        return result
            .OrderByDescending(item => LevelRank(item.Level))
            .ThenBy(item => item.StudentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ActivityName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StudentId, StringComparer.Ordinal)
            .ToArray();
    }

    public static string MapRiskLevel(RiskLevel level) => level switch
    {
        RiskLevel.Alto => "critical",
        RiskLevel.Medio => "attention",
        _ => "normal"
    };

    private static string MapLevel(string type, bool overdue, bool needsGrading) =>
        overdue || string.Equals(type, "no_recent_access", StringComparison.Ordinal) ? "risk" :
        needsGrading ? "attention" : "attention";

    private static string NormalizeType(string type, bool needsGrading) =>
        needsGrading || type.Contains("aguardando", StringComparison.OrdinalIgnoreCase)
            ? "awaiting_grading"
            : type.Contains("não entregue", StringComparison.OrdinalIgnoreCase) || type.Contains("não entregue", StringComparison.OrdinalIgnoreCase)
                ? "pending_submission"
                : type;

    private static int LevelRank(string level) => level switch
    {
        "critical" or "risk" => 3,
        "attention" => 2,
        _ => 1
    };
}
