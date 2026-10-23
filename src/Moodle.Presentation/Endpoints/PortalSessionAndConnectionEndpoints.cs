using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Sessão autenticada do portal e gerenciamento das conexões Moodle do usuário.
/// </summary>
internal static class PortalSessionAndConnectionEndpoints
{
    public static void MapSessionAndConnections(WebApplication app, string rateLimitPolicy)
    {
        app.MapGet("/api/session", async (
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            IMoodleSnapshotSyncQueue snapshotSyncQueue,
            CancellationToken cancellationToken) =>
        {
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null)
            {
                return Results.Json(new AppEnvelope<AppSessionDto>(
                    new(false, null), new AppMeta(DateTimeOffset.UtcNow, null)), statusCode: StatusCodes.Status401Unauthorized);
            }

            var profile = await accountService.GetProfileAsync(identity.Id, cancellationToken);
            if (profile is null)
            {
                return Results.NotFound();
            }

            if (!string.IsNullOrWhiteSpace(identity.ConnectorClientId))
            {
                foreach (var connection in profile.MoodleConnections.Where(item =>
                             string.Equals(item.Status, "active", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(item.Status, "unknown", StringComparison.OrdinalIgnoreCase)))
                {
                    await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
                        identity.Id,
                        identity.ConnectorClientId,
                        connection.Alias,
                        identity.Id.ToString(),
                        Dataset: MoodleSnapshotDatasets.Connection,
                        Priority: 100),
                        cancellationToken);
                }
            }

            context.Response.Headers.CacheControl = "no-store";
            var roles = context.User.FindAll(ClaimTypes.Role).Select(x => x.Value)
                .Concat(context.User.FindAll("role").Select(x => x.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var permissions = context.User.FindAll("platform_permission").Select(x => x.Value)
                .Where(permission => !context.User.FindAll("platform_permission_deny")
                    .Any(x => string.Equals(x.Value, permission, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Results.Ok(new AppEnvelope<AppSessionDto>(
                new(true, new AppUserDto(profile.Id, profile.Name, roles, permissions),
                    context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Moodle.Application.Configuration.ReportTemplateOptions>>().Value.TemplatesEnabled),
                new(DateTimeOffset.UtcNow, null)));
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/api/connections", async (
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            var profile = await accountService.GetProfileAsync(identity.Id, cancellationToken);
            if (profile is null)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "no-store";
            var connections = profile.MoodleConnections.Select(MapConnection).ToArray();
            return Results.Ok(new AppListEnvelope<AppConnectionDto>(
                connections,
                new(1, 20, connections.Length, false, DateTimeOffset.UtcNow, null, null, connections.Length)));
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/connections", async (
            ConnectMoodleInput input,
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.ConnectionsManage))
            {
                return Results.Forbid();
            }

            await antiforgery.ValidateRequestAsync(context);
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(input.MoodleAlias) ||
                string.IsNullOrWhiteSpace(input.MoodleBaseUrl) ||
                string.IsNullOrWhiteSpace(input.MoodleUsername) ||
                string.IsNullOrWhiteSpace(input.MoodlePassword))
            {
                return Results.BadRequest(new { ok = false, error = "Preencha alias, URL, usuario e senha do Moodle." });
            }

            try
            {
                await accountService.ConnectMoodleAsync(
                    new ConnectMoodleAccountRequest(
                        identity.Id,
                        input.MoodleAlias,
                        input.MoodleBaseUrl,
                        input.MoodleUsername,
                        input.MoodlePassword,
                        input.IsDefault,
                        input.CanWrite),
                    cancellationToken);

                var profile = await accountService.GetProfileAsync(identity.Id, cancellationToken);
                var connection = profile?.MoodleConnections
                    .FirstOrDefault(item => string.Equals(item.Alias, input.MoodleAlias.Trim(), StringComparison.OrdinalIgnoreCase));
                if (connection is null)
                {
                    return Results.Problem(
                        "A conexão foi registrada, mas não pôde ser relida com segurança.",
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                return Results.Ok(MapConnection(connection));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPut("/api/connections/{id}", async (
            string id,
            UpdateMoodleInput input,
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.ConnectionsManage))
            {
                return Results.Forbid();
            }

            await antiforgery.ValidateRequestAsync(context);
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(input.MoodleAlias) || string.IsNullOrWhiteSpace(input.MoodleBaseUrl))
            {
                return Results.BadRequest(new { ok = false, error = "Preencha alias e URL do Moodle." });
            }

            try
            {
                await accountService.UpdateMoodleAsync(new UpdateMoodleAccountRequest(
                    identity.Id,
                    id,
                    input.MoodleAlias,
                    input.MoodleBaseUrl,
                    input.MoodleUsername,
                    input.MoodlePassword,
                    input.IsDefault,
                    input.CanWrite), cancellationToken);
                var profile = await accountService.GetProfileAsync(identity.Id, cancellationToken);
                var connection = profile?.MoodleConnections.FirstOrDefault(item => item.Id == id);
                return connection is null ? Results.NotFound() : Results.Ok(MapConnection(connection));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/api/connections/{id}/data-summary", async (
            string id,
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.ConnectionsManage))
            {
                return Results.Forbid();
            }

            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                return Results.Ok(await accountService.GetMoodleDataSummaryAsync(identity.Id, id, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapDelete("/api/connections/{id}", async (
            string id,
            [FromBody] AppDeleteConnectionInput input,
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.ConnectionsManage))
            {
                return Results.Forbid();
            }

            await antiforgery.ValidateRequestAsync(context);
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                await accountService.DeleteMoodleAsync(identity.Id, id, input.DeleteLinkedData, input.ConfirmationText, cancellationToken);
                return Results.Ok(new { ok = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/connections/{id}/validate", async (
            string id,
            HttpContext context,
            IAccountService accountService,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.ConnectionsManage))
            {
                return Results.Forbid();
            }

            await antiforgery.ValidateRequestAsync(context);
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var validation = await accountService.ValidateMoodleAsync(identity.Id, id, cancellationToken);
                return Results.Ok(new { status = validation.Status, lastValidatedAt = validation.LastValidatedAt });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        }).RequireRateLimiting(rateLimitPolicy);
    }

    private static AppConnectionDto MapConnection(MoodleConnectionDto connection)
    {
        return new AppConnectionDto(
            connection.Id,
            connection.Alias,
            connection.Alias,
            connection.BaseUrl,
            connection.Status,
            connection.IsDefault,
            new[] { "read" }.Concat(connection.CanWrite ? new[] { "write" } : Array.Empty<string>()).ToArray(),
            connection.LastValidatedAt);
    }
}
