using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure;

internal sealed class HttpContextMoodleCredentialsProvider(
    IHttpContextAccessor httpContextAccessor,
    ConnectorDbContext dbContext,
    IConnectorSecretProtector secretProtector,
    IMoodleConnectionSelection selection,
    IConnectorExecutionContext executionContext,
    IMoodleEndpointValidator endpointValidator,
    IOptions<MoodleApiOptions> moodleApiOptions,
    ILogger<HttpContextMoodleCredentialsProvider> logger) : IMoodleCredentialsProvider, IMoodleConnectionCatalog, IDisposable
{
    internal HttpContextMoodleCredentialsProvider(
        IHttpContextAccessor httpContextAccessor,
        ConnectorDbContext dbContext,
        IConnectorSecretProtector secretProtector,
        IMoodleConnectionSelection selection,
        IMoodleEndpointValidator endpointValidator,
        ILogger<HttpContextMoodleCredentialsProvider> logger)
        : this(
            httpContextAccessor,
            dbContext,
            secretProtector,
            selection,
            new ConnectorExecutionContext(),
            endpointValidator,
            Options.Create(new MoodleApiOptions()),
            logger)
    {
    }

    private static readonly object ResolvedClientIdItemKey = new();
    private static readonly object CredentialsCacheItemKey = new();
    // File downloads may run concurrently inside one worker scope, while the
    // provider and its DbContext are scoped. Keep connection resolution
    // single-flight and cache only for this scope; the HTTP request cache
    // remains the first-level cache when a request context exists.
    private readonly SemaphoreSlim credentialsResolutionGate = new(1, 1);
    private readonly Dictionary<string, MoodleCredentials> resolvedCredentialsCache = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<string>> GetActiveAliasesAsync(CancellationToken cancellationToken)
    {
        await credentialsResolutionGate.WaitAsync(cancellationToken);
        try
        {
            var clientId = executionContext.ClientId ??
                await ResolveClientIdAsync(httpContextAccessor.HttpContext?.User, cancellationToken);
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw LogFailure(MoodleErrorContract.ConnectionNotFound,
                    "Authenticated connector client context was not found.",
                    stage: MoodleIntegrationStage.ConnectionLookup);
            }

            return await dbContext.ConnectorClients.AsNoTracking()
                .Where(connection => connection.ClientId == clientId && connection.IsActive)
                .Select(connection => connection.MoodleAlias)
                .Distinct()
                .OrderBy(alias => alias)
                .ToArrayAsync(cancellationToken);
        }
        finally
        {
            credentialsResolutionGate.Release();
        }
    }

    public async Task<IReadOnlyList<MoodleConnectionSummary>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
    {
        await credentialsResolutionGate.WaitAsync(cancellationToken);
        try
        {
            var clientId = executionContext.ClientId ??
                await ResolveClientIdAsync(httpContextAccessor.HttpContext?.User, cancellationToken);
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw LogFailure(MoodleErrorContract.ConnectionNotFound,
                    "Authenticated connector client context was not found.",
                    stage: MoodleIntegrationStage.ConnectionLookup);
            }

            var connections = await dbContext.ConnectorClients.AsNoTracking()
                .Where(connection => connection.ClientId == clientId && connection.IsActive)
                .OrderBy(connection => connection.MoodleAlias)
                .Select(connection => new MoodleConnectionSummary(connection.MoodleAlias, connection.IsDefault))
                .ToArrayAsync(cancellationToken);

            return connections;
        }
        finally
        {
            credentialsResolutionGate.Release();
        }
    }

    public async Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken)
    {
        await credentialsResolutionGate.WaitAsync(cancellationToken);
        try
        {
            return await ResolveCurrentCredentialsAsync(cancellationToken);
        }
        finally
        {
            credentialsResolutionGate.Release();
        }
    }

    public MoodleCredentials? TryGetResolvedCredentials(string connectionAlias)
    {
        var requestedAlias = MoodleConnectionAlias.Normalize(connectionAlias);
        if (requestedAlias is null ||
            !string.Equals(requestedAlias, MoodleConnectionAlias.Normalize(selection.Alias), StringComparison.Ordinal))
        {
            return null;
        }

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            lock (httpContext.Items)
            {
                if (httpContext.Items.TryGetValue(CredentialsCacheItemKey, out var cachedValue) &&
                    cachedValue is Dictionary<string, MoodleCredentials> credentialsCache)
                {
                    var matches = credentialsCache.Values
                        .Where(credentials =>
                            string.Equals(
                                MoodleConnectionAlias.Normalize(credentials.Alias),
                                requestedAlias,
                                StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(credentials.ConnectionId))
                        .GroupBy(credentials => credentials.ConnectionId, StringComparer.Ordinal)
                        .Take(2)
                        .ToArray();
                    if (matches.Length == 1)
                    {
                        return matches[0].First();
                    }
                }
            }
        }

        lock (resolvedCredentialsCache)
        {
            var matches = resolvedCredentialsCache.Values
                .Where(credentials =>
                    string.Equals(
                        MoodleConnectionAlias.Normalize(credentials.Alias),
                        requestedAlias,
                        StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(credentials.ConnectionId))
                .GroupBy(credentials => credentials.ConnectionId, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0].First() : null;
        }
    }

    private async Task<MoodleCredentials> ResolveCurrentCredentialsAsync(CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var principal = httpContext?.User;
        var clientId = executionContext.ClientId ?? await ResolveClientIdAsync(principal, cancellationToken);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw LogFailure(
                MoodleErrorContract.ConnectionNotFound,
                "Authenticated connector client context was not found.",
                stage: MoodleIntegrationStage.ConnectionLookup);
        }

        var requestedAlias = MoodleConnectionAlias.Normalize(selection.Alias);
        var requestCacheKey = $"{clientId}\u001f{requestedAlias ?? "<default>"}";
        MoodleCredentials? cachedCredentials = null;
        var hasRequestCachedCredentials = false;
        if (httpContext is not null)
        {
            lock (httpContext.Items)
            {
                if (httpContext.Items.TryGetValue(CredentialsCacheItemKey, out var cachedValue) &&
                    cachedValue is Dictionary<string, MoodleCredentials> credentialsCache)
                {
                    hasRequestCachedCredentials = credentialsCache.TryGetValue(requestCacheKey, out cachedCredentials);
                }
            }
        }
        if (hasRequestCachedCredentials && cachedCredentials is not null)
        {
            return cachedCredentials;
        }
        lock (resolvedCredentialsCache)
        {
            if (resolvedCredentialsCache.TryGetValue(requestCacheKey, out cachedCredentials))
            {
                return cachedCredentials;
            }
        }

        var connections = await dbContext.ConnectorClients
            .AsNoTracking()
            .Where(connection => connection.ClientId == clientId)
            .OrderByDescending(connection => connection.IsDefault)
            .ThenBy(connection => connection.MoodleAlias)
            .ToArrayAsync(cancellationToken);

        ConnectorClientCredentialEntity entity;
        if (requestedAlias is null)
        {
            if (connections.Length == 0)
            {
                throw LogFailure(
                    MoodleErrorContract.ConnectionNotFound,
                    "No Moodle connection belongs to the authenticated connector client.",
                    stage: MoodleIntegrationStage.ConnectionLookup);
            }

            var defaults = connections.Where(connection => connection.IsDefault).ToArray();
            if (defaults.Length != 1)
            {
                throw LogFailure(
                    MoodleErrorContract.DefaultConnectionNotConfigured,
                    $"Expected one default Moodle connection but found {defaults.Length}.",
                    stage: MoodleIntegrationStage.ConnectionLookup);
            }

            entity = defaults[0];
        }
        else
        {
            var aliasMatches = connections
                .Where(connection => MoodleConnectionAlias.Normalize(connection.MoodleAlias) == requestedAlias)
                .ToArray();
            var matches = aliasMatches.Length > 0
                ? aliasMatches
                : connections
                    .Where(connection => MoodleConnectionAlias.Normalize(connection.MoodleTarget) == requestedAlias)
                    .ToArray();

            if (matches.Length == 0)
            {
                matches = connections
                    .Where(connection =>
                        (MoodleConnectionAlias.Normalize(connection.MoodleAlias)?.Contains(requestedAlias, StringComparison.Ordinal) == true) ||
                        (MoodleConnectionAlias.Normalize(connection.MoodleTarget)?.Contains(requestedAlias, StringComparison.Ordinal) == true))
                    .ToArray();
            }

            if (matches.Length == 0)
            {
                throw LogFailure(
                    MoodleErrorContract.ConnectionNotFound,
                    $"Moodle connection alias '{requestedAlias}' was not found.",
                    stage: MoodleIntegrationStage.ConnectionLookup);
            }

            if (matches.Length > 1)
            {
                var exactMatches = matches
                    .Where(connection => string.Equals(
                        connection.MoodleAlias?.Trim(),
                        requestedAlias,
                        StringComparison.Ordinal))
                    .ToArray();
                var activeMatches = matches.Where(connection => connection.IsActive).ToArray();
                var defaultMatches = matches.Where(connection => connection.IsDefault).ToArray();
                matches = exactMatches.Length == 1
                    ? exactMatches
                    : activeMatches.Length == 1
                        ? activeMatches
                        : defaultMatches.Length == 1
                            ? defaultMatches
                            : matches;
                if (matches.Length > 1)
                {
                    throw LogFailure(
                        MoodleErrorContract.ConnectionNotFound,
                        $"Moodle connection alias '{requestedAlias}' is ambiguous.",
                        stage: MoodleIntegrationStage.ConnectionLookup);
                }
            }

            entity = matches[0];
        }

        var safeBaseUrl = GetSafeBaseUrl(entity.MoodleBaseUrl);
        if (!entity.IsActive)
        {
            throw LogFailure(
                MoodleErrorContract.ConnectionDisabled,
                "The selected Moodle connection is disabled.",
                entity,
                safeBaseUrl,
                MoodleIntegrationStage.ConnectionState);
        }

        if (!Uri.TryCreate(entity.MoodleBaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(baseUri.Host) ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw LogFailure(
                MoodleErrorContract.NetworkError,
                "The selected Moodle connection has an invalid base URL.",
                entity,
                safeBaseUrl,
                MoodleIntegrationStage.UrlValidation);
        }

        var isLocalStubEndpoint = moodleApiOptions.Value.UseStubData &&
            (baseUri.Host.Equals("moodle.local", StringComparison.OrdinalIgnoreCase) ||
             baseUri.Host.EndsWith(".moodle.local", StringComparison.OrdinalIgnoreCase));
        if (!isLocalStubEndpoint)
        {
            try
            {
                baseUri = await endpointValidator.ValidateAsync(entity.MoodleBaseUrl, cancellationToken);
            }
            catch (MoodleApiException exception)
            {
                throw new MoodleApiException(
                    exception.ErrorCode,
                    exception.Message,
                    exception.HttpStatusCode,
                    exception,
                    exception.AuditId,
                    entity.Id,
                    MoodleConnectionAlias.Normalize(entity.MoodleAlias),
                    safeBaseUrl,
                    exception.FunctionName,
                    exception.DurationMs,
                    exception.RemoteErrorCode,
                    exception.Stage);
            }
        }
        safeBaseUrl = GetSafeBaseUrl(baseUri.AbsoluteUri);

        if (string.IsNullOrWhiteSpace(entity.MoodleUsernameEncrypted) ||
            string.IsNullOrWhiteSpace(entity.MoodlePasswordEncrypted))
        {
            throw LogFailure(
                MoodleErrorContract.TokenMissing,
                "The selected Moodle connection has incomplete encrypted credentials.",
                entity,
                safeBaseUrl,
                MoodleIntegrationStage.CredentialPresence);
        }

        string username;
        string password;
        try
        {
            username = secretProtector.Unprotect(entity.MoodleUsernameEncrypted);
            password = secretProtector.Unprotect(entity.MoodlePasswordEncrypted);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or InvalidOperationException)
        {
            var failure = new MoodleApiException(
                MoodleErrorContract.TokenDecryptionFailed,
                "The selected Moodle credentials could not be decrypted.",
                innerException: exception,
                connectionId: entity.Id,
                connectionAlias: MoodleConnectionAlias.Normalize(entity.MoodleAlias),
                endpoint: safeBaseUrl,
                stage: MoodleIntegrationStage.CredentialDecryption);
            try
            {
                logger.LogError(
                    "Moodle credential decryption failed. AuditId={AuditId} ErrorCode={ErrorCode}",
                    failure.AuditId,
                    failure.ErrorCode);
            }
            catch (Exception) { }
            throw failure;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw LogFailure(
                MoodleErrorContract.TokenMissing,
                "The decrypted Moodle credentials are incomplete.",
                entity,
                safeBaseUrl,
                MoodleIntegrationStage.TokenRequest);
        }

        var resolvedCredentials = new MoodleCredentials(
            entity.ClientId,
            entity.Id,
            MoodleConnectionAlias.NormalizeOrDefault(entity.MoodleAlias),
            safeBaseUrl!,
            username,
            password,
            MoodleConnectionAlias.NormalizeOrDefault(entity.MoodleTarget),
            entity.CanWrite);
        lock (resolvedCredentialsCache)
        {
            resolvedCredentialsCache[requestCacheKey] = resolvedCredentials;
        }
        if (httpContext is not null)
        {
            lock (httpContext.Items)
            {
                if (httpContext.Items.TryGetValue(CredentialsCacheItemKey, out var cachedValue) &&
                    cachedValue is Dictionary<string, MoodleCredentials> existingCache)
                {
                    existingCache[requestCacheKey] = resolvedCredentials;
                }
                else
                {
                    httpContext.Items[CredentialsCacheItemKey] =
                        new Dictionary<string, MoodleCredentials>(StringComparer.Ordinal)
                        {
                            [requestCacheKey] = resolvedCredentials
                        };
                }
            }
        }

        return resolvedCredentials;
    }

    public void Dispose() => credentialsResolutionGate.Dispose();

    private async Task<string?> ResolveClientIdAsync(
        ClaimsPrincipal? principal,
        CancellationToken cancellationToken)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.Items.TryGetValue(ResolvedClientIdItemKey, out var cachedClientId) == true &&
            cachedClientId is string cached &&
            !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        string? resolvedClientId = null;
        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;
        if (Guid.TryParse(subject, out var userId))
        {
            var account = await dbContext.UserAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(account?.ConnectorClientId))
            {
                resolvedClientId = account.ConnectorClientId;
            }
        }

        if (resolvedClientId is null)
        {
            var email = principal.FindFirst(ClaimTypes.Email)?.Value
                ?? principal.FindFirst("email")?.Value
                ?? principal.FindFirst("preferred_username")?.Value;
            if (!string.IsNullOrWhiteSpace(email))
            {
                var normalizedEmail = email.Trim().ToLowerInvariant();
                var account = await dbContext.UserAccounts
                    .AsNoTracking()
                    .SingleOrDefaultAsync(user => user.Email == normalizedEmail, cancellationToken);
                if (!string.IsNullOrWhiteSpace(account?.ConnectorClientId))
                {
                    resolvedClientId = account.ConnectorClientId;
                }
            }
        }

        resolvedClientId ??= principal.FindFirst("connector_client_id")?.Value ?? subject;
        if (httpContext is not null && !string.IsNullOrWhiteSpace(resolvedClientId))
        {
            httpContext.Items[ResolvedClientIdItemKey] = resolvedClientId;
        }

        return resolvedClientId;
    }

    private MoodleApiException LogFailure(
        string errorCode,
        string internalMessage,
        ConnectorClientCredentialEntity? entity = null,
        string? endpoint = null,
        MoodleIntegrationStage stage = MoodleIntegrationStage.Unknown)
    {
        var failure = new MoodleApiException(
            errorCode,
            internalMessage,
            connectionId: entity?.Id,
            connectionAlias: MoodleConnectionAlias.Normalize(entity?.MoodleAlias ?? selection.Alias),
            endpoint: endpoint,
            stage: stage);
        try
        {
            logger.LogWarning(
                "Moodle connection resolution failed. AuditId={AuditId} ErrorCode={ErrorCode}",
                failure.AuditId,
                failure.ErrorCode);
        }
        catch (Exception) { }
        return failure;
    }

    private static string? GetSafeBaseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        }.Uri.AbsoluteUri.TrimEnd('/');
    }
}
