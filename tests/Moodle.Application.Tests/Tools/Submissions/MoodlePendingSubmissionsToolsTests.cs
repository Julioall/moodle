using MediatR;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Submissions.Queries;
using Moodle.Domain;
using Moodle.Presentation.Tools.Submissions;

namespace Moodle.Application.Tests.Tools.Submissions;

public sealed class MoodlePendingSubmissionsToolsTests
{
    [Fact]
    public async Task Pending_tool_does_not_request_activity_snapshot_as_delivery_inventory()
    {
        var mediator = new CapturingMediator();
        var snapshots = new RecordingSnapshotCoordinator();
        var selection = new FakeConnectionSelection();
        var tool = new MoodlePendingSubmissionsTools(
            mediator,
            selection,
            new FakeUserResolver(),
            snapshots);

        var result = await tool.ListarAlunosPendentesAtividadeAsync(
            "course-1",
            moodleAlias: "fieg");

        Assert.False(result.IsError ?? false);
        Assert.Equal("fieg", selection.Alias);
        Assert.Equal(
            CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook,
            snapshots.Request!.Requirements);
        Assert.Equal(
            CourseReadSnapshotRequirements.None,
            snapshots.Request.Requirements & CourseReadSnapshotRequirements.Activities);
        Assert.NotNull(mediator.PendingQuery);
        Assert.Null(mediator.PendingQuery!.PrefetchedParticipants);
    }

    private sealed class RecordingSnapshotCoordinator : IMoodleCourseReadSnapshotCoordinator
    {
        public CourseReadSnapshotRequest? Request { get; private set; }

        public Task<CourseReadSnapshot?> ReadAsync(
            CourseReadSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult<CourseReadSnapshot?>(null);
        }
    }

    private sealed class FakeConnectionSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class FakeUserResolver : IMoodleUserResolver
    {
        public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult<long?>(42);
    }

    private sealed class CapturingMediator : IMediator
    {
        public GetStudentsWithPendingSubmissionsQuery? PendingQuery { get; private set; }

        public Task<TResponse> Send<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default)
        {
            PendingQuery = Assert.IsType<GetStudentsWithPendingSubmissionsQuery>(request);
            var result = new GetStudentsWithPendingSubmissionsResult(
                PendingQuery.CourseId,
                0,
                PendingQuery.DueDaysAhead,
                [],
                [],
                null);
            return Task.FromResult((TResponse)(object)result);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromException<object?>(new NotSupportedException(request.GetType().Name));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Publish<TNotification>(
            TNotification notification,
            CancellationToken cancellationToken = default)
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
