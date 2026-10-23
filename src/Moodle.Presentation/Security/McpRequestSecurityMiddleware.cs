using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Security;

/// <summary>
/// Restores the local account claims required by the portal and MCP tool
/// authorization boundary. Authentication remains scheme-specific; this
/// component only enriches an already authenticated principal.
/// </summary>
internal sealed class McpPrincipalEnricher(
    ConnectorDbContext dbContext,
    IPlatformPermissionService permissionService,
    IMcpToolUsageTelemetry toolTelemetry)
{
    public async Task EnrichAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? principal.FindFirstValue("preferred_username");
        if (string.IsNullOrWhiteSpace(email) || principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var account = await toolTelemetry.MeasureRuntimePhaseAsync(
            "account_lookup",
            () => dbContext.UserAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(user => user.Email == normalizedEmail, cancellationToken),
            result => result is null ? "not_found" : "success");
        if (account is null)
        {
            return;
        }

        foreach (var existingClaim in identity.FindAll("connector_client_id")
                     .Concat(identity.FindAll("platform_permission"))
                     .Concat(identity.FindAll("platform_permission_deny"))
                     .Concat(identity.FindAll("team_id"))
                     .Concat(identity.FindAll(ClaimTypes.Role))
                     .Concat(identity.FindAll("role"))
                     .ToArray())
        {
            identity.RemoveClaim(existingClaim);
        }

        if (!string.IsNullOrWhiteSpace(account.ConnectorClientId))
        {
            identity.AddClaim(new Claim("connector_client_id", account.ConnectorClientId));
        }

        var effectivePermissions = await toolTelemetry.MeasureRuntimePhaseAsync(
            "permissions",
            () => permissionService.GetEffectivePermissionsAsync(account.Id, cancellationToken));
        foreach (var permission in effectivePermissions)
        {
            identity.AddClaim(new Claim("platform_permission", permission));
        }

        var overrides = await toolTelemetry.MeasureRuntimePhaseAsync(
            "permission_overrides",
            () => dbContext.UserPermissionOverrides.AsNoTracking()
                .Where(item => item.UserId == account.Id && !item.IsAllowed)
                .ToArrayAsync(cancellationToken));
        foreach (var deny in overrides)
        {
            identity.AddClaim(new Claim("platform_permission_deny", deny.Permission));
        }

        var memberships = await toolTelemetry.MeasureRuntimePhaseAsync(
            "team_memberships",
            () => dbContext.TeamMemberships
                .AsNoTracking()
                .Where(item => item.UserId == account.Id && item.IsActive)
                .ToArrayAsync(cancellationToken));
        foreach (var membership in memberships)
        {
            identity.AddClaim(new Claim("team_id", membership.TeamId.ToString()));
            identity.AddClaim(new Claim(ClaimTypes.Role, membership.Role));
        }
    }
}

/// <summary>
/// Applies local account enrichment only to the portal API and MCP boundary.
/// Static assets and public OAuth discovery endpoints never receive a database
/// lookup as a side effect.
/// </summary>
internal sealed class AuthenticatedPrincipalEnrichmentMiddleware(McpPrincipalEnricher enricher) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if ((context.Request.Path.StartsWithSegments("/api") ||
             context.Request.Path.StartsWithSegments(McpRequestSecurityMiddleware.McpPath, StringComparison.OrdinalIgnoreCase)) &&
            context.User.Identity?.IsAuthenticated == true)
        {
            await enricher.EnrichAsync(context, context.RequestAborted);
        }

        await next(context);
    }
}

/// <summary>
/// Protects the human portal surface with the cookie-only policy. OAuth bearer
/// tokens remain scoped to <c>/mcp</c>; they cannot become a substitute for a
/// browser session on <c>/api</c>.
/// </summary>
internal sealed class PortalApiAuthorizationMiddleware(IAuthorizationService authorizationService) : IMiddleware
{
    private static readonly PathString[] PublicPaths =
    [
        "/api/status",
        "/api/info",
        "/api/csrf",
        "/api/account/register",
        "/api/account/login"
    ];

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/api") ||
            PublicPaths.Any(path => context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var authorization = await authorizationService.AuthorizeAsync(
            context.User,
            resource: null,
            MoodleScopePolicies.PortalSession);
        if (authorization.Succeeded)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            ok = false,
            error = "portal_session_required",
            message = "Faça login no portal para acessar este recurso."
        }, context.RequestAborted);
    }
}

/// <summary>
/// Keeps the administrative API-key contract outside individual endpoint
/// handlers. It is deliberately separate from browser cookies and MCP OAuth.
/// </summary>
internal sealed class AdminApiKeyAuthorizationMiddleware(IOptions<AdminApiOptions> adminOptions) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/admin"))
        {
            await next(context);
            return;
        }

        var options = adminOptions.Value;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "about:blank",
                title = "admin_api_key_not_configured",
                status = StatusCodes.Status503ServiceUnavailable,
                detail = "Configure AdminApi:ApiKey antes de usar o endpoint administrativo."
            }, context.RequestAborted);
            return;
        }

        var providedKey = context.Request.Headers[options.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(providedKey) || !ConstantTimeEquals(providedKey, options.ApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }

    private static bool ConstantTimeEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

/// <summary>
/// Owns the transport-level MCP security contract. It intentionally does not
/// authorize portal routes: portal session/CSRF authorization lives under
/// <c>/api</c>, while this middleware protects only <c>/mcp</c>.
/// </summary>
internal sealed class McpRequestSecurityMiddleware(
    IOptions<McpServerSecurityOptions> securityOptions,
    IMcpConnectorClientResolver connectorClientResolver,
    ConnectorDbContext dbContext,
    IPlatformPermissionService permissionService,
    IAuthorizationAuditService authorizationAuditService,
    McpFixedWindowRateLimiter rateLimiter,
    IMcpToolUsageTelemetry toolTelemetry,
    McpPrincipalEnricher principalEnricher,
    ILogger<McpRequestSecurityMiddleware> logger) : IMiddleware
{
    public const string McpPath = "/mcp";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments(McpPath, StringComparison.OrdinalIgnoreCase) ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var security = securityOptions.Value;
        if (!HasCredentials(context, security) && await IsDiscoveryRequestAsync(context))
        {
            await next(context);
            return;
        }

        var hasApiKey = !string.IsNullOrWhiteSpace(context.Request.Headers[security.ApiKeyHeader].ToString());
        var hasBearerToken = HasBearerToken(context);
        var isAuthenticated = false;

        if (security.RequireApiKey && hasApiKey)
        {
            var providedApiKey = context.Request.Headers[security.ApiKeyHeader].ToString();
            var client = await toolTelemetry.MeasureRuntimePhaseAsync(
                "api_key_lookup",
                () => connectorClientResolver.ResolveByApiKeyAsync(providedApiKey, context.RequestAborted),
                result => result is null ? "not_found" : "success");
            if (client is not null)
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, client.ClientId),
                    new("connector_client_id", client.ClientId)
                };

                if (client.CanWrite)
                {
                    claims.Add(new("scope", "moodle.write"));
                }

                var accountId = await toolTelemetry.MeasureRuntimePhaseAsync(
                    "account_lookup",
                    () => dbContext.UserAccounts
                        .AsNoTracking()
                        .Where(account => account.ConnectorClientId == client.ClientId)
                        .Select(account => (Guid?)account.Id)
                        .SingleOrDefaultAsync(context.RequestAborted),
                    result => result is null ? "not_found" : "success");
                if (accountId is Guid localUserId)
                {
                    var effectivePermissions = await toolTelemetry.MeasureRuntimePhaseAsync(
                        "permissions",
                        () => permissionService.GetEffectivePermissionsAsync(localUserId, context.RequestAborted));
                    foreach (var permission in effectivePermissions)
                    {
                        claims.Add(new Claim("platform_permission", permission));
                    }
                }
                else
                {
                    foreach (var permission in PlatformPermissionCatalog.AllRead)
                    {
                        claims.Add(new Claim("platform_permission", permission));
                    }

                    if (client.CanWrite)
                    {
                        foreach (var permission in PlatformPermissionCatalog.AllWrite)
                        {
                            claims.Add(new Claim("platform_permission", permission));
                        }
                    }
                }

                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "connector-api-key"));
                isAuthenticated = true;
            }
            else if (!security.RequireJwt || !hasBearerToken)
            {
                if (security.RequireJwt && await TryWriteOAuthToolChallengeAsync(
                        context,
                        "invalid_token",
                        "API key invalida. Faça login via OAuth para continuar."))
                {
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await AuditFailureAsync(context, "invalid_api_key", "API key do conector invalida ou inativa.");
                await context.Response.WriteAsJsonAsync(new
                {
                    ok = false,
                    error = "invalid_api_key",
                    message = "API key do conector invalida ou inativa."
                });
                return;
            }
        }

        if (!isAuthenticated && security.RequireJwt && hasBearerToken)
        {
            var authResult = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
            if (!authResult.Succeeded || authResult.Principal?.Identity?.IsAuthenticated != true)
            {
                if (await TryWriteOAuthToolChallengeAsync(
                        context,
                        "invalid_token",
                        "JWT ausente, expirado ou rejeitado pelo broker OAuth. Faça login novamente."))
                {
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                SetOAuthAuthenticateHeader(context);
                await AuditFailureAsync(context, "missing_or_invalid_jwt", "JWT ausente, invalido ou rejeitado pelo broker OAuth.");
                await context.Response.WriteAsJsonAsync(new
                {
                    ok = false,
                    error = "missing_or_invalid_jwt",
                    message = "Envie Authorization: Bearer <jwt> valido emitido pelo broker OAuth."
                });
                return;
            }

            context.User = authResult.Principal;
            await principalEnricher.EnrichAsync(context, context.RequestAborted);
            if (!context.User.HasClaim(claim => claim.Type == "connector_client_id"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await AuditFailureAsync(
                    context,
                    "moodle_connection_not_linked",
                    "Usuario autenticado sem conexao Moodle vinculada.");
                await context.Response.WriteAsJsonAsync(new
                {
                    ok = false,
                    error = "moodle_connection_not_linked",
                    message = "Conecte uma conta Moodle antes de usar as tools do conector."
                });
                return;
            }

            isAuthenticated = true;
        }

        if (!isAuthenticated)
        {
            if (security.RequireJwt && security.RequireApiKey)
            {
                if (await TryWriteOAuthToolChallengeAsync(
                        context,
                        "invalid_token",
                        "Autenticação OAuth necessária para usar as tools do Moodle."))
                {
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                SetOAuthAuthenticateHeader(context);
                await AuditFailureAsync(context, "missing_mcp_credentials", "Credenciais MCP ausentes.");
                await context.Response.WriteAsJsonAsync(new
                {
                    ok = false,
                    error = "missing_mcp_credentials",
                    message = $"Envie Authorization: Bearer <jwt> valido ou o header {security.ApiKeyHeader} com uma API key valida do conector."
                });
                return;
            }

            if (security.RequireJwt)
            {
                if (await TryWriteOAuthToolChallengeAsync(
                        context,
                        "invalid_token",
                        "Autenticação OAuth necessária para usar as tools do Moodle."))
                {
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                SetOAuthAuthenticateHeader(context);
                await AuditFailureAsync(context, "missing_or_invalid_jwt", "JWT ausente ou invalido.");
                await context.Response.WriteAsJsonAsync(new
                {
                    ok = false,
                    error = "missing_or_invalid_jwt",
                    message = "Envie Authorization: Bearer <jwt> valido emitido pelo broker OAuth."
                });
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await AuditFailureAsync(context, "missing_api_key", "API key MCP ausente.");
            await context.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = "missing_api_key",
                message = $"Envie o header {security.ApiKeyHeader} com uma API key valida do conector."
            });
            return;
        }

        if (!rateLimiter.TryAcquire(GetRateLimitPartitionKey(context, security.ApiKeyHeader), out var retryAfter))
        {
            AdmissionFailureTelemetry.MarkMcpRateLimited(context);
            toolTelemetry.RecordTransportRateLimitRejection();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString("0");
            await context.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = "rate_limited",
                message = "Limite de chamadas MCP excedido para este usuario/conector. Tente novamente depois."
            });
            return;
        }

        try
        {
            await next(context);
        }
        catch (JsonException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteParseErrorAsync(context, exception, "invalid_mcp_request", "Requisição MCP invalida. Envie um payload JSON-RPC 2.0 valido.");
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteParseErrorAsync(context, exception, "invalid_mcp_request", "Requisição MCP invalida. Nao foi possivel ler o payload recebido.");
        }
    }

    internal static bool HasBearerToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(authorization["Bearer ".Length..]);
    }

    private static bool HasCredentials(HttpContext context, McpServerSecurityOptions security) =>
        HasBearerToken(context) ||
        !string.IsNullOrWhiteSpace(context.Request.Headers[security.ApiKeyHeader].ToString());

    private static string GetRateLimitPartitionKey(HttpContext context, string apiKeyHeader)
    {
        var connectorClientId = context.User.FindFirst("connector_client_id")?.Value;
        if (!string.IsNullOrWhiteSpace(connectorClientId)) return $"connector:{connectorClientId}";

        var subject = context.User.FindFirst("sub")?.Value ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(subject)) return $"subject:{subject}";

        var apiKey = context.Request.Headers[apiKeyHeader].ToString();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
            return $"api-key:{Convert.ToHexString(hash)}";
        }

        var address = context.Connection.RemoteIpAddress;
        var clientAddress = address is null
            ? null
            : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        return $"ip:{clientAddress ?? "unknown"}";
    }

    private static async Task<bool> IsDiscoveryRequestAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.ContentLength is 0) return false;

        context.Request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (!document.RootElement.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String) return false;

            return methodElement.GetString() is
                "initialize" or
                "notifications/initialized" or
                "tools/list" or
                "resources/templates/list";
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            context.Request.Body.Position = 0;
        }
    }

    private async Task<bool> TryWriteOAuthToolChallengeAsync(HttpContext context, string error, string errorDescription)
    {
        var response = await BuildOAuthToolChallengeResponseAsync(context, error, errorDescription);
        if (response is null) return false;

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(response, context.RequestAborted);
        await AuditFailureAsync(context, error, errorDescription);
        return true;
    }

    private static async Task<JsonObject?> BuildOAuthToolChallengeResponseAsync(HttpContext context, string error, string errorDescription)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.ContentLength is 0) return null;

        context.Request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (!document.RootElement.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                !string.Equals(methodElement.GetString(), "tools/call", StringComparison.Ordinal)) return null;

            JsonNode? id = document.RootElement.TryGetProperty("id", out var idElement)
                ? JsonNode.Parse(idElement.GetRawText())
                : null;
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = errorDescription }),
                    ["_meta"] = new JsonObject
                    {
                        ["mcp/www_authenticate"] = new JsonArray(BuildOAuthAuthenticateChallenge(context, error, errorDescription))
                    },
                    ["isError"] = true
                }
            };
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            context.Request.Body.Position = 0;
        }
    }

    private async Task AuditFailureAsync(HttpContext context, string reason, string message)
    {
        var principal = context.User;
        var actorSubject = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("connector_client_id");
        var actorEmail = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email") ?? principal.FindFirstValue("preferred_username");
        await authorizationAuditService.RecordFailureAsync(new AuthorizationFailureAuditRequest(
            "mcp_auth", reason, message, actorSubject, actorEmail, context.Request.Path.Value,
            principal.Identity?.AuthenticationType), context.RequestAborted);
    }

    private async Task WriteParseErrorAsync(HttpContext context, Exception exception, string errorCode, string message)
    {
        logger.LogWarning("MCP request parsing failed. ErrorCode={ErrorCode}", errorCode);
        await context.Response.WriteAsJsonAsync(new { ok = false, error = errorCode, message }, context.RequestAborted);
    }

    private static void SetOAuthAuthenticateHeader(HttpContext context) =>
        context.Response.Headers.WWWAuthenticate = BuildOAuthAuthenticateChallenge(
            context, "invalid_token", "Token ausente, expirado ou invalido para o Moodle.");

    private static string BuildOAuthAuthenticateChallenge(HttpContext context, string error, string errorDescription)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var appDomain = Environment.GetEnvironmentVariable("APP_DOMAIN") ?? configuration["APP_DOMAIN"];
        var publicBaseUrl = string.IsNullOrWhiteSpace(appDomain)
            ? $"{context.Request.Scheme}://{context.Request.Host}"
            : appDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || appDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? appDomain.TrimEnd('/')
                : $"https://{appDomain.TrimEnd('/')}";
        var oauth = context.RequestServices.GetRequiredService<IOptions<OAuthBrokerOptions>>().Value;
        var audienceScope = string.IsNullOrWhiteSpace(oauth.ScopeName) ? "moodle-mcp-audience" : oauth.ScopeName.Trim();
        var scopes = new[] { "openid", "profile", "email", "offline_access", audienceScope };
        return string.Join(", ", new[]
        {
            $"Bearer resource_metadata=\"{publicBaseUrl}/.well-known/oauth-protected-resource/mcp\"",
            $"scope=\"{EscapeHeaderValue(string.Join(' ', scopes))}\"",
            $"error=\"{EscapeHeaderValue(error)}\"",
            $"error_description=\"{EscapeHeaderValue(errorDescription)}\""
        });
    }

    private static string EscapeHeaderValue(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
