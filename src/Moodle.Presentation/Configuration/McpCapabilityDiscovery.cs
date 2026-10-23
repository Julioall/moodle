using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Presentation.Configuration;

/// <summary>
/// tools/list has no Moodle alias. Discover each owned active connection without
/// treating the default connection as the authority for the entire MCP surface.
/// This controls advertisement only; execution still validates its own connection.
/// </summary>
public sealed class McpCapabilityDiscovery(
    IMoodleConnectionCatalog connections,
    IMoodleConnectionSelection selection,
    IMoodleFunctionCatalog functions,
    ILogger<McpCapabilityDiscovery> logger)
{
    public async Task<McpCapabilitySnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        var profiles = new List<MoodleFunctionProfile>();
        var incomplete = false;
        var previousAlias = selection.Alias;
        try
        {
            var aliases = await connections.GetActiveAliasesAsync(cancellationToken);
            foreach (var alias in aliases)
            {
                selection.Alias = alias;
                try
                {
                    profiles.Add(await functions.GetCurrentAsync(false, cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    incomplete = true;
                    LogDiscoveryFailure(exception);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            incomplete = true;
            LogDiscoveryFailure(exception);
        }
        finally
        {
            selection.Alias = previousAlias;
        }

        return new McpCapabilitySnapshot(profiles, incomplete);
    }

    private void LogDiscoveryFailure(Exception exception) =>
        logger.LogWarning(
            "MCP capability discovery incomplete. ErrorCode={ErrorCode} Stage={Stage}. Read tools remain discoverable; capability-dependent writes require a verified profile.",
            exception is MoodleApiException moodle ? moodle.ErrorCode : "discovery_failed",
            exception is MoodleApiException staged ? staged.Stage.ToString() : "Unknown");
}

public sealed class McpCapabilitySnapshot(
    IReadOnlyList<MoodleFunctionProfile> profiles,
    bool incomplete)
{
    private readonly IReadOnlyList<HashSet<string>> _capabilities = profiles
        .Select(profile => profile.Functions.Where(function => function.IsAvailable)
            .Select(function => function.Name).ToHashSet(StringComparer.OrdinalIgnoreCase))
        .ToArray();

    public string? GetHiddenReason(string requiredCapabilities, bool readOnly)
    {
        var required = requiredCapabilities.Split([' ', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // All requirements must be met by one connection, never a union of
        // unrelated credentials. Partial discovery cannot prove read unavailability.
        if (required.Length == 0 || _capabilities.Any(profile => required.All(profile.Contains)))
            return null;
        if (incomplete && readOnly)
            return null;
        return incomplete ? "capability_discovery_incomplete" : "required_capabilities_unavailable";
    }
}
