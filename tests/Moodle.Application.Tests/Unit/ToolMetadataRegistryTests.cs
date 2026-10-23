using Xunit;
using ModelContextProtocol.Server;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;
using Moodle.Presentation.Tools;
using Moodle.Presentation.Tools.Completion;
using Moodle.Presentation.Tools.Forums;
using Moodle.Presentation.Tools.Gradebook;
using Moodle.Presentation.Tools.Grading;

namespace Moodle.Application.Tests.Unit;

public class ToolMetadataRegistryTests
{
    [Fact]
    public void Empty_registry_does_not_scan_loaded_assemblies()
    {
        var reg = new ToolMetadataRegistry();

        Assert.Empty(reg.Entries);
    }

    [Fact]
    public void RegisterFromType_PopulatesRegistryForCourseTools()
    {
        var reg = new ToolMetadataRegistry();
        reg.RegisterFromType(typeof(MoodleCoursesTools));

        Assert.True(reg.TryGet("get_course", out var meta));
        Assert.NotNull(meta);
        Assert.Equal("courses", meta!.Family);
        Assert.Equal("R1", meta.Classification);
        Assert.False(meta.Structural);
        Assert.Equal("tool.courses.view", meta.RequiredPlatformPermission);
        Assert.Equal(MoodleScopePolicies.ReadCourses, meta.RequiredOAuthScopes);
    }

    [Fact]
    public void Standard_search_and_fetch_contracts_are_registered_as_structural_tools()
    {
        var reg = new ToolMetadataRegistry();
        reg.RegisterFromType(typeof(MoodleCoursesTools));

        Assert.True(reg.TryGet("search", out var search));
        Assert.Equal("courses", search!.Family);
        Assert.Equal("R6", search.Classification);
        Assert.Equal("search", search.CanonicalOperation);
        Assert.True(search.Structural);

        Assert.True(reg.TryGet("fetch", out var fetch));
        Assert.Equal("courses", fetch!.Family);
        Assert.Equal("R6", fetch.Classification);
        Assert.Equal("fetch", fetch.CanonicalOperation);
        Assert.True(fetch.Structural);

        var searchMethod = typeof(MoodleCoursesTools).GetMethod(nameof(MoodleCoursesTools.SearchAsync));
        var searchContract = searchMethod!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: true)
            .Cast<McpServerToolAttribute>()
            .Single();
        Assert.Equal("search", searchContract.Name);
        Assert.Equal(typeof(MoodleCoursesTools.SearchResponse), searchContract.OutputSchemaType);
        Assert.Contains(searchMethod.GetParameters(), parameter => parameter.Name == "query");

        var fetchMethod = typeof(MoodleCoursesTools).GetMethod(nameof(MoodleCoursesTools.FetchAsync));
        var fetchContract = fetchMethod!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: true)
            .Cast<McpServerToolAttribute>()
            .Single();
        Assert.Equal("fetch", fetchContract.Name);
        Assert.Equal(typeof(MoodleCoursesTools.FetchResponse), fetchContract.OutputSchemaType);
        Assert.Contains(fetchMethod.GetParameters(), parameter => parameter.Name == "id");
    }

    [Fact]
    public void RegisterFromType_UsesExplicitMetadataForUniversalRead()
    {
        var reg = new ToolMetadataRegistry();
        reg.RegisterFromType(typeof(MoodleUniversalTools));

        Assert.True(reg.TryGet("moodle_execute_read", out var meta));
        Assert.NotNull(meta);
        Assert.Equal("discovery", meta!.Family);
        Assert.Equal("R6", meta.Classification);
        Assert.Equal("read", meta.Kind);
        Assert.Equal("moodle.execute_read", meta.CanonicalOperation);
        Assert.True(meta.Structural);
        Assert.True(McpReadInvocationScope.IsSafeReadTool(meta.Family, meta.Kind, meta.CanonicalOperation));
    }

    [Fact]
    public void Inference_UsesDomainImplementationAndMarksControlledBoundaries()
    {
        var reg = new ToolMetadataRegistry();
        reg.RegisterFromType(typeof(MoodleCourseActivitiesTools));
        reg.RegisterFromType(typeof(MoodleAssignmentSubmissionsTools));
        reg.RegisterFromType(typeof(MoodleForumTools));

        Assert.True(reg.TryGet("list_course_assignments", out var assignments));
        Assert.Equal("assignments", assignments!.Family);
        Assert.Equal("R4", assignments.Classification);
        Assert.Equal("specialized", assignments.Kind);

        Assert.True(reg.TryGet("list_assignment_submissions", out var submissions));
        Assert.Equal("assignments", submissions!.Family);
        Assert.Equal("R4", submissions.Classification);
        Assert.Equal("specialized", submissions.Kind);

        Assert.True(reg.TryGet("confirm_forum_post", out var forumWrite));
        Assert.Equal("R5", forumWrite!.Classification);
        Assert.Equal("controlled-write", forumWrite.Kind);
    }

    [Fact]
    public void Tool_contracts_do_not_inherit_the_wrong_domain_scope()
    {
        var reg = new ToolMetadataRegistry();
        reg.RegisterFromType(typeof(MoodleStudentPerformanceTools));
        reg.RegisterFromType(typeof(MoodleForumParticipationTools));
        reg.RegisterFromType(typeof(MoodleAccessMonitoringTools));

        Assert.True(reg.TryGet("get_student_activity_grades", out var grades));
        Assert.Equal("tool.assignments.view", grades!.RequiredPlatformPermission);
        Assert.Contains(MoodleScopePolicies.ReadAssignments, grades.RequiredOAuthScopes);
        Assert.Contains(MoodleScopePolicies.ReadSubmissions, grades.RequiredOAuthScopes);

        Assert.True(reg.TryGet("list_students_without_forum_participation", out var forumParticipation));
        Assert.Equal("tool.followup.view", forumParticipation!.RequiredPlatformPermission);
        Assert.Contains(MoodleScopePolicies.ReadForums, forumParticipation.RequiredOAuthScopes);

        Assert.True(reg.TryGet("list_students_without_recent_access", out var access));
        Assert.Equal("tool.followup.view", access!.RequiredPlatformPermission);
        Assert.Contains(MoodleScopePolicies.ReadAccess, access.RequiredOAuthScopes);
    }

    [Fact]
    public void Moodle_wrappers_declare_the_remote_capabilities_they_need()
    {
        var reg = new ToolMetadataRegistry(RegisteredMcpToolContainers.All);

        Assert.True(reg.TryGet("list_course_contents", out var contents));
        Assert.Equal("core_course_get_contents", contents!.RequiredMoodleCapabilities);
        Assert.True(reg.TryGet("list_assignment_submissions", out var submissions));
        Assert.Equal("mod_assign_get_submissions", submissions!.RequiredMoodleCapabilities);
        Assert.True(reg.TryGet("confirm_forum_post", out var forumWrite));
        Assert.Equal("mod_forum_add_discussion", forumWrite!.RequiredMoodleCapabilities);
        Assert.True(reg.TryGet("report_students_at_risk", out var risk));
        Assert.Equal("core_enrol_get_enrolled_users", risk!.RequiredMoodleCapabilities);
    }

    [Fact]
    public void Risk_report_is_authorized_by_report_permission_without_classroom_access()
    {
        var registry = new ToolMetadataRegistry(RegisteredMcpToolContainers.All);
        Assert.True(registry.TryGet("report_students_at_risk", out var risk));
        Assert.Equal("tool.reports.view", risk!.RequiredPlatformPermission);

        var grantedScopes = ToolAuthorizationMapping.ScopesForPermissions(["tool.reports.view"]);
        var requiredScopes = risk.RequiredOAuthScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.All(requiredScopes, scope => Assert.Contains(scope, grantedScopes));
        Assert.DoesNotContain(MoodleScopePolicies.ReadActivities, requiredScopes);
        Assert.Contains(MoodleScopePolicies.ReadAccess, requiredScopes);
        Assert.Contains(MoodleScopePolicies.ReadStudents, requiredScopes);
        Assert.Contains(MoodleScopePolicies.ReadAssignments, requiredScopes);
        Assert.Contains(MoodleScopePolicies.ReadSubmissions, requiredScopes);
    }

    [Fact]
    public void Registry_covers_the_complete_registered_mcp_surface()
    {
        var reg = new ToolMetadataRegistry(RegisteredMcpToolContainers.All);

        Assert.Equal(100, reg.Entries.Count);
        Assert.All(reg.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Key));
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Classification));
            Assert.Matches("^R[1-6]$", entry.Value.Classification);
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.ExposureStatus));
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Evidence));
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.RequiredPlatformPermission));
            Assert.DoesNotContain("moodle.admin", entry.Value.RequiredOAuthScopes, StringComparison.OrdinalIgnoreCase);
        });

        Assert.True(reg.TryGet("list_course_contents", out var contents));
        Assert.Equal("tool.classroom.view", contents!.RequiredPlatformPermission);
        Assert.Equal(MoodleScopePolicies.ReadContents, contents.RequiredOAuthScopes);

        Assert.True(reg.TryGet("moodle_execute_read", out var universalRead));
        Assert.Equal(MoodleScopePolicies.ReadAny, universalRead!.RequiredOAuthScopes);
        Assert.DoesNotContain(MoodleScopePolicies.WriteAny, universalRead.RequiredOAuthScopes, StringComparison.OrdinalIgnoreCase);

        Assert.True(reg.TryGet("get_connector_build_info", out var buildInfo));
        Assert.Equal("tool.connections.manage", buildInfo!.RequiredPlatformPermission);
        Assert.Equal(MoodleScopePolicies.ReadAny, buildInfo.RequiredOAuthScopes);
        Assert.True(buildInfo.Structural);

        Assert.True(reg.TryGet("moodle_list_connections", out var connectionList));
        Assert.Equal("connector.connections.list", connectionList!.CanonicalOperation);
        Assert.Equal("tool.connections.manage", connectionList.RequiredPlatformPermission);
        Assert.Equal(MoodleScopePolicies.ReadAny, connectionList.RequiredOAuthScopes);
        Assert.True(connectionList.Structural);

        Assert.False(reg.TryGet("list_all_gradable_submissions", out _));

        Assert.True(reg.TryGet("get_student_submission", out var canonicalSubmission));
        Assert.True(reg.TryGet("get_submission_status", out var submissionAlias));
        Assert.Equal("assignments.submissions.get_student", canonicalSubmission!.CanonicalOperation);
        Assert.Equal(canonicalSubmission.CanonicalOperation, submissionAlias!.CanonicalOperation);
        Assert.Equal("get_student_submission", submissionAlias.CompatibilityAliasOf);
        Assert.Equal("CompatibilityAlias", submissionAlias.ExposureStatus);
        Assert.Equal("mod_assign_get_submissions", canonicalSubmission.RequiredMoodleCapabilities);
        Assert.Equal(canonicalSubmission.RequiredMoodleCapabilities, submissionAlias.RequiredMoodleCapabilities);

        var inventory = new ToolSurfaceInventory(reg);
        Assert.Equal(reg.Entries.Count, inventory.Total);
        Assert.InRange(inventory.StructuralCount, 1, inventory.Total);
        Assert.InRange(inventory.SpecializedCount, 1, inventory.Total);
        Assert.InRange(inventory.ControlledWriteCount, 1, inventory.Total);
        Assert.Equal(0, inventory.DeprecatedCount);
        Assert.InRange(inventory.DiagnosticCount, 0, inventory.Total);
        Assert.InRange(inventory.ProductionHiddenCount, 0, inventory.Total);
        Assert.Equal(1, inventory.CompatibilityAliasCount);
    }

    [Fact]
    public void Catalog_is_the_single_source_for_conditional_mcp_registration()
    {
        var enabled = RegisteredMcpToolContainers.GetEnabledContainers(
            new FeatureOptions(),
            new AssignmentWriteFeatureOptions { AssignmentGradeWriteEnabled = false });

        Assert.False(new ToolMetadataRegistry(RegisteredMcpToolContainers.All).TryGet("prepare_demo_action", out _));
        Assert.False(new ToolMetadataRegistry(RegisteredMcpToolContainers.All).TryGet("confirm_demo_action", out _));
        Assert.Equal(
            RegisteredMcpToolContainers.All,
            RegisteredMcpToolContainers.AlwaysOn
                .Concat(RegisteredMcpToolContainers.Conditional.Select(container => container.ContainerType))
                .ToArray());
    }

    [Fact]
    public void Suspended_planner_tools_are_not_registered_in_the_mcp_catalog()
    {
        var registry = new ToolMetadataRegistry(RegisteredMcpToolContainers.All);

        Assert.False(registry.TryGet("list_tasks", out _));
        Assert.False(registry.TryGet("list_agenda_events", out _));
        Assert.False(registry.TryGet("create_task", out _));
        Assert.False(registry.TryGet("create_agenda_event", out _));
        Assert.False(registry.TryGet("get_student_completion", out _));
    }

    [Fact]
    public void Disabled_write_features_hide_write_tools_but_keep_reads_available()
    {
        var features = new FeatureOptions();
        var assignment = new AssignmentWriteFeatureOptions();

        Assert.False(RegisteredMcpToolContainers.IsToolEnabled("moodle_prepare_write", features, assignment));
        Assert.False(RegisteredMcpToolContainers.IsToolEnabled("prepare_welcome_message", features, assignment));
        Assert.True(RegisteredMcpToolContainers.IsToolEnabled("moodle_execute_read", features, assignment));
        Assert.True(RegisteredMcpToolContainers.IsToolEnabled("moodle_reconcile_write", features, assignment));
        Assert.Contains(typeof(MoodleWriteReconciliationTools), RegisteredMcpToolContainers.AlwaysOn);
    }
}
