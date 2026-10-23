using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Presentation.Security;
using OpenIddict.Abstractions;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Fluxos de sessão local do portal, isolados do OAuth usado pelas tools MCP.
/// </summary>
internal static class PortalAuthenticationEndpoints
{
    public static void MapLoginAndLogout(WebApplication app, string rateLimitPolicy)
    {
        app.MapGet("/api/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken });
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapGet("/auth/login", (string? email, string? returnUrl) =>
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(email))
            {
                query.Add($"email={Uri.EscapeDataString(email)}");
            }

            if (IsLocalReturnUrl(returnUrl))
            {
                query.Add($"returnUrl={Uri.EscapeDataString(returnUrl!)}");
            }

            return Results.Redirect($"/{(query.Count > 0 ? $"?{string.Join("&", query)}" : string.Empty)}");
        });

        app.MapPost("/auth/login", async (
            HttpContext context,
            ConnectorDbContext dbContext,
            IAccountService accountService,
            IPlatformPermissionService platformPermissionService,
            PublicAccountRateLimiter publicAccountRateLimiter,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            if (!publicAccountRateLimiter.TryAcquire(
                    PublicAccountOperation.Login,
                    email,
                    out var retryAfterSeconds))
            {
                return PublicAccountRateLimitResponse.TooManyRequests(context, retryAfterSeconds, html: true);
            }

            var account = await accountService.ValidateLoginAsync(
                new LoginAccountRequest(email, password),
                cancellationToken);

            if (account is null)
            {
                var query = new List<string>();
                if (!string.IsNullOrEmpty(email))
                {
                    query.Add($"email={Uri.EscapeDataString(email)}");
                }

                if (!string.IsNullOrEmpty(returnUrl))
                {
                    query.Add($"returnUrl={Uri.EscapeDataString(returnUrl)}");
                }

                query.Add("error=" + Uri.EscapeDataString("E-mail ou senha invalidos."));
                return Results.Redirect($"/?{string.Join("&", query)}");
            }

            await SignInAppAccountAsync(
                context,
                dbContext,
                platformPermissionService,
                account.Id,
                account.Name,
                account.Email,
                cancellationToken);
            return Results.Redirect(IsLocalReturnUrl(returnUrl) ? returnUrl : "/");
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            return Results.SignOut(authenticationSchemes: new[] { CookieAuthenticationDefaults.AuthenticationScheme });
        });
    }

    public static async Task SignInAppAccountAsync(
        HttpContext context,
        ConnectorDbContext dbContext,
        IPlatformPermissionService platformPermissionService,
        Guid id,
        string name,
        string email,
        CancellationToken cancellationToken)
    {
        // Existing accounts may predate platform-permission groups. Reconcile defaults
        // before issuing claims so MCP tools do not receive a stale or empty set.
        await platformPermissionService.EnsureDefaultPermissionsAsync(id, cancellationToken);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id.ToString()),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Email, email),
            new(OpenIddictConstants.Claims.Subject, id.ToString()),
            new(OpenIddictConstants.Claims.Name, name),
            new(OpenIddictConstants.Claims.Email, email)
        };

        var memberships = await dbContext.TeamMemberships
            .AsNoTracking()
            .Where(item => item.UserId == id && item.IsActive)
            .ToArrayAsync(cancellationToken);
        foreach (var membership in memberships)
        {
            claims.Add(new Claim("team_id", membership.TeamId.ToString()));
            claims.Add(new Claim(ClaimTypes.Role, membership.Role));
        }

        var effectivePermissions = await platformPermissionService.GetEffectivePermissionsAsync(id, cancellationToken);
        foreach (var permission in effectivePermissions)
        {
            claims.Add(new Claim("platform_permission", permission));
        }

        var userOverrides = await dbContext.UserPermissionOverrides
            .AsNoTracking()
            .Where(item => item.UserId == id)
            .ToArrayAsync(cancellationToken);
        foreach (var permission in userOverrides)
        {
            claims.Add(new Claim(
                permission.IsAllowed ? "platform_permission" : "platform_permission_deny",
                permission.Permission));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }

    private static bool IsLocalReturnUrl(string? returnUrl)
    {
        return !string.IsNullOrWhiteSpace(returnUrl) &&
               returnUrl.StartsWith("/", StringComparison.Ordinal) &&
               !returnUrl.StartsWith("//", StringComparison.Ordinal);
    }
}
