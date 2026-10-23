namespace Moodle.Presentation.Configuration;

/// <summary>
/// Stable capability contracts for domain wrappers whose MCP name is not the
/// Moodle Web Service function name. A tool is exposed only when every listed
/// function is present in the selected connection's discovered profile.
/// Local portal/snapshot tools intentionally have no remote capability.
/// </summary>
internal static class MoodleToolCapabilityMapping
{
    private static readonly IReadOnlyDictionary<string, string> Capabilities =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["list_course_participants"] = "core_enrol_get_enrolled_users",
            ["list_course_students"] = "core_enrol_get_enrolled_users",
            ["list_course_groups"] = "core_group_get_course_groups",
            ["get_group_members"] = "core_enrol_get_enrolled_users",
            ["list_course_contents"] = "core_course_get_contents",
            ["get_course_module"] = "core_course_get_contents",
            ["list_course_resources"] = "core_course_get_contents",
            ["list_course_files"] = "core_course_get_contents",
            ["list_course_pages"] = "core_course_get_contents",
            ["list_course_urls"] = "core_course_get_contents",
            ["list_course_activities"] = "core_course_get_contents",
            ["get_course_activity"] = "core_course_get_contents",
            ["list_course_assignments"] = "core_course_get_contents",
            ["get_assignment"] = "core_course_get_contents",
            ["list_course_quizzes"] = "core_course_get_contents",
            ["get_quiz"] = "core_course_get_contents",
            ["list_course_scorms"] = "core_course_get_contents",
            ["ler_scorm"] = "mod_scorm_get_scorms_by_courses",
            ["list_activity_deadlines"] = "core_course_get_contents",
            ["list_assignment_submissions"] = "mod_assign_get_submissions",
            ["get_student_submission"] = "mod_assign_get_submissions",
            ["list_pending_submissions"] = "mod_assign_get_submissions",
            ["list_late_submissions"] = "mod_assign_get_submissions",
            ["list_submissions_awaiting_grading"] = "mod_assign_get_submissions",
            // This tool reads the current attempt and feedback state through
            // its dedicated gateway. Exposing it only because the bulk
            // submissions function exists caused a runtime failure whenever
            // mod_assign_get_submission_status was disabled for a connection.
            ["get_submission_status"] = "mod_assign_get_submission_status",
            ["get_student_activity_grades"] = "gradereport_user_get_grade_items",
            ["list_students_below_min_grade"] = "gradereport_user_get_grade_items",
            ["get_student_gradebook"] = "gradereport_user_get_grade_items",
            ["list_students_without_forum_participation"] = "mod_forum_get_discussion_posts",
            ["read_forum"] = "mod_forum_get_forums_by_courses mod_forum_get_forum_discussions mod_forum_get_discussion_posts",
            ["create_forum_post_preview"] = "mod_forum_can_add_discussion",
            ["confirm_forum_post"] = "mod_forum_add_discussion",
            ["list_students_without_recent_access"] = "core_user_get_course_user_profiles",
            ["report_students_at_risk"] = "core_enrol_get_enrolled_users",
            ["prepare_welcome_message"] = "core_message_send_instant_messages",
            ["confirm_welcome_message"] = "core_message_send_instant_messages",
            ["prepare_access_reminder"] = "core_message_send_instant_messages",
            ["confirm_access_reminder"] = "core_message_send_instant_messages",
            ["prepare_activity_reminder"] = "core_message_send_instant_messages",
            ["confirm_activity_reminder"] = "core_message_send_instant_messages",
            ["prepare_recovery_message"] = "core_message_send_instant_messages",
            ["confirm_recovery_message"] = "core_message_send_instant_messages",
            ["prepare_closing_message"] = "core_message_send_instant_messages",
            ["confirm_closing_message"] = "core_message_send_instant_messages",
            ["prepare_followup_message"] = "core_message_send_instant_messages",
            ["confirm_followup_message"] = "core_message_send_instant_messages",
            ["generate_course_grades_report"] = "gradereport_user_get_grades_table",
            ["export_course_grades_excel"] = "gradereport_user_get_grades_table",
            ["generate_weekly_performance_report"] = "mod_assign_get_submissions",
            ["generate_class_council_report"] = "gradereport_user_get_grades_table",
            ["generate_course_summary"] = "core_course_get_contents",
            ["generate_full_post_execution_report"] = "core_course_get_contents",
            ["audit_virtual_classroom_checklist"] = "core_course_get_contents",
            ["generate_monitor_class_report"] = "core_course_get_contents",
        };

    public static string For(string toolName) =>
        Capabilities.TryGetValue(toolName, out var value) ? value : string.Empty;
}
