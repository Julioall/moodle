using Moodle.Domain;

namespace Moodle.Application.Abstractions;

public interface IMoodleCourseContentsGateway
{
    Task<CourseContentsSummary> GetCourseContentsAsync(
        string userExternalId,
        string courseId,
        IReadOnlyCollection<string> moduleTypes,
        bool includeHidden,
        bool onlyWithFiles,
        CancellationToken cancellationToken);
}
