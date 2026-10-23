using Moodle.Application.Abstractions;
using Moodle.Application.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class MoodleAssignmentGradeScaleResolverTests
{
    [Fact]
    public void ReconcileAuthoritativeSources_ConflictingNumericBoundsAreUnknown()
    {
        var settings = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
            new AssignmentSettingsSummary("501", 40m, IsGradable: true, RawGrade: 40m),
            10,
            501,
            "connection:test");
        var gradebook = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(
            Gradebook(20m),
            10,
            501,
            "connection:test");

        var result = MoodleAssignmentGradeScaleResolver.ReconcileAuthoritativeSources(
            settings,
            gradebook,
            10,
            501,
            "connection:test");

        Assert.Equal("unknown", result.GradingMode);
        Assert.Equal("conflict", result.Source);
        Assert.Contains(settings.Version, result.SourceIdentity);
        Assert.Contains(gradebook.Version, result.SourceIdentity);
    }

    [Fact]
    public void ReconcileAuthoritativeSources_UsesExactActivityItemWhenSettingsAreUnknown()
    {
        var settings = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
            new AssignmentSettingsSummary("501", 0m),
            10,
            501,
            "connection:test");
        var gradebook = new CourseGradebook("10", "101",
        [
            GradeItem("course-total", "course", "", "", 0m, 100m),
            GradeItem("other-assignment", "mod", "assign", "502", 0m, 70m),
            GradeItem("exact-assignment", "mod", "assign", "501", 2m, 20m)
        ]);
        var exactScale = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(
            gradebook,
            10,
            501,
            "connection:test");

        var result = MoodleAssignmentGradeScaleResolver.ReconcileAuthoritativeSources(
            settings,
            exactScale,
            10,
            501,
            "connection:test");

        Assert.Equal("numeric", result.GradingMode);
        Assert.Equal(2m, result.MinimumGrade);
        Assert.Equal(20m, result.MaximumGrade);
        Assert.Equal("activity_gradebook", result.Source);
    }

    [Fact]
    public void ReconcileAuthoritativeSources_DoesNotConvertExplicitFeedbackOnlyToNumeric()
    {
        var settings = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
            new AssignmentSettingsSummary("501", 0m, IsGradable: false, RawGrade: 0m),
            10,
            501,
            "connection:test");
        var gradebook = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(
            Gradebook(10m),
            10,
            501,
            "connection:test");

        var result = MoodleAssignmentGradeScaleResolver.ReconcileAuthoritativeSources(
            settings,
            gradebook,
            10,
            501,
            "connection:test");

        Assert.Equal("unknown", result.GradingMode);
        Assert.Equal("conflict", result.Source);
    }

    [Fact]
    public void FromAssignmentSettings_PreservesNegativeQualitativeScaleIdentity()
    {
        var settings = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
            new AssignmentSettingsSummary("501", 0m, IsGradable: true, RawGrade: -7m, GradeScaleId: 7),
            10,
            501,
            "connection:test");
        var gradebook = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(
            new CourseGradebook("10", "101", [
                new GradebookItem(
                    "grade-item-scale",
                    "Atividade",
                    "mod",
                    "assign",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "501",
                    ScaleId: 7,
                    GradeType: 2)
            ]),
            10,
            501,
            "connection:test");

        var result = MoodleAssignmentGradeScaleResolver.ReconcileAuthoritativeSources(
            settings,
            gradebook,
            10,
            501,
            "connection:test");

        Assert.Equal("scale", result.GradingMode);
        Assert.Equal(7, result.ScaleId);
    }

    [Fact]
    public void FromActivityGradebook_DoesNotInferNumericModeWithoutMaximum()
    {
        var gradebook = new CourseGradebook("10", "101", [
            new GradebookItem(
                "grade-item-incomplete",
                "Atividade",
                "mod",
                "assign",
                null,
                null,
                null,
                0m,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "501",
                ScaleId: 0,
                GradeType: 1)
        ]);

        var result = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(gradebook, 10, 501, "connection:test");

        Assert.Equal("unknown", result.GradingMode);
        Assert.Equal("activity_gradebook_definition_incomplete", result.SourceIdentity);
    }

    [Fact]
    public void ReconcileAuthoritativeSources_BlocksAmbiguousExactGradebookItems()
    {
        var settings = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
            new AssignmentSettingsSummary("501", 10m, IsGradable: true, RawGrade: 10m),
            10,
            501,
            "connection:test");
        var ambiguous = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(
            new CourseGradebook("10", "101", [
                GradeItem("first", "mod", "assign", "501", 0m, 10m),
                GradeItem("second", "mod", "assign", "501", 0m, 10m)
            ]),
            10,
            501,
            "connection:test");

        var result = MoodleAssignmentGradeScaleResolver.ReconcileAuthoritativeSources(
            settings,
            ambiguous,
            10,
            501,
            "connection:test");

        Assert.Equal("unknown", result.GradingMode);
        Assert.Equal("conflict", result.Source);
    }

    [Fact]
    public void FromActivityGradebook_StableVersionIgnoresObservationTime()
    {
        var first = Gradebook(10m) with { ReadAt = DateTimeOffset.Parse("2026-10-01T00:00:00Z") };
        var second = Gradebook(10m) with { ReadAt = DateTimeOffset.Parse("2026-10-08T00:00:00Z") };

        var firstResolution = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(first, 10, 501, "connection:test");
        var secondResolution = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(second, 10, 501, "connection:test");

        Assert.Equal(firstResolution.Version, secondResolution.Version);
    }

    private static CourseGradebook Gradebook(decimal maximumGrade) =>
        new("10", "101", [GradeItem("exact-assignment", "mod", "assign", "501", 0m, maximumGrade)]);

    private static GradebookItem GradeItem(
        string id,
        string itemType,
        string itemModule,
        string itemInstance,
        decimal minimumGrade,
        decimal maximumGrade) =>
        new(
            id,
            "Item",
            itemType,
            itemModule,
            null,
            null,
            null,
            minimumGrade,
            maximumGrade,
            null,
            null,
            null,
            null,
            null,
            null,
            itemInstance,
            ScaleId: 0,
            GradeType: 1);
}
