using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Presentation.Tools.Grading;

namespace Moodle.Application.Tests.Tools.Grading;

public sealed class MoodleGradingToolSelectionContractTests
{
    [Fact]
    public void StartPendingRun_ToolSchemaBuildsWithOptionalStringArray()
    {
        var method = typeof(MoodleGradingTools).GetMethod(
            nameof(MoodleGradingTools.IniciarFluxoCorrecaoPendentesAsync),
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The start pending grading tool method was not found.");
        var tool = McpServerTool.Create(
            method,
            new MoodleGradingTools(null!, null!, null!),
            new McpServerToolCreateOptions());

        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        var submissionIds = properties.GetProperty("submissionIds");

        Assert.Equal("array", submissionIds.GetProperty("type").GetString());
        Assert.Equal("string", submissionIds.GetProperty("items").GetProperty("type").GetString());
        var required = tool.ProtocolTool.InputSchema.TryGetProperty("required", out var requiredSchema)
            ? requiredSchema.EnumerateArray().Select(property => property.GetString()).ToArray()
            : [];
        Assert.DoesNotContain("submissionIds", required);
    }

    [Fact]
    public async Task StartPendingRun_RejectsEmptySelectionBeforeResolvingServices()
    {
        var tool = new MoodleGradingTools(null!, null!, null!);

        var result = await tool.IniciarFluxoCorrecaoPendentesAsync(
            submissionIds: []);

        Assert.True(result.IsError);
        var message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("submissionIds", message, StringComparison.Ordinal);
    }

}
