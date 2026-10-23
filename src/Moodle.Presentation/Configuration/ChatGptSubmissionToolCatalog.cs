using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Moodle.Application.Configuration;

namespace Moodle.Presentation.Configuration;

/// <summary>
/// Deterministically derives the review-facing tool block from the production
/// MCP registration contract. App description and review test cases remain
/// editorial metadata, but tool names, titles, and impact hints cannot drift
/// from the executable server.
/// </summary>
public static class ChatGptSubmissionToolCatalog
{
    private const string ReadOnlyJustification =
        "Retrieves authorized Moodle or connector data without creating, updating, sending, or deleting records.";
    private const string StatefulJustification =
        "Creates, updates, queues, sends, or otherwise changes state in the authenticated Moodle or connector context.";
    private const string PrivateWorldJustification =
        "Operates only within the authenticated Moodle or connector context and does not publish to the public internet.";
    private const string NonDestructiveJustification =
        "Does not delete, overwrite, revoke access, cancel state, or send an irreversible action.";
    private const string DestructiveJustification =
        "May delete, overwrite, cancel, or send an action that cannot be undone; the server still enforces authorization and confirmation where required.";

    public static JsonObject CreateProductionTools()
    {
        var metadataRegistry = new ToolMetadataRegistry(RegisteredMcpToolContainers.All);
        var exposurePolicy = new CognitiveExposurePolicy(ToolExposureProfile.Production);
        var featureOptions = new FeatureOptions
        {
            MessagesWriteEnabled = true,
            UniversalMoodleWriteEnabled = true,
            UniversalMoodleFileDownloadEnabled = true
        };
        var assignmentWrites = new AssignmentWriteFeatureOptions { AssignmentGradeWriteEnabled = true };
        var contracts = RegisteredMcpToolContainers.AlwaysOn
            .Concat(RegisteredMcpToolContainers.GetEnabledContainers(featureOptions, assignmentWrites))
            .Where(container => container.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(container => container.GetMethods())
            .SelectMany(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: true)
                .Cast<McpServerToolAttribute>())
            .Where(contract => !string.IsNullOrWhiteSpace(contract.Name))
            .Where(contract => metadataRegistry.TryGet(contract.Name!, out var metadata) &&
                               exposurePolicy.ShouldExpose(contract.Name!, metadata))
            .OrderBy(contract => contract.Name, StringComparer.Ordinal)
            .ToArray();

        var tools = new JsonObject();
        foreach (var contract in contracts)
        {
            var name = contract.Name!;
            if (!tools.TryAdd(name, CreateToolEntry(name, contract)))
            {
                throw new InvalidOperationException($"A tool de submissão '{name}' foi registrada mais de uma vez.");
            }
        }

        return tools;
    }

    private static JsonObject CreateToolEntry(string name, McpServerToolAttribute contract)
    {
        var (readOnlyJustification, destructiveJustification) = name switch
        {
            "refresh_grading_scale" =>
            (
                "Changes connector-local grading context and invalidates affected local drafts; it does not write grades or feedback to Moodle.",
                contract.Destructive ? DestructiveJustification : NonDestructiveJustification
            ),
            _ =>
            (
                contract.ReadOnly ? ReadOnlyJustification : StatefulJustification,
                contract.Destructive ? DestructiveJustification : NonDestructiveJustification
            )
        };

        return new JsonObject
        {
            ["annotations"] = new JsonObject
            {
                ["title"] = contract.Title,
                ["destructiveHint"] = contract.Destructive,
                ["idempotentHint"] = contract.Idempotent,
                ["openWorldHint"] = contract.OpenWorld,
                ["readOnlyHint"] = contract.ReadOnly
            },
            ["justifications"] = new JsonObject
            {
                ["read_only_justification"] = readOnlyJustification,
                ["open_world_justification"] = contract.OpenWorld
                    ? "May interact with public or third-party destinations outside the authenticated Moodle or connector context."
                    : PrivateWorldJustification,
                ["destructive_justification"] = destructiveJustification
            }
        };
    }
}
