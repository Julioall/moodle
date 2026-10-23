using Microsoft.Extensions.Options;
using Moodle.Application.Configuration;
using Moodle.Application.Reports.Templates;
using Moodle.Domain.Reports;

namespace Moodle.Application.Tests.Reports.Templates;

public sealed class ReportDefinitionTests
{
    private readonly ReportDefinitionValidator validator = new(new ReportCatalogService(),
        Options.Create(new ReportTemplateOptions()));

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":1,\"sourceId\":\"student_course\",\"columns\":null}")]
    [InlineData("{\"schemaVersion\":1,\"sourceId\":\"student_course\",\"columns\":[null]}")]
    [InlineData("{\"schemaVersion\":1,\"sourceId\":\"student_course\",\"columns\":[{\"fieldId\":null}]}")]
    public void NullAndMalformedAreValidationErrors(string? json) => Assert.False(validator.Parse(json).IsValid);

    [Theory]
    [InlineData("sorts")]
    [InlineData("sql")]
    [InlineData("readPolicy")]
    public void UnknownPropertiesCannotExpandDefinition(string property)
    {
        var json = $$"""{"schemaVersion":1,"sourceId":"student_course","columns":[{"fieldId":"student.name"}],"{{property}}":[]} """;
        Assert.False(validator.Parse(json).IsValid);
        var columnJson = $$"""{"schemaVersion":1,"sourceId":"student_course","columns":[{"fieldId":"student.name","{{property}}":[]}]}""";
        Assert.False(validator.Parse(columnJson).IsValid);
    }

    [Fact]
    public void FiltersAndParametersAreAcceptedAndNormalized()
    {
        var json = """{"schemaVersion":1,"sourceId":"student_course","columns":[{"fieldId":"student.name"}],"filters":[{"fieldId":"access.days_since_course","operator":"greater_than","value":"7"}],"parameters":{"inactiveDaysThreshold":10,"minGradePercent":65}}""";
        var result = validator.Parse(json);
        Assert.True(result.IsValid);
        Assert.Equal("access.days_since_course", Assert.Single(result.Definition!.Filters!).FieldId);
        Assert.Equal(10, result.Definition.Parameters!.InactiveDaysThreshold);
        Assert.Equal(65m, result.Definition.Parameters.MinGradePercent);
    }

    [Fact]
    public void RejectsInvalidFiltersAndParameters()
    {
        Assert.Contains(validator.Validate(new(1, "student_course", [new("student.name")],
            [new("student.name", "greater_than", "3")] )).Errors, error => error.Code == "unsupported_filter_operator");
        Assert.Contains(validator.Validate(new(1, "student_course", [new("student.name")],
            [new("access.days_since_course", "greater_than", "x")])).Errors, error => error.Code == "invalid_filter_value");
        Assert.Contains(validator.Validate(new(1, "student_course", [new("student.name")],
            Parameters: new ReportParameters(0, 101))).Errors, error => error.Code == "invalid_parameter");
    }

    [Fact]
    public void NormalizationPreservesOrderAndEffectiveLabels()
    {
        var result = validator.Validate(new(1, "student_course", [new("course.name", " UC "), new("student.name")]));
        Assert.True(result.IsValid);
        Assert.Equal(new[] { "course.name", "student.name" }, result.Definition!.Columns.Select(c => c.FieldId));
        Assert.Equal(new[] { "UC", "Nome do estudante" }, result.Definition.Columns.Select(c => c.Label));
    }

    [Fact]
    public void LegacyDefinitionsDropRetiredGroupAndNonTotalGradeFields()
    {
        var result = validator.Validate(new(1, "student_course",
            [new("student.name"), new("enrollment.groups"), new("grade.value"), new("grade.max"),
                new("grade.percentage"), new("grade.below_minimum_items")],
            [new("grade.percentage", "greater_or_equal", "60")]));

        Assert.True(result.IsValid);
        Assert.Equal(["student.name", "grade.value"], result.Definition!.Columns.Select(column => column.FieldId));
        Assert.Empty(result.Definition.Filters!);
    }

    [Fact]
    public void LegacyGradeColumnsAreReplacedByTheTotalGrade()
    {
        var result = validator.Validate(new(1, "student_course",
            [new("student.name"), new("grade.percentage"), new("activity.pending")]));

        Assert.True(result.IsValid);
        Assert.Equal(["student.name", "grade.value", "activity.pending"],
            result.Definition!.Columns.Select(column => column.FieldId));
    }

    [Fact]
    public void LegacyNotSubmittedColumnIsNormalizedToPendingWithoutDuplicate()
    {
        var result = validator.Validate(new(1, "student_course",
            [new("student.name"), new("activity.pending"), new("activity.not_submitted")],
            [new("activity.not_submitted", "greater_than", "0")]));

        Assert.True(result.IsValid);
        Assert.Equal(["student.name", "activity.pending"], result.Definition!.Columns.Select(column => column.FieldId));
        Assert.Equal("activity.pending", Assert.Single(result.Definition.Filters!).FieldId);
    }

    [Fact]
    public void LegacyNotSubmittedOnlyColumnIsPreservedAsPending()
    {
        var result = validator.Validate(new(1, "student_course", [new("activity.not_submitted")]));

        Assert.True(result.IsValid);
        var column = Assert.Single(result.Definition!.Columns);
        Assert.Equal("activity.pending", column.FieldId);
        Assert.Equal("Quantidade pendente", column.Label);
    }

    [Fact]
    public void LegacyCourseAccessStateIsNormalizedToNeverCourse()
    {
        var result = validator.Validate(new(1, "student_course",
            [new("access.course_state"), new("access.never_course")],
            [new("access.course_state", "equals", "Nunca acessou")]));

        Assert.True(result.IsValid);
        Assert.Equal(["access.never_course"], result.Definition!.Columns.Select(column => column.FieldId));
        var filter = Assert.Single(result.Definition.Filters!);
        Assert.Equal("access.never_course", filter.FieldId);
        Assert.Equal("Sim", filter.Value);
    }

    [Fact]
    public void LegacyPlatformAccessStateIsNormalizedToLastAccessDate()
    {
        var result = validator.Validate(new(1, "student_course",
            [new("access.platform_state")],
            [new("access.platform_state", "equals", "Nunca acessou"),
             new("access.platform_state", "equals", "Com registro")]));

        Assert.True(result.IsValid);
        var column = Assert.Single(result.Definition!.Columns);
        Assert.Equal("access.platform_at", column.FieldId);
        Assert.Equal("Último acesso ao AVA", column.Label);
        var filter = Assert.Single(result.Definition.Filters!);
        Assert.Equal("access.platform_at", filter.FieldId);
        Assert.Equal("is_missing", filter.Operator);
        Assert.Null(filter.Value);
    }

    [Fact]
    public void CustomColumnLabelsSurviveLegacyFieldNormalization()
    {
        var result = validator.Validate(new(1, "student_course",
            [new("access.course_state", "Acessou a UC?")]));

        Assert.True(result.IsValid);
        var column = Assert.Single(result.Definition!.Columns);
        Assert.Equal("access.never_course", column.FieldId);
        Assert.Equal("Acessou a UC?", column.Label);
    }

    [Fact]
    public void RejectsDuplicatesAndCaseInsensitiveEffectiveLabels()
    {
        Assert.Contains(validator.Validate(new(1, "student_course", [new("student.name"), new("student.name")])).Errors,
            error => error.Code == "duplicate_field");
        Assert.Contains(validator.Validate(new(1, "student_course", [new("student.id", " Nome "), new("student.name", "nome")])).Errors,
            error => error.Code == "duplicate_label");
    }

    [Fact]
    public void RejectsUnsupportedSchemaSourceFieldAndLimits()
    {
        Assert.False(validator.Validate(new(2, "student_course", [new("student.name")])).IsValid);
        Assert.False(validator.Validate(new(1, "student_assignment", [new("student.name")])).IsValid);
        Assert.True(validator.Validate(new(1, "student_course", [new("student.email")])).IsValid);
        Assert.False(validator.Validate(new(1, "student_course", [])).IsValid);
        Assert.False(validator.Validate(new(1, "student_course", [new("student.name", new string('a', 81))])).IsValid);
        Assert.False(validator.Parse(new string(' ', 32769)).IsValid);
        Assert.False(validator.Validate(new(1, "student_course", Enumerable.Repeat(new ReportColumnDefinition("student.name"), 15).ToArray())).IsValid);
    }

    [Fact]
    public void KnownZeroAndEmptyTextAreDistinctFromMissingAndUnavailable()
    {
        Assert.Equal(ReportCellState.Known, ReportCell.Known(0m).State);
        Assert.Equal(ReportCellState.Known, ReportCell.Known("").State);
        Assert.Equal(ReportCellState.Missing, ReportCell.Missing().State);
        Assert.Equal("live_failed", ReportCell.Unavailable("live_failed").Reason);
        Assert.Equal(ReportCellState.Unavailable, ReportCell.Unavailable("live_failed").State);
    }
}
