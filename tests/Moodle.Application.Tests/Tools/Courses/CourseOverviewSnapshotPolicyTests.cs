using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Application.Registry;
using Moodle.Domain;
using Moodle.Domain.Registry;
using Moodle.Infrastructure;
using Moodle.Presentation.Tools;
using Moodle.Presentation.Tools.Portal;

namespace Moodle.Application.Tests.Tools.Courses;

public sealed class CourseOverviewSnapshotPolicyTests
{
    [Fact]
    public async Task Missing_heads_enqueue_bounded_students_and_gradebook_refreshes()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Coordinator.ReadAsync(Request());

        Assert.NotNull(result);
        Assert.Null(result.Students);
        Assert.Null(result.Gradebook);
        Assert.Collection(fixture.Queue.Requests,
            request =>
            {
                Assert.Equal(MoodleSnapshotDatasets.Students, request.Dataset);
                Assert.Equal(MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1, request.WorkPolicy);
                Assert.Equal("course_overview", request.Trigger);
            },
            request =>
            {
                Assert.Equal(MoodleSnapshotDatasets.Gradebook, request.Dataset);
                Assert.Equal(MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1, request.WorkPolicy);
                Assert.Equal("course_overview", request.Trigger);
            });
        Assert.True(result.Metadata.GradebookRefreshQueued);
        Assert.Equal(1, fixture.Store.StudentsReads);
        Assert.Equal(1, fixture.Store.GradebookReads);
    }

    [Fact]
    public async Task Stale_gradebook_is_queued_even_when_cross_dataset_skew_is_reported()
    {
        var studentsUpdated = DateTimeOffset.UtcNow;
        var student = new CourseParticipantSummary("10", "Aluno", null, false, null, null, null, [], [], "active");
        var students = new MoodleSnapshotEnvelope<CourseParticipantsPage>(
            new CourseParticipantsPage("42", 1, 500, ParticipantStatusFilter.Active, true, false, false, [student]),
            studentsUpdated,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot");
        var gradebook = new MoodleSnapshotEnvelope<CourseGradebookSnapshot>(
            new CourseGradebookSnapshot("42", new Dictionary<string, CourseGradebook>(),
                new GradebookSnapshotCoverage("bulk", 0, 0, true, false, [], [])),
            studentsUpdated.AddMinutes(-20),
            IsStale: true,
            IsFrozen: false,
            Tier: "hot");
        await using var fixture = await Fixture.CreateAsync(students, gradebook);

        var result = await fixture.Coordinator.ReadAsync(Request());

        Assert.NotNull(result);
        Assert.Contains("snapshot_skew", result.Metadata.IncompleteDatasets);
        var queued = Assert.Single(fixture.Queue.Requests);
        Assert.Equal(MoodleSnapshotDatasets.Gradebook, queued.Dataset);
        Assert.Equal(MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1, queued.WorkPolicy);
        Assert.True(result.Metadata.GradebookRefreshQueued);
        Assert.Equal(1, fixture.Store.StudentsReads);
        Assert.Equal(1, fixture.Store.GradebookReads);
    }

    [Fact]
    public async Task Complete_gradebook_for_a_different_roster_is_refreshed()
    {
        var now = DateTimeOffset.UtcNow;
        var student = new CourseParticipantSummary("new-student", "Aluno", null, false, null, null, null, [], [], "active");
        var students = new MoodleSnapshotEnvelope<CourseParticipantsPage>(
            new CourseParticipantsPage("42", 1, 500, ParticipantStatusFilter.Active, true, false, false, [student]),
            now,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot");
        var gradebook = new MoodleSnapshotEnvelope<CourseGradebookSnapshot>(
            new CourseGradebookSnapshot("42", new Dictionary<string, CourseGradebook>(),
                new GradebookSnapshotCoverage("bulk", 1, 1, true, false, [], [])
                {
                    ReturnedStudentIds = ["former-student"],
                }),
            now,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot");
        await using var fixture = await Fixture.CreateAsync(students, gradebook);

        var result = await fixture.Coordinator.ReadAsync(Request());

        Assert.NotNull(result);
        Assert.True(result.Metadata.GradebookRefreshQueued);
        var queued = Assert.Single(fixture.Queue.Requests);
        Assert.Equal(MoodleSnapshotDatasets.Gradebook, queued.Dataset);
        Assert.Equal(MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1, queued.WorkPolicy);
    }

    private static CourseReadSnapshotRequest Request() => new(
        "42",
        "demo",
        "owner",
        CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook,
        AllowStale: true,
        Policy: CourseReadSnapshotPolicy.CourseOverviewV1);

    private sealed class Fixture(
        ConnectorDbContext db,
        MoodleSnapshotToolContext coordinator,
        SnapshotStore store,
        SnapshotQueue queue) : IAsyncDisposable
    {
        public MoodleSnapshotToolContext Coordinator { get; } = coordinator;
        public SnapshotStore Store { get; } = store;
        public SnapshotQueue Queue { get; } = queue;

        public static async Task<Fixture> CreateAsync(
            MoodleSnapshotEnvelope<CourseParticipantsPage>? students = null,
            MoodleSnapshotEnvelope<CourseGradebookSnapshot>? gradebook = null)
        {
            var db = new ConnectorDbContext(new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase("course-overview-policy-" + Guid.NewGuid().ToString("N"))
                .Options);
            var owner = new UserAccountEntity
            {
                Id = Guid.NewGuid(),
                Name = "Tutor",
                Email = "tutor@example.com",
                PasswordHash = "hash",
                ConnectorClientId = "client",
            };
            db.UserAccounts.Add(owner);
            await db.SaveChangesAsync();

            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, owner.Id.ToString())], "test")),
            };
            var identityResolver = new PortalMcpIdentityResolver(
                new FixedHttpContextAccessor { HttpContext = httpContext },
                db);
            var courses = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
                [new CourseSummary("42", null, "LOG", "Lógica", "Lógica", null, null, null, null, true, null, null, null, null, null, null)],
                DateTimeOffset.UtcNow,
                IsStale: false,
                IsFrozen: false,
                Tier: "warm");
            var store = new SnapshotStore(courses, students, gradebook);
            var queue = new SnapshotQueue();
            var registry = new FakeConnectionRegistry();
            var coordinator = new MoodleSnapshotToolContext(identityResolver, registry, store, queue);
            return new Fixture(db, coordinator, store, queue);
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class FakeConnectionRegistry : IConnectionRegistry
    {
        public Task<Moodle.Domain.Registry.ConnectionInfo?> ResolveConnectionAsync(string? alias, CancellationToken cancellationToken = default) =>
            Task.FromResult<Moodle.Domain.Registry.ConnectionInfo?>(new Moodle.Domain.Registry.ConnectionInfo(Guid.NewGuid(), "demo", "https://moodle.example"));
    }

    private sealed class SnapshotStore(
        MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>> courses,
        MoodleSnapshotEnvelope<CourseParticipantsPage>? students,
        MoodleSnapshotEnvelope<CourseGradebookSnapshot>? gradebook) : IMoodleSnapshotStore
    {
        public int StudentsReads { get; private set; }
        public int GradebookReads { get; private set; }

        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?> GetCoursesAsync(Guid ownerId, string connectionAlias, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?>(courses);

        public Task<MoodleSnapshotEnvelope<CourseContentsSummary>?> GetActivitiesAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<CourseContentsSummary>?>(null);

        public Task<MoodleSnapshotEnvelope<CourseParticipantsPage>?> GetStudentsAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default)
        {
            StudentsReads++;
            return Task.FromResult(students);
        }

        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?> GetGroupsAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?>(null);

        public Task<MoodleSnapshotEnvelope<T>?> GetAsync<T>(Guid ownerId, string connectionAlias, string dataset, string courseId = "", CancellationToken cancellationToken = default)
        {
            if (dataset == MoodleSnapshotDatasets.Gradebook) GradebookReads++;
            return Task.FromResult(gradebook is not null && typeof(T) == typeof(CourseGradebookSnapshot)
                ? (MoodleSnapshotEnvelope<T>)(object)gradebook
                : null);
        }

        public Task SaveAsync<T>(Guid ownerId, string connectionAlias, string dataset, string courseId, T payload, string tier, bool frozen, bool complete, int recordCount, DateTimeOffset now, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Invalidate(Guid ownerId, string connectionAlias, string dataset, string courseId = "") { }
    }

    private sealed class SnapshotQueue : IMoodleSnapshotSyncQueue
    {
        public List<MoodleSnapshotSyncRequest> Requests { get; } = [];
        public bool Enqueue(MoodleSnapshotSyncRequest request) { Requests.Add(request); return true; }
        public Task<bool> EnqueueAsync(MoodleSnapshotSyncRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(true);
        }
    }
}
