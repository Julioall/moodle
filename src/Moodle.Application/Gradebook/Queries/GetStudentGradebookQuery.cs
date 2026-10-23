using MediatR;
using Moodle.Application.Abstractions;

namespace Moodle.Application.Gradebook.Queries;

public sealed record GetStudentGradebookQuery(
    string CourseId,
    string StudentId,
    CourseGradebookSnapshot? PrefetchedGradebook = null) : IRequest<CourseGradebook>;

public sealed class GetStudentGradebookQueryHandler(IMoodleGradebookGateway gateway)
    : IRequestHandler<GetStudentGradebookQuery, CourseGradebook>
{
    public async Task<CourseGradebook> Handle(GetStudentGradebookQuery request, CancellationToken cancellationToken)
    {
        var prefetchedSnapshot = request.PrefetchedGradebook;
        if (string.Equals(prefetchedSnapshot?.CourseId, request.CourseId, StringComparison.OrdinalIgnoreCase) &&
            prefetchedSnapshot is not null &&
            prefetchedSnapshot.TryGetForStudent(request.StudentId, out var prefetched))
        {
            return prefetched;
        }

        return await gateway.GetStudentGradebookAsync(request.CourseId, request.StudentId, cancellationToken);
    }
}
