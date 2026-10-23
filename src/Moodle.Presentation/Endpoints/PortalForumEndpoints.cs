using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Moodle.Application.Abstractions;
using Moodle.Application.Forums;
using Moodle.Application.Registry;
using Moodle.Infrastructure;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Leitura de fóruns Moodle exposta ao Portal autenticado.
/// </summary>
internal static class PortalForumEndpoints
{
    public static void MapForums(WebApplication app, string rateLimitPolicy)
    {
        app.MapGet("/api/courses/{connectionRef}/{courseId}/forums", async (
            string connectionRef,
            string courseId,
            HttpContext context,
            ConnectorDbContext dbContext,
            IConnectionRegistry connectionRegistry,
            IMoodleCurrentUserIdGateway currentUserIdGateway,
            IMoodleForumGateway forumGateway,
            CancellationToken cancellationToken) =>
        {
            if (!HasAppPermission(context, AppPermissionCatalog.CoursesView)) return Results.Forbid();
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
            if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
            var userId = await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
            var forums = await forumGateway.GetForumsByCoursesAsync(userId.ToString(), courseId, cancellationToken);
            var data = forums.Select(AppForumContractMapper.ToDto).ToArray();
            return Results.Ok(new AppListEnvelope<AppForumDto>(data, new(1, data.Length, data.Length, false, DateTimeOffset.UtcNow, connectionRef)));
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/api/courses/{connectionRef}/{courseId}/forums/{forumId}", async (
            string connectionRef,
            string courseId,
            string forumId,
            int? page,
            int? pageSize,
            bool? includePosts,
            HttpContext context,
            ConnectorDbContext dbContext,
            IConnectionRegistry connectionRegistry,
            IMoodleCurrentUserIdGateway currentUserIdGateway,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            if (!HasAppPermission(context, AppPermissionCatalog.CoursesView)) return Results.Forbid();
            var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
            if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
            var userId = await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
            var result = await mediator.Send(new ReadForumQuery(
                userId.ToString(), courseId, forumId, Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? 10, 1, 25), "timemodified", "DESC", includePosts ?? true, 10), cancellationToken);
            return result is null
                ? AppErrorResults.NotFound("forum_not_found", "Fórum não encontrado neste curso.")
                : Results.Ok(new AppEnvelope<AppForumReadDto>(AppForumContractMapper.ToDto(result), new(DateTimeOffset.UtcNow, connectionRef)));
        }).RequireRateLimiting(rateLimitPolicy);

    }

    private static Task<AppIdentity?> ResolveAppIdentityAsync(
        HttpContext context,
        ConnectorDbContext dbContext,
        CancellationToken cancellationToken) =>
        PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);

    private static bool HasAppPermission(HttpContext context, string permission) =>
        PortalEndpointAuthorization.HasAppPermission(context, permission);
}
