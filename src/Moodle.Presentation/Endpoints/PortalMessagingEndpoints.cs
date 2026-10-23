using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Messages;
using Moodle.Application.Registry;
using Moodle.Infrastructure;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Preparação, confirmação e histórico de mensagens do Portal.
/// </summary>
internal static class PortalMessagingEndpoints
{
    public static void MapMessages(WebApplication app, string rateLimitPolicy)
    {
        app.MapPost("/api/messages/prepare", async (HttpContext context, ConnectorDbContext dbContext, IAntiforgery antiforgery, IMediator mediator, AppMessagePrepareInput input, CancellationToken cancellationToken) =>
        {
            if (!HasAppPermission(context, AppPermissionCatalog.MessagesPrepare)) return Results.Forbid();
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);
            if (!Enum.TryParse<TutorMessageType>(input.MessageType, true, out var messageType)) return Results.BadRequest(new { error = new { code = "invalid_message_type", message = "Tipo de mensagem inválido." } });
            if (input.RecipientIds is null || input.RecipientIds.Count == 0 || input.RecipientIds.Count > 100) return Results.BadRequest(new { error = new { code = "invalid_recipients", message = "Informe de 1 a 100 destinatários explícitos." } });
            try
            {
                var preview = await mediator.Send(new PrepareTutorMessageCommand(input.CourseId, messageType, input.RecipientIds, input.CustomText), cancellationToken);
                return Results.Ok(new AppEnvelope<TutorMessagePreview>(preview, new(DateTimeOffset.UtcNow, null)));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = new { code = "invalid_message", message = ex.Message } }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = new { code = "message_disabled", message = ex.Message } }); }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/messages/confirm", async (HttpContext context, ConnectorDbContext dbContext, IAntiforgery antiforgery, IMediator mediator, AppMessageConfirmInput input, CancellationToken cancellationToken) =>
        {
            if (!HasAppPermission(context, AppPermissionCatalog.MessagesPrepare)) return Results.Forbid();
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);
            if (input.PendingActionId == Guid.Empty || string.IsNullOrWhiteSpace(input.ConfirmationText)) return Results.BadRequest(new { error = new { code = "invalid_confirmation", message = "Confirmação explícita é obrigatória." } });
            try
            {
                var result = await mediator.Send(new ConfirmTutorMessageCommand(input.PendingActionId, input.ConfirmationText), cancellationToken);
                return Results.Ok(new AppEnvelope<TutorMessageSendResult>(result, new(DateTimeOffset.UtcNow, null)));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = new { code = "message_confirmation_failed", message = ex.Message } });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/api/messages/conversations", async (
            string? connectionRef,
            HttpContext context,
            ConnectorDbContext dbContext,
            IConnectionRegistry connectionRegistry,
            IMoodleMessageGateway messageGateway,
            CancellationToken cancellationToken) =>
        {
            if (!HasAppPermission(context, AppPermissionCatalog.MessagesPrepare)) return Results.Forbid();
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            Moodle.Domain.Registry.ConnectionInfo? resolved;
            try
            {
                resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
            }
            catch (MoodleApiException exception) when (
                string.IsNullOrWhiteSpace(connectionRef) &&
                exception.ErrorCode == "moodle_connection_not_found")
            {
                return Results.Ok(new AppEnvelope<AppMoodleConversationsDto>(
                    new AppMoodleConversationsDto(1, 0, Array.Empty<AppMoodleConversationDto>()),
                    new(DateTimeOffset.UtcNow, null)));
            }

            if (resolved is null && string.IsNullOrWhiteSpace(connectionRef))
            {
                return Results.Ok(new AppEnvelope<AppMoodleConversationsDto>(
                    new AppMoodleConversationsDto(1, 0, Array.Empty<AppMoodleConversationDto>()),
                    new(DateTimeOffset.UtcNow, null)));
            }
            if (resolved is null)
                return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");

            var result = await messageGateway.GetConversationsAsync(cancellationToken);
            var data = new AppMoodleConversationsDto(
                ContractVersion: 1,
                CurrentMoodleUserId: result.CurrentMoodleUserId,
                Items: result.Items.Select(MapConversation).ToArray());
            return Results.Ok(new AppEnvelope<AppMoodleConversationsDto>(data, new(DateTimeOffset.UtcNow, resolved.Alias)));
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/api/messages/conversations/{moodleUserId:long}", async (
            long moodleUserId,
            string? connectionRef,
            int? limit,
            HttpContext context,
            ConnectorDbContext dbContext,
            IConnectionRegistry connectionRegistry,
            IMoodleMessageGateway messageGateway,
            CancellationToken cancellationToken) =>
        {
            if (!HasAppPermission(context, AppPermissionCatalog.MessagesPrepare)) return Results.Forbid();
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            if (moodleUserId <= 0) return Results.BadRequest(new { error = new { code = "invalid_moodle_user", message = "O usuário Moodle informado é inválido." } });
            var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
            if (resolved is null)
                return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");

            var result = await messageGateway.GetMessagesAsync(moodleUserId, Math.Clamp(limit ?? 50, 1, 100), cancellationToken);
            var data = new AppMoodleMessagesDto(
                ContractVersion: 1,
                ConversationId: result.ConversationId,
                CurrentMoodleUserId: result.CurrentMoodleUserId,
                Items: result.Items.Select(item => new AppMoodleMessageDto(
                    item.Id, item.Text, item.CreatedAtUnix, item.SenderMoodleUserId, item.SenderType)).ToArray());
            return Results.Ok(new AppEnvelope<AppMoodleMessagesDto>(data, new(DateTimeOffset.UtcNow, resolved.Alias)));
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/messages/conversations/{moodleUserId:long}/prepare", async (
            long moodleUserId,
            string? connectionRef,
            HttpContext context,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            IConnectionRegistry connectionRegistry,
            IMediator mediator,
            AppMoodleDirectMessageInput input,
            CancellationToken cancellationToken) =>
        {
            if (!HasAppPermission(context, AppPermissionCatalog.MessagesPrepare)) return Results.Forbid();
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);
            if (moodleUserId <= 0 || string.IsNullOrWhiteSpace(input.Message) || input.Message.Trim().Length > 4000)
                return Results.BadRequest(new { error = new { code = "invalid_message", message = "Informe uma mensagem entre 1 e 4000 caracteres." } });
            var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
            if (resolved is null)
                return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");

            try
            {
                var preview = await mediator.Send(new PrepareDirectMoodleMessageCommand(moodleUserId, input.Message), cancellationToken);
                return Results.Ok(new AppEnvelope<TutorMessagePreview>(preview, new(DateTimeOffset.UtcNow, resolved.Alias)));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = new { code = "invalid_message", message = ex.Message } });
            }
            catch (KeyNotFoundException ex)
            {
                return AppErrorResults.NotFound("conversation_target_not_found", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = new { code = "message_disabled", message = ex.Message } });
            }
        }).RequireRateLimiting(rateLimitPolicy);

    }

    private static AppMoodleConversationDto MapConversation(MoodleConversationSummary item) => new(
    item.Id,
    new AppMoodleMessageMemberDto(item.Member.Id, item.Member.FullName, item.Member.ProfileImageUrl),
    item.LastMessage is null
        ? null
        : new AppMoodleConversationLastMessageDto(item.LastMessage.Text, item.LastMessage.CreatedAtUnix),
    item.UnreadCount,
    item.StudentId);

    private static Task<AppIdentity?> ResolveAppIdentityAsync(
        HttpContext context,
        ConnectorDbContext dbContext,
        CancellationToken cancellationToken) =>
        PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);

    private static bool HasAppPermission(HttpContext context, string permission) =>
        PortalEndpointAuthorization.HasAppPermission(context, permission);
}
