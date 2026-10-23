namespace Moodle.Application.Abstractions;

/// <summary>
/// Resolves a course total maximum when Moodle omits <c>grademax</c> from
/// <c>gradereport_user_get_grade_items</c>. Implementations must only return a
/// value when the source exposes enough information to calculate it safely.
/// </summary>
public interface IMoodleCourseGradeMaxGateway
{
    Task<CourseGradeMaxResolution> ResolveAsync(string courseId, IReadOnlyCollection<GradebookItem> items,
        bool bypassCache, CancellationToken cancellationToken) => bypassCache
            ? throw new NotSupportedException("This gateway does not support live scale reads.")
            : ResolveAsync(courseId, items, cancellationToken);
    Task<CourseGradeMaxResolution> ResolveAsync(
        string courseId,
        IReadOnlyCollection<GradebookItem> items,
        CancellationToken cancellationToken);
}

public sealed record CourseGradeMaxResolution(
    decimal? MaxGrade,
    string? Source,
    string? Warning);
