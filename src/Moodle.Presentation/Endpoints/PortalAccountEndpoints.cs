using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Presentation.Security;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Conta do portal, acesso a equipes e administração de grupos de permissões.
/// </summary>
internal static class PortalAccountEndpoints
{
    public static void MapAccountsAndAccessControl(WebApplication app, string rateLimitPolicy)
    {
        app.MapPost("/api/account/register", async (
            RegisterAccountInput input,
            HttpContext context,
            ConnectorDbContext dbContext,
            IAccountService accountService,
            IPlatformPermissionService platformPermissionService,
            IAntiforgery antiforgery,
            PublicAccountRateLimiter publicAccountRateLimiter,
            CancellationToken cancellationToken) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            if (string.IsNullOrWhiteSpace(input.Name) ||
                string.IsNullOrWhiteSpace(input.Email) ||
                string.IsNullOrWhiteSpace(input.Password))
                return Results.BadRequest(new { ok = false, error = "Preencha todos os campos obrigatórios." });

            if (input.Password.Length < 8)
                return Results.BadRequest(new { ok = false, error = "A senha deve ter pelo menos 8 caracteres." });

            if (!publicAccountRateLimiter.TryAcquire(
                    PublicAccountOperation.Registration,
                    input.Email,
                    out var retryAfterSeconds))
            {
                return PublicAccountRateLimitResponse.TooManyRequests(context, retryAfterSeconds);
            }

            try
            {
                var account = await accountService.RegisterAsync(
                    new RegisterAccountRequest(input.Name, input.Email, input.Password),
                    cancellationToken);
                await PortalAuthenticationEndpoints.SignInAppAccountAsync(context, dbContext, platformPermissionService, account.Id, account.Name, account.Email, cancellationToken);

                return Results.Ok(new
                {
                    ok = true,
                    redirectUrl = "/?step=moodle"
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { ok = false, error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/account/password", async (
            ChangePasswordInput input,
            HttpContext context,
            ConnectorDbContext dbContext,
            IAccountService accountService,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);
            try
            {
                await accountService.ChangePasswordAsync(
                    new ChangePasswordRequest(identity.Id, input.CurrentPassword, input.NewPassword), cancellationToken);
                return Results.Ok(new { ok = true, message = "Senha alterada com sucesso." });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/api/admin/accounts", async (
            HttpContext context,
            ConnectorDbContext dbContext,
            IAccountService accountService,
            CancellationToken cancellationToken) =>
        {
            if (await ResolveAppIdentityAsync(context, dbContext, cancellationToken) is null) return Results.Unauthorized();
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.AdminView)) return Results.Forbid();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { ok = true, accounts = await accountService.ListAccountsAsync(cancellationToken) });
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/admin/accounts/{userId:guid}/reset-password", async (
            Guid userId,
            HttpContext context,
            ConnectorDbContext dbContext,
            IAccountService accountService,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (await ResolveAppIdentityAsync(context, dbContext, cancellationToken) is null) return Results.Unauthorized();
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.AdminView)) return Results.Forbid();
            await antiforgery.ValidateRequestAsync(context);
            try
            {
                await accountService.ResetPasswordToDefaultAsync(userId, cancellationToken);
                return Results.Ok(new { ok = true, message = "Senha redefinida para a senha padrão configurada." });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/admin/accounts/delete", async (
            AdminDeleteAccountsInput input,
            HttpContext context,
            ConnectorDbContext dbContext,
            IAccountService accountService,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.AdminView)) return Results.Forbid();
            await antiforgery.ValidateRequestAsync(context);

            try
            {
                var result = await accountService.DeleteAccountsAsAdminAsync(
                    new AdminDeleteAccountsRequest(
                        identity.Id,
                        input.UserIds ?? [],
                        input.Password,
                        input.ConfirmationText),
                    cancellationToken);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new { ok = true, result });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/account/login", async (
            LoginInput input,
            HttpContext context,
            ConnectorDbContext dbContext,
            IAccountService accountService,
            IPlatformPermissionService platformPermissionService,
            IAntiforgery antiforgery,
            PublicAccountRateLimiter publicAccountRateLimiter,
            CancellationToken cancellationToken) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            if (string.IsNullOrWhiteSpace(input.Email) ||
                string.IsNullOrWhiteSpace(input.Password))
            {
                return Results.BadRequest(new { ok = false, error = "Preencha e-mail e senha." });
            }

            if (!publicAccountRateLimiter.TryAcquire(
                    PublicAccountOperation.Login,
                    input.Email,
                    out var retryAfterSeconds))
            {
                return PublicAccountRateLimitResponse.TooManyRequests(context, retryAfterSeconds);
            }

            var account = await accountService.ValidateLoginAsync(
                new LoginAccountRequest(input.Email, input.Password),
                cancellationToken);

            if (account is null)
            {
                return Results.Json(
                    new { ok = false, error = "E-mail ou senha inválidos." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            await PortalAuthenticationEndpoints.SignInAppAccountAsync(context, dbContext, platformPermissionService, account.Id, account.Name, account.Email, cancellationToken);
            return Results.Ok(new
            {
                ok = true,
                account.Id,
                account.Name,
                account.Email,
                account.HasMoodleConnected
            });
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/api/account/me", async (
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();

            context.Response.Headers.CacheControl = "no-store";

            var profile = await accountService.GetProfileAsync(identity.Id, cancellationToken);
            if (profile is null) return Results.NotFound();

            return Results.Ok(new
            {
                ok = true,
                profile.Id,
                profile.Name,
                profile.Email,
                profile.HasMoodleConnected,
                profile.ApiKey,
                hasApiKey = !string.IsNullOrWhiteSpace(profile.ApiKey),
                profile.MoodleConnections
            });
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/api/teams", async (
            HttpContext context,
            ConnectorDbContext dbContext,
            ITeamAccessService teamAccessService,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();

            var teams = await teamAccessService.GetTeamsAsync(identity.Id, cancellationToken);
            return Results.Ok(new { ok = true, teams });
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/teams/{teamId:guid}/invitations", async (
            Guid teamId,
            TeamInvitationInput input,
            HttpContext context,
            ConnectorDbContext dbContext,
            ITeamAccessService teamAccessService,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);

            if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Role))
                return Results.BadRequest(new { ok = false, error = "Informe e-mail e papel do convite." });

            try
            {
                var invitation = await teamAccessService.CreateInvitationAsync(
                    new CreateTeamInvitationRequest(
                        identity.Id,
                        teamId,
                        input.Email,
                        input.Role,
                        input.Scopes ?? [],
                        TimeSpan.FromHours(Math.Clamp(input.ExpiresInHours ?? 72, 1, 24 * 30))),
                    cancellationToken);
                return Results.Ok(new { ok = true, invitation });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
            catch (InvalidOperationException)
            {
                return Results.Forbid();
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/team-invitations/accept", async (
            TeamInvitationAcceptInput input,
            HttpContext context,
            ConnectorDbContext dbContext,
            ITeamAccessService teamAccessService,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);
            if (string.IsNullOrWhiteSpace(input.Token))
                return Results.BadRequest(new { ok = false, error = "Informe o token do convite." });

            try
            {
                var team = await teamAccessService.AcceptInvitationAsync(identity.Id, identity.Email, input.Token, cancellationToken);
                return Results.Ok(new { ok = true, team });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/api/permission-groups", async (
            HttpContext context,
            ConnectorDbContext dbContext,
            IPlatformPermissionService permissionService,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await permissionService.EnsureDefaultPermissionsAsync(identity.Id, cancellationToken);
            var groups = await permissionService.GetGroupsAsync(identity.Id, cancellationToken);
            return Results.Ok(new { ok = true, groups });
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/api/permission-catalog", async (HttpContext context, ConnectorDbContext dbContext, CancellationToken cancellationToken) =>
        {
            if (await ResolveAppIdentityAsync(context, dbContext, cancellationToken) is null) return Results.Unauthorized();
            return Results.Ok(new
            {
                ok = true,
                permissions = PlatformPermissionCatalog.All
                    .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            });
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/permission-groups", async (
            CreatePermissionGroupInput input,
            HttpContext context,
            ConnectorDbContext dbContext,
            IPlatformPermissionService permissionService,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);
            try
            {
                var group = await permissionService.CreateGroupAsync(
                    new CreatePermissionGroupRequest(identity.Id, input.Name, input.Description ?? string.Empty, input.Permissions ?? []), cancellationToken);
                return Results.Ok(new { ok = true, group });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
            catch (InvalidOperationException) { return Results.Forbid(); }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPut("/api/permission-groups/{groupId:guid}", async (
            Guid groupId,
            UpdatePermissionGroupInput input,
            HttpContext context,
            ConnectorDbContext dbContext,
            IPlatformPermissionService permissionService,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);
            try
            {
                var group = await permissionService.UpdateGroupAsync(
                    new UpdatePermissionGroupRequest(identity.Id, groupId, input.Name, input.Description ?? string.Empty, input.Permissions ?? []), cancellationToken);
                return Results.Ok(new { ok = true, group });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
            catch (InvalidOperationException) { return Results.NotFound(new { ok = false, error = "Grupo de permissões não encontrado." }); }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/permission-groups/{groupId:guid}/members", async (
            Guid groupId,
            PermissionGroupMemberInput input,
            HttpContext context,
            ConnectorDbContext dbContext,
            IPlatformPermissionService permissionService,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);
            try
            {
                await permissionService.AddMemberAsync(new AddPermissionGroupMemberRequest(identity.Id, groupId, input.UserId), cancellationToken);
                return Results.Ok(new { ok = true });
            }
            catch (InvalidOperationException) { return Results.Forbid(); }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPut("/api/users/{userId:guid}/platform-permissions", async (
            Guid userId,
            SetUserPermissionInput input,
            HttpContext context,
            ConnectorDbContext dbContext,
            IPlatformPermissionService permissionService,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);
            try
            {
                await permissionService.SetUserPermissionAsync(new SetUserPermissionRequest(identity.Id, userId, input.Permission, input.IsAllowed), cancellationToken);
                return Results.Ok(new { ok = true });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
            catch (InvalidOperationException) { return Results.Forbid(); }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/account/api-key/rotate", async (
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);

            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var apiKey = await accountService.RotateApiKeyAsync(identity.Id, cancellationToken);
                return Results.Ok(new
                {
                    ok = true,
                    apiKey,
                    message = "Nova API key gerada. A chave anterior foi invalidada."
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/account/connect-moodle", async (
            ConnectMoodleInput input,
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);

            if (string.IsNullOrWhiteSpace(input.MoodleAlias) ||
                string.IsNullOrWhiteSpace(input.MoodleBaseUrl) ||
                string.IsNullOrWhiteSpace(input.MoodleUsername) ||
                string.IsNullOrWhiteSpace(input.MoodlePassword))
                return Results.BadRequest(new { ok = false, error = "Preencha alias, URL, usuario e senha do Moodle." });

            try
            {
                var apiKey = await accountService.ConnectMoodleAsync(
                    new ConnectMoodleAccountRequest(
                        identity.Id,
                        input.MoodleAlias,
                        input.MoodleBaseUrl,
                        input.MoodleUsername,
                        input.MoodlePassword,
                        input.IsDefault,
                        input.CanWrite),
                    cancellationToken);

                return Results.Ok(new { ok = true, apiKey, input.MoodleAlias, input.MoodleBaseUrl, input.IsDefault });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPut("/api/account/moodle/{id}", async (
            string id,
            UpdateMoodleInput input,
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);

            if (string.IsNullOrWhiteSpace(input.MoodleAlias) || string.IsNullOrWhiteSpace(input.MoodleBaseUrl))
                return Results.BadRequest(new { ok = false, error = "Preencha alias e URL do Moodle." });

            try
            {
                await accountService.UpdateMoodleAsync(
                    new UpdateMoodleAccountRequest(
                        identity.Id,
                        id,
                        input.MoodleAlias,
                        input.MoodleBaseUrl,
                        input.MoodleUsername,
                        input.MoodlePassword,
                        input.IsDefault,
                        input.CanWrite),
                    cancellationToken);

                return Results.Ok(new { ok = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapDelete("/api/account/moodle/{id}", async (
            string id,
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);

            try
            {
                await accountService.DeleteMoodleAsync(identity.Id, id, cancellationToken);
                return Results.Ok(new { ok = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapDelete("/api/account", async (
            [FromBody] DeleteAccountInput input,
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);

            try
            {
                await accountService.DeleteAccountAsync(
                    new DeleteAccountRequest(identity.Id, input.Password, input.ConfirmationText),
                    cancellationToken);
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Ok(new { ok = true, message = "Conta excluída definitivamente." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

    }

    private static Task<AppIdentity?> ResolveAppIdentityAsync(
        HttpContext context,
        ConnectorDbContext dbContext,
        CancellationToken cancellationToken) =>
        PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
}

public sealed record ChangePasswordInput(string CurrentPassword, string NewPassword);
