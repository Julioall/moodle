using Moodle.Presentation.Security;

namespace Moodle.Presentation.Configuration;

/// <summary>
/// Deterministic authorization mapping for legacy tools while their explicit
/// metadata is being completed. The mapping is based on the declared domain
/// family first; name matching is only a compatibility fallback.
/// </summary>
internal static class ToolAuthorizationMapping
{
    public static string PermissionFor(string toolName, MoodleToolMetadataAttribute metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.RequiredPlatformPermission))
            return metadata.RequiredPlatformPermission.Trim().ToLowerInvariant();

        var normalized = toolName.ToLowerInvariant();
        var family = metadata.Family.ToLowerInvariant();

        if (normalized.Contains("monitor_class_report", StringComparison.Ordinal)) return "tool.reports.view";
        if (normalized.Contains("without_recent_access", StringComparison.Ordinal) ||
            normalized.Contains("without_forum_participation", StringComparison.Ordinal)) return "tool.followup.view";
        if (family == "follow-up" && normalized.Contains("risk", StringComparison.Ordinal)) return "tool.reports.view";
        if (normalized.Contains("gradebook", StringComparison.Ordinal) ||
            normalized.Contains("grade", StringComparison.Ordinal))
            return IsWriteTool(normalized, metadata) ? "tool.assignments.grade" : "tool.assignments.view";
        if (family == "courses") return "tool.courses.view";
        if (family is "assignments" or "grading")
            return IsWriteTool(normalized, metadata) ? "tool.assignments.grade" : "tool.assignments.view";
        if (family == "students") return "tool.students.view";
        if (family == "classroom-audit") return "tool.classroom.view";
        if (family == "follow-up") return "tool.followup.view";
        if (family == "forums") return IsWriteTool(normalized, metadata) ? "tool.forums.write" : "tool.forums.view";
        if (family == "reports") return "tool.reports.view";
        if (family == "messaging") return IsWriteTool(normalized, metadata) ? "tool.messages.send" : "tool.messages.view";
        if (family is "memory" or "memory-document") return "tool.memory.manage";
        if (family == "pedagogy") return "tool.pedagogy.view";
        if (family is "discovery" or "infrastructure" or "demopendingaction") return "tool.connections.manage";

        if (normalized.Contains("forum", StringComparison.Ordinal)) return "tool.forums.view";
        if (normalized.Contains("report", StringComparison.Ordinal) || normalized.Contains("monitor", StringComparison.Ordinal)) return "tool.reports.view";
        if (normalized.Contains("student", StringComparison.Ordinal)) return "tool.students.view";
        if (normalized.Contains("course", StringComparison.Ordinal)) return "tool.courses.view";
        if (normalized.Contains("submission", StringComparison.Ordinal) || normalized.Contains("assignment", StringComparison.Ordinal) || normalized.Contains("grade", StringComparison.Ordinal))
            return IsWriteTool(normalized, metadata) ? "tool.assignments.grade" : "tool.assignments.view";
        return string.Empty;
    }

    public static string[] OAuthScopesFor(string toolName, MoodleToolMetadataAttribute metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.RequiredOAuthScopes))
            return Parse(metadata.RequiredOAuthScopes);

        var normalized = toolName.ToLowerInvariant();
        var family = metadata.Family.ToLowerInvariant();

        if (normalized.Contains("monitor_class_report", StringComparison.Ordinal))
            return [MoodleScopePolicies.ReadAccess, MoodleScopePolicies.ReadStudents];
        if (normalized.Contains("without_recent_access", StringComparison.Ordinal))
            return [MoodleScopePolicies.ReadAccess, MoodleScopePolicies.ReadStudents];
        if (normalized.Contains("without_forum_participation", StringComparison.Ordinal))
            return [MoodleScopePolicies.ReadForums, MoodleScopePolicies.ReadStudents];
        if (normalized.Contains("completion", StringComparison.Ordinal))
            return [MoodleScopePolicies.ReadActivities, MoodleScopePolicies.ReadStudents];
        if (family == "follow-up" && normalized.Contains("risk", StringComparison.Ordinal))
            // Risk uses participants/access and gradebook data, not activity completion.
            return [MoodleScopePolicies.ReadAccess, MoodleScopePolicies.ReadStudents, MoodleScopePolicies.ReadAssignments, MoodleScopePolicies.ReadSubmissions];
        if (normalized.Contains("gradebook", StringComparison.Ordinal) || normalized.Contains("grade", StringComparison.Ordinal))
        {
            var scopes = new List<string> { MoodleScopePolicies.ReadAssignments, MoodleScopePolicies.ReadSubmissions };
            if (IsWriteTool(normalized, metadata))
                scopes.Add(MoodleScopePolicies.WriteAssignmentsGrade);
            return scopes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        if (family is "memory" or "memory-document" or "pedagogy" or "demopendingaction") return [];
        if (family == "courses") return [MoodleScopePolicies.ReadCourses];
        if (family == "students")
            return normalized.Contains("group", StringComparison.Ordinal)
                ? [MoodleScopePolicies.ReadStudents, MoodleScopePolicies.ReadGroups]
                : [MoodleScopePolicies.ReadStudents];
        if (family == "forums")
            return IsWriteTool(normalized, metadata)
                ? [MoodleScopePolicies.ReadForums, MoodleScopePolicies.WriteForums]
                : [MoodleScopePolicies.ReadForums];
        if (family == "follow-up") return [MoodleScopePolicies.ReadAccess, MoodleScopePolicies.ReadStudents];
        if (family == "reports")
        {
            if (normalized.Contains("course_summary", StringComparison.Ordinal) ||
                normalized.Contains("monitor", StringComparison.Ordinal))
                return [MoodleScopePolicies.ReadAccess, MoodleScopePolicies.ReadStudents];
            return [MoodleScopePolicies.ReadAccess, MoodleScopePolicies.ReadStudents, MoodleScopePolicies.ReadAssignments, MoodleScopePolicies.ReadSubmissions];
        }
        if (family == "classroom-audit")
        {
            if (normalized.Contains("audit_virtual_classroom_checklist", StringComparison.Ordinal))
                return [MoodleScopePolicies.ReadContents, MoodleScopePolicies.ReadResources, MoodleScopePolicies.ReadActivities, MoodleScopePolicies.ReadScorms, MoodleScopePolicies.ReadForums];
            if (normalized.Contains("quiz", StringComparison.Ordinal)) return [MoodleScopePolicies.ReadQuizzes];
            if (normalized.Contains("scorm", StringComparison.Ordinal)) return [MoodleScopePolicies.ReadScorms];
            if (normalized.Contains("resource", StringComparison.Ordinal) || normalized.Contains("file", StringComparison.Ordinal) || normalized.Contains("page", StringComparison.Ordinal) || normalized.Contains("url", StringComparison.Ordinal)) return [MoodleScopePolicies.ReadResources];
            if (normalized.Contains("activit", StringComparison.Ordinal) || normalized.Contains("deadline", StringComparison.Ordinal)) return [MoodleScopePolicies.ReadActivities];
            return [MoodleScopePolicies.ReadContents];
        }
        if (family is "assignments" or "grading")
        {
            var scopes = new List<string> { MoodleScopePolicies.ReadAssignments };
            if (normalized.Contains("submission", StringComparison.Ordinal) || family == "grading") scopes.Add(MoodleScopePolicies.ReadSubmissions);
            if (IsWriteTool(normalized, metadata))
                scopes.Add(normalized.Contains("feedback", StringComparison.Ordinal) ? MoodleScopePolicies.WriteAssignmentsFeedback : MoodleScopePolicies.WriteAssignmentsGrade);
            return scopes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        if (family == "messaging") return IsWriteTool(normalized, metadata) ? [MoodleScopePolicies.WriteMessages] : [];
        if (family is "discovery" or "infrastructure")
            return normalized.Contains("write", StringComparison.Ordinal) || IsWriteTool(normalized, metadata) ? [MoodleScopePolicies.WriteAny] : [MoodleScopePolicies.ReadAny];

        return [];
    }

    public static string[] ScopesForPermissions(IEnumerable<string> permissions)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var canDelegateMoodleWrite = false;
        foreach (var permission in permissions)
        {
            switch (permission.ToLowerInvariant())
            {
                case "tool.courses.view": scopes.Add(MoodleScopePolicies.ReadCourses); break;
                case "tool.students.view": scopes.UnionWith([MoodleScopePolicies.ReadStudents, MoodleScopePolicies.ReadGroups]); break;
                case "tool.classroom.view": scopes.UnionWith([MoodleScopePolicies.ReadContents, MoodleScopePolicies.ReadResources, MoodleScopePolicies.ReadActivities, MoodleScopePolicies.ReadQuizzes, MoodleScopePolicies.ReadScorms, MoodleScopePolicies.ReadForums]); break;
                case "tool.assignments.view": scopes.UnionWith([MoodleScopePolicies.ReadAssignments, MoodleScopePolicies.ReadSubmissions]); break;
                case "tool.assignments.grade":
                    scopes.UnionWith([MoodleScopePolicies.ReadAssignments, MoodleScopePolicies.ReadSubmissions, MoodleScopePolicies.WriteAssignmentsGrade]);
                    canDelegateMoodleWrite = true;
                    break;
                case "tool.messages.send":
                    scopes.Add(MoodleScopePolicies.WriteMessages);
                    canDelegateMoodleWrite = true;
                    break;
                case "tool.forums.view": scopes.Add(MoodleScopePolicies.ReadForums); break;
                case "tool.forums.write":
                    scopes.UnionWith([MoodleScopePolicies.ReadForums, MoodleScopePolicies.WriteForums]);
                    canDelegateMoodleWrite = true;
                    break;
                case "tool.followup.view": scopes.UnionWith([MoodleScopePolicies.ReadAccess, MoodleScopePolicies.ReadStudents, MoodleScopePolicies.ReadForums]); break;
                case "tool.reports.view": scopes.UnionWith([MoodleScopePolicies.ReadAccess, MoodleScopePolicies.ReadStudents, MoodleScopePolicies.ReadAssignments, MoodleScopePolicies.ReadSubmissions]); break;
                case "tool.connections.manage": scopes.Add(MoodleScopePolicies.ReadAny); break;
            }
        }

        // Keep the coarse scope for compatibility with the universal tool's
        // exposure contract. The executor still requires the function-specific
        // write scope before preparing or confirming an operation.
        if (canDelegateMoodleWrite)
            scopes.Add(MoodleScopePolicies.WriteAny);

        return scopes.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsWriteTool(string toolName, MoodleToolMetadataAttribute metadata) =>
        metadata.Kind.Contains("write", StringComparison.OrdinalIgnoreCase) ||
        metadata.Kind.Contains("controlled-write", StringComparison.OrdinalIgnoreCase) ||
        toolName.StartsWith("prepare_", StringComparison.Ordinal) ||
        toolName.StartsWith("confirm_", StringComparison.Ordinal) ||
        toolName.Contains("write", StringComparison.Ordinal) ||
        toolName.Contains("send", StringComparison.Ordinal) ||
        toolName.Contains("post", StringComparison.Ordinal) ||
        toolName.Contains("grade_launch", StringComparison.Ordinal);

    private static string[] Parse(string value) => value
        .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
