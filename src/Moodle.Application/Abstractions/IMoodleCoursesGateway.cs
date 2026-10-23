using Moodle.Domain;

namespace Moodle.Application.Abstractions;

public interface IMoodleCoursesGateway
{
    Task<IReadOnlyList<CourseSummary>> GetReportCoursesLiveAsync(string userExternalId,
        IReadOnlyCollection<string> courseIds, bool includeCategoryPath, bool singleAttempt,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This gateway does not support live report scope validation.");
    Task<IReadOnlyList<CourseHierarchyNode>> GetMyCourseHierarchyAsync(string userExternalId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CourseHierarchyNode>>([]);

    Task<PagedCourses> GetMyCoursesByCategoryAsync(string userExternalId, string categoryPath, int limit, int page, CancellationToken cancellationToken) =>
        Task.FromResult(new PagedCourses([], 0, page, limit));

    Task<PagedCourses> GetMyCoursesAsync(
        string userExternalId,
        int limit,
        int page,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(
        string userExternalId,
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task<CourseSummary?> GetMyCourseAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken);
}

public sealed record CourseHierarchyNode(string Path, string Name, int Level, int CourseCount);
