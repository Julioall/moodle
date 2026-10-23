using Moodle.Application.Configuration;
using Moodle.Application.Reports.Templates;
using Moodle.Domain.Reports;

namespace Moodle.Application.Tests.Reports.Templates;

public sealed class ReportCatalogTests
{
    [Fact]
    public void CatalogHasExactlyV1FieldsWithExplicitPolicies()
    {
        var fields = new ReportCatalogService().Fields;
        Assert.Equal(30, fields.Count);
        Assert.Equal(30, fields.Select(f => f.FieldId).Distinct().Count());
        Assert.All(fields.Where(f => f.FieldId.StartsWith("grade.") || f.FieldId.StartsWith("access.") || f.FieldId.StartsWith("enrollment.")),
            f => Assert.Equal(ReportReadPolicy.LiveRequired, f.ReadPolicy));
        Assert.All(fields.Where(f => f.FieldId.StartsWith("course.") ||
                f.FieldId.StartsWith("student.") && f.FieldId != "student.email"),
            f => Assert.Equal(ReportReadPolicy.SnapshotPreferred, f.ReadPolicy));
        Assert.Equal(ReportReadPolicy.LiveRequired, fields.Single(f => f.FieldId == "student.email").ReadPolicy);
        Assert.Contains(fields, field => field.FieldId == "access.never_course" && field.Group == "Acesso");
        Assert.Contains(fields, field => field.FieldId == "tracking.situation" && field.Label == "Situação");
        Assert.Contains(fields, field => field.FieldId == "grade.value" && field.Group == "Desempenho");
        Assert.DoesNotContain(fields, field => field.FieldId is "enrollment.groups" or "grade.max" or "grade.percentage" or
            "activity.not_submitted" or "access.course_state" or "access.platform_state");
        Assert.Equal(["grade.value"], fields.Where(field => field.FieldId.StartsWith("grade.", StringComparison.Ordinal))
            .Select(field => field.FieldId));
        Assert.DoesNotContain(fields, field => field.Group == "Campos avançados");
    }

    [Fact]
    public void NameOnlyStillRequiresLivePopulationWithoutGradebook()
    {
        var requirements = new ReportCatalogService().Requirements(new(1, "student_course", [new("student.name")]));
        var requirement = Assert.Single(requirements);
        Assert.Equal("participants", requirement.Family);
        Assert.Equal(ReportReadPolicy.LiveRequired, requirement.ReadPolicy);
    }

    [Fact]
    public void TotalGradeRequiresLiveGradebookWithoutScale()
    {
        var requirements = new ReportCatalogService().Requirements(new(1, "student_course",
            [new("grade.value"), new("course.name")]));
        Assert.Equal(3, requirements.Count);
        Assert.DoesNotContain(requirements, requirement => requirement.Family == "grade_scale");
        Assert.Equal(ReportReadPolicy.LiveRequired, requirements.Single(r => r.Family == "gradebook").ReadPolicy);
        Assert.Equal(ReportReadPolicy.SnapshotPreferred, requirements.Single(r => r.Family == "course").ReadPolicy);
    }

    [Fact]
    public void FiltersRequireTheirSourceEvenWhenTheFieldIsNotAColumn()
    {
        var requirements = new ReportCatalogService().Requirements(new(1, "student_course",
            [new("student.name")], [new("activity.pending", "greater_than", "0")]));
        Assert.Contains(requirements, requirement => requirement.Family == "activities" && requirement.ReadPolicy == ReportReadPolicy.LiveRequired);
        Assert.Contains(requirements, requirement => requirement.Family == "gradebook" && requirement.ReadPolicy == ReportReadPolicy.LiveRequired);
    }

    [Fact]
    public void ConfigurationCannotRaiseV1Budgets()
    {
        Assert.True(new ReportTemplateOptions().IsValid());
        Assert.False(new ReportTemplateOptions { PreviewMaxGradeReads = 6 }.IsValid());
        Assert.False(new ReportTemplateOptions { MaxRows = 10001 }.IsValid());
        Assert.False(new ReportTemplateOptions { MaxCourses = 21 }.IsValid());
        Assert.False(new ReportTemplateOptions { MaxAssignmentsPerCourse = 26 }.IsValid());
        Assert.False(new ReportTemplateOptions { GenerationTimeoutSeconds = 601 }.IsValid());
        Assert.False(new ReportTemplateOptions { MaxColumns = 0 }.IsValid());
        Assert.False(new ReportTemplateOptions { MaxFilters = 21 }.IsValid());
        Assert.False(new ReportTemplateOptions { PreviewTimeoutSeconds = 11 }.IsValid());
    }
}
