using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Moodle.Application.Abstractions;
using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly IMoodleRestClient _restClient;
    private readonly IMoodleCredentialsProvider? _credentialsProvider;
    private readonly IMemoryCache _cache;

    public CapabilityRegistry(
        IMoodleRestClient restClient,
        IMoodleCredentialsProvider? credentialsProvider = null,
        IMemoryCache? cache = null)
    {
        _restClient = restClient;
        _credentialsProvider = credentialsProvider;
        _cache = cache ?? new MemoryCache(new MemoryCacheOptions());
    }

    public async Task<CapabilitySnapshot> GetSnapshotAsync(ConnectionInfo connectionInfo, string userToken, CancellationToken cancellationToken = default)
    {
        var credentialFingerprint = CreateCredentialFingerprint(userToken);
        var cacheKey = CreateCacheKey(connectionInfo.ConnectionId, credentialFingerprint);

        if (_cache.TryGetValue<CapabilitySnapshot>(cacheKey, out var snapshot) && snapshot is not null)
        {
            return snapshot;
        }

        var credentials = _credentialsProvider is null
            ? new MoodleCredentials(
                "internal",
                connectionInfo.ConnectionId.ToString(),
                connectionInfo.Alias,
                connectionInfo.BaseUrl,
                userToken,
                "unused",
                "moodle",
                false)
            : await _credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);

        if (!string.Equals(credentials.Alias, connectionInfo.Alias, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(credentials.BaseUrl.TrimEnd('/'), connectionInfo.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The resolved Moodle connection changed before capability discovery.");
        }

        var payload = await _restClient.CallAsync(credentials, "core_webservice_get_site_info", new Dictionary<string, object?>(), true, cancellationToken);
        var node = JsonNode.Parse(payload.GetRawText());

        var functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (node?["functions"] is JsonArray functionsArray)
        {
            foreach (var functionNode in functionsArray)
            {
                var funcName = functionNode?["name"]?.ToString();
                if (!string.IsNullOrEmpty(funcName))
                {
                    functions.Add(funcName);
                }
            }
        }

        var newSnapshot = new CapabilitySnapshot(
            connectionInfo.ConnectionId,
            credentialFingerprint,
            functions,
            DateTimeOffset.UtcNow
        );

        _cache.Set(
            cacheKey,
            newSnapshot,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(60),
                Size = 1
            });
        return newSnapshot;
    }

    public void Invalidate(ConnectionInfo connectionInfo, string userToken)
    {
        _cache.Remove(CreateCacheKey(connectionInfo.ConnectionId, CreateCredentialFingerprint(userToken)));
    }

    private static string CreateCacheKey(Guid connectionId, string credentialFingerprint) =>
        $"moodle-capability:{connectionId:N}:{credentialFingerprint}";

    private static string CreateCredentialFingerprint(string credentialReference)
    {
        var bytes = Encoding.UTF8.GetBytes(credentialReference ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
