using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Courses;

public sealed record ListMyCoursesQuery(string UserExternalId, int Limit, int Page = 1) : IRequest<PagedCourses>;

public sealed record SearchCoursesQuery(string UserExternalId, string Query, int Limit) : IRequest<IReadOnlyList<CourseSummary>>;

public sealed record GetCourseQuery(string UserExternalId, string CourseId) : IRequest<CourseSummary?>;

public sealed class ListMyCoursesQueryHandler(IMoodleCoursesGateway gateway)
    : IRequestHandler<ListMyCoursesQuery, PagedCourses>
{
    public Task<PagedCourses> Handle(ListMyCoursesQuery request, CancellationToken cancellationToken)
    {
        if (request.Page < 1) throw new ArgumentOutOfRangeException(nameof(request.Page), "A página deve ser maior ou igual a 1. A paginação começa em 1.");
        var safeLimit = Math.Clamp(request.Limit, 1, 100);
        return gateway.GetMyCoursesAsync(request.UserExternalId, safeLimit, request.Page, cancellationToken);
    }
}

public sealed class SearchCoursesQueryHandler(IMoodleCoursesGateway gateway)
    : IRequestHandler<SearchCoursesQuery, IReadOnlyList<CourseSummary>>
{
    public Task<IReadOnlyList<CourseSummary>> Handle(SearchCoursesQuery request, CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(request.Limit, 1, 20);
        return gateway.SearchMyCoursesAsync(request.UserExternalId, request.Query, safeLimit, cancellationToken);
    }
}

public sealed class GetCourseQueryHandler(IMoodleCoursesGateway gateway)
    : IRequestHandler<GetCourseQuery, CourseSummary?>
{
    public Task<CourseSummary?> Handle(GetCourseQuery request, CancellationToken cancellationToken)
    {
        return gateway.GetMyCourseAsync(request.UserExternalId, request.CourseId, cancellationToken);
    }
}
