using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Moodle.Presentation.Configuration;

/// <summary>
/// Deterministic registry of MCP tool metadata built once at startup.
/// Registers explicit tool containers once at startup. It never scans loaded
/// assemblies, which keeps the catalog deterministic and avoids discovering
/// unrelated or conditional tools implicitly.
/// </summary>
public sealed class ToolMetadataRegistry
{
    private readonly Dictionary<string, MoodleToolMetadataAttribute> _map;

    public ToolMetadataRegistry(IEnumerable<Type>? initialToolContainers = null)
    {
        _map = new Dictionary<string, MoodleToolMetadataAttribute>(StringComparer.OrdinalIgnoreCase);
        if (initialToolContainers is null)
        {
            return;
        }

        foreach (var toolContainer in initialToolContainers)
        {
            RegisterFromType(toolContainer);
        }
    }

    public bool TryGet(string toolName, out MoodleToolMetadataAttribute? metadata)
    {
        if (toolName is null)
        {
            metadata = null;
            return false;
        }

        return _map.TryGetValue(toolName, out metadata!);
    }

    public IReadOnlyList<KeyValuePair<string, MoodleToolMetadataAttribute>> Entries =>
        _map.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Register metadata discovered from a specific tool container type.
    /// This allows deterministic association of MCP tool names to metadata
    /// at the moment the tools are registered, avoiding reflection per-request
    /// and preventing reliance on assembly scanning order.
    /// </summary>
    public void RegisterFromType(Type toolContainerType)
    {
        if (toolContainerType == null) return;

        MethodInfo[] methods;
        try
        {
            methods = toolContainerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        }
        catch
        {
            return;
        }

        foreach (var method in methods)
        {
            try
            {
                var cad = method.GetCustomAttributesData();
                var mcptoolAttrData = cad.FirstOrDefault(a => a.AttributeType.Name == "McpServerToolAttribute");
                if (mcptoolAttrData != null)
                {
                    string? toolName = null;
                    if (mcptoolAttrData.ConstructorArguments.Count > 0)
                    {
                        toolName = mcptoolAttrData.ConstructorArguments[0].Value as string;
                    }
                    if (string.IsNullOrEmpty(toolName))
                    {
                        var named = mcptoolAttrData.NamedArguments.FirstOrDefault(na => na.MemberName == "Name");
                        toolName = named.TypedValue.Value as string;
                    }

                    if (!string.IsNullOrEmpty(toolName))
                    {
                        var metaDataAttr = cad.FirstOrDefault(a => a.AttributeType.Name == "MoodleToolMetadataAttribute");
                        if (metaDataAttr != null)
                        {
                            var metadata = new MoodleToolMetadataAttribute();
                            foreach (var na in metaDataAttr.NamedArguments)
                            {
                                switch (na.MemberName)
                                {
                                    case "Family": metadata.Family = na.TypedValue.Value as string ?? string.Empty; break;
                                    case "Classification": metadata.Classification = na.TypedValue.Value as string ?? string.Empty; break;
                                    case "Kind": metadata.Kind = na.TypedValue.Value as string ?? string.Empty; break;
                                    case "CanonicalOperation": metadata.CanonicalOperation = na.TypedValue.Value as string ?? string.Empty; break;
                                    case "CompatibilityAliasOf": metadata.CompatibilityAliasOf = na.TypedValue.Value as string ?? string.Empty; break;
                                    case "Structural": metadata.Structural = na.TypedValue.Value is bool b && b; break;
                                    case "ExposureStatus": metadata.ExposureStatus = na.TypedValue.Value as string ?? "Keep"; break;
                                    case "ExposureReason": metadata.ExposureReason = na.TypedValue.Value as string ?? string.Empty; break;
                                    case "Evidence": metadata.Evidence = na.TypedValue.Value as string ?? string.Empty; break;
                                    case "RequiredPlatformPermission": metadata.RequiredPlatformPermission = na.TypedValue.Value as string ?? string.Empty; break;
                                    case "RequiredOAuthScopes": metadata.RequiredOAuthScopes = na.TypedValue.Value as string ?? string.Empty; break;
                                    case "RequiredMoodleCapabilities": metadata.RequiredMoodleCapabilities = na.TypedValue.Value as string ?? string.Empty; break;
                                }
                            }

                            Complete(metadata, toolContainerType, toolName);

                            _map[toolName] = metadata;
                        }
                        else
                        {
                            if (!_map.ContainsKey(toolName))
                            {
                                var inferred = ToolMetadataInference.Create(
                                    toolContainerType,
                                    toolName,
                                    ReadOnly: GetBooleanNamedArgument(mcptoolAttrData, "ReadOnly"),
                                    Destructive: GetBooleanNamedArgument(mcptoolAttrData, "Destructive"));
                                inferred.RequiredPlatformPermission = PlatformToolPermissionMapping.For(toolName, inferred);
                                inferred.RequiredOAuthScopes = string.Join(' ', ToolAuthorizationMapping.OAuthScopesFor(toolName, inferred));
                                Complete(inferred, toolContainerType, toolName);
                                _map[toolName] = inferred;
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore individual method failures
            }
        }
    }

    private static void Complete(MoodleToolMetadataAttribute metadata, Type declaringType, string toolName)
    {
        if (string.IsNullOrWhiteSpace(metadata.ExposureStatus)) metadata.ExposureStatus = "Keep";
        if (string.IsNullOrWhiteSpace(metadata.ExposureReason))
        {
            metadata.ExposureReason = metadata.Structural
                ? "Structural connector primitive."
                : "Explicit metadata retained as the source-of-truth exposure decision.";
        }
        if (string.IsNullOrWhiteSpace(metadata.Evidence))
        {
            metadata.Evidence = $"Explicit metadata on {declaringType.FullName}.{toolName}.";
        }
        if (string.IsNullOrWhiteSpace(metadata.RequiredPlatformPermission))
        {
            metadata.RequiredPlatformPermission = PlatformToolPermissionMapping.For(toolName, metadata);
        }
        if (string.IsNullOrWhiteSpace(metadata.RequiredOAuthScopes))
        {
            metadata.RequiredOAuthScopes = string.Join(' ', ToolAuthorizationMapping.OAuthScopesFor(toolName, metadata));
        }
        // Domain mappings take precedence over prefix inference: a connector
        // wrapper such as report_students_at_risk is not a Moodle report_* function.
        if (string.IsNullOrWhiteSpace(metadata.RequiredMoodleCapabilities))
        {
            metadata.RequiredMoodleCapabilities = MoodleToolCapabilityMapping.For(toolName);
        }
        if (string.IsNullOrWhiteSpace(metadata.RequiredMoodleCapabilities) &&
            IsConcreteMoodleFunction(metadata.CanonicalOperation))
        {
            metadata.RequiredMoodleCapabilities = metadata.CanonicalOperation.Trim();
        }
    }

    private static bool IsConcreteMoodleFunction(string operation) =>
        operation.StartsWith("core_", StringComparison.OrdinalIgnoreCase) ||
        operation.StartsWith("mod_", StringComparison.OrdinalIgnoreCase) ||
        operation.StartsWith("enrol_", StringComparison.OrdinalIgnoreCase) ||
        operation.StartsWith("gradereport_", StringComparison.OrdinalIgnoreCase) ||
        operation.StartsWith("report_", StringComparison.OrdinalIgnoreCase) ||
        operation.StartsWith("tool_", StringComparison.OrdinalIgnoreCase);

    private static bool? GetBooleanNamedArgument(CustomAttributeData attribute, string name)
    {
        var argument = attribute.NamedArguments.FirstOrDefault(item =>
            string.Equals(item.MemberName, name, StringComparison.Ordinal));
        return argument.TypedValue.Value is bool value ? value : null;
    }
}
