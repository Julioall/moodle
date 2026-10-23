using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.Forums;
using Moodle.Application.PendingActions;
using Moodle.Application.Tools;
using Moodle.Domain;

namespace Moodle.Application.Tests.Forums;

public sealed class PostForumCommandHandlerTests
{
    [Fact]
    public async Task Deve_criar_previa_para_nova_discussao()
    {
        var pendingActions = new FakePendingActionService();
        var sut = CreatePreviewHandler(pendingActions: pendingActions);

        var result = await sut.Handle(
            new CreateForumPostPreviewCommand(
                "777",
                "CURSO-1",
                "21",
                "Aviso da semana",
                "<p>Mensagem aos estudantes.</p>",
                DiscussionId: null,
                ReplyToPostId: null,
                GroupId: 0),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("discussion", result!.Mode);
        Assert.Equal("701", result.ForumId);
        Assert.Equal("21", result.ForumModuleId);
        Assert.Equal("Aviso da semana", result.Subject);
        Assert.Contains("NOVA DISCUSSAO", result.ConfirmationText, StringComparison.Ordinal);
        Assert.NotNull(pendingActions.LastPayload);
        Assert.Equal("discussion", pendingActions.LastPayload!.Mode);
        Assert.Equal("701", pendingActions.LastPayload.ForumId);
    }

    [Fact]
    public async Task Deve_criar_previa_de_resposta_usando_post_inicial_quando_post_nao_for_informado()
    {
        var forumGateway = new FakeForumGateway
        {
            Discussions = [Discussion("9001", "8001", "Avisos")],
            Posts = [Post("8001", "9001", parentPostId: null)]
        };
        var sut = CreatePreviewHandler(forumGateway: forumGateway);

        var result = await sut.Handle(
            new CreateForumPostPreviewCommand(
                "777",
                "CURSO-1",
                "701",
                "Re: Avisos",
                "<p>Recebido.</p>",
                DiscussionId: "9001",
                ReplyToPostId: null,
                GroupId: 0),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("reply", result!.Mode);
        Assert.Equal("9001", result.DiscussionId);
        Assert.Equal("8001", result.ReplyToPostId);
        Assert.Contains("post inicial 8001", result.Warnings[0], StringComparison.Ordinal);
        Assert.Equal(["9001"], forumGateway.PostDiscussionIds);
    }

    [Fact]
    public async Task Deve_confirmar_nova_discussao_chamando_mod_forum_add_discussion()
    {
        var fixture = new ConfirmFixture();
        var action = fixture.CreatePendingAction(new ForumPostPendingPayload(
            "123",
            "701",
            "21",
            "Forum Geral",
            "discussion",
            DiscussionId: null,
            ReplyToPostId: null,
            "Aviso",
            "<p>Mensagem.</p>",
            GroupId: 0));
        fixture.PendingActions.Actions.Add(action);
        var sut = fixture.CreateHandler();

        var result = await sut.Handle(
            new ConfirmForumPostCommand(action.Id, action.ConfirmationText),
            CancellationToken.None);

        Assert.Equal("confirmed", result.Status);
        Assert.Equal("9901", result.DiscussionId);
        Assert.Null(result.PostId);
        Assert.Equal("mod_forum_add_discussion", result.MoodleFunction);
        Assert.Equal("777", fixture.ForumGateway.LastUserExternalId);
        Assert.Equal("701", fixture.ForumGateway.LastForumId);
        Assert.Equal("Aviso", fixture.ForumGateway.LastSubject);
        Assert.Equal("<p>Mensagem.</p>", fixture.ForumGateway.LastMessageHtml);
        Assert.Equal("moodle.write.forums", fixture.Confirmations.LastRequiredScope);
        var audit = Assert.Single(fixture.AuditLogs.Logs);
        Assert.Equal("forum_post_succeeded", audit.Status);
        Assert.Equal("mod_forum_add_discussion", audit.MoodleFunction);
    }

    [Fact]
    public async Task Deve_confirmar_resposta_chamando_mod_forum_add_discussion_post()
    {
        var fixture = new ConfirmFixture();
        var action = fixture.CreatePendingAction(new ForumPostPendingPayload(
            "123",
            "701",
            "21",
            "Forum Geral",
            "reply",
            "9001",
            "8001",
            "Re: Aviso",
            "<p>Resposta.</p>",
            GroupId: 0));
        fixture.PendingActions.Actions.Add(action);
        var sut = fixture.CreateHandler();

        var result = await sut.Handle(
            new ConfirmForumPostCommand(action.Id, action.ConfirmationText),
            CancellationToken.None);

        Assert.Equal("confirmed", result.Status);
        Assert.Equal("9001", result.DiscussionId);
        Assert.Equal("8801", result.PostId);
        Assert.Equal("mod_forum_add_discussion_post", result.MoodleFunction);
        Assert.Equal("8001", fixture.ForumGateway.LastReplyToPostId);
        var audit = Assert.Single(fixture.AuditLogs.Logs);
        Assert.Equal("forum_post_succeeded", audit.Status);
        Assert.Equal("mod_forum_add_discussion_post", audit.MoodleFunction);
    }

    private static CreateForumPostPreviewCommandHandler CreatePreviewHandler(
        FakePendingActionService? pendingActions = null,
        FakeForumGateway? forumGateway = null)
    {
        return new CreateForumPostPreviewCommandHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            forumGateway ?? new FakeForumGateway(),
            pendingActions ?? new FakePendingActionService());
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
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(-1),
            ReplyCount: 1,
            UnreadCount: 0,
            Pinned: false,
            Locked: false,
            CanReply: true,
            PostsReturned: 0,
            PostsTotal: 0,
            Posts: []);
    }

    private static ForumPostSummary Post(string postId, string discussionId, string? parentPostId)
    {
        return new ForumPostSummary(
            postId,
            discussionId,
            parentPostId,
            UserId: "101",
            UserFullName: "Ana Souza",
            "Avisos",
            "Mensagem",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddHours(-1),
            Deleted: false,
            CanReply: true,
            PostRead: true,
            IsPrivateReply: false,
            Children: [],
            Attachments: []);
    }

    private sealed class ConfirmFixture
    {
        public FakePendingActionRepository PendingActions { get; } = new();
        public FakeActionConfirmationService Confirmations { get; } = new();
        public FakeForumGateway ForumGateway { get; } = new();
        public FakeAuditLogRepository AuditLogs { get; } = new();

        public ConfirmForumPostCommandHandler CreateHandler()
        {
            return new ConfirmForumPostCommandHandler(
                PendingActions,
                Confirmations,
                ForumGateway,
                AuditLogs);
        }

        public PendingMoodleAction CreatePendingAction(ForumPostPendingPayload payload)
        {
            return new PendingMoodleAction
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000901"),
                ToolName = "criar_previa_post_forum",
                RiskLevel = ToolRiskLevel.HumanConfirmedWrite,
                CreatedBySubject = "teacher-1",
                CreatedByMoodleUserId = 777,
                CourseId = 123,
                PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                PreviewJson = "{}",
                ConfirmationText = "CONFIRMO POST FORUM",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                IdempotencyKey = "idem-1",
                CorrelationId = "audit-1"
            };
        }
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
        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId,
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles,
            CancellationToken cancellationToken)
        {
            var module = new CourseModuleSummary(
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
                []);

            return Task.FromResult(new CourseContentsSummary(
                courseId,
                moduleTypes.ToArray(),
                includeHidden,
                onlyWithFiles,
                [new CourseSectionSummary("1", 1, "Topico 1", null, true, 1, false, [module])]));
        }
    }

    private sealed class FakeForumGateway : IMoodleForumGateway
    {
        public IReadOnlyList<ForumDiscussionSummary> Discussions { get; init; } = [];
        public IReadOnlyList<ForumPostSummary> Posts { get; init; } = [];
        public List<string> PostDiscussionIds { get; } = [];
        public string? LastUserExternalId { get; private set; }
        public string? LastForumId { get; private set; }
        public string? LastReplyToPostId { get; private set; }
        public string? LastSubject { get; private set; }
        public string? LastMessageHtml { get; private set; }

        public Task<IReadOnlyList<ForumDiscussionSummary>> GetForumDiscussionsPaginatedAsync(
            string userExternalId,
            string forumId,
            string sortBy,
            string sortDirection,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            LastForumId = forumId;
            return Task.FromResult(Discussions);
        }

        public Task<IReadOnlyList<ForumPostSummary>> GetDiscussionPostsAsync(
            string userExternalId,
            string discussionId,
            string sortBy,
            string sortDirection,
            CancellationToken cancellationToken)
        {
            PostDiscussionIds.Add(discussionId);
            return Task.FromResult<IReadOnlyList<ForumPostSummary>>(
                Posts.Where(post => post.DiscussionId == discussionId).ToArray());
        }

        public Task<ForumWriteResult> AddDiscussionAsync(
            string userExternalId,
            string forumId,
            string subject,
            string messageHtml,
            int groupId,
            CancellationToken cancellationToken)
        {
            LastUserExternalId = userExternalId;
            LastForumId = forumId;
            LastSubject = subject;
            LastMessageHtml = messageHtml;
            return Task.FromResult(new ForumWriteResult(
                Success: true,
                "mod_forum_add_discussion",
                "ok",
                DiscussionId: "9901",
                PostId: null,
                Warnings: []));
        }

        public Task<ForumWriteResult> AddDiscussionPostAsync(
            string userExternalId,
            string postId,
            string subject,
            string messageHtml,
            CancellationToken cancellationToken)
        {
            LastUserExternalId = userExternalId;
            LastReplyToPostId = postId;
            LastSubject = subject;
            LastMessageHtml = messageHtml;
            return Task.FromResult(new ForumWriteResult(
                Success: true,
                "mod_forum_add_discussion_post",
                "ok",
                DiscussionId: null,
                PostId: "8801",
                Warnings: []));
        }

        public Task<IReadOnlyList<ForumInfo>> GetForumsByCoursesAsync(
            string userExternalId,
            string courseId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ForumInfo>>([]);
        }
    }

    private sealed class FakePendingActionService : IPendingActionService
    {
        public Guid PendingActionId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000900");
        public ForumPostPendingPayload? LastPayload { get; private set; }

        public Task<PendingActionResponse> CreatePendingActionAsync(
            string toolName,
            ToolRiskLevel riskLevel,
            object payload,
            object preview,
            string confirmationText,
            TimeSpan expiresIn,
            long? courseId,
            CancellationToken cancellationToken)
        {
            LastPayload = Assert.IsType<ForumPostPendingPayload>(payload);
            return Task.FromResult(new PendingActionResponse(
                "pending_confirmation",
                PendingActionId,
                toolName,
                riskLevel,
                preview,
                confirmationText,
                DateTimeOffset.UtcNow.Add(expiresIn)));
        }
    }

    private sealed class FakePendingActionRepository : IPendingMoodleActionRepository
    {
        public List<PendingMoodleAction> Actions { get; } = [];

        public Task AddAsync(PendingMoodleAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task<PendingMoodleAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Actions.SingleOrDefault(action => action.Id == id));
        }

        public Task<PendingActionConfirmationClaimResult> TryConfirmWithAuditAsync(Guid id, string confirmedBySubject, DateTimeOffset confirmedAt, MoodleAuditLog confirmationAudit, CancellationToken cancellationToken)
        {
            var action = Actions.SingleOrDefault(candidate => candidate.Id == id);
            if (action?.Status != PendingActionStatus.PendingConfirmation)
                return Task.FromResult(new PendingActionConfirmationClaimResult(false, action?.Status ?? PendingActionStatus.Expired, action?.ConfirmedAt));
            action.Confirm(confirmedBySubject, confirmedAt);
            return Task.FromResult(new PendingActionConfirmationClaimResult(true, action.Status, action.ConfirmedAt));
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActionConfirmationService : IActionConfirmationService
    {
        public string? LastRequiredScope { get; private set; }

        public Task<ActionConfirmationResponse> ConfirmAsync(
            Guid pendingActionId,
            string? confirmationText,
            string? requiredScope,
            CancellationToken cancellationToken)
        {
            LastRequiredScope = requiredScope;
            return Task.FromResult(new ActionConfirmationResponse(
                "confirmed",
                pendingActionId,
                "criar_previa_post_forum",
                ToolRiskLevel.HumanConfirmedWrite,
                DateTimeOffset.UtcNow,
                "audit-1"));
        }
    }

    private sealed class FakeAuditLogRepository : IMoodleAuditLogRepository
    {
        public List<MoodleAuditLog> Logs { get; } = [];

        public Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(
            string correlationId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);
        }

        public Task<int> CountByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(
            Guid batchJobId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);
        }

        public Task<int> CountByBatchJobIdAsync(Guid batchJobId, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
