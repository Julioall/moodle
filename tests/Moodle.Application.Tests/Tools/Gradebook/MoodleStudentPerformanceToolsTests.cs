using System.Text.Json;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;
using Moodle.Domain;
using Moodle.Presentation.Tools;
using Moodle.Presentation.Tools.Gradebook;

namespace Moodle.Application.Tests.Tools.Gradebook;

public sealed class MoodleStudentPerformanceToolsTests
{
    [Fact]
    public async Task Nao_usa_snapshot_de_gradebook_stale_para_sugerir_destinatarios()
    {
        var mediator = new FakeMediator();
        var oldSnapshot = new CourseGradebookSnapshot(
            "101",
            new Dictionary<string, CourseGradebook>
            {
                ["student-1"] = new CourseGradebook(
                    "101",
                    "student-1",
                    [new GradebookItem(
                        "course-total",
                        "Total do curso",
                        "course",
                        "course",
                        null,
                        45m,
                        "45",
                        0m,
                        250m,
                        18m,
                        null,
                        null,
                        null,
                        null,
                        null)])
            },
            new GradebookSnapshotCoverage("bulk", 1, 1, true, false, [], []));
        var snapshot = new CourseReadSnapshot(
            "101",
            Activities: null,
            Students: null,
            Groups: null,
            Submissions: null,
            Gradebook: new MoodleSnapshotEnvelope<CourseGradebookSnapshot>(
                oldSnapshot,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                IsStale: true,
                IsFrozen: false,
                Tier: "hot"),
            new CourseReadSnapshotMetadata(
                [MoodleSnapshotDatasets.Students, MoodleSnapshotDatasets.Gradebook],
                [],
                [MoodleSnapshotDatasets.Gradebook],
                [],
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(-1),
                0,
                IsComplete: false,
                RefreshQueued: true));

        var tool = new MoodleStudentPerformanceTools(
            mediator,
            new FakeSelection(),
            new FakeUserResolver(),
            snapshotCoordinator: new FakeSnapshotCoordinator(snapshot));

        var result = await tool.ListarAlunosAbaixoMinimoAsync("101", minGradePercent: 60m);

        Assert.False(result.IsError ?? false);
        Assert.NotNull(mediator.LastBelowMinimumQuery);
        Assert.Null(mediator.LastBelowMinimumQuery!.PrefetchedGradebook);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Empty(structured.GetProperty("data").GetProperty("SuggestedRecipientIds").EnumerateArray());
        var freshness = structured.GetProperty("freshness");
        Assert.Equal("live", freshness.GetProperty("source").GetString());
        Assert.True(freshness.GetProperty("decisionSafe").GetBoolean());
    }

    private sealed class FakeSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class FakeUserResolver : IMoodleUserResolver
    {
        public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult<long?>(42);
    }

    private sealed class FakeSnapshotCoordinator(CourseReadSnapshot snapshot)
        : IMoodleCourseReadSnapshotCoordinator
    {
        public Task<CourseReadSnapshot?> ReadAsync(
            CourseReadSnapshotRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CourseReadSnapshot?>(snapshot);
    }

    private sealed class FakeMediator : IMediator
    {
        public GetStudentsBelowMinGradeQuery? LastBelowMinimumQuery { get; private set; }

        public Task<TResponse> Send<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default)
        {
            if (request is GetStudentsBelowMinGradeQuery belowMinimum)
            {
                LastBelowMinimumQuery = belowMinimum;
                var liveResult = new GetStudentsBelowMinGradeResult(
                    "101",
                    60m,
                    1,
                    [],
                    [],
                    "Leitura live: 90/100, acima do mínimo.");
                return Task.FromResult((TResponse)(object)liveResult);
            }

            throw new NotSupportedException(request.GetType().Name);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromException<object?>(new NotSupportedException(request.GetType().Name));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(
            object request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<object?>();
    }
}
