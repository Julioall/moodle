using System.Text.Json.Nodes;
using Moodle.Application.Benchmarking;
using Moodle.Domain.Registry;

namespace Moodle.Application.Tests.ShadowComparison;

public sealed class CoursesShadowTests
{
    [Fact]
    public async Task ShadowComparison_GetCourse_ShouldAchieve100PercentParity()
    {
        // Arrange
        var profile = new CourseComparisonProfile();
        var runner = new ShadowComparisonRunner([profile]);

        var legacyJson = """
        [
            {
                "id": 42,
                "shortname": "CS101",
                "fullname": "Computer Science 101",
                "startdate": 1600000000,
                "enddate": 1605000000,
                "visible": 1
            }
        ]
        """;

        var registryJson = """
        {
            "courses": [
                {
                    "id": 42,
                    "shortname": "CS101",
                    "fullname": "Computer Science 101",
                    "startdate": 1600000000,
                    "enddate": 1605000000,
                    "visible": 1,
                    "extra_field_from_moodle": "should be ignored"
                }
            ]
        }
        """;

        Task<JsonNode?> LegacyExecution() => Task.FromResult(JsonNode.Parse(legacyJson));
        Task<(JsonNode?, string)> RegistryExecution() => Task.FromResult((JsonNode.Parse(registryJson), "Allow"));

        // Act
        var conn = new ConnectionInfo(Guid.NewGuid(), "test_alias", "http://test");
        var result = await runner.RunComparisonAsync("core_enrol_get_users_courses", conn, "unknown", "course-list", LegacyExecution, RegistryExecution);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(100.0, result.Comparison.SemanticParityPercent);
        Assert.Empty(result.Comparison.MissingItems);
        Assert.Empty(result.Comparison.FieldDifferences);
        Assert.Empty(result.Comparison.ExtraItems);
    }

    [Fact]
    public async Task ShadowComparison_ListCourses_ShouldDetectMissingAndDifferences()
    {
        // Arrange
        var profile = new CourseComparisonProfile();
        var runner = new ShadowComparisonRunner([profile]);

        var legacyJson = """
        [
            {
                "id": 42,
                "shortname": "CS101",
                "fullname": "Computer Science 101",
                "startdate": 1600000000,
                "enddate": 1605000000,
                "visible": 1
            },
            {
                "id": 43,
                "shortname": "CS102",
                "fullname": "Computer Science 102",
                "startdate": 1600000000,
                "enddate": 1605000000,
                "visible": 1
            }
        ]
        """;

        var registryJson = """
        [
            {
                "id": 42,
                "shortname": "CS101",
                "fullname": "Computer Science 101 Modified",
                "startdate": 1600000000,
                "enddate": 1605000000,
                "visible": 0
            }
        ]
        """;

        Task<JsonNode?> LegacyExecution() => Task.FromResult(JsonNode.Parse(legacyJson));
        Task<(JsonNode?, string)> RegistryExecution() => Task.FromResult((JsonNode.Parse(registryJson), "Allow"));

        // Act
        var conn = new ConnectionInfo(Guid.NewGuid(), "test_alias", "http://test");
        var result = await runner.RunComparisonAsync("core_course_get_courses_by_field", conn, "unknown", "course-list", LegacyExecution, RegistryExecution);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Comparison.SemanticParityPercent < 100.0);
        Assert.Contains(result.Comparison.MissingItems, m => m.Contains("43"));
        Assert.Contains(result.Comparison.FieldDifferences, d => d.Contains("fullname"));
        Assert.Contains(result.Comparison.FieldDifferences, d => d.Contains("visible"));
    }
}
