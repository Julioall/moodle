using Moodle.Presentation.Configuration;
using Xunit;

namespace Moodle.Application.Tests.Unit;

public class CognitiveExposurePolicyTests
{
    [Theory]
    [InlineData(ToolExposureProfile.SkillCoursesHideGetCourse, "get_course")]
    [InlineData(ToolExposureProfile.SkillCoursesHideSearchCourses, "search_courses")]
    [InlineData(ToolExposureProfile.SkillCoursesHideListMyCourses, "list_my_courses")]
    public void Incremental_courses_profiles_hide_only_the_selected_wrapper(ToolExposureProfile profile, string toolName)
    {
        var policy = new CognitiveExposurePolicy(profile);
        var metadata = new MoodleToolMetadataAttribute
        {
            Family = "courses",
            Classification = "R1",
            Structural = false
        };

        Assert.False(policy.ShouldExpose(toolName, metadata));
        Assert.True(policy.ShouldExpose("moodle_execute_read", new MoodleToolMetadataAttribute { Structural = true }));
    }

    [Fact]
    public void SkillCoursesOptimized_Hides_Course_R1_R2_NonStructural_ButKeeps_Structural()
    {
        var policy = new CognitiveExposurePolicy(ToolExposureProfile.SkillCoursesOptimized);

        var getCourseMeta = new MoodleToolMetadataAttribute
        {
            Family = "courses",
            Classification = "R1",
            Structural = false
        };

        var execReadMeta = new MoodleToolMetadataAttribute
        {
            Family = "universal",
            Classification = "R0",
            Structural = true
        };

        Assert.False(policy.ShouldExpose("get_course", getCourseMeta));
        Assert.True(policy.ShouldExpose("moodle_execute_read", execReadMeta));
    }

    [Fact]
    public void Combined_courses_profile_hides_get_and_search_but_keeps_list()
    {
        var policy = new CognitiveExposurePolicy(ToolExposureProfile.SkillCoursesHideGetAndSearchCourses);

        Assert.False(policy.ShouldExpose("get_course", new MoodleToolMetadataAttribute { Family = "courses", Classification = "R1" }));
        Assert.False(policy.ShouldExpose("search_courses", new MoodleToolMetadataAttribute { Family = "courses", Classification = "R1" }));
        Assert.True(policy.ShouldExpose("list_my_courses", new MoodleToolMetadataAttribute { Family = "courses", Classification = "R1" }));
    }

    [Fact]
    public void Production_exposes_registered_surface_but_fails_closed_for_unknown_metadata()
    {
        var policy = new CognitiveExposurePolicy(ToolExposureProfile.Production);

        Assert.True(policy.ShouldExpose("list_my_courses", new MoodleToolMetadataAttribute
        {
            Family = "courses", Classification = "R1", ExposureStatus = "Keep"
        }));
        Assert.False(policy.ShouldExpose("future_unregistered_tool", null));
        Assert.False(policy.ShouldExpose("legacy_tool", new MoodleToolMetadataAttribute
        {
            ExposureStatus = "Deprecated"
        }));
    }

    [Fact]
    public void Production_hides_approved_for_hide_but_full_diagnostic_profile_keeps_it()
    {
        var metadata = new MoodleToolMetadataAttribute
        {
            Family = "courses",
            Classification = "R1",
            ExposureStatus = "ApprovedForHide"
        };

        Assert.False(new CognitiveExposurePolicy(ToolExposureProfile.Production)
            .ShouldExpose("search_courses", metadata));
        Assert.True(new CognitiveExposurePolicy(ToolExposureProfile.Full)
            .ShouldExpose("search_courses", metadata));
    }

    [Fact]
    public void Compatibility_aliases_remain_exposed_until_migration_evidence_exists()
    {
        var metadata = new MoodleToolMetadataAttribute
        {
            ExposureStatus = "CompatibilityAlias",
            CompatibilityAliasOf = "get_student_submission"
        };

        Assert.True(new CognitiveExposurePolicy(ToolExposureProfile.Production)
            .ShouldExpose("get_submission_status", metadata));
        Assert.True(new CognitiveExposurePolicy(ToolExposureProfile.Full)
            .ShouldExpose("get_submission_status", metadata));
    }

    [Theory]
    [InlineData("Diagnostic")]
    [InlineData("Internal")]
    public void Production_hides_technical_metadata_but_full_keeps_it_callable(string exposureStatus)
    {
        var metadata = new MoodleToolMetadataAttribute
        {
            Family = "discovery",
            Kind = "diagnostic",
            Classification = "R6",
            ExposureStatus = exposureStatus,
            Structural = true
        };

        Assert.False(new CognitiveExposurePolicy(ToolExposureProfile.Production)
            .ShouldExpose("moodle_check_function", metadata));
        Assert.True(new CognitiveExposurePolicy(ToolExposureProfile.Full)
            .ShouldExpose("moodle_check_function", metadata));
    }
}
