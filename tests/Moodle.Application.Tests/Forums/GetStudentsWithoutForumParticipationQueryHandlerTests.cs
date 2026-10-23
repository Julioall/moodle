using System.Threading;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Forums.Queries;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Microsoft.Extensions.Options;

namespace Moodle.Application.Tests.Forums;

public sealed class GetStudentsWithoutForumParticipationQueryHandlerTests
{
    [Fact]
    public async Task Forum_sem_discussoes_nao_faz_leituras_de_posts()
    {
        var forum = new CountingForumGateway([]);
        var handler = CreateHandler(forum);

        var result = await handler.Handle(
            new GetStudentsWithoutForumParticipationQuery("course-1", "forum-1", MaxDiscussionsToScan: 25),
            CancellationToken.None);

        Assert.Equal("known", result.ParticipationStatus);
        Assert.True(result.IsComplete);
        Assert.Equal(0, forum.PostReads);
        Assert.Equal(3, result.StudentsWithoutParticipation.Count);
    }

    [Fact]
    public async Task Pagina_de_25_discussões_retorna_cobertura_completa_e_mede_baseline_serial()
    {
        var discussions = Enumerable.Range(1, 25)
            .Select(index => Discussion($"discussion-{index}", authorId: index == 1 ? "student-1" : null))
            .ToArray();
        var forum = new CountingForumGateway(discussions)
        {
            BeforePostRead = async (_, cancellationToken) => await Task.Delay(1, cancellationToken),
        };
        var handler = CreateHandler(forum);

        var result = await handler.Handle(
            new GetStudentsWithoutForumParticipationQuery("course-1", "forum-1", MaxDiscussionsToScan: 26),
            CancellationToken.None);

        Assert.Equal("known", result.ParticipationStatus);
        Assert.True(result.IsComplete);
        Assert.Equal(25, forum.PostReads);
        Assert.Equal(1, forum.MaxConcurrentPostReads);
        Assert.Equal(2, result.StudentsWithoutParticipation.Count);
        Assert.DoesNotContain(result.StudentsWithoutParticipation, student => student.StudentId == "student-1");
    }

    [Fact]
    public async Task Falha_de_uma_discussao_mantem_cobertura_parcial_sem_falso_negativo()
    {
        var forum = new CountingForumGateway(
        [
            Discussion("discussion-1", authorId: "student-1"),
            Discussion("discussion-2", authorId: null),
            Discussion("discussion-3", authorId: null),
        ])
        {
            FailedDiscussionId = "discussion-2",
        };
        var handler = CreateHandler(forum);

        var result = await handler.Handle(
            new GetStudentsWithoutForumParticipationQuery("course-1", "forum-1", MaxDiscussionsToScan: 10),
            CancellationToken.None);

        Assert.Equal("partial", result.ParticipationStatus);
        Assert.False(result.IsComplete);
        Assert.Empty(result.StudentsWithoutParticipation);
        Assert.Empty(result.SuggestedRecipientIds);
        Assert.Contains("análise de participação é parcial", result.Warning);
        Assert.Equal(3, forum.PostReads);
    }

    [Fact]
    public async Task Leitura_limitada_a_duas_concorrentes_preserva_resultado_de_participacao()
    {
        var discussions = Enumerable.Range(1, 25)
            .Select(index => Discussion($"discussion-{index}", authorId: index == 1 ? "student-1" : null))
            .ToArray();
        var forum = new CountingForumGateway(discussions)
        {
            BeforePostRead = SynchronizeFirstConcurrentPair(),
        };
        var handler = CreateHandler(forum, maxConcurrentPosts: 2);

        var result = await handler.Handle(
            new GetStudentsWithoutForumParticipationQuery("course-1", "forum-1", MaxDiscussionsToScan: 26),
            CancellationToken.None);

        Assert.Equal("known", result.ParticipationStatus);
        Assert.True(result.IsComplete);
        Assert.Equal(25, forum.PostReads);
        Assert.Equal(2, forum.MaxConcurrentPostReads);
        Assert.Equal(["student-2", "student-3"], result.SuggestedRecipientIds);
    }

    [Fact]
    public async Task Reutiliza_vaga_liberada_antes_de_concluir_leitura_lenta_anterior()
    {
        var firstPairStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var forum = new CountingForumGateway(
        [
            Discussion("discussion-1", "student-1"),
            Discussion("discussion-2", null),
            Discussion("discussion-3", null),
        ])
        {
            BeforePostRead = async (discussionId, cancellationToken) =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    firstPairStarted.TrySetResult(true);
                }

                if (discussionId == "discussion-1")
                {
                    await firstPairStarted.Task.WaitAsync(cancellationToken);
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else if (discussionId == "discussion-2")
                {
                    await firstPairStarted.Task.WaitAsync(cancellationToken);
                }
                else if (discussionId == "discussion-3")
                {
                    thirdStarted.TrySetResult(true);
                }
            }
        };
        var handler = CreateHandler(forum, maxConcurrentPosts: 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var pending = handler.Handle(
            new GetStudentsWithoutForumParticipationQuery("course-1", "forum-1", MaxDiscussionsToScan: 4),
            timeout.Token);
        try
        {
            await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }

        var result = await pending;

        Assert.Equal("known", result.ParticipationStatus);
        Assert.True(result.IsComplete);
        Assert.Equal(3, forum.PostReads);
        Assert.Equal(2, forum.MaxConcurrentPostReads);
    }

    [Fact]
    public async Task Resposta_429_de_uma_discussao_mantem_cobertura_parcial_e_contabiliza_falha()
    {
        var forum = new CountingForumGateway(
        [
            Discussion("discussion-1", authorId: "student-1"),
            Discussion("discussion-2", authorId: null),
            Discussion("discussion-3", authorId: null),
        ])
        {
            FailedDiscussionId = "discussion-2",
            FailureStatusCode = 429,
            BeforePostRead = SynchronizeFirstConcurrentPair(),
        };
        var telemetry = new RecordingForumTelemetry();
        var handler = CreateHandler(forum, telemetry: telemetry, maxConcurrentPosts: 2);

        var result = await handler.Handle(
            new GetStudentsWithoutForumParticipationQuery("course-1", "forum-1", MaxDiscussionsToScan: 10),
            CancellationToken.None);

        Assert.Equal("partial", result.ParticipationStatus);
        Assert.False(result.IsComplete);
        Assert.Empty(result.StudentsWithoutParticipation);
        Assert.Empty(result.SuggestedRecipientIds);
        Assert.Equal(3, forum.PostReads);
        Assert.Equal((3, 3, 1, 2), telemetry.LastBatch);
    }

    private static GetStudentsWithoutForumParticipationQueryHandler CreateHandler(
        CountingForumGateway forum,
        RecordingForumTelemetry? telemetry = null,
        int maxConcurrentPosts = 1) =>
        new(
            new CountingParticipantsGateway(),
            forum,
            new CurrentUserGateway(),
            Options.Create(new MoodleReadLimitsOptions
            {
                MaxConcurrentForumPostReads = maxConcurrentPosts,
                MaxConcurrentPerConnection = 2
            }),
            telemetry);

    private static ForumDiscussionSummary Discussion(string id, string? authorId) =>
        new(
            id,
            $"post-{id}",
            id,
            id,
            null,
            authorId,
            authorId,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            ReplyCount: 0,
            UnreadCount: 0,
            Pinned: false,
            Locked: false,
            CanReply: true,
            PostsReturned: 0,
            PostsTotal: 0,
            Posts: []);

    private static Func<string, CancellationToken, Task> SynchronizeFirstConcurrentPair()
    {
        var arrivals = 0;
        var bothArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return async (_, cancellationToken) =>
        {
            var arrival = Interlocked.Increment(ref arrivals);
            if (arrival == 2)
            {
                bothArrived.TrySetResult(true);
            }

            if (arrival <= 2)
            {
                await bothArrived.Task.WaitAsync(cancellationToken);
            }
        };
    }

    private sealed class CountingParticipantsGateway : IMoodleParticipantsGateway
    {
    public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This legacy fixture does not support limited previews.");
        public Task<CourseParticipantsPage> GetCourseParticipantsAsync(
            string userExternalId,
            string courseId,
            ParticipantStatusFilter statusFilter,
            int page,
            int pageSize,
            bool studentsOnly,
            bool includeEmail,
            string? groupId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CourseParticipantsPage(
                courseId,
                page,
                pageSize,
                statusFilter,
                studentsOnly,
                includeEmail,
                HasMore: false,
                [
                    Participant("student-1"),
                    Participant("student-2"),
                    Participant("student-3"),
                ]));

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(
            string userExternalId, string courseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CourseGroupSummary>>([]);

        private static CourseParticipantSummary Participant(string id) =>
            new(id, id, null, false, null, null, null, [], []);
    }

    private sealed class CurrentUserGateway : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(42L);
    }

    private sealed class CountingForumGateway(IReadOnlyList<ForumDiscussionSummary> discussions) : IMoodleForumGateway
    {
        private int _activePostReads;
        private int _maxConcurrentPostReads;

        public Func<string, CancellationToken, Task>? BeforePostRead { get; init; }
        public string? FailedDiscussionId { get; init; }
        public int? FailureStatusCode { get; init; }
        public int PostReads => Volatile.Read(ref _postReads);
        public int MaxConcurrentPostReads => _maxConcurrentPostReads;
        private int _postReads;

        public Task<IReadOnlyList<ForumDiscussionSummary>> GetForumDiscussionsPaginatedAsync(
            string userExternalId,
            string forumId,
            string sortBy,
            string sortDirection,
            int page,
            int pageSize,
            CancellationToken cancellationToken) => Task.FromResult(discussions);

        public async Task<IReadOnlyList<ForumPostSummary>> GetDiscussionPostsAsync(
            string userExternalId,
            string discussionId,
            string sortBy,
            string sortDirection,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _postReads);
            var active = Interlocked.Increment(ref _activePostReads);
            UpdateMaximum(ref _maxConcurrentPostReads, active);
            try
            {
                if (BeforePostRead is not null)
                {
                    await BeforePostRead(discussionId, cancellationToken);
                }
                if (string.Equals(FailedDiscussionId, discussionId, StringComparison.Ordinal))
                {
                    throw FailureStatusCode is { } statusCode
                        ? new MoodleApiException("http_error", "Simulated read failure.", httpStatusCode: statusCode)
                        : new InvalidOperationException("Simulated post read failure.");
                }
                return [];
            }
            finally
            {
                Interlocked.Decrement(ref _activePostReads);
            }
        }

        public Task<ForumWriteResult> AddDiscussionAsync(
            string userExternalId, string forumId, string subject, string messageHtml, int groupId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ForumWriteResult> AddDiscussionPostAsync(
            string userExternalId, string postId, string subject, string messageHtml, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ForumInfo>> GetForumsByCoursesAsync(
            string userExternalId, string courseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ForumInfo>>([]);

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            var current = Volatile.Read(ref maximum);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref maximum, candidate, current);
                if (observed == current)
                {
                    return;
                }
                current = observed;
            }
        }
    }

    private sealed class RecordingForumTelemetry : IMoodleCallTelemetry
    {
        public (int Discussions, int Calls, int Failures, int MaxConcurrency)? LastBatch { get; private set; }

        public void RecordMoodleWebServiceCall(string? connectionAlias = null)
        {
        }

        public void RecordForumPostReadBatch(
            string? toolName,
            string? canonicalOperation,
            int discussions,
            int postReadCalls,
            int failedDiscussions,
            int maxConcurrentPosts,
            double durationMs)
        {
            LastBatch = (discussions, postReadCalls, failedDiscussions, maxConcurrentPosts);
        }
    }
}
