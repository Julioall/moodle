using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Moodle.Application.Abstractions;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

public sealed record MoodleAssignmentGradeScaleResolution(
    string GradingMode,
    decimal? MinimumGrade,
    decimal? MaximumGrade,
    string Source,
    string SourceIdentity,
    long? ScaleId,
    string Version);

/// <summary>
/// Resolves only the definition for the exact assign activity. It never
/// converts a qualitative scale or infers a numeric maximum from rubric text.
/// </summary>
public static class MoodleAssignmentGradeScaleResolver
{
    public static string ConnectionScope(AssistedGradingBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return !string.IsNullOrWhiteSpace(batch.MoodleConnectionId)
            ? $"connection:{batch.MoodleConnectionId.Trim()}"
            : $"client:{(string.IsNullOrWhiteSpace(batch.ConnectorClientId) ? "default" : batch.ConnectorClientId)}" +
              $":alias:{(string.IsNullOrWhiteSpace(batch.ConnectionAlias) ? "default" : batch.ConnectionAlias)}";
    }

    public static MoodleAssignmentGradeScaleResolution FromAssignmentSettings(
        AssignmentSettingsSummary? settings,
        long courseId,
        long assignmentId,
        string connectionScope)
    {
        if (settings?.RawGrade is > 0 && settings.MaxGrade > 0)
        {
            return Create(
                courseId,
                assignmentId,
                connectionScope,
                "numeric",
                0m,
                settings.MaxGrade,
                "assignment_settings",
                $"mod_assign:{assignmentId.ToString(CultureInfo.InvariantCulture)}:grade:{Format(settings.RawGrade.Value)}",
                null);
        }

        if (settings?.RawGrade is < 0)
        {
            return Create(
                courseId,
                assignmentId,
                connectionScope,
                "scale",
                null,
                null,
                "assignment_settings",
                $"mod_assign:{assignmentId.ToString(CultureInfo.InvariantCulture)}:scale",
                settings.GradeScaleId);
        }

        if (settings?.RawGrade == 0m || settings?.IsGradable == false)
        {
            return Create(
                courseId,
                assignmentId,
                connectionScope,
                "feedback_only",
                null,
                null,
                "assignment_settings",
                $"mod_assign:{assignmentId.ToString(CultureInfo.InvariantCulture)}:feedback_only",
                null);
        }

        // Compatibility for older gateways that explicitly classified a
        // positive maximum as gradable but do not retain Moodle's signed grade.
        if (settings?.IsGradable == true && settings.MaxGrade > 0)
        {
            return Create(
                courseId,
                assignmentId,
                connectionScope,
                "numeric",
                0m,
                settings.MaxGrade,
                "assignment_settings",
                $"mod_assign:{assignmentId.ToString(CultureInfo.InvariantCulture)}:legacy_numeric",
                null);
        }

        return Unknown(courseId, assignmentId, connectionScope, "assignment_settings_unavailable");
    }

    public static MoodleAssignmentGradeScaleResolution FromActivityGradebook(
        CourseGradebook? gradebook,
        long courseId,
        long assignmentId,
        string connectionScope)
    {
        var exactItems = gradebook?.Items
            .Where(item =>
                string.Equals(item.ItemType, "mod", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ItemModule, "assign", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    item.ItemInstance,
                    assignmentId.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            .ToArray() ?? [];

        if (exactItems.Length != 1)
        {
            return Unknown(
                courseId,
                assignmentId,
                connectionScope,
                exactItems.Length > 1 ? "activity_gradebook_ambiguous" : "activity_gradebook_item_missing");
        }

        var gradeItem = exactItems[0];
        if (gradeItem.ScaleId is > 0 || gradeItem.GradeType == 2)
        {
            return Create(
                courseId,
                assignmentId,
                connectionScope,
                "scale",
                null,
                null,
                "activity_gradebook",
                $"grade_item:{gradeItem.Id}",
                gradeItem.ScaleId);
        }

        if (gradeItem.GradeType == 3 || gradeItem.GradeType == 0)
        {
            return Create(
                courseId,
                assignmentId,
                connectionScope,
                "feedback_only",
                null,
                null,
                "activity_gradebook",
                $"grade_item:{gradeItem.Id}:type:{gradeItem.GradeType}",
                gradeItem.ScaleId);
        }

        if (gradeItem.GradeMax is > 0m &&
            gradeItem.GradeMin is decimal minimumGrade &&
            (gradeItem.ScaleId == 0 || gradeItem.GradeType == 1))
        {
            return Create(
                courseId,
                assignmentId,
                connectionScope,
                "numeric",
                minimumGrade,
                gradeItem.GradeMax,
                "activity_gradebook",
                $"grade_item:{gradeItem.Id}:type:{gradeItem.GradeType}",
                gradeItem.ScaleId);
        }

        return Unknown(courseId, assignmentId, connectionScope, "activity_gradebook_definition_incomplete");
    }

    public static MoodleAssignmentGradeScaleResolution ReconcileAuthoritativeSources(
        MoodleAssignmentGradeScaleResolution settings,
        MoodleAssignmentGradeScaleResolution? activityGradebook,
        long courseId,
        long assignmentId,
        string connectionScope)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GradingMode == "unknown")
        {
            return activityGradebook is { GradingMode: not "unknown" }
                ? activityGradebook
                : settings;
        }

        if (activityGradebook is null || activityGradebook.GradingMode == "unknown")
        {
            return activityGradebook is { SourceIdentity: "activity_gradebook_ambiguous" }
                ? Conflict(settings, activityGradebook, courseId, assignmentId, connectionScope)
                : settings;
        }

        var sameModeAndBounds = settings.GradingMode == activityGradebook.GradingMode &&
            settings.MinimumGrade == activityGradebook.MinimumGrade &&
            settings.MaximumGrade == activityGradebook.MaximumGrade;
        var sameQualitativeScale = settings.GradingMode != "scale" || settings.ScaleId == activityGradebook.ScaleId;
        return sameModeAndBounds && sameQualitativeScale
            ? settings
            : Conflict(settings, activityGradebook, courseId, assignmentId, connectionScope);
    }

    public static MoodleAssignmentGradeScaleResolution Unknown(
        long courseId,
        long assignmentId,
        string connectionScope,
        string sourceIdentity) =>
        Create(
            courseId,
            assignmentId,
            connectionScope,
            "unknown",
            null,
            null,
            "unavailable",
            sourceIdentity,
            null);

    private static MoodleAssignmentGradeScaleResolution Create(
        long courseId,
        long assignmentId,
        string connectionScope,
        string gradingMode,
        decimal? minimumGrade,
        decimal? maximumGrade,
        string source,
        string sourceIdentity,
        long? scaleId)
    {
        var canonical = string.Join("\n",
            $"connection:{connectionScope}",
            $"course:{courseId.ToString(CultureInfo.InvariantCulture)}",
            $"assignment:{assignmentId.ToString(CultureInfo.InvariantCulture)}",
            $"mode:{gradingMode}",
            $"min:{minimumGrade?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "unknown"}",
            $"max:{maximumGrade?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "unknown"}",
            $"scale:{scaleId?.ToString(CultureInfo.InvariantCulture) ?? "none"}",
            $"source:{source}",
            $"sourceIdentity:{sourceIdentity}");
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new MoodleAssignmentGradeScaleResolution(
            gradingMode,
            minimumGrade,
            maximumGrade,
            source,
            sourceIdentity,
            scaleId,
            version);
    }

    private static string Format(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static MoodleAssignmentGradeScaleResolution Conflict(
        MoodleAssignmentGradeScaleResolution settings,
        MoodleAssignmentGradeScaleResolution activityGradebook,
        long courseId,
        long assignmentId,
        string connectionScope) =>
        Create(
            courseId,
            assignmentId,
            connectionScope,
            "unknown",
            null,
            null,
            "conflict",
            $"settings:{settings.Version}:activity_gradebook:{activityGradebook.Version}",
            null);
}
