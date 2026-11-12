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

        // Browser sessions are issued with a stable local account GUID.
        // Never resolve an unknown/missing subject by an untrusted email claim:
        // an old cookie could otherwise be rebound to a different account
        // when an email is reused after deletion.
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(OpenIddictConstants.Claims.Subject)
            ?? principal.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
            return null;

        var account = await dbContext.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        return account is null
            ? null
            : new AppIdentity(account.Id, account.Name, account.Email, account.ConnectorClientId);
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

        return context.User.FindAll("platform_permission")
            .Any(x => string.Equals(x.Value, permission, StringComparison.OrdinalIgnoreCase) ||
                      (platformPermission is not null && string.Equals(x.Value, platformPermission, StringComparison.OrdinalIgnoreCase)));
    }

}
