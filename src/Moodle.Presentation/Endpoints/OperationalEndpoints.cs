using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moodle.Application.Configuration;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;
using Moodle.Presentation.Tools;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Endpoints públicos usados por infraestrutura, monitores e clientes OAuth.
/// Mantê-los fora do bootstrap evita que contratos operacionais se misturem às rotas do portal.
/// </summary>
internal static class OperationalEndpoints
{
    public static void MapStatusAndHealth(
        WebApplication app,
        string mcpPath)
    {
        app.MapGet("/api/status", (
            HttpContext context,
            IOptions<McpServerSecurityOptions> security,
            IOptions<OAuthBrokerOptions> oauth,
            IOptions<AssignmentWriteFeatureOptions> assignmentWrites,
            IOptions<FeatureOptions> features,
            ToolSurfaceInventory inventory,
            ConnectorBuildInfoProvider buildInfoProvider) =>
        {
            var publicBaseUrl = GetPublicBaseUrl(context);
            var build = buildInfoProvider.Get();
            var individualGradeToolCount = inventory.Entries.Count(entry =>
                entry.Family.Contains("individualgrade", StringComparison.OrdinalIgnoreCase));
            var disabledToolCount =
                             (assignmentWrites.Value.AssignmentGradeWriteEnabled ? 0 : individualGradeToolCount);
            var toolsCount = inventory.Total - inventory.ProductionHiddenCount - disabledToolCount;
            return Results.Ok(new
            {
                ok = true,
                service = "moodle-gpt-connector",
                status = "online",
                transport = "mcp-streamable-http",
                endpoint = mcpPath,
                source = "aspnetcore-mcp",
                version = build.Version ?? "unknown",
                gitCommit = build.Commit ?? "unknown",
                buildDate = build.BuiltAt?.ToString("O") ?? "unknown",
                buildId = build.BuildId ?? "unknown",
                deployedAt = build.DeployedAt?.ToString("O") ?? "unknown",
                build,
                toolsCount,
                universalExecutorEnabled = true,
                capabilityDiscoveryEnabled = true,
                auth = new
                {
                    requireJwt = security.Value.RequireJwt,
                    requireApiKey = security.Value.RequireApiKey,
                    issuer = ResolveOAuthIssuer(oauth.Value, publicBaseUrl),
                    audience = ResolveOAuthAudience(oauth.Value, publicBaseUrl, mcpPath),
                    dynamicClientRegistrationEnabled = security.Value.RequireJwt,
                    chatGptClientConfigured =
                        !string.IsNullOrWhiteSpace(oauth.Value.ChatGptClientId) &&
                        !string.IsNullOrWhiteSpace(oauth.Value.ChatGptRedirectUri),
                    chatGptRedirectConfigured = !string.IsNullOrWhiteSpace(oauth.Value.ChatGptRedirectUri)
                }
            });
        });

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        });
    }

    public static void MapOAuthDiscovery(WebApplication app, string mcpPath)
    {
        Func<HttpContext, IResult> protectedResourceMetadata = context =>
            BuildOAuthProtectedResourceMetadata(context, mcpPath);
        app.MapGet("/.well-known/oauth-protected-resource", protectedResourceMetadata);
        app.MapGet("/.well-known/oauth-protected-resource/{**resourcePath}", protectedResourceMetadata);
        app.MapGet("/.well-known/oauth-authorization-server", BuildOAuthAuthorizationServerMetadata);
    }

    public static string[] GetMcpOauthScopes(OAuthBrokerOptions? options = null)
    {
        return GetProtocolOAuthScopes(options)
            .Concat(new[]
            {
                MoodleScopePolicies.ReadAny,
                MoodleScopePolicies.WriteAny,
                MoodleScopePolicies.ReadCourses,
                MoodleScopePolicies.ReadStudents,
                MoodleScopePolicies.ReadGroups,
                MoodleScopePolicies.ReadAccess,
                MoodleScopePolicies.ReadContents,
                MoodleScopePolicies.ReadResources,
                MoodleScopePolicies.ReadActivities,
                MoodleScopePolicies.ReadAssignments,
                MoodleScopePolicies.ReadSubmissions,
                MoodleScopePolicies.ReadQuizzes,
                MoodleScopePolicies.ReadScorms,
                MoodleScopePolicies.ReadForums,
                MoodleScopePolicies.WriteMessages,
                MoodleScopePolicies.WriteAssignmentsFeedback,
                MoodleScopePolicies.WriteAssignmentsGrade,
                MoodleScopePolicies.WriteCourseContent,
                MoodleScopePolicies.WriteForums
            })
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string[] GetProtocolOAuthScopes(OAuthBrokerOptions? options = null)
    {
        var audienceScope = string.IsNullOrWhiteSpace(options?.ScopeName)
            ? "moodle-mcp-audience"
            : options!.ScopeName.Trim();
        return ["openid", "profile", "email", "offline_access", audienceScope];
    }

    public static string GetPublicBaseUrl(HttpContext context)
    {
        var configuredBaseUrl = BuildPublicBaseUrlFromAppDomain(
            Environment.GetEnvironmentVariable("APP_DOMAIN") ??
            context.RequestServices.GetRequiredService<IConfiguration>()["APP_DOMAIN"]);
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return configuredBaseUrl;
        }

        return $"{context.Request.Scheme}://{context.Request.Host}";
    }

    public static string? BuildPublicBaseUrlFromAppDomain(string? appDomain)
    {
        if (string.IsNullOrWhiteSpace(appDomain))
        {
            return null;
        }

        var normalized = appDomain.Trim();
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.TrimEnd('/');
        }

        return $"https://{normalized.TrimEnd('/')}";
    }

    public static string ResolveOAuthIssuer(OAuthBrokerOptions options, string publicBaseUrl)
    {
        var configuredIssuer = string.IsNullOrWhiteSpace(options.Issuer)
            ? publicBaseUrl
            : options.Issuer.Trim();

        // OpenIddict serializes the issuer as an absolute URI. Return the same
        // canonical representation from every OAuth discovery surface so the
        // `iss` parameter in the authorization response matches exactly.
        return Uri.TryCreate(configuredIssuer, UriKind.Absolute, out var issuerUri)
            ? issuerUri.AbsoluteUri
            : configuredIssuer;
    }

    public static string ResolveOAuthAudience(OAuthBrokerOptions options, string publicBaseUrl, string mcpPath)
    {
        return string.IsNullOrWhiteSpace(options.Audience)
            ? $"{publicBaseUrl.TrimEnd('/')}{mcpPath}"
            : options.Audience.Trim();
    }

    private static IResult BuildOAuthProtectedResourceMetadata(HttpContext context, string mcpPath)
    {
        var oauth = context.RequestServices.GetRequiredService<IOptions<OAuthBrokerOptions>>();
        var security = context.RequestServices.GetRequiredService<IOptions<McpServerSecurityOptions>>();
        if (!security.Value.RequireJwt)
        {
            return Results.NotFound(new
            {
                error = "oauth_not_configured",
                message = "OAuth/JWT nao esta configurado para o endpoint MCP."
            });
        }

        var publicBaseUrl = GetPublicBaseUrl(context);
        var authority = ResolveOAuthIssuer(oauth.Value, publicBaseUrl);
        var scopes = GetMcpOauthScopes(oauth.Value);
        return Results.Json(new
        {
            resource = $"{publicBaseUrl}{mcpPath}",
            resource_name = "Moodle",
            authorization_servers = new[] { authority },
            scopes_supported = scopes,
            bearer_methods_supported = new[] { "header" },
            resource_documentation = $"{publicBaseUrl}/"
        });
    }

    private static IResult BuildOAuthAuthorizationServerMetadata(HttpContext context)
    {
        var oauth = context.RequestServices.GetRequiredService<IOptions<OAuthBrokerOptions>>();
        var publicBaseUrl = GetPublicBaseUrl(context);
        var issuer = ResolveOAuthIssuer(oauth.Value, publicBaseUrl);
        return Results.Json(new
        {
            issuer,
            authorization_endpoint = $"{publicBaseUrl}/authorize",
            token_endpoint = $"{publicBaseUrl}/token",
            registration_endpoint = $"{publicBaseUrl}/register",
            jwks_uri = $"{publicBaseUrl}/.well-known/jwks",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" },
            scopes_supported = GetMcpOauthScopes(oauth.Value),
            client_id_metadata_document_supported = false
        });
    }
}
