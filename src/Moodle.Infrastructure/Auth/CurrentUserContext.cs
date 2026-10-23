using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

public sealed class CurrentUserContext(
    IHttpContextAccessor httpContextAccessor,
    IConnectorExecutionContext executionContext) : ICurrentUserContext
{
    public string Subject =>
        Principal?.FindFirst("sub")?.Value ??
        Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
        executionContext.Subject ??
        string.Empty;

    public string? Email =>
        Principal?.FindFirst("email")?.Value ??
        Principal?.FindFirst(ClaimTypes.Email)?.Value ??
        executionContext.Email;

    public IReadOnlyCollection<string> Scopes =>
        Principal?.FindAll("scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? executionContext.Scopes;

    public bool HasScope(string scope)
    {
        var currentScopes = Scopes;
        if (currentScopes.Contains(scope, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Hierarchical match: "moodle.write" is satisfied by "moodle.write.assignments.grade" etc.
        var prefix = scope + ".";
        return currentScopes.Any(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasPlatformPermission(string permission)
    {
        if (Principal?.FindAll("platform_permission_deny")
            .Any(claim => string.Equals(claim.Value, permission, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return false;
        }

        return Principal?.FindAll("platform_permission")
            .Any(claim => string.Equals(claim.Value, permission, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;
}
