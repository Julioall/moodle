using System.Text.Json;
using System.Text.Json.Nodes;
using Moodle.Application.Abstractions;
using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

public sealed class SafeReadExecutor : ISafeReadExecutor
{
    private readonly IConnectionRegistry _connectionRegistry;
    private readonly IOperationRegistry _operationRegistry;
    private readonly ICapabilityRegistry _capabilityRegistry;
    private readonly IPolicyEngine _policyEngine;
    private readonly IResponseNormalizer _responseNormalizer;
    private readonly IMoodleCredentialsProvider _credentialsProvider;
    private readonly IMoodleRestClient _restClient;

    public SafeReadExecutor(
        IConnectionRegistry connectionRegistry,
        IOperationRegistry operationRegistry,
        ICapabilityRegistry capabilityRegistry,
        IPolicyEngine policyEngine,
        IResponseNormalizer responseNormalizer,
        IMoodleCredentialsProvider credentialsProvider,
        IMoodleRestClient restClient)
    {
        _connectionRegistry = connectionRegistry;
        _operationRegistry = operationRegistry;
        _capabilityRegistry = capabilityRegistry;
        _policyEngine = policyEngine;
        _responseNormalizer = responseNormalizer;
        _credentialsProvider = credentialsProvider;
        _restClient = restClient;
    }

    public async Task<JsonNode?> ExecuteAsync(
        string operationName,
        Dictionary<string, object?> parameters,
        string? moodleAlias = null,
        NormalizationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Fetch Operation Schema before touching connection or credentials.
        // Unknown operations must fail closed without turning the connection
        // registry into an oracle for arbitrary function names.
        var operation = _operationRegistry.GetOperation(operationName);

        if (operation is null)
        {
            throw new InvalidOperationException($"Operation '{operationName}' is not registered.");
        }

        // 2. Resolve Connection
        var connectionInfo = await _connectionRegistry.ResolveConnectionAsync(moodleAlias, cancellationToken);
        if (connectionInfo == null)
        {
            throw new InvalidOperationException($"Could not resolve connection for alias '{moodleAlias}'.");
        }

        // 3. Evaluate Policy
        var policyResult = _policyEngine.Evaluate(operation);
        if (policyResult.Decision == PolicyDecision.Deny)
        {
            throw new InvalidOperationException($"Policy Denied: {policyResult.Reason}");
        }

        if (policyResult.Decision == PolicyDecision.RedirectToControlledWrite)
        {
            throw new InvalidOperationException($"Policy Redirect: {policyResult.Reason}");
        }

        // 4. Retrieve Moodle Credentials (using internal provider for real execution)
        // Note: In real architecture, connectionInfo might contain the token or reference it.
        var connection = await _credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);

        // 5 & 6. Capability Check and Execution (with bounded retry for invalid cache)
        JsonElement rawElement;
        bool cacheInvalidated = false;
        while (true)
        {
            var capabilitySnapshot = await _capabilityRegistry.GetSnapshotAsync(connectionInfo, connection.Username ?? "", cancellationToken);
            if (!capabilitySnapshot.IsFunctionAvailable(operationName))
            {
                throw new InvalidOperationException($"Capability Denied: The function '{operationName}' is not available for this connection.");
            }

            try
            {
                rawElement = await _restClient.CallAsync(connection, operationName, parameters, allowServiceToken: true, cancellationToken);
                break; // Success
            }
            catch (Application.MoodleApi.MoodleApiException ex) when (ex.RemoteErrorCode == "webservice_access_exception" || ex.ErrorCode == "webservice_access_exception" || ex.RemoteErrorCode == "accessdenied")
            {
                if (cacheInvalidated)
                {
                    // Already retried once, throw
                    throw new InvalidOperationException($"Capability Denied: Moodle rejected '{operationName}' even after cache refresh.", ex);
                }

                cacheInvalidated = true;
                _capabilityRegistry.Invalidate(connectionInfo, connection.Username ?? "");
            }
        }

        // Convert JsonElement to JsonNode for the normalizer
        var rawNode = JsonNode.Parse(rawElement.GetRawText());

        // 7. Normalization
        if (!string.IsNullOrEmpty(operation.NormalizationProfile))
        {
            var normalizedResponse = _responseNormalizer.Normalize(operation.NormalizationProfile, rawNode, context);
            return normalizedResponse;
        }

        return rawNode;
    }
}
