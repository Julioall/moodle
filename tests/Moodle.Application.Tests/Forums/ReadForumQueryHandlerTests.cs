using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Forums;
using Moodle.Domain;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Forums;

public sealed class ReadForumQueryHandlerTests
{
    private readonly ITestOutputHelper output;

    public ReadForumQueryHandlerTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task Deve_resolver_forum_e_ler_discussoes_com_posts()
    {
        var forumGateway = new FakeForumGateway
        {
            Discussions = [Discussion("9001", "8001", "Avisos")],
            Posts =
            [
                Post("8001", "9001", "Avisos", "Primeira mensagem"),
                Post("8002", "9001", "Re: Avisos", "Resposta")
            ]
        };
        var sut = CreateHandler(forumGateway: forumGateway);

        var result = await sut.Handle(
            new ReadForumQuery(
                "usuario-42",
                "CURSO-1",
                "21",
                Page: 1,
                PageSize: 10,
                SortBy: "timemodified",
                SortDirection: "DESC",
                IncludePosts: true,
                PostsPerDiscussion: 1),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("701", forumGateway.LastForumId);
        Assert.Equal(11, forumGateway.LastPageSize);
        Assert.Equal(["9001"], forumGateway.PostDiscussionIds);
        Assert.Equal("701", result!.ForumId);
        Assert.Equal("21", result.ForumModuleId);
        Assert.Equal("Forum Geral", result.ForumName);
        Assert.Single(result.Discussions);
        Assert.Equal(1, result.Discussions[0].PostsReturned);
        Assert.Equal(2, result.Discussions[0].PostsTotal);
        Assert.Equal("Primeira mensagem", result.Discussions[0].Posts[0].MessageText);
    }

    [Fact]
    public async Task Deve_paginar_discussoes_sem_buscar_posts_quando_desabilitado()
    {
        var forumGateway = new FakeForumGateway
        {
            Discussions =
            [
                Discussion("9001", "8001", "Topico 1"),
                Discussion("9002", "8002", "Topico 2"),
                Discussion("9003", "8003", "Topico 3")
            ]
        };
        var sut = CreateHandler(forumGateway: forumGateway);

        var result = await sut.Handle(
            new ReadForumQuery(
                "usuario-42",
                "CURSO-1",
                "701",
                Page: 1,
                PageSize: 2,
                SortBy: "id",
                SortDirection: "ASC",
                IncludePosts: false,
                PostsPerDiscussion: 50),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, forumGateway.LastPageSize);
        Assert.True(result!.HasMore);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Empty(forumGateway.PostDiscussionIds);
    }

    [Fact]
    public async Task Deve_retornar_null_para_forum_inexistente()
    {
        var forumGateway = new FakeForumGateway();
        var sut = CreateHandler(
            contentsGateway: new FakeContentsGateway { ReturnWithoutForum = true },
            forumGateway: forumGateway);

        var result = await sut.Handle(
            new ReadForumQuery(
                "usuario-42",
                "CURSO-1",
                "999",
                Page: 1,
                PageSize: 10,
                SortBy: "timemodified",
                SortDirection: "DESC",
                IncludePosts: true,
                PostsPerDiscussion: 50),
            CancellationToken.None);

        Assert.Null(result);
        Assert.False(forumGateway.WasDiscussionsCalled);
    }

    [Fact]
    public async Task Forum_sem_discussoes_nao_faz_leituras_de_posts()
    {
        var forumGateway = new FakeForumGateway();
        var sut = CreateHandler(forumGateway: forumGateway);

        var result = await sut.Handle(
            new ReadForumQuery(
                "usuario-42", "CURSO-1", "21", 1, 25, "id", "ASC", IncludePosts: true, PostsPerDiscussion: 20),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.Discussions);
        Assert.Equal(0, forumGateway.PostReads);
    }

    [Fact]
    public async Task Le_discussoes_de_uma_pagina_de_25_preservando_a_ordem_e_registra_baseline_serial()
    {
        var discussions = Enumerable.Range(1, 25)
            .Select(index => Discussion($"discussion-{index}", $"post-{index}", $"Topic {index}"))
            .ToArray();
        var forumGateway = new FakeForumGateway
        {
            Discussions = discussions,
            BeforePostRead = async (discussionId, cancellationToken) =>
                await Task.Delay(discussionId == "discussion-1" ? 20 : 1, cancellationToken)
        };
        var sut = CreateHandler(forumGateway: forumGateway);

        var result = await sut.Handle(
            new ReadForumQuery(
                "usuario-42", "CURSO-1", "21", 1, 25, "id", "ASC", IncludePosts: true, PostsPerDiscussion: 20),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(25, forumGateway.PostReads);
        Assert.Equal(1, forumGateway.MaxConcurrentPostReads);
        Assert.Equal(
            discussions.Select(discussion => discussion.DiscussionId),
            result!.Discussions.Select(discussion => discussion.DiscussionId));
    }

    [Fact]
    public async Task Falha_de_post_preserva_o_contrato_de_erro_da_leitura_do_forum()
    {
        var forumGateway = new FakeForumGateway
        {
            Discussions = [
                Discussion("discussion-1", "post-1", "Topic 1"),
                Discussion("discussion-2", "post-2", "Topic 2"),
                Discussion("discussion-3", "post-3", "Topic 3")],
            FailedDiscussionId = "discussion-2",
        };
        var sut = CreateHandler(forumGateway: forumGateway);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(
            new ReadForumQuery(
                "usuario-42", "CURSO-1", "21", 1, 25, "id", "ASC", IncludePosts: true, PostsPerDiscussion: 20),
            CancellationToken.None));
    }

    [Fact]
    public async Task Leitura_limitada_a_duas_concorrentes_preserva_ordem_e_contagem_de_posts()
    {
        var discussions = Enumerable.Range(1, 25)
            .Select(index => Discussion($"discussion-{index}", $"post-{index}", $"Topic {index}"))
            .ToArray();
        var forumGateway = new FakeForumGateway
        {
            Discussions = discussions,
            Posts = discussions.Select(discussion => Post(
                discussion.FirstPostId,
                discussion.DiscussionId,
                discussion.Subject,
                $"Body {discussion.DiscussionId}")).ToArray(),
            BeforePostRead = async (discussionId, cancellationToken) =>
                await Task.Delay(discussionId == "discussion-1" ? 25 : 1, cancellationToken)
        };
        var sut = CreateHandler(forumGateway: forumGateway, maxConcurrentPosts: 2);

        var result = await sut.Handle(
            new ReadForumQuery(
                "usuario-42", "CURSO-1", "21", 1, 25, "id", "ASC", IncludePosts: true, PostsPerDiscussion: 1),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(25, forumGateway.PostReads);
        Assert.Equal(2, forumGateway.MaxConcurrentPostReads);
        Assert.Equal(discussions.Select(discussion => discussion.DiscussionId),
            result!.Discussions.Select(discussion => discussion.DiscussionId));
        Assert.All(result.Discussions, discussion =>
        {
            Assert.Equal(1, discussion.PostsReturned);
            Assert.Equal(1, discussion.PostsTotal);
            Assert.Equal($"Body {discussion.DiscussionId}", discussion.Posts[0].MessageText);
        });
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 4)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 4)]
    [InlineData(20, 1)]
    [InlineData(20, 2)]
    [InlineData(20, 4)]
    [InlineData(25, 1)]
    [InlineData(25, 2)]
    [InlineData(25, 4)]
    public async Task Mantem_contagem_de_chamadas_e_respeita_teto_para_tamanhos_e_concorrencias_sinteticos(
        int discussionCount,
        int maxConcurrentPosts)
    {
        var expectedConcurrency = Math.Min(discussionCount, maxConcurrentPosts);
        var reachedConcurrency = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var discussions = Enumerable.Range(1, discussionCount)
            .Select(index => Discussion($"discussion-{index}", $"post-{index}", $"Topic {index}"))
            .ToArray();
        var forumGateway = new FakeForumGateway
        {
            Discussions = discussions,
            BeforePostRead = async (_, cancellationToken) =>
            {
                if (expectedConcurrency == 0)
                {
                    return;
                }

                if (Interlocked.Increment(ref startedCount) == expectedConcurrency)
                {
                    reachedConcurrency.TrySetResult();
                }
                await reachedConcurrency.Task.WaitAsync(cancellationToken);
            }
        };
        var sut = CreateHandler(
            forumGateway: forumGateway,
            maxConcurrentPosts: maxConcurrentPosts,
            maxConcurrentPerConnection: maxConcurrentPosts);

        var result = await sut.Handle(
            new ReadForumQuery(
                "usuario-42", "CURSO-1", "21", 1, 25, "id", "ASC", IncludePosts: true, PostsPerDiscussion: 1),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(discussionCount, forumGateway.PostReads);
        Assert.Equal(expectedConcurrency, forumGateway.MaxConcurrentPostReads);
        Assert.Equal(discussionCount, result!.ReturnedCount);
        Assert.Equal(discussions.Select(discussion => discussion.DiscussionId),
            result.Discussions.Select(discussion => discussion.DiscussionId));
    }

    [Fact]
    public async Task Reutiliza_vaga_liberada_sem_aguardar_a_leitura_lenta_do_bloco()
    {
        var firstPairStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var discussions = Enumerable.Range(1, 3)
            .Select(index => Discussion($"discussion-{index}", $"post-{index}", $"Topic {index}"))
            .ToArray();
        var forumGateway = new FakeForumGateway
        {
            Discussions = discussions,
            BeforePostRead = async (discussionId, cancellationToken) =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    firstPairStarted.TrySetResult();
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
                    thirdStarted.TrySetResult();
                }
            }
        };
        var sut = CreateHandler(forumGateway: forumGateway, maxConcurrentPosts: 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var pending = sut.Handle(
            new ReadForumQuery(
                "usuario-42", "CURSO-1", "21", 1, 3, "id", "ASC", IncludePosts: true, PostsPerDiscussion: 1),
            timeout.Token);
        try
        {
            await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        var result = await pending;

        Assert.NotNull(result);
        Assert.Equal(3, forumGateway.PostReads);
        Assert.Equal(2, forumGateway.MaxConcurrentPostReads);
        Assert.Equal(discussions.Select(discussion => discussion.DiscussionId),
            result!.Discussions.Select(discussion => discussion.DiscussionId));
    }

    [Fact]
    public async Task Falha_fatal_interrompe_novas_leituras_e_aguarda_chamadas_ativas()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discussions = Enumerable.Range(1, 3)
            .Select(index => Discussion($"discussion-{index}", $"post-{index}", $"Topic {index}"))
            .ToArray();
        var forumGateway = new FakeForumGateway
        {
            Discussions = discussions,
            FailedDiscussionId = "discussion-1",
            BeforePostRead = async (discussionId, cancellationToken) =>
            {
                if (discussionId == "discussion-2")
                {
                    secondStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                else if (discussionId == "discussion-1")
                {
                    await secondStarted.Task.WaitAsync(cancellationToken);
                }
            }
        };
        var sut = CreateHandler(forumGateway: forumGateway, maxConcurrentPosts: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(
            new ReadForumQuery(
                "usuario-42", "CURSO-1", "21", 1, 3, "id", "ASC", IncludePosts: true, PostsPerDiscussion: 1),
            CancellationToken.None));

        Assert.Equal(2, forumGateway.PostReads);
        Assert.Equal(2, forumGateway.MaxConcurrentPostReads);
    }

    [Fact]
    public async Task Cancelamento_interrompe_novas_chamadas_de_posts_ao_libertar_vagas()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var discussions = Enumerable.Range(1, 25)
            .Select(index => Discussion($"discussion-{index}", $"post-{index}", $"Topic {index}"))
            .ToArray();
        var forumGateway = new FakeForumGateway
        {
            Discussions = discussions,
            BeforePostRead = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    started.TrySetResult();
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        var sut = CreateHandler(forumGateway: forumGateway, maxConcurrentPosts: 2);
        using var cancellation = new CancellationTokenSource();

        var pending = sut.Handle(
            new ReadForumQuery(
                "usuario-42", "CURSO-1", "21", 1, 25, "id", "ASC", IncludePosts: true, PostsPerDiscussion: 1),
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(2, forumGateway.PostReads);
    }

    [Fact]
    public async Task Forum_single_sem_posts_preserva_discussao_sem_buscar_mensagens()
    {
        var forumGateway = new FakeForumGateway
        {
            Forums = [new ForumInfo("701", "123", "single", "Aviso único", NumDiscussions: 1)]
        };
        var sut = CreateHandler(forumGateway: forumGateway);

        var result = await sut.Handle(
            new ReadForumQuery(
                "usuario-42", "CURSO-1", "21", 1, 25, "id", "ASC", IncludePosts: false, PostsPerDiscussion: 20),
            CancellationToken.None);

        Assert.NotNull(result);
        var discussion = Assert.Single(result!.Discussions);
        Assert.Equal("701", discussion.DiscussionId);
        Assert.Equal("Aviso único", discussion.Subject);
        Assert.Empty(discussion.Posts);
        Assert.Equal(0, forumGateway.PostReads);
    }

    [Fact]
    [Trait("Category", "ManualPerformance")]
    public async Task Benchmark_sintetico_de_duracao_para_leitura_de_posts()
    {
        const int warmupRuns = 2;
        const int measuredRuns = 20;
        const int uniformDelayMs = 5;
        const int slowFirstDelayMs = 80;
        var variant = Environment.GetEnvironmentVariable("MCP_BENCH_VARIANT") ?? "unspecified";

        output.WriteLine("variant|discussions|concurrency|delay_shape|warmup|samples|gateway_calls|http_attempts|retries|failed|coverage|p50_ms|p95_ms");

        foreach (var discussionCount in new[] { 0, 1, 20, 25 })
        foreach (var concurrency in new[] { 1, 2, 4 })
        foreach (var slowFirst in new[] { false, true })
        {
            var samples = new List<double>(measuredRuns);
            var maxObservedConcurrency = 0;
            for (var run = -warmupRuns; run < measuredRuns; run++)
            {
                var discussions = Enumerable.Range(1, discussionCount)
                    .Select(index => Discussion($"discussion-{index}", $"post-{index}", $"Topic {index}"))
                    .ToArray();
                var forumGateway = new FakeForumGateway
                {
                    Discussions = discussions,
                    Posts = discussions.Select(discussion => Post(
                        discussion.FirstPostId,
                        discussion.DiscussionId,
                        discussion.Subject,
                        $"Synthetic body {discussion.DiscussionId}")).ToArray(),
                    BeforePostRead = async (discussionId, cancellationToken) =>
                    {
                        var delay = slowFirst && discussionId == "discussion-1"
                            ? slowFirstDelayMs
                            : uniformDelayMs;
                        await Task.Delay(delay, cancellationToken);
                    }
                };
                var sut = CreateHandler(
                    forumGateway: forumGateway,
                    maxConcurrentPosts: concurrency,
                    maxConcurrentPerConnection: concurrency);
                var stopwatch = Stopwatch.StartNew();
                var result = await sut.Handle(
                    new ReadForumQuery(
                        "synthetic-user",
                        "CURSO-1",
                        "21",
                        Page: 1,
                        PageSize: 25,
                        SortBy: "id",
                        SortDirection: "ASC",
                        IncludePosts: true,
                        PostsPerDiscussion: 1),
                    CancellationToken.None);
                stopwatch.Stop();

                Assert.NotNull(result);
                Assert.Equal(discussionCount, forumGateway.PostReads);
                Assert.Equal(discussionCount, result!.Discussions.Count);
                Assert.Equal(discussionCount, result.Discussions.Sum(discussion => discussion.PostsReturned));
                Assert.All(result.Discussions, discussion => Assert.Single(discussion.Posts));
                maxObservedConcurrency = Math.Max(maxObservedConcurrency, forumGateway.MaxConcurrentPostReads);
                if (run >= 0)
                {
                    samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
            }

            samples.Sort();
            var p50 = Percentile(samples, 0.50);
            var p95 = Percentile(samples, 0.95);
            output.WriteLine(
                $"{variant}|{discussionCount}|{concurrency}|{(slowFirst ? $"one-slow-{slowFirstDelayMs}ms" : $"uniform-{uniformDelayMs}ms")}|{warmupRuns}|{measuredRuns}|{discussionCount}|not-exercised|not-exercised|0|{discussionCount}/{discussionCount}|{p50.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|{p95.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
            output.WriteLine($"observed_max_concurrency|{variant}|{discussionCount}|{concurrency}|{(slowFirst ? "one-slow" : "uniform")}|{maxObservedConcurrency}");
        }

        static double Percentile(IReadOnlyList<double> sortedSamples, double percentile)
        {
            var rank = Math.Max(1, (int)Math.Ceiling(percentile * sortedSamples.Count));
            return sortedSamples[rank - 1];
        }
    }

    private static ReadForumQueryHandler CreateHandler(
        FakeContentsGateway? contentsGateway = null,
        FakeForumGateway? forumGateway = null,
        int maxConcurrentPosts = 1,
        int maxConcurrentPerConnection = 4)
    {
        return new ReadForumQueryHandler(
            new FakeCoursesGateway(),
            contentsGateway ?? new FakeContentsGateway(),
            forumGateway ?? new FakeForumGateway(),
            Options.Create(new MoodleReadLimitsOptions
            {
                MaxConcurrentForumPostReads = maxConcurrentPosts,
                MaxConcurrentPerConnection = maxConcurrentPerConnection
            }));
    }

    private static ForumDiscussionSummary Discussion(string discussionId, string firstPostId, string subject)
    {
        return new ForumDiscussionSummary(
            discussionId,
            firstPostId,
            subject,
            subject,
            "Mensagem inicial",
            "101",
            "Ana Souza",
            new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero),
            ReplyCount: 1,
            UnreadCount: 0,
            Pinned: false,
            Locked: false,
            CanReply: true,
            PostsReturned: 0,
            PostsTotal: 0,
            Posts: []);
    }

    private static ForumPostSummary Post(string postId, string discussionId, string subject, string text)
    {
        return new ForumPostSummary(
            postId,
            discussionId,
            ParentPostId: null,
            UserId: "101",
            UserFullName: "Ana Souza",
            subject,
            text,
            new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 10, 5, 0, TimeSpan.Zero),
            Deleted: false,
            CanReply: true,
            PostRead: true,
            IsPrivateReply: false,
            Children: [],
            Attachments: []);
    }

    private sealed class FakeCoursesGateway : IMoodleCoursesGateway
    {
        public Task<PagedCourses> GetMyCoursesAsync(string userExternalId, int limit, int page, CancellationToken cancellationToken) { throw new NotSupportedException(); }

        public Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(
            string userExternalId,
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<CourseSummary?> GetMyCourseAsync(
            string userExternalId,
            string courseId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<CourseSummary?>(new CourseSummary(
                "123",
                "ID-123",
                "CURSO-1",
                "Curso 1",
                "Curso 1",
                10,
                "Categoria",
                null,
                null,
                true,
                null,
                null,
                null,
                null,
                null,
                null));
        }
    }

    private sealed class FakeContentsGateway : IMoodleCourseContentsGateway
    {
        public bool ReturnWithoutForum { get; init; }

        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId,
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles,
            CancellationToken cancellationToken)
        {
            var modules = ReturnWithoutForum
                ? Array.Empty<CourseModuleSummary>()
                :
                [
                    new CourseModuleSummary(
                        "21",
                        "701",
                        "forum",
                        "Forum Geral",
                        "https://moodle.example/mod/forum/view.php?id=21",
                        true,
                        true,
                        "Forum do curso",
                        null,
                        [],
                        [])
                ];

            return Task.FromResult(new CourseContentsSummary(
                courseId,
                moduleTypes.ToArray(),
                includeHidden,
                onlyWithFiles,
                [new CourseSectionSummary("1", 1, "Topico 1", null, true, modules.Length, modules.Length == 0, modules)]));
        }
    }

    private sealed class FakeForumGateway : IMoodleForumGateway
    {
        private int _activePostReads;
        private int _maxConcurrentPostReads;

        public IReadOnlyList<ForumDiscussionSummary> Discussions { get; init; } = [];

        public IReadOnlyList<ForumPostSummary> Posts { get; init; } = [];

        public IReadOnlyList<ForumInfo> Forums { get; init; } = [];

        public bool WasDiscussionsCalled { get; private set; }

        public string LastForumId { get; private set; } = string.Empty;

        public int LastPageSize { get; private set; }

        public List<string> PostDiscussionIds { get; } = [];

        public Func<string, CancellationToken, Task>? BeforePostRead { get; init; }

        public string? FailedDiscussionId { get; init; }

        public int PostReads => PostDiscussionIds.Count;

        public int MaxConcurrentPostReads => _maxConcurrentPostReads;

        public Task<IReadOnlyList<ForumDiscussionSummary>> GetForumDiscussionsPaginatedAsync(
            string userExternalId,
            string forumId,
            string sortBy,
            string sortDirection,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            WasDiscussionsCalled = true;
            LastForumId = forumId;
            LastPageSize = pageSize;
            return Task.FromResult(Discussions);
        }

        public async Task<IReadOnlyList<ForumPostSummary>> GetDiscussionPostsAsync(
            string userExternalId,
            string discussionId,
            string sortBy,
            string sortDirection,
            CancellationToken cancellationToken)
        {
            lock (PostDiscussionIds)
            {
                PostDiscussionIds.Add(discussionId);
            }
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
                    throw new InvalidOperationException("Simulated post read failure.");
                }
                return Posts.Where(post => post.DiscussionId == discussionId).ToArray();
            }
            finally
            {
                Interlocked.Decrement(ref _activePostReads);
            }
        }

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

        public Task<ForumWriteResult> AddDiscussionAsync(
            string userExternalId,
            string forumId,
            string subject,
            string messageHtml,
            int groupId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ForumWriteResult> AddDiscussionPostAsync(
            string userExternalId,
            string postId,
            string subject,
            string messageHtml,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ForumInfo>> GetForumsByCoursesAsync(
            string userExternalId,
            string courseId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Forums);
        }
    }
}
