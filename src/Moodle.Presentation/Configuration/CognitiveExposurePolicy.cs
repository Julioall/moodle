namespace Moodle.Presentation.Configuration;

public enum ToolExposureProfile
{
    Production,
    Full,
    FullWithCoursesSkill,
    SkillCoursesOptimized,
    SkillCoursesHideGetCourse,
    SkillCoursesHideSearchCourses,
    SkillCoursesHideListMyCourses,
    SkillCoursesHideGetAndSearchCourses
}

public sealed class CognitiveExposurePolicy : IMcpToolExposurePolicy
{
    private static readonly string[] ProductionHiddenStatuses =
    [
        "ApprovedForHide",
        "Deprecated",
        "Diagnostic",
        "Internal"
    ];

    private readonly ToolExposureProfile _profile;

    internal static bool IsProductionHiddenStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) &&
        ProductionHiddenStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    public CognitiveExposurePolicy(ToolExposureProfile profile)
    {
        _profile = profile;
    }

    public bool ShouldExpose(string toolName, MoodleToolMetadataAttribute? metadata)
    {
        // Full profiles preserve backwards compatibility. Optimized profiles
        // fail closed when a tool has no deterministic metadata instead of
        // silently exposing an unclassified surface.
        if (metadata == null)
        {
            return _profile is ToolExposureProfile.Full or ToolExposureProfile.FullWithCoursesSkill;
        }

        if (_profile == ToolExposureProfile.Production && IsProductionHiddenStatus(metadata.ExposureStatus))
            return false;

        if (_profile == ToolExposureProfile.SkillCoursesHideGetCourse && toolName.Equals("get_course", StringComparison.OrdinalIgnoreCase))
            return false;
        if (_profile == ToolExposureProfile.SkillCoursesHideSearchCourses && toolName.Equals("search_courses", StringComparison.OrdinalIgnoreCase))
            return false;
        if (_profile == ToolExposureProfile.SkillCoursesHideListMyCourses && toolName.Equals("list_my_courses", StringComparison.OrdinalIgnoreCase))
            return false;

        if (_profile == ToolExposureProfile.SkillCoursesHideGetAndSearchCourses &&
            (toolName.Equals("get_course", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("search_courses", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (_profile == ToolExposureProfile.SkillCoursesOptimized)
        {
            // Profile C: hide only non-structural Course wrappers classified as R1/R2.
            // Do NOT hide structural primitives like `moodle_execute_read` or
            // registry/discovery helpers at this stage.
            if (metadata.Family == "courses" &&
                (metadata.Classification == "R1" || metadata.Classification == "R2") &&
                !metadata.Structural)
            {
                return false;
            }
        }

        return true;
    }
}
