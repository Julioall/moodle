using MediatR;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Application.Courses;
using Moodle.Domain;
using Moodle.Infrastructure;

namespace Moodle.Presentation;

/// <summary>
/// Centraliza a definição de cursos do dashboard e de “Meus Cursos”.
/// O snapshot de cursos é deliberadamente bruto; os filtros de ciclo,
/// sequência e preferências de acompanhamento precisam ser aplicados antes do refresh.
/// </summary>
internal sealed class DashboardCourseScopeResolver(
    IMediator mediator,
    ConnectorDbContext dbContext,
    IMoodleSnapshotStore snapshotStore)
{
    public async Task<IReadOnlyList<CourseSummary>> ResolveAsync(
        Guid ownerId,
        string connectionAlias,
        CancellationToken cancellationToken)
    {
        // O snapshot de cursos é a mesma fonte persistente usada pelo portal.
        // Quando existe, o dashboard não deve bloquear a tela refazendo a
        // paginação completa do Moodle; a fila de sincronização o atualiza
        // em segundo plano quando necessário.
        var snapshot = await snapshotStore.GetCoursesAsync(ownerId, connectionAlias, cancellationToken);
        if (snapshot is not null)
        {
            return await FilterAsync(ownerId, connectionAlias, snapshot.Data, cancellationToken);
        }

        var allCourses = new List<CourseSummary>();
        var page = 1;
        PagedCourses current;
        do
        {
            current = await mediator.Send(new ListMyCoursesQuery(ownerId.ToString(), 100, page), cancellationToken);
            allCourses.AddRange(current.Items);
            page++;
        }
        while (current.HasNextPage);

        return await FilterAsync(ownerId, connectionAlias, allCourses, cancellationToken);
    }

    public async Task<IReadOnlyList<CourseSummary>> FilterAsync(
        Guid ownerId,
        string connectionAlias,
        IReadOnlyList<CourseSummary> courses,
        CancellationToken cancellationToken)
    {
        var ignoredCourseIds = await dbContext.UserIgnoredCourses
            .AsNoTracking()
            .Where(item => item.OwnerId == ownerId && item.ConnectionAlias == connectionAlias)
            .Select(item => item.CourseId)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);
        var trackedCourseIds = await dbContext.UserTrackedCourses
            .AsNoTracking()
            .Where(item => item.OwnerId == ownerId && item.ConnectionAlias == connectionAlias)
            .Select(item => item.CourseId)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        return NormalizeEndDates(courses)
            .Where(course =>
                !ignoredCourseIds.Contains(course.CourseId) &&
                (trackedCourseIds.Contains(course.CourseId) ||
                 ((course.StartDate is null || course.StartDate <= now) &&
                  (course.EndDate is null || course.EndDate >= now))))
            .ToArray();
    }

    private static IReadOnlyList<CourseSummary> NormalizeEndDates(IReadOnlyList<CourseSummary> courses)
    {
        var adjustedEndDates = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var groups = courses
            .Where(course => !string.IsNullOrWhiteSpace(course.CategoryName))
            .GroupBy(course => course.CategoryName!.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.ToLowerInvariant())
                .ToArray() is { Length: > 0 } parts ? string.Join(" > ", parts) : string.Empty)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key));

        foreach (var group in groups)
        {
            if (group.Count() < 2) continue;
            var endDates = group
                .Select(course => course.EndDate)
                .Where(date => date.HasValue)
                .Select(date => date!.Value)
                .ToArray();
            // Match Meus Cursos: sequence inference needs at least two
            // observed end dates. With only one end date, adjusting the
            // course would make the dashboard hide a course that the portal
            // still considers active.
            if (endDates.Length < 2) continue;
            var distinctEndDates = endDates.Distinct().ToArray();

            IEnumerable<IEnumerable<CourseSummary>> sequences = distinctEndDates.Length == 1
                ? [group]
                : distinctEndDates.Select(endDate => group.Where(course => course.EndDate == endDate));

            foreach (var sequence in sequences)
            {
                var starts = sequence
                    .Select(course => course.StartDate)
                    .Where(date => date.HasValue)
                    .Select(date => date!.Value)
                    .Distinct()
                    .OrderBy(date => date)
                    .ToArray();
                if (starts.Length < 2) continue;

                foreach (var course in sequence)
                {
                    if (course.StartDate is not { } start) continue;
                    var nextStart = starts.FirstOrDefault(candidate => candidate > start);
                    if (nextStart > start) adjustedEndDates[course.CourseId] = nextStart;
                }
            }
        }

        return courses
            .Select(course => adjustedEndDates.TryGetValue(course.CourseId, out var endDate)
                ? course with { EndDate = endDate }
                : course)
            .ToArray();
    }
}
