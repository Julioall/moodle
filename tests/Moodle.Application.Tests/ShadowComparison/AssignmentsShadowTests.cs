using System.Text.Json.Nodes;
using Moodle.Application.Benchmarking;
using Moodle.Domain.Registry;

namespace Moodle.Application.Tests.ShadowComparison;

public sealed class AssignmentsShadowTests
{
    [Fact]
    public async Task ShadowComparison_GetSubmissions_ShouldAchieve100PercentParity()
    {
        // Arrange
        var profile = new AssignmentComparisonProfile();
        var runner = new ShadowComparisonRunner([profile]);

        var legacyJson = """
        [
            {
                "userid": 100,
                "status": "submitted",
                "timecreated": 1600000000,
                "timemodified": 1600000050,
                "gradingstatus": "graded",
                "attemptnumber": 0
            }
        ]
        """;

        var registryJson = """
        {
            "assignments": [
                {
                    "submissions": [
                        {
                            "userid": 100,
                            "status": "submitted",
                            "timecreated": 1600000000,
                            "timemodified": 1600000050,
                            "gradingstatus": "graded",
                            "attemptnumber": 0,
                            "plugins": [
                                { "type": "file", "name": "File submissions" }
                            ]
                        }
                    ]
                }
            ]
        }
        """;

        Task<JsonNode?> LegacyExecution() => Task.FromResult(JsonNode.Parse(legacyJson));
        Task<(JsonNode?, string)> RegistryExecution() => Task.FromResult((JsonNode.Parse(registryJson), "Allow"));

        // Act
        var conn = new ConnectionInfo(Guid.NewGuid(), "test_alias", "http://test");
        var result = await runner.RunComparisonAsync("mod_assign_get_submissions", conn, "unknown", "assignment-submissions", LegacyExecution, RegistryExecution);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(100.0, result.Comparison.SemanticParityPercent);
        Assert.Empty(result.Comparison.MissingItems);
        Assert.Empty(result.Comparison.FieldDifferences);
        Assert.Empty(result.Comparison.ExtraItems);
    }
}
