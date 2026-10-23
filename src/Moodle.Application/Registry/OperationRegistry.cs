using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

/// <summary>
/// Builds operation policy from the requested function name. Availability is
/// still checked against the token-specific capability snapshot by the
/// executor; this registry intentionally does not duplicate a static Moodle
/// function inventory.
/// </summary>
public sealed class OperationRegistry : IOperationRegistry
{
    public MoodleOperation? GetOperation(string operationName)
    {
        if (string.IsNullOrWhiteSpace(operationName))
        {
            return null;
        }

        var functionName = operationName.Trim();
        var risk = MoodleFunctionClassifier.Classify(functionName);
        return risk switch
        {
            MoodleFunctionRisk.Read => new MoodleOperation(
                functionName, GetCategory(functionName), OperationType.Read, ToolRiskLevel.ReadOnly,
                OperationPolicy.Direct, GetNormalizationProfile(functionName)),
            MoodleFunctionRisk.ControlledWrite or MoodleFunctionRisk.Destructive or MoodleFunctionRisk.Unknown => new MoodleOperation(
                functionName, GetCategory(functionName), OperationType.ControlledWrite, ToolRiskLevel.HumanConfirmedWrite,
                OperationPolicy.Aggregated, "controlled-write"),
            _ => null
        };
    }

    // The operation surface is token-specific and discovered at execution
    // time, so there is no process-wide inventory to return here.
    public IReadOnlyList<MoodleOperation> GetAllOperations() => [];

    private static string GetCategory(string functionName) =>
        functionName switch
        {
            _ when functionName.StartsWith("core_course_", StringComparison.OrdinalIgnoreCase) => "course",
            _ when functionName.StartsWith("core_enrol_", StringComparison.OrdinalIgnoreCase) => "enrollment",
            _ when functionName.StartsWith("core_user_", StringComparison.OrdinalIgnoreCase) => "student",
            _ when functionName.StartsWith("core_group_", StringComparison.OrdinalIgnoreCase) => "participants",
            _ when functionName.StartsWith("core_completion_", StringComparison.OrdinalIgnoreCase) => "completion",
            _ when functionName.StartsWith("gradereport_", StringComparison.OrdinalIgnoreCase) || functionName.StartsWith("core_grades_", StringComparison.OrdinalIgnoreCase) => "gradebook",
            _ when functionName.StartsWith("mod_assign_", StringComparison.OrdinalIgnoreCase) => "assignment",
            _ when functionName.StartsWith("mod_forum_", StringComparison.OrdinalIgnoreCase) => "forum",
            _ when functionName.StartsWith("core_message_", StringComparison.OrdinalIgnoreCase) || functionName.StartsWith("message_", StringComparison.OrdinalIgnoreCase) => "messaging",
            _ when functionName.StartsWith("core_calendar_", StringComparison.OrdinalIgnoreCase) => "calendar",
            _ when functionName.StartsWith("core_files_", StringComparison.OrdinalIgnoreCase) => "files",
            _ => "moodle"
        };

    private static string GetNormalizationProfile(string functionName) =>
        functionName switch
        {
            _ when functionName.Contains("course", StringComparison.OrdinalIgnoreCase) => "course-list",
            _ when functionName.Contains("submission", StringComparison.OrdinalIgnoreCase) => "assignment-submissions",
            _ when functionName.Contains("grade", StringComparison.OrdinalIgnoreCase) => "gradebook",
            _ when functionName.Contains("completion", StringComparison.OrdinalIgnoreCase) => "activity-completion",
            _ => "generic-read"
        };
}
