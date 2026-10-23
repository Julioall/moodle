using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;
using OpenIddict.Abstractions;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Regras compartilhadas de identidade e autorização da API do portal.
/// O bearer OAuth do MCP não cria uma sessão do portal: esta resolução depende
/// de uma identidade autenticada pelo cookie local.
/// </summary>
internal static class PortalEndpointAuthorization
{
    public static async Task<AppIdentity?> ResolveAppIdentityAsync(
        HttpContext context,
        ConnectorDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? principal.FindFirstValue("preferred_username");
        var name = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("name")
            ?? principal.FindFirstValue("preferred_username")
            ?? email;
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(OpenIddictConstants.Claims.Subject)
            ?? principal.FindFirstValue("sub");

        if (Guid.TryParse(subject, out var userId))
        {
            var accountById = await dbContext.UserAccounts.FindAsync([userId], cancellationToken);
            if (accountById is not null)
            {
                return new AppIdentity(
                    accountById.Id,
                    accountById.Name,
                    accountById.Email,
                    accountById.ConnectorClientId);
            }
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var accountByEmail = await dbContext.UserAccounts
            .SingleOrDefaultAsync(account => account.Email == normalizedEmail, cancellationToken);
        if (accountByEmail is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(name) &&
            !string.Equals(accountByEmail.Name, name.Trim(), StringComparison.Ordinal))
        {
            accountByEmail.Name = name.Trim();
            accountByEmail.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new AppIdentity(
            accountByEmail.Id,
            accountByEmail.Name,
            accountByEmail.Email,
            accountByEmail.ConnectorClientId);
    }

    public static bool HasAppPermission(HttpContext context, string permission)
    {
        var platformPermission = permission switch
        {
            AppPermissionCatalog.ConnectionsManage => "tool.connections.manage",
            _ => null
        };

        if (context.User.FindAll("platform_permission_deny").Any(x =>
                string.Equals(x.Value, permission, StringComparison.OrdinalIgnoreCase) ||
                (platformPermission is not null && string.Equals(x.Value, platformPermission, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if ((string.Equals(permission, AppPermissionCatalog.SettingsView, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(permission, AppPermissionCatalog.AdminView, StringComparison.OrdinalIgnoreCase)) &&
            HasPlatformToolPermission(context.User, PlatformPermissionCatalog.PermissionGroupsManage))
        {
            return true;
        }

        return context.User.FindAll("platform_permission")
            .Any(x => string.Equals(x.Value, permission, StringComparison.OrdinalIgnoreCase) ||
                      (platformPermission is not null && string.Equals(x.Value, platformPermission, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasPlatformToolPermission(ClaimsPrincipal? principal, string permission)
    {
        if (principal is null)
        {
            return false;
        }

        if (principal.FindAll("platform_permission_deny")
            .Any(x => string.Equals(x.Value, permission, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return principal.FindAll("platform_permission")
            .Any(x => string.Equals(x.Value, permission, StringComparison.OrdinalIgnoreCase));
    }
}
