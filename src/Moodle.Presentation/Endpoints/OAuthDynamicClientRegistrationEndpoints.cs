using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Moodle.Presentation.Configuration;
using OpenIddict.Abstractions;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Implements OAuth Dynamic Client Registration for ChatGPT MCP connections.
/// </summary>
internal static class OAuthDynamicClientRegistrationEndpoints
{
    private const int MaximumRequestBytes = 16 * 1024;
    private const int MaximumRedirectUris = 10;
    private const string StableChatGptRedirectUri = "/connector_platform_oauth_redirect";

    public static void Map(WebApplication app, string mcpPath, string rateLimitPolicy)
    {
        app.MapPost("/register", async (
                HttpContext context,
                IOpenIddictApplicationManager applicationManager,
                IOptions<OAuthBrokerOptions> oauth,
                CancellationToken cancellationToken) =>
            {
                var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodySizeFeature is { IsReadOnly: false })
                {
                    bodySizeFeature.MaxRequestBodySize = MaximumRequestBytes;
                }
                if (context.Request.ContentLength is > MaximumRequestBytes)
                {
                    return InvalidMetadata("The registration request is too large.");
                }

                if (!context.Request.HasJsonContentType())
                {
                    return InvalidMetadata("The registration request must use application/json.");
                }

                DynamicClientRegistrationRequest? registration;
                try
                {
                    registration = await context.Request.ReadFromJsonAsync<DynamicClientRegistrationRequest>(
                        cancellationToken);
                }
                catch (JsonException)
                {
                    return InvalidMetadata("The registration request contains invalid JSON.");
                }

                if (registration is null ||
                    registration.RedirectUris is not { Length: > 0 and <= MaximumRedirectUris } ||
                    registration.RedirectUris.Any(uri => !IsAllowedRedirectUri(uri)) ||
                    registration.RedirectUris.Distinct(StringComparer.Ordinal).Count() != registration.RedirectUris.Length)
                {
                    return InvalidMetadata("redirect_uris must contain one or more approved ChatGPT callback URLs.");
                }

                var grantTypes = registration.GrantTypes ?? [OpenIddictConstants.GrantTypes.AuthorizationCode];
                if (grantTypes.Length is 0 or > 2 ||
                    !grantTypes.Contains(OpenIddictConstants.GrantTypes.AuthorizationCode, StringComparer.Ordinal) ||
                    grantTypes.Any(grant => grant is not OpenIddictConstants.GrantTypes.AuthorizationCode and
                        not OpenIddictConstants.GrantTypes.RefreshToken))
                {
                    return InvalidMetadata("Only authorization_code and refresh_token grant types are supported.");
                }

                var responseTypes = registration.ResponseTypes ?? [OpenIddictConstants.ResponseTypes.Code];
                if (responseTypes.Length != 1 ||
                    !responseTypes.Contains(OpenIddictConstants.ResponseTypes.Code, StringComparer.Ordinal))
                {
                    return InvalidMetadata("Only the code response type is supported.");
                }

                if (!string.IsNullOrWhiteSpace(registration.TokenEndpointAuthMethod) &&
                    !string.Equals(registration.TokenEndpointAuthMethod, "none", StringComparison.Ordinal))
                {
                    return InvalidMetadata("Only public OAuth clients using token_endpoint_auth_method 'none' are supported.");
                }

                if (!string.IsNullOrWhiteSpace(registration.ClientName) &&
                    (registration.ClientName.Length > 128 || registration.ClientName.Any(char.IsControl)))
                {
                    return InvalidMetadata("client_name must contain at most 128 printable characters.");
                }

                var clientId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
                var displayName = string.IsNullOrWhiteSpace(registration.ClientName)
                    ? "Moodle plugin client"
                    : registration.ClientName.Trim();
                var publicBaseUrl = OperationalEndpoints.GetPublicBaseUrl(context);
                var audience = OperationalEndpoints.ResolveOAuthAudience(oauth.Value, publicBaseUrl, mcpPath);
                var descriptor = CreateApplicationDescriptor(
                    clientId,
                    displayName,
                    registration.RedirectUris.Select(uri => new Uri(uri, UriKind.Absolute)),
                    grantTypes.Contains(OpenIddictConstants.GrantTypes.RefreshToken, StringComparer.Ordinal),
                    oauth.Value,
                    audience);

                await applicationManager.CreateAsync(descriptor, cancellationToken);

                return Results.Json(new
                {
                    client_id = clientId,
                    client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    client_name = displayName,
                    redirect_uris = registration.RedirectUris,
                    grant_types = grantTypes,
                    response_types = responseTypes,
                    token_endpoint_auth_method = "none"
                }, statusCode: StatusCodes.Status201Created);
            })
            .RequireRateLimiting(rateLimitPolicy);
    }

    internal static bool IsAllowedRedirectUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.IsWellFormedOriginalString() ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.IdnHost, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (string.Equals(uri.AbsolutePath, StableChatGptRedirectUri, StringComparison.Ordinal))
        {
            return true;
        }

        const string callbackPrefix = "/connector/oauth/";
        if (!uri.AbsolutePath.StartsWith(callbackPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var callbackId = uri.AbsolutePath[callbackPrefix.Length..];
        return callbackId.Length is > 0 and <= 128 &&
               callbackId.All(character =>
                   char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    internal static OpenIddictApplicationDescriptor CreateApplicationDescriptor(
        string clientId,
        string displayName,
        IEnumerable<Uri> redirectUris,
        bool allowRefreshToken,
        OAuthBrokerOptions oauth,
        string audience)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = displayName,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            ConsentType = OpenIddictConstants.ConsentTypes.Implicit
        };

        foreach (var redirectUri in redirectUris)
        {
            descriptor.RedirectUris.Add(redirectUri);
        }

        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode);
        if (allowRefreshToken)
        {
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.RefreshToken);
        }

        descriptor.Permissions.Add(OpenIddictConstants.Permissions.ResponseTypes.Code);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Email);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Profile);
        foreach (var scope in OperationalEndpoints.GetMcpOauthScopes(oauth))
        {
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + scope);
        }

        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Resource + audience);
        descriptor.Requirements.Add(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
        return descriptor;
    }

    private static IResult InvalidMetadata(string description) =>
        Results.Json(new
        {
            error = "invalid_client_metadata",
            error_description = description
        }, statusCode: StatusCodes.Status400BadRequest);

    private sealed class DynamicClientRegistrationRequest
    {
        [JsonPropertyName("client_name")]
        public string? ClientName { get; init; }

        [JsonPropertyName("redirect_uris")]
        public string[]? RedirectUris { get; init; }

        [JsonPropertyName("grant_types")]
        public string[]? GrantTypes { get; init; }

        [JsonPropertyName("response_types")]
        public string[]? ResponseTypes { get; init; }

        [JsonPropertyName("token_endpoint_auth_method")]
        public string? TokenEndpointAuthMethod { get; init; }
    }
}
