namespace Moodle.Presentation.Configuration;

/// <summary>
/// Supplies a deterministic baseline for MCP methods that predate the metadata
/// attribute. Explicit attributes always win; this prevents an unclassified tool
/// from silently becoming an exposure-policy exception.
/// </summary>
internal static class ToolMetadataInference
{
    public static MoodleToolMetadataAttribute Create(
        Type declaringType,
        string toolName,
        bool? ReadOnly = null,
        bool? Destructive = null)
    {
        var family = InferFamily(declaringType, toolName);
        var structural = family is "discovery" or "infrastructure" or "demopendingaction";
        var controlledWrite = (family is "infrastructure" or "demopendingaction") ||
            (!structural && ReadOnly == false &&
             !family.Equals("memory", StringComparison.OrdinalIgnoreCase) &&
             !family.Equals("memory-document", StringComparison.OrdinalIgnoreCase));
        var specialized = !structural && (controlledWrite || IsSpecializedFamily(family));
        var classification = structural
            ? "R6"
            : controlledWrite
                ? "R5"
                : specialized
                    ? ClassificationForFamily(family)
                    : "R3";

        return new MoodleToolMetadataAttribute
        {
            Family = family,
            Classification = classification,
            Kind = controlledWrite ? "controlled-write" : structural ? "structural" : specialized ? "specialized" : "wrapper",
            CanonicalOperation = toolName,
            Structural = structural,
            ExposureStatus = "Keep",
            ExposureReason = controlledWrite
                ? "Controlled write boundary; available only through explicit prepare/confirm flow."
                : structural
                    ? "Structural discovery or protocol primitive required by the connector boundary."
                : specialized
                        ? "Specialized domain contract with orchestration, validation, or human-safety semantics."
                        : "Direct domain wrapper retained until a domain-specific skill proves a safe replacement.",
            Evidence = $"Inferred from implementation container {declaringType.FullName}; verify against the method body before changing exposure."
        };
    }

    private static string InferFamily(Type declaringType, string toolName)
    {
        var typeName = declaringType.Name;
        var normalizedType = typeName
            .Replace("Moodle", string.Empty, StringComparison.Ordinal)
            .Replace("Tools", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        if (normalizedType.Contains("universalwrite", StringComparison.Ordinal)) return "infrastructure";
        if (normalizedType.Contains("universal", StringComparison.Ordinal)) return "discovery";
        if (normalizedType.Contains("demopendingaction", StringComparison.Ordinal)) return "demopendingaction";
        if (normalizedType.Contains("assignment", StringComparison.Ordinal) ||
            normalizedType.Contains("submission", StringComparison.Ordinal) ||
            toolName.Contains("assignment", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("submission", StringComparison.OrdinalIgnoreCase)) return "assignments";
        if (normalizedType.Contains("participant", StringComparison.Ordinal) ||
            normalizedType.Contains("student", StringComparison.Ordinal) ||
            normalizedType.Contains("completion", StringComparison.Ordinal)) return "students";
        if (normalizedType.Contains("courseactiv", StringComparison.Ordinal))
        {
            return toolName.Contains("assignment", StringComparison.OrdinalIgnoreCase)
                ? "assignments"
                : "classroom-audit";
        }
        if (normalizedType.Contains("coursecontent", StringComparison.Ordinal)) return "classroom-audit";
        if (normalizedType.Contains("forum", StringComparison.Ordinal)) return "forums";
        if (normalizedType.Contains("grading", StringComparison.Ordinal) ||
            normalizedType.Contains("gradebook", StringComparison.Ordinal)) return "grading";
        if (normalizedType.Contains("accessmonitoring", StringComparison.Ordinal) ||
            normalizedType.Contains("risk", StringComparison.Ordinal)) return "follow-up";
        if (normalizedType.Contains("report", StringComparison.Ordinal)) return "reports";
        if (normalizedType.Contains("monitor", StringComparison.Ordinal)) return "classroom-audit";
        if (normalizedType.Contains("message", StringComparison.Ordinal)) return "messaging";
        if (normalizedType.Contains("memorydocument", StringComparison.Ordinal)) return "memory-document";
        if (normalizedType.Contains("memory", StringComparison.Ordinal)) return "memory";
        if (normalizedType.Contains("pedagogy", StringComparison.Ordinal)) return "pedagogy";
        return normalizedType;
    }

    private static bool IsSpecializedFamily(string family) => family is
        "assignments" or "students" or "classroom-audit" or "follow-up" or
        "grading" or "messaging" or "memory" or "memory-document" or "pedagogy";

    private static string ClassificationForFamily(string family) => family switch
    {
        "grading" or "messaging" or "pedagogy" => "R5",
        "memory" or "memory-document" => "R6",
        _ => "R4"
    };
}
