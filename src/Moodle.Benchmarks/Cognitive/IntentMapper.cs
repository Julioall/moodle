using System;
using System.Collections.Generic;

namespace Moodle.Benchmarks.Cognitive;

/// <summary>
/// Maps MCP tool names to canonical benchmark intent strings.
/// The scorer uses tool names observed in ToolInvocations to determine IntentAccuracy,
/// since the agent reports its chosen tool — not an intent label.
/// </summary>
public static class IntentMapper
{
    private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---------------------------------------------------------------
        // REAL MCP tool names (observed in benchmark traces from manifest)
        // ---------------------------------------------------------------

        // courses.list — real names
        { "list_my_courses",                    "courses.list" },
        { "get_my_courses",                     "courses.list" },
        { "get_enrolled_courses",               "courses.list" },

        // courses.search — real names
        { "search_courses",                     "courses.search" },
        { "find_course",                        "courses.search" },
        { "search",                             "courses.search" },  // universal search tool
        { "course_search",                      "courses.search" },

        // courses.details — real names
        // 'get_course' is used by model for both details and search tasks
        { "get_course",                         "courses.details" },
        { "get_course_by_id",                   "courses.details" },
        { "get_course_info",                    "courses.details" },
        { "course_details",                     "courses.details" },

        // courses.structure — real names
        { "list_course_contents",               "courses.structure" },
        { "get_course_contents",                "courses.structure" },
        { "list_course_resources",              "courses.structure" },
        { "get_course_resources",               "courses.structure" },
        { "course_contents",                    "courses.structure" },

        // ---------------------------------------------------------------
        // Legacy assumed names (moodle_ prefix) — kept for compatibility
        // ---------------------------------------------------------------

        // courses.list
        { "moodle_list_my_courses",             "courses.list" },
        { "moodle_courses_list",                "courses.list" },
        { "moodle_get_enrolled_courses",        "courses.list" },

        // courses.search
        { "moodle_search_courses",              "courses.search" },
        { "moodle_courses_search",              "courses.search" },
        { "moodle_find_course",                 "courses.search" },
        { "moodle_course_search",               "courses.search" },
        { "moodle_search",                      "courses.search" },

        // courses.details
        { "moodle_get_course_by_id",            "courses.details" },
        { "moodle_courses_get_by_id",           "courses.details" },
        { "moodle_get_course_details",          "courses.details" },
        { "moodle_course_details",              "courses.details" },
        { "moodle_get_course_info",             "courses.details" },

        // courses.structure
        { "moodle_get_course_contents",         "courses.structure" },
        { "moodle_courses_get_contents",        "courses.structure" },
        { "moodle_course_contents",             "courses.structure" },
        { "moodle_list_course_contents",        "courses.structure" },

        // Generic read
        { "moodle_execute_read",                "generic.read" },
        { "moodle_read",                        "generic.read" },
        { "fetch",                              "generic.read" },  // universal fetch tool

        // Discovery / core
        { "moodle_get_site_info",               "core.site_info" },
        { "moodle_list_capabilities",           "core.capabilities" },
        { "moodle_get_capabilities",            "core.capabilities" },

        // assignments
        { "list_course_assignments",            "assignments.activities" },
        { "list_course_activities",             "assignments.activities" },
        { "get_assignment",                     "assignments.activities" },
        { "list_assignment_submissions",        "assignments.submissions" },
        { "get_student_submission",             "assignments.submissions" },
        { "list_pending_submissions",           "assignments.submissions" },
        { "list_late_submissions",              "assignments.submissions" },
        { "list_submissions_awaiting_grading",  "assignments.submissions" },
        { "get_submission_status",              "assignments.submissions" },
        { "list_students_with_pending_submissions", "assignments.followup" },

        // students / participants
        { "list_course_participants",            "students.list" },
        { "list_course_students",                "students.list" },
        { "list_course_groups",                  "students.groups" },
        { "get_group_members",                   "students.groups" },
        { "list_students_without_recent_access", "students.activity" },
    };

    /// <summary>
    /// Resolves the canonical intent from a tool name.
    /// Returns null if the tool is not known (potential hallucination).
    /// </summary>
    public static string? Resolve(string toolName)
    {
        if (_map.TryGetValue(toolName, out var intent))
            return intent;

        // Try prefix-based heuristics for tools not in the explicit map
        if (toolName.Contains("list_my_courses", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("enrolled_courses", StringComparison.OrdinalIgnoreCase))
            return "courses.list";

        if (toolName.Contains("search_course", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("find_course", StringComparison.OrdinalIgnoreCase))
            return "courses.search";

        if (toolName.Contains("course_content", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("course_structure", StringComparison.OrdinalIgnoreCase))
            return "courses.structure";

        if (toolName.Contains("submission", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("assignment", StringComparison.OrdinalIgnoreCase))
            return "assignments.submissions";

        if (toolName.Contains("student", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("participant", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("group", StringComparison.OrdinalIgnoreCase))
            return "students.list";

        return null; // Unknown — may be hallucination
    }

    /// <summary>
    /// Returns true if the tool name is known in any profile's tool manifest.
    /// Tools returning null from Resolve are candidates for HallucinationDetected.
    /// </summary>
    public static string? ResolveOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation)) return null;
        if (operation.StartsWith("core_enrol_get_users_courses", StringComparison.OrdinalIgnoreCase)) return "courses.list";
        if (operation.StartsWith("core_course_get_courses_by_field", StringComparison.OrdinalIgnoreCase)) return "courses.details";
        if (operation.StartsWith("core_course_get_contents", StringComparison.OrdinalIgnoreCase)) return "courses.structure";
        if (operation.StartsWith("mod_assign_", StringComparison.OrdinalIgnoreCase)) return "assignments.submissions";
        if (operation.StartsWith("core_enrol_get_enrolled_users", StringComparison.OrdinalIgnoreCase) ||
            operation.StartsWith("core_group_", StringComparison.OrdinalIgnoreCase)) return "students.list";
        return Resolve(operation);
    }

    public static bool IsKnownTool(string toolName)
        => Resolve(toolName) != null;
}
