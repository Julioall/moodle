using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Infrastructure;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Borda OAuth de autorização. Mantém o protocolo fora do bootstrap do host e
/// aplica o escopo efetivo da conta e da conexão Moodle ativa.
/// </summary>
internal static class OAuthAuthorizationEndpoints
{
    public static void MapAuthorization(WebApplication app, string mcpPath)
    {
        app.MapMethods("/authorize", [HttpMethods.Get, HttpMethods.Post], async (
            HttpContext context,
            ConnectorDbContext dbContext,
            IPlatformPermissionService platformPermissionService,
            IOptions<OAuthBrokerOptions> oauth,
            CancellationToken cancellationToken) =>
        {
            var request = Microsoft.AspNetCore.OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(context)
                ?? throw new InvalidOperationException("Requisicao OAuth invalida.");

            var authenticateResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (authenticateResult.Principal?.Identity?.IsAuthenticated != true)
            {
                var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                return Results.Redirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                return Results.Redirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            var claims = new ClaimsIdentity(
                TokenValidationParameters.DefaultAuthenticationType,
                OpenIddictConstants.Claims.Name,
                OpenIddictConstants.Claims.Role);

            claims.SetClaim(OpenIddictConstants.Claims.Subject, identity.Id.ToString());
            claims.SetClaim(OpenIddictConstants.Claims.Name, identity.Name);
            claims.SetClaim(OpenIddictConstants.Claims.Email, identity.Email);
            claims.SetClaim(
                "connector_client_id",
                string.IsNullOrWhiteSpace(identity.ConnectorClientId)
                    ? identity.Id.ToString()
                    : identity.ConnectorClientId);

            var effectivePermissions = await platformPermissionService.GetEffectivePermissionsAsync(identity.Id, cancellationToken);
            foreach (var permission in effectivePermissions)
            {
                claims.AddClaim(new Claim("platform_permission", permission));
            }

            var principal = new ClaimsPrincipal(claims);
            var protocolScopes = OperationalEndpoints.GetProtocolOAuthScopes(oauth.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var delegatedScopes = ToolAuthorizationMapping.ScopesForPermissions(effectivePermissions)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var connection = string.IsNullOrWhiteSpace(identity.ConnectorClientId)
                ? null
                : await dbContext.ConnectorClients
                    .AsNoTracking()
                    .Where(item => item.ClientId == identity.ConnectorClientId && item.IsActive)
                    .OrderByDescending(item => item.IsDefault)
                    .FirstOrDefaultAsync(cancellationToken);
            if (connection is null)
            {
                delegatedScopes.RemoveWhere(scope => scope.StartsWith("moodle.read.", StringComparison.OrdinalIgnoreCase) ||
                                                     scope.StartsWith("moodle.write.", StringComparison.OrdinalIgnoreCase));
            }
            else if (!connection.CanWrite)
            {
                delegatedScopes.RemoveWhere(scope => scope.StartsWith("moodle.write.", StringComparison.OrdinalIgnoreCase));
            }

            var grantedScopes = request.GetScopes()
                .Where(scope => protocolScopes.Contains(scope) || delegatedScopes.Contains(scope))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            principal.SetScopes(grantedScopes);

            var resource = request.GetParameter("resource")?.ToString();
            if (string.IsNullOrWhiteSpace(resource))
            {
                resource = OperationalEndpoints.ResolveOAuthAudience(
                    oauth.Value,
                    OperationalEndpoints.GetPublicBaseUrl(context),
                    mcpPath);
            }

            principal.SetResources(resource);

            foreach (var claim in principal.Claims)
            {
                claim.SetDestinations(GetClaimDestinations(claim));
            }

            return Results.SignIn(
                principal,
                authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        });
    }

    private static IEnumerable<string> GetClaimDestinations(Claim claim)
    {
        return claim.Type switch
        {
            OpenIddictConstants.Claims.Subject or OpenIddictConstants.Claims.Name or OpenIddictConstants.Claims.Email =>
                [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
            _ => [OpenIddictConstants.Destinations.AccessToken]
        };
    }
}
