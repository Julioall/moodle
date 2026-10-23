using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Auditing;
using Moodle.Application.MoodleApi;
using Moodle.Application.PendingActions;
using Moodle.Application.Tools;
using Moodle.Domain;

namespace Moodle.Application.Forums;

public sealed record CreateForumPostPreviewCommand(
    string UserExternalId,
    string CourseId,
    string ForumId,
    string Subject,
    string MessageHtml,
    string? DiscussionId,
    string? ReplyToPostId,
    int GroupId) : IRequest<CreateForumPostPreviewResult?>;

public sealed record CreateForumPostPreviewResult(
    [property: JsonPropertyName("pendingActionId")] Guid PendingActionId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("forumId")] string ForumId,
    [property: JsonPropertyName("forumModuleId")] string ForumModuleId,
    [property: JsonPropertyName("forumName")] string ForumName,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("discussionId")] string? DiscussionId,
    [property: JsonPropertyName("replyToPostId")] string? ReplyToPostId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("messageHtml")] string MessageHtml,
    [property: JsonPropertyName("groupId")] int GroupId,
    [property: JsonPropertyName("confirmationText")] string ConfirmationText,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

public sealed record ConfirmForumPostCommand(
    Guid PendingActionId,
    string ConfirmationText) : IRequest<ConfirmForumPostResult>;

public sealed record ConfirmForumPostResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("pendingActionId")] Guid PendingActionId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("forumId")] string ForumId,
    [property: JsonPropertyName("forumModuleId")] string ForumModuleId,
    [property: JsonPropertyName("forumName")] string ForumName,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("discussionId")] string? DiscussionId,
    [property: JsonPropertyName("postId")] string? PostId,
    [property: JsonPropertyName("moodleFunction")] string? MoodleFunction,
    [property: JsonPropertyName("auditId")] string? AuditId,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

public sealed record ForumPostPendingPayload(
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("forumId")] string ForumId,
    [property: JsonPropertyName("forumModuleId")] string ForumModuleId,
    [property: JsonPropertyName("forumName")] string ForumName,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("discussionId")] string? DiscussionId,
    [property: JsonPropertyName("replyToPostId")] string? ReplyToPostId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("messageHtml")] string MessageHtml,
    [property: JsonPropertyName("groupId")] int GroupId);

public sealed class CreateForumPostPreviewCommandHandler(
    IMoodleCoursesGateway coursesGateway,
    IMoodleCourseContentsGateway contentsGateway,
    IMoodleForumGateway forumGateway,
    IPendingActionService pendingActions,
    IOptions<PendingActionOptions>? pendingActionOptions = null)
    : IRequestHandler<CreateForumPostPreviewCommand, CreateForumPostPreviewResult?>
{
    private const string ToolName = "criar_previa_post_forum";
    private const int MaxSubjectLength = 255;
    private const int MaxMessageLength = 20000;
    private readonly TimeSpan pendingActionExpiration =
        (pendingActionOptions?.Value ?? new PendingActionOptions())
            .ResolveExpiration(PendingActionTtlClass.Volatile);
    private static readonly string[] ForumModuleTypes = ["forum"];

    public async Task<CreateForumPostPreviewResult?> Handle(
        CreateForumPostPreviewCommand request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var course = await coursesGateway.GetMyCourseAsync(
            request.UserExternalId.Trim(),
            request.CourseId.Trim(),
            cancellationToken);
        if (course is null)
        {
            return null;
        }

        var forum = await ResolveForumAsync(
            request.UserExternalId.Trim(),
            course.CourseId,
            request.ForumId.Trim(),
            cancellationToken);
        if (forum is null)
        {
            return null;
        }

        var subject = NormalizeSubject(request.Subject);
        var messageHtml = NormalizeMessage(request.MessageHtml);
        var forumInstanceId = forum.InstanceId ?? forum.ActivityId;
        var mode = IsReply(request) ? "reply" : "discussion";
        string? discussionId = null;
        string? replyToPostId = null;
        var warnings = new List<string>();

        if (mode == "reply")
        {
            discussionId = request.DiscussionId?.Trim();
            var discussion = await ResolveDiscussionAsync(
                request.UserExternalId.Trim(),
                forumInstanceId,
                discussionId!,
                cancellationToken);
            if (discussion is null)
            {
                return null;
            }

            if (discussion.Locked == true || discussion.CanReply == false)
            {
                throw new InvalidOperationException("A discussao informada nao permite resposta pelo usuario atual.");
            }

            var posts = await forumGateway.GetDiscussionPostsAsync(
                request.UserExternalId.Trim(),
                discussionId!,
                "created",
                "ASC",
                cancellationToken);
            var replyTarget = ResolveReplyTarget(posts, request.ReplyToPostId);
            if (replyTarget is null)
            {
                return null;
            }

            if (replyTarget.Deleted == true || replyTarget.CanReply == false)
            {
                throw new InvalidOperationException("O post informado nao permite resposta pelo usuario atual.");
            }

            replyToPostId = replyTarget.PostId;
            if (string.IsNullOrWhiteSpace(request.ReplyToPostId))
            {
                warnings.Add($"Como replyToPostId nao foi informado, a resposta sera enviada ao post inicial {replyToPostId} da discussao {discussionId}.");
            }
        }

        var payload = new ForumPostPendingPayload(
            course.CourseId,
            forumInstanceId,
            forum.ActivityId,
            forum.Name,
            mode,
            discussionId,
            replyToPostId,
            subject,
            messageHtml,
            NormalizeGroupId(request.GroupId));
        var confirmationText = BuildConfirmationText(payload);
        var pending = await pendingActions.CreatePendingActionAsync(
            ToolName,
            ToolRiskLevel.HumanConfirmedWrite,
            payload,
            new
            {
                payload.CourseId,
                payload.ForumId,
                payload.ForumModuleId,
                payload.ForumName,
                payload.Mode,
                payload.DiscussionId,
                payload.ReplyToPostId,
                payload.Subject,
                payload.MessageHtml,
                payload.GroupId
            },
            confirmationText,
            pendingActionExpiration,
            ParseCourseId(course.CourseId),
            cancellationToken);

        return new CreateForumPostPreviewResult(
            pending.PendingActionId,
            payload.CourseId,
            payload.ForumId,
            payload.ForumModuleId,
            payload.ForumName,
            payload.Mode,
            payload.DiscussionId,
            payload.ReplyToPostId,
            payload.Subject,
            payload.MessageHtml,
            payload.GroupId,
            pending.ConfirmationText,
            pending.ExpiresAt,
            warnings);
    }

    private async Task<CourseActivitySummary?> ResolveForumAsync(
        string userExternalId,
        string courseId,
        string forumId,
        CancellationToken cancellationToken)
    {
        var contents = await contentsGateway.GetCourseContentsAsync(
            userExternalId,
            courseId,
            ForumModuleTypes,
            includeHidden: true,
            onlyWithFiles: false,
            cancellationToken);

        var module = contents.Sections
            .SelectMany(section => section.Modules)
            .FirstOrDefault(activity =>
                string.Equals(activity.ModuleType, "forum", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(activity.ModuleId, forumId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(activity.InstanceId, forumId, StringComparison.OrdinalIgnoreCase)));

        return module is null
            ? null
            : Moodle.Application.Activities.ListCourseActivitiesQueryHandler.ToActivity(module);
    }

    private async Task<ForumDiscussionSummary?> ResolveDiscussionAsync(
        string userExternalId,
        string forumId,
        string discussionId,
        CancellationToken cancellationToken)
    {
        for (var page = 1; page <= 20; page++)
        {
            var discussions = await forumGateway.GetForumDiscussionsPaginatedAsync(
                userExternalId,
                forumId,
                "timemodified",
                "DESC",
                page,
                100,
                cancellationToken);
            var discussion = discussions.FirstOrDefault(item =>
                string.Equals(item.DiscussionId, discussionId, StringComparison.OrdinalIgnoreCase));
            if (discussion is not null)
            {
                return discussion;
            }

            if (discussions.Count < 100)
            {
                return null;
            }
        }

        return null;
    }

    private static ForumPostSummary? ResolveReplyTarget(
        IReadOnlyList<ForumPostSummary> posts,
        string? replyToPostId)
    {
        if (!string.IsNullOrWhiteSpace(replyToPostId))
        {
            return posts.FirstOrDefault(post =>
                string.Equals(post.PostId, replyToPostId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return posts.FirstOrDefault(post => string.IsNullOrWhiteSpace(post.ParentPostId)) ??
            posts.OrderBy(post => post.CreatedAt).FirstOrDefault();
    }

    private static void ValidateRequest(CreateForumPostPreviewCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.UserExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", nameof(request.UserExternalId));
        }

        if (string.IsNullOrWhiteSpace(request.CourseId))
        {
            throw new ArgumentException("O identificador do curso e obrigatorio.", nameof(request.CourseId));
        }

        if (string.IsNullOrWhiteSpace(request.ForumId))
        {
            throw new ArgumentException("O identificador do forum e obrigatorio.", nameof(request.ForumId));
        }

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            throw new ArgumentException("O assunto do post e obrigatorio.", nameof(request.Subject));
        }

        if (string.IsNullOrWhiteSpace(request.MessageHtml))
        {
            throw new ArgumentException("A mensagem do post e obrigatoria.", nameof(request.MessageHtml));
        }

        if (!string.IsNullOrWhiteSpace(request.ReplyToPostId) &&
            string.IsNullOrWhiteSpace(request.DiscussionId))
        {
            throw new ArgumentException("Para responder a um post, informe tambem o discussionId da discussao.", nameof(request.DiscussionId));
        }
    }

    private static bool IsReply(CreateForumPostPreviewCommand request)
    {
        return !string.IsNullOrWhiteSpace(request.DiscussionId) ||
            !string.IsNullOrWhiteSpace(request.ReplyToPostId);
    }

    private static string NormalizeSubject(string subject)
    {
        var normalized = subject.Trim();
        return normalized.Length <= MaxSubjectLength
            ? normalized
            : normalized[..MaxSubjectLength];
    }

    private static string NormalizeMessage(string messageHtml)
    {
        var normalized = messageHtml.Trim();
        return normalized.Length <= MaxMessageLength
            ? normalized
            : normalized[..MaxMessageLength];
    }

    private static int NormalizeGroupId(int groupId)
    {
        return Math.Max(0, groupId);
    }

    private static long? ParseCourseId(string courseId)
    {
        return long.TryParse(courseId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    private static string BuildConfirmationText(ForumPostPendingPayload payload)
    {
        return payload.Mode == "reply"
            ? $"CONFIRMO A RESPOSTA NA DISCUSSAO {payload.DiscussionId} DO FORUM {payload.ForumId} DO CURSO {payload.CourseId} COM ESCOPO FORUM_POST"
            : $"CONFIRMO A PUBLICACAO DE NOVA DISCUSSAO NO FORUM {payload.ForumId} DO CURSO {payload.CourseId} COM ESCOPO FORUM_POST";
    }
}

public sealed class ConfirmForumPostCommandHandler(
    IPendingMoodleActionRepository pendingActions,
    IActionConfirmationService confirmations,
    IMoodleForumGateway forumGateway,
    IMoodleAuditLogRepository auditLogs)
    : IRequestHandler<ConfirmForumPostCommand, ConfirmForumPostResult>
{
    private const string CommitToolName = "confirmar_post_forum_moodle";
    private const string AddDiscussionFunction = "mod_forum_add_discussion";
    private const string AddDiscussionPostFunction = "mod_forum_add_discussion_post";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ConfirmForumPostResult> Handle(
        ConfirmForumPostCommand request,
        CancellationToken cancellationToken)
    {
        var action = await pendingActions.GetByIdAsync(request.PendingActionId, cancellationToken)
            ?? throw new MoodleApiException(MoodleErrorContract.PendingActionNotFound, "Acao pendente nao encontrada.");
        var payload = JsonSerializer.Deserialize<ForumPostPendingPayload>(action.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Payload de publicacao em forum invalido.");
        var confirmation = await confirmations.ConfirmAsync(
            request.PendingActionId,
            request.ConfirmationText,
            requiredScope: "moodle.write.forums",
            cancellationToken);

        if (confirmation.Status == "already_confirmed")
        {
            return new ConfirmForumPostResult(
                "already_confirmed",
                request.PendingActionId,
                payload.CourseId,
                payload.ForumId,
                payload.ForumModuleId,
                payload.ForumName,
                payload.Mode,
                payload.DiscussionId,
                PostId: null,
                MoodleFunction: null,
                confirmation.AuditId,
                ["Esta acao ja estava confirmada e nao foi executada novamente para evitar publicacao duplicada no forum."]);
        }

        var userExternalId = action.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ??
            action.CreatedBySubject;
        try
        {
            var writeResult = payload.Mode == "reply"
                ? await forumGateway.AddDiscussionPostAsync(
                    userExternalId,
                    payload.ReplyToPostId ?? throw new InvalidOperationException("Payload de resposta sem post de destino."),
                    payload.Subject,
                    payload.MessageHtml,
                    cancellationToken)
                : await forumGateway.AddDiscussionAsync(
                    userExternalId,
                    payload.ForumId,
                    payload.Subject,
                    payload.MessageHtml,
                    payload.GroupId,
                    cancellationToken);

            await RecordForumPostAuditAsync(
                action,
                payload,
                "forum_post_succeeded",
                writeResult,
                errorCode: null,
                errorMessage: null,
                cancellationToken);
            await auditLogs.SaveChangesAsync(cancellationToken);

            return new ConfirmForumPostResult(
                confirmation.Status,
                request.PendingActionId,
                payload.CourseId,
                payload.ForumId,
                payload.ForumModuleId,
                payload.ForumName,
                payload.Mode,
                payload.Mode == "reply" ? payload.DiscussionId : writeResult.DiscussionId,
                payload.Mode == "reply" ? writeResult.PostId : null,
                writeResult.MoodleFunction,
                confirmation.AuditId,
                writeResult.Warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var executionUnknown = ex is HttpRequestException ||
                ex is MoodleApiException moodleError &&
                MoodleErrorContract.NormalizeCode(moodleError.ErrorCode) is MoodleErrorContract.NetworkError or MoodleErrorContract.RequestTimeout;
            if (executionUnknown)
            {
                action.MarkExecutionUnknown();
                await pendingActions.SaveChangesAsync(cancellationToken);
            }
            await RecordForumPostAuditAsync(
                action,
                payload,
                executionUnknown ? "forum_post_execution_unknown" : "forum_post_failed",
                new { exceptionType = ex.GetType().Name },
                ex.GetType().Name,
                ex.Message,
                cancellationToken);
            await auditLogs.SaveChangesAsync(cancellationToken);

            return new ConfirmForumPostResult(
                executionUnknown ? "execution_unknown" : "failed",
                request.PendingActionId,
                payload.CourseId,
                payload.ForumId,
                payload.ForumModuleId,
                payload.ForumName,
                payload.Mode,
                payload.DiscussionId,
                PostId: null,
                payload.Mode == "reply" ? AddDiscussionPostFunction : AddDiscussionFunction,
                confirmation.AuditId,
                [ex.Message]);
        }
    }

    private Task RecordForumPostAuditAsync(
        PendingMoodleAction action,
        ForumPostPendingPayload payload,
        string status,
        object responseSummary,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        return auditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = action.CorrelationId,
            ToolName = CommitToolName,
            RiskLevel = ToolRiskLevel.HumanConfirmedWrite,
            ActorSubject = action.CreatedBySubject,
            ActorEmail = action.CreatedByEmail,
            ActorMoodleUserId = action.CreatedByMoodleUserId,
            CourseId = action.CourseId,
            MoodleFunction = payload.Mode == "reply" ? AddDiscussionPostFunction : AddDiscussionFunction,
            RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(payload),
            ResponseSummaryJson = AuditPayloadSanitizer.SerializeSanitized(responseSummary),
            Status = status,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        }, cancellationToken);
    }
}
