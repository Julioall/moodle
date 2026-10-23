using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Presentation;

namespace Moodle.Application.Tests.Portal;

public sealed class DashboardPendingSnapshotBuilderTests
{
    [Fact]
    public async Task ReadPendingUsesOnlyBulkSupportSnapshotReads()
    {
        var store = new RecordingSnapshotStore();
        var courseIds = new[] { "course-1", "course-2", "course-3" };

        await DashboardPendingSnapshotBatchReader.ReadAsync(
            store,
            Guid.NewGuid(),
            "goias",
            courseIds,
            CancellationToken.None);

        Assert.Equal(
            [MoodleSnapshotDatasets.Students],
            store.BulkDatasets);
        Assert.All(store.BulkCourseIds, ids => Assert.Equal(courseIds, ids));
        Assert.Equal(0, store.IndividualReads);
    }

    private sealed class RecordingSnapshotStore : IMoodleSnapshotStore
    {
        public List<string> BulkDatasets { get; } = [];
        public List<IReadOnlyCollection<string>> BulkCourseIds { get; } = [];
        public int IndividualReads { get; private set; }

        public Task<IReadOnlyDictionary<string, MoodleSnapshotEnvelope<T>>> GetManyAsync<T>(
            Guid ownerId,
            string connectionAlias,
            string dataset,
            IReadOnlyCollection<string> courseIds,
            CancellationToken cancellationToken = default)
        {
            BulkDatasets.Add(dataset);
            BulkCourseIds.Add(courseIds.ToArray());
            return Task.FromResult<IReadOnlyDictionary<string, MoodleSnapshotEnvelope<T>>>(
                new Dictionary<string, MoodleSnapshotEnvelope<T>>(StringComparer.Ordinal));
        }

        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?> GetCoursesAsync(Guid ownerId, string connectionAlias, CancellationToken cancellationToken = default) =>
            GetAsync<IReadOnlyList<CourseSummary>>(ownerId, connectionAlias, MoodleSnapshotDatasets.Courses, cancellationToken: cancellationToken);

        public Task<MoodleSnapshotEnvelope<CourseContentsSummary>?> GetActivitiesAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            GetAsync<CourseContentsSummary>(ownerId, connectionAlias, MoodleSnapshotDatasets.Activities, courseId, cancellationToken);

        public Task<MoodleSnapshotEnvelope<CourseParticipantsPage>?> GetStudentsAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            GetAsync<CourseParticipantsPage>(ownerId, connectionAlias, MoodleSnapshotDatasets.Students, courseId, cancellationToken);

        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?> GetGroupsAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            GetAsync<IReadOnlyList<CourseGroupSummary>>(ownerId, connectionAlias, MoodleSnapshotDatasets.Groups, courseId, cancellationToken);

        public Task<MoodleSnapshotEnvelope<T>?> GetAsync<T>(
            Guid ownerId,
            string connectionAlias,
            string dataset,
            string courseId = "",
            CancellationToken cancellationToken = default)
        {
            IndividualReads++;
            return Task.FromResult<MoodleSnapshotEnvelope<T>?>(null);
        }

        public Task SaveAsync<T>(Guid ownerId, string connectionAlias, string dataset, string courseId, T payload, string tier, bool frozen, bool complete, int recordCount, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Invalidate(Guid ownerId, string connectionAlias, string dataset, string courseId = "") { }
    }
}
