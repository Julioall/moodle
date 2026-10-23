using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;

namespace Moodle.Presentation.Tools.Portal;

/// <summary>
/// Resolves the local portal account represented by the authenticated MCP
/// principal. API keys may identify an account through connector_client_id,
/// while JWT/OAuth flows normally provide the account id or email.
/// </summary>
public sealed class PortalMcpIdentityResolver(IHttpContextAccessor httpContextAccessor, ConnectorDbContext dbContext)
{
    public async Task<PortalMcpIdentity> ResolveAsync(CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            throw new InvalidOperationException("Usuário autenticado não identificado.");

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        if (Guid.TryParse(subject, out var accountId))
        {
            var accountById = await dbContext.UserAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(account => account.Id == accountId, cancellationToken);
            if (accountById is not null)
                return ToIdentity(accountById);
        }

        var connectorClientId = principal.FindFirstValue("connector_client_id");
        if (!string.IsNullOrWhiteSpace(connectorClientId))
        {
            var accountByClient = await dbContext.UserAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(account => account.ConnectorClientId == connectorClientId, cancellationToken);
            if (accountByClient is not null)
                return ToIdentity(accountByClient);
        }

        var email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? principal.FindFirstValue("preferred_username");
        if (!string.IsNullOrWhiteSpace(email))
        {
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var accountByEmail = await dbContext.UserAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(account => account.Email == normalizedEmail, cancellationToken);
            if (accountByEmail is not null)
                return ToIdentity(accountByEmail);
        }

        throw new InvalidOperationException("Usuário autenticado não identificado.");
    }

    private static PortalMcpIdentity ToIdentity(UserAccountEntity account) =>
        new(account.Id, account.Name, account.Email, account.ConnectorClientId);
}

public sealed record PortalMcpIdentity(Guid Id, string Name, string Email, string? ConnectorClientId = null);
