using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Moodle.Application.Configuration;
using Moodle.Presentation.Configuration;

namespace Moodle.Application.Tests.Tools;

public sealed class ChatGptAppSubmissionTests
{
    [Fact]
    public void Submission_tracks_the_production_tool_surface_and_annotations()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindSubmissionPath()));
        var submissionTools = document.RootElement.GetProperty("tools");
        var metadataRegistry = new ToolMetadataRegistry(RegisteredMcpToolContainers.All);
        var exposurePolicy = new CognitiveExposurePolicy(ToolExposureProfile.Production);
        var attributes = RegisteredMcpToolContainers.AlwaysOn
            .Concat(RegisteredMcpToolContainers.GetEnabledContainers(
                new FeatureOptions
                {
                    MessagesWriteEnabled = true,
                    UniversalMoodleWriteEnabled = true,
                    UniversalMoodleFileDownloadEnabled = true
                },
                new AssignmentWriteFeatureOptions { AssignmentGradeWriteEnabled = true }))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(item => item.Attribute is not null)
            .Select(item => (Name: item.Attribute!.Name ?? item.Method.Name, Attribute: item.Attribute!))
            .Where(item => metadataRegistry.TryGet(item.Name, out var metadata) &&
                           exposurePolicy.ShouldExpose(item.Name, metadata))
            .ToDictionary(item => item.Name, StringComparer.Ordinal);

        Assert.Equal(attributes.Keys.OrderBy(name => name), submissionTools.EnumerateObject().Select(property => property.Name).OrderBy(name => name));

        foreach (var (name, tool) in attributes)
        {
            var attribute = tool.Attribute;
            Assert.NotNull(attribute.OutputSchemaType);
            var annotations = submissionTools.GetProperty(name).GetProperty("annotations");
            Assert.Equal(attribute.ReadOnly, annotations.GetProperty("readOnlyHint").GetBoolean());
            Assert.Equal(attribute.OpenWorld, annotations.GetProperty("openWorldHint").GetBoolean());
            Assert.Equal(attribute.Destructive, annotations.GetProperty("destructiveHint").GetBoolean());
        }
    }

    [Fact]
    public void Submission_provides_the_required_review_cases_for_registered_tools()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindSubmissionPath()));
        var toolNames = document.RootElement.GetProperty("tools").EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var positiveCases = document.RootElement.GetProperty("test_cases").EnumerateArray().ToArray();
        var negativeCases = document.RootElement.GetProperty("negative_test_cases").EnumerateArray().ToArray();

        Assert.Equal(5, positiveCases.Length);
        Assert.Equal(3, negativeCases.Length);
        Assert.All(positiveCases, testCase =>
        {
            var toolName = testCase.GetProperty("tools_triggered").GetString();
            Assert.False(string.IsNullOrWhiteSpace(toolName));
            Assert.Contains(toolName!, toolNames);
        });
        Assert.All(negativeCases, testCase => Assert.Equal(JsonValueKind.Null, testCase.GetProperty("tools_triggered").ValueKind));
    }

    private static string FindSubmissionPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "chatgpt-app-submission.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("chatgpt-app-submission.json nao foi encontrado.");
    }
}
