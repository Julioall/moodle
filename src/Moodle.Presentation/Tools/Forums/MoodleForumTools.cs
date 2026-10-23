using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Forums;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Domain;

namespace Moodle.Presentation.Tools;

[McpServerToolType]
public sealed class MoodleForumTools(
    IMediator mediator,
    IMoodleConnectionSelection moodleSelection,
    IMoodleUserResolver moodleUserResolver)
{
    private static readonly string[] AllowedSortBy = ["id", "timemodified", "timestart", "timeend"];

    [McpServerTool(
        Name = "read_forum",
        Title = "Read Forum",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ReadForumResponse>))]
    [Description("Le discussoes e posts de um forum Moodle usando a funcao de discussoes anunciada pela conexao e mod_forum_get_discussion_posts. Se o usuario citar uma instituicao ou conexao, consulte moodle_list_connections e informe o alias correspondente em moodleAlias.")]
    public Task<CallToolResult> LerForumAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Identificador do forum. Pode ser cmid ou instance id.")]
        string forumId,
        [Description("Pagina de discussoes, iniciando em 1.")]
        int pagina = 1,
        [Description("Tamanho da pagina de discussoes, de 1 a 25.")]
        int tamanhoPagina = 10,
        [Description("Quando true, carrega posts de cada discussao via mod_forum_get_discussion_posts.")]
        bool incluirPosts = true,
        [Description("Quantidade maxima de posts retornados por discussao, de 1 a 100.")]
        int postsPorDiscussao = 50,
        [Description("Campo de ordenacao das discussoes: id, timemodified, timestart ou timeend.")]
        string ordenarPor = "timemodified",
        [Description("Direcao da ordenacao das discussoes: ASC ou DESC.")]
        string ordem = "DESC",
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return ReadForumCoreAsync(
            courseId,
            forumId,
            pagina,
            tamanhoPagina,
            incluirPosts,
            postsPorDiscussao,
            ordenarPor,
            ordem,
            moodleAlias,
            cancellationToken,
            language: "pt");
    }

    [McpServerTool(
        Name = "create_forum_post_preview",
        Title = "Create Forum Post Preview",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<CreateForumPostPreviewResult>))]
    [Description("Cria uma previa de publicacao em forum Moodle. A publicacao real exige confirmar_post_forum_moodle com o texto literal retornado.")]
    public Task<CallToolResult> CriarPreviaPostForumAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Identificador do forum. Pode ser cmid ou instance id.")]
        string forumId,
        [Description("Assunto da nova discussao ou resposta.")]
        string assunto,
        [Description("Mensagem HTML a publicar no forum. Texto simples tambem e aceito, mas sera enviado como conteudo HTML ao Moodle.")]
        string mensagemHtml,
        [Description("Identificador da discussao quando a publicacao for uma resposta. Omitir para criar nova discussao.")]
        string? discussionId = null,
        [Description("Post alvo da resposta. Quando omitido e discussionId for informado, responde ao post inicial da discussao.")]
        string? replyToPostId = null,
        [Description("Grupo Moodle para nova discussao. Use 0 para o padrao do Moodle.")]
        int groupId = 0,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return CreateForumPostPreviewCoreAsync(
            courseId,
            forumId,
            assunto,
            mensagemHtml,
            discussionId,
            replyToPostId,
            groupId,
            moodleAlias,
            cancellationToken,
            language: "pt");
    }

    [McpServerTool(
        Name = "confirm_forum_post",
        Title = "Confirm Forum Post",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ConfirmForumPostResult>))]
    [Description("Confirma e executa uma publicacao pendente em forum Moodle. Exige pendingActionId e confirmationText literal da previa.")]
    public Task<CallToolResult> ConfirmarPostForumMoodleAsync(
        [Description("Identificador da acao pendente gerado por criar_previa_post_forum.")]
        Guid pendingActionId,
        [Description("Texto literal de confirmacao retornado na previa.")]
        string confirmationText,
        CancellationToken cancellationToken = default)
    {
        return ConfirmForumPostCoreAsync(
            pendingActionId,
            confirmationText,
            cancellationToken,
            language: "pt");
    }

    private async Task<CallToolResult> ReadForumCoreAsync(
        string courseId,
        string forumId,
        int page,
        int pageSize,
        bool includePosts,
        int postsPerDiscussion,
        string sortBy,
        string sortDirection,
        string? moodleAlias,
        CancellationToken cancellationToken,
        string language)
    {
        if (string.IsNullOrWhiteSpace(courseId))
        {
            return ToolResultHelper.Error<ReadForumResponse>(language == "pt" ? "Informe um identificador de curso." : "Provide a course identifier.");
        }

        if (string.IsNullOrWhiteSpace(forumId))
        {
            return ToolResultHelper.Error<ReadForumResponse>(language == "pt" ? "Informe um identificador de forum." : "Provide a forum identifier.");
        }

        if (!TryNormalizeSortBy(sortBy, out var normalizedSortBy))
        {
            return ToolResultHelper.Error<ReadForumResponse>(
                language == "pt"
                    ? "Campo de ordenacao invalido. Use id, timemodified, timestart ou timeend."
                    : "Invalid sort field. Use id, timemodified, timestart, or timeend.");
        }

        if (!TryNormalizeSortDirection(sortDirection, out var normalizedSortDirection))
        {
            return ToolResultHelper.Error<ReadForumResponse>(
                language == "pt"
                    ? "Direcao de ordenacao invalida. Use ASC ou DESC."
                    : "Invalid sort direction. Use ASC or DESC.");
        }

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
        {
            return ToolResultHelper.Error<ReadForumResponse>(
                language == "pt"
                    ? "Usuario nao autenticado para ler forum."
                    : "User is not authenticated to read forum.");
        }

        ForumReadPage? forumPage;
        try
        {
            forumPage = await mediator.Send(
                new ReadForumQuery(
                    moodleUserId.Value.ToString(),
                    courseId,
                    forumId,
                    page,
                    pageSize,
                    normalizedSortBy,
                    normalizedSortDirection,
                    includePosts,
                    postsPerDiscussion),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ToolResultHelper.Error<ReadForumResponse>(ex.Message, errorCode: MoodleErrorContract.InvalidPage);
        }
        catch (MoodleApiException exception)
        {
            return ToolResultHelper.Error<ReadForumResponse>(exception);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<ReadForumResponse>(exception);
        }

        if (forumPage is null)
        {
            return ToolResultHelper.Error<ReadForumResponse>(
                language == "pt"
                    ? "Curso ou forum nao encontrados entre os dados autorizados do usuario."
                    : "Course or forum was not found in the user's authorized data.");
        }

        var data = ToResponse(forumPage);
        var response = new ToolResponse<ReadForumResponse>("ok", data, [], AuditId: null, DateTimeOffset.UtcNow);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildNarration(data, language) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private async Task<CallToolResult> CreateForumPostPreviewCoreAsync(
        string courseId,
        string forumId,
        string subject,
        string messageHtml,
        string? discussionId,
        string? replyToPostId,
        int groupId,
        string? moodleAlias,
        CancellationToken cancellationToken,
        string language)
    {
        if (string.IsNullOrWhiteSpace(courseId))
        {
            return ToolResultHelper.Error<CreateForumPostPreviewResult>(language == "pt" ? "Informe um identificador de curso." : "Provide a course identifier.");
        }

        if (string.IsNullOrWhiteSpace(forumId))
        {
            return ToolResultHelper.Error<CreateForumPostPreviewResult>(language == "pt" ? "Informe um identificador de forum." : "Provide a forum identifier.");
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            return ToolResultHelper.Error<CreateForumPostPreviewResult>(language == "pt" ? "Informe o assunto do post." : "Provide the post subject.");
        }

        if (string.IsNullOrWhiteSpace(messageHtml))
        {
            return ToolResultHelper.Error<CreateForumPostPreviewResult>(language == "pt" ? "Informe a mensagem do post." : "Provide the post message.");
        }

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
        {
            return ToolResultHelper.Error<CreateForumPostPreviewResult>(
                language == "pt"
                    ? "Usuario nao autenticado para publicar em forum."
                    : "User is not authenticated to publish in forum.");
        }

        CreateForumPostPreviewResult? data;
        try
        {
            data = await mediator.Send(
                new CreateForumPostPreviewCommand(
                    moodleUserId.Value.ToString(),
                    courseId,
                    forumId,
                    subject,
                    messageHtml,
                    discussionId,
                    replyToPostId,
                    groupId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return ToolResultHelper.Error<CreateForumPostPreviewResult>(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<CreateForumPostPreviewResult>(ex.Message);
        }
        catch
        {
            return ToolResultHelper.Error<CreateForumPostPreviewResult>(
                language == "pt"
                    ? "Nao foi possivel criar a previa de publicacao no forum neste momento."
                    : "Could not create the forum post preview at this time.");
        }

        if (data is null)
        {
            return ToolResultHelper.Error<CreateForumPostPreviewResult>(
                language == "pt"
                    ? "Curso, forum, discussao ou post nao encontrados entre os dados autorizados do usuario."
                    : "Course, forum, discussion, or post was not found in the user's authorized data.");
        }

        var response = new ToolResponse<CreateForumPostPreviewResult>(
            "pending_confirmation",
            data,
            data.Warnings,
            AuditId: null,
            DateTimeOffset.UtcNow);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildForumPostPreviewNarration(data, language) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private async Task<CallToolResult> ConfirmForumPostCoreAsync(
        Guid pendingActionId,
        string confirmationText,
        CancellationToken cancellationToken,
        string language)
    {
        if (pendingActionId == Guid.Empty)
        {
            return ToolResultHelper.Error<ConfirmForumPostResult>(language == "pt" ? "Informe uma acao pendente valida." : "Provide a valid pending action id.");
        }

        if (string.IsNullOrWhiteSpace(confirmationText))
        {
            return ToolResultHelper.Error<ConfirmForumPostResult>(language == "pt" ? "Informe o texto literal de confirmacao." : "Provide the literal confirmation text.");
        }

        ConfirmForumPostResult data;
        try
        {
            data = await mediator.Send(
                new ConfirmForumPostCommand(pendingActionId, confirmationText),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<ConfirmForumPostResult>(ex.Message);
        }
        catch
        {
            return ToolResultHelper.Error<ConfirmForumPostResult>(
                language == "pt"
                    ? "Nao foi possivel confirmar a publicacao no forum neste momento."
                    : "Could not confirm the forum post at this time.");
        }

        var status = data.Status == "failed"
            ? "error"
            : data.Status == "already_confirmed"
                ? "already_confirmed"
                : "ok";
        var response = new ToolResponse<ConfirmForumPostResult>(
            status,
            data,
            data.Warnings,
            data.AuditId,
            DateTimeOffset.UtcNow);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildConfirmForumPostNarration(data, language) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = data.Status == "failed"
        };
    }

    private static string BuildNarration(ReadForumResponse response, string language)
    {
        if (language == "pt")
        {
            return response.ReturnedCount == 0
                ? $"Nao encontrei discussoes no forum {response.ForumName} para a pagina informada."
                : $"Li {response.ReturnedCount} discussao(oes) do forum {response.ForumName}. Posts carregados: {response.IncludePosts}.";
        }

        return response.ReturnedCount == 0
            ? $"No discussions were found in forum {response.ForumName} for the requested page."
            : $"Read {response.ReturnedCount} discussion(s) from forum {response.ForumName}. Posts loaded: {response.IncludePosts}.";
    }

    private static string BuildForumPostPreviewNarration(CreateForumPostPreviewResult response, string language)
    {
        if (language == "pt")
        {
            return response.Mode == "reply"
                ? $"Previa criada para responder a discussao {response.DiscussionId} no forum {response.ForumName}. Confirme com o texto literal retornado."
                : $"Previa criada para nova discussao no forum {response.ForumName}. Confirme com o texto literal retornado.";
        }

        return response.Mode == "reply"
            ? $"Preview created to reply to discussion {response.DiscussionId} in forum {response.ForumName}. Confirm with the returned literal text."
            : $"Preview created for a new discussion in forum {response.ForumName}. Confirm with the returned literal text.";
    }

    private static string BuildConfirmForumPostNarration(ConfirmForumPostResult response, string language)
    {
        if (response.Status == "already_confirmed")
        {
            return language == "pt"
                ? "A acao ja estava confirmada e nao foi executada novamente."
                : "The action was already confirmed and was not executed again.";
        }

        if (response.Status == "failed")
        {
            return language == "pt"
                ? "A publicacao no forum falhou. Consulte os avisos retornados."
                : "The forum post failed. Check the returned warnings.";
        }

        if (language == "pt")
        {
            return response.Mode == "reply"
                ? $"Resposta publicada no forum {response.ForumName}. Post Moodle: {response.PostId}."
                : $"Discussao publicada no forum {response.ForumName}. Discussao Moodle: {response.DiscussionId}.";
        }

        return response.Mode == "reply"
            ? $"Reply published in forum {response.ForumName}. Moodle post: {response.PostId}."
            : $"Discussion published in forum {response.ForumName}. Moodle discussion: {response.DiscussionId}.";
    }

    private static ReadForumResponse ToResponse(ForumReadPage page)
    {
        return new ReadForumResponse(
            page.CourseId,
            page.ForumId,
            page.ForumModuleId,
            page.ForumName,
            page.Page,
            page.PageSize,
            page.SortBy,
            page.SortDirection,
            page.IncludePosts,
            page.PostsPerDiscussion,
            page.ReturnedCount,
            page.HasMore,
            page.Discussions.Select(ToDiscussionItem).ToArray());
    }

    private static ForumDiscussionItem ToDiscussionItem(ForumDiscussionSummary discussion)
    {
        return new ForumDiscussionItem(
            discussion.DiscussionId,
            discussion.FirstPostId,
            discussion.Name,
            discussion.Subject,
            discussion.MessageText,
            discussion.AuthorUserId,
            discussion.AuthorFullName,
            discussion.CreatedAt,
            discussion.ModifiedAt,
            discussion.LastModifiedAt,
            discussion.ReplyCount,
            discussion.UnreadCount,
            discussion.Pinned,
            discussion.Locked,
            discussion.CanReply,
            discussion.PostsReturned,
            discussion.PostsTotal,
            discussion.Posts.Select(ToPostItem).ToArray());
    }

    private static ForumPostItem ToPostItem(ForumPostSummary post)
    {
        return new ForumPostItem(
            post.PostId,
            post.DiscussionId,
            post.ParentPostId,
            post.UserId,
            post.UserFullName,
            post.Subject,
            post.MessageText,
            post.CreatedAt,
            post.ModifiedAt,
            post.Deleted,
            post.CanReply,
            post.PostRead,
            post.IsPrivateReply,
            post.Children,
            post.Attachments.Select(ToAttachmentItem).ToArray());
    }

    private static ForumAttachmentItem ToAttachmentItem(ForumAttachmentSummary attachment)
    {
        return new ForumAttachmentItem(
            attachment.FileName,
            attachment.FilePath,
            attachment.MimeType,
            attachment.SizeBytes,
            attachment.FileUrl,
            attachment.IsExternalFile);
    }

    private static bool TryNormalizeSortBy(string? sortBy, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(sortBy)
            ? "timemodified"
            : sortBy.Trim().ToLowerInvariant();

        return AllowedSortBy.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeSortDirection(string? sortDirection, out string normalized)
    {
        if (string.Equals(sortDirection?.Trim(), "ASC", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "ASC";
            return true;
        }

        if (string.IsNullOrWhiteSpace(sortDirection) ||
            string.Equals(sortDirection.Trim(), "DESC", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "DESC";
            return true;
        }

        normalized = "DESC";
        return false;
    }



    public sealed record ReadForumResponse(
        [property: JsonPropertyName("courseId")] string CourseId,
        [property: JsonPropertyName("forumId")] string ForumId,
        [property: JsonPropertyName("forumModuleId")] string ForumModuleId,
        [property: JsonPropertyName("forumName")] string ForumName,
        [property: JsonPropertyName("page")] int Page,
        [property: JsonPropertyName("pageSize")] int PageSize,
        [property: JsonPropertyName("sortBy")] string SortBy,
        [property: JsonPropertyName("sortDirection")] string SortDirection,
        [property: JsonPropertyName("includePosts")] bool IncludePosts,
        [property: JsonPropertyName("postsPerDiscussion")] int PostsPerDiscussion,
        [property: JsonPropertyName("returnedCount")] int ReturnedCount,
        [property: JsonPropertyName("hasMore")] bool HasMore,
        [property: JsonPropertyName("discussions")] IReadOnlyList<ForumDiscussionItem> Discussions);

    public sealed record ForumDiscussionItem(
        [property: JsonPropertyName("discussionId")] string DiscussionId,
        [property: JsonPropertyName("firstPostId")] string FirstPostId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("messageText")] string? MessageText,
        [property: JsonPropertyName("authorUserId")] string? AuthorUserId,
        [property: JsonPropertyName("authorFullName")] string? AuthorFullName,
        [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("modifiedAt")] DateTimeOffset? ModifiedAt,
        [property: JsonPropertyName("lastModifiedAt")] DateTimeOffset? LastModifiedAt,
        [property: JsonPropertyName("replyCount")] int ReplyCount,
        [property: JsonPropertyName("unreadCount")] int UnreadCount,
        [property: JsonPropertyName("pinned")] bool? Pinned,
        [property: JsonPropertyName("locked")] bool? Locked,
        [property: JsonPropertyName("canReply")] bool? CanReply,
        [property: JsonPropertyName("postsReturned")] int PostsReturned,
        [property: JsonPropertyName("postsTotal")] int PostsTotal,
        [property: JsonPropertyName("posts")] IReadOnlyList<ForumPostItem> Posts);

    public sealed record ForumPostItem(
        [property: JsonPropertyName("postId")] string PostId,
        [property: JsonPropertyName("discussionId")] string DiscussionId,
        [property: JsonPropertyName("parentPostId")] string? ParentPostId,
        [property: JsonPropertyName("userId")] string? UserId,
        [property: JsonPropertyName("userFullName")] string? UserFullName,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("messageText")] string? MessageText,
        [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("modifiedAt")] DateTimeOffset? ModifiedAt,
        [property: JsonPropertyName("deleted")] bool? Deleted,
        [property: JsonPropertyName("canReply")] bool? CanReply,
        [property: JsonPropertyName("postRead")] bool? PostRead,
        [property: JsonPropertyName("isPrivateReply")] bool? IsPrivateReply,
        [property: JsonPropertyName("children")] IReadOnlyList<string> Children,
        [property: JsonPropertyName("attachments")] IReadOnlyList<ForumAttachmentItem> Attachments);

    public sealed record ForumAttachmentItem(
        [property: JsonPropertyName("fileName")] string? FileName,
        [property: JsonPropertyName("filePath")] string? FilePath,
        [property: JsonPropertyName("mimeType")] string? MimeType,
        [property: JsonPropertyName("sizeBytes")] long? SizeBytes,
        [property: JsonPropertyName("fileUrl")] string? FileUrl,
        [property: JsonPropertyName("isExternalFile")] bool? IsExternalFile);
}
