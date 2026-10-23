using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;
using Moodle.Domain;

public sealed record StudentRef(string ConnectionRef, string StudentId);
public sealed record StudentCourseDto(
    string ConnectionRef, string CourseId, string Name, string? Url,
    string EnrollmentStatus, decimal? Progress, DateTimeOffset? LastCourseAccessAt,
    IReadOnlyList<StudentGradeDto> Grades);
public sealed record StudentGradeDto(
    string ItemId, string Name, decimal? Grade, decimal? Maximum,
    decimal? Percentage, string? Feedback, bool ReadOnly);
public sealed record StudentDto(
    StudentRef StudentRef, string ConnectionRef, string StudentId, string Name,
    string? Email, bool? Suspended, DateTimeOffset? FirstAccessAt,
    DateTimeOffset? LastAccessAt, DateTimeOffset? LastCourseAccessAt,
    string Risk, IReadOnlyList<string> RiskFactors, IReadOnlyList<StudentCourseDto> Courses,
    string? MoodleUrl)
{
    public int PendingCount { get; init; }
}

public static class StudentContractMapper
{
    public static StudentDto ToDto(
        string connectionRef,
        CourseParticipantSummary participant,
        IReadOnlyList<StudentCourseDto>? courses = null,
        string? moodleUrl = null)
    {
        var factors = new List<string>();
        var risk = "normal";
        if (!participant.LastCourseAccessAt.HasValue)
        {
            risk = "risco";
            factors.Add("Sem acesso ao curso");
        }
        else if ((DateTimeOffset.UtcNow - participant.LastCourseAccessAt.Value).TotalDays >= 14)
        {
            risk = "atencao";
            factors.Add("Sem acesso ao curso há 14 dias ou mais");
        }

        return new(new(connectionRef, participant.UserId), connectionRef, participant.UserId,
            participant.FullName, participant.Email, participant.Suspended,
            participant.FirstAccessAt, participant.LastAccessAt, participant.LastCourseAccessAt,
            risk, factors, courses ?? Array.Empty<StudentCourseDto>(), moodleUrl);
    }

    public static StudentGradeDto ToGradeDto(StudentGradeItem item) =>
        new(item.ItemId, item.ItemName, item.GradeRaw, item.GradeMax,
            item.PercentageFormatted, item.Feedback, ReadOnly: true);
}
