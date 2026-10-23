using Microsoft.EntityFrameworkCore;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Presentation;

namespace Moodle.Application.Tests.Portal;

public sealed class DashboardCourseScopeResolverTests
{
    [Fact]
    public async Task Filters_ignored_and_finished_sequence_courses_like_my_courses()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.UserIgnoredCourses.Add(new UserIgnoredCourseEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ConnectionAlias = "goias",
            CourseId = "ignored",
        });
        await db.SaveChangesAsync();

        var startOfFirstSequence = DateTimeOffset.UtcNow.AddMonths(-6);
        var startOfCurrentSequence = DateTimeOffset.UtcNow.AddMonths(-1);
        var endOfSequences = DateTimeOffset.UtcNow.AddMonths(6);
        var courses = new[]
        {
            Course("finished-by-sequence", startOfFirstSequence, endOfSequences),
            Course("current", startOfCurrentSequence, endOfSequences),
            Course("ignored", startOfCurrentSequence, endOfSequences),
        };

        var resolver = new DashboardCourseScopeResolver(null!, db, null!);
        var result = await resolver.FilterAsync(ownerId, "goias", courses, CancellationToken.None);

        Assert.Equal(["current"], result.Select(course => course.CourseId));
    }

    [Fact]
    public async Task Does_not_infer_a_sequence_end_from_a_single_observed_end_date()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ConnectorDbContext(options);

        var firstStart = DateTimeOffset.UtcNow.AddMonths(-2);
        var secondStart = DateTimeOffset.UtcNow.AddMonths(-1);
        var sharedFutureEnd = DateTimeOffset.UtcNow.AddMonths(2);
        var courses = new[]
        {
            Course("first", firstStart, sharedFutureEnd),
            Course("second", secondStart, null),
        };

        var resolver = new DashboardCourseScopeResolver(null!, db, null!);
        var result = await resolver.FilterAsync(ownerId, "senai", courses, CancellationToken.None);

        Assert.Equal(["first", "second"], result.Select(course => course.CourseId));
    }

    [Fact]
    public async Task Includes_an_explicitly_tracked_course_even_when_it_is_outside_the_current_cycle()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.UserTrackedCourses.Add(new UserTrackedCourseEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ConnectionAlias = "goias",
            CourseId = "tracked-finished",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var courses = new[]
        {
            Course("tracked-finished", DateTimeOffset.UtcNow.AddMonths(-6), DateTimeOffset.UtcNow.AddMonths(-1)),
            Course("not-tracked-finished", DateTimeOffset.UtcNow.AddMonths(-6), DateTimeOffset.UtcNow.AddMonths(-1)),
        };

        var resolver = new DashboardCourseScopeResolver(null!, db, null!);
        var result = await resolver.FilterAsync(ownerId, "goias", courses, CancellationToken.None);

        Assert.Equal(["tracked-finished"], result.Select(course => course.CourseId));
    }

    [Fact]
    public async Task Includes_current_courses_by_default_and_excludes_courses_outside_the_current_cycle()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ConnectorDbContext(options);
        var now = DateTimeOffset.UtcNow;
        var courses = new[]
        {
            Course("current", now.AddDays(-1), now.AddDays(1)),
            Course("future", now.AddDays(1), now.AddDays(30)),
            Course("finished", now.AddDays(-30), now.AddDays(-1)),
        };

        var resolver = new DashboardCourseScopeResolver(null!, db, null!);
        var result = await resolver.FilterAsync(ownerId, "goias", courses, CancellationToken.None);

        Assert.Equal(["current"], result.Select(course => course.CourseId));
    }

    private static CourseSummary Course(string id, DateTimeOffset start, DateTimeOffset? end) =>
        new(id, null, null, id, id, null, "Senai > Turma", start, end, true, null, null, null, null, null, null);
}
