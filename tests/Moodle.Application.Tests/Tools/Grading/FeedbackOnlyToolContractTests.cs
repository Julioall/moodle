using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Moodle.Application.Grading;
using Moodle.Presentation.Tools.Grading;
using Xunit;

namespace Moodle.Application.Tests.Tools.Grading;

public sealed class FeedbackOnlyToolContractTests
{
    [Fact]
    public void SaveAiGradingBatch_ExplicitNullGradesAreAllowedByInputSchema()
    {
        var schema = CreateTool(nameof(MoodleGradingTools.SalvarCorrecoesIaLoteAsync))
            .ProtocolTool.InputSchema;
        var itemSchema = ResolveArrayItemSchema(schema, "items");

        AssertAllowsNull(itemSchema["properties"]?["nota"], schema, "items[].nota");
        AssertAllowsNull(
            itemSchema["properties"]?["proposal"]?["properties"]?["suggestedGrade"],
            schema,
            "items[].proposal.suggestedGrade");
    }

    [Fact]
    public void PrepareBatchAndExport_FeedbackOnlyGradesAreNullableInOutputSchemas()
    {
        var batchSchema = RequireOutputSchema(CreateTool(nameof(MoodleGradingTools.PrepararLoteCorrecaoIaAsync)));
        var batchItem = batchSchema["properties"]?["data"]?["properties"]?["items"]?["items"];
        AssertAllowsNull(batchItem?["properties"]?["maxGrade"], batchSchema, "data.items[].maxGrade");

        var exportSchema = RequireOutputSchema(CreateTool(nameof(MoodleGradingTools.ExportarCorrecoesCsvAsync)));
        Assert.NotNull(exportSchema["properties"]?["data"]?["properties"]?["fileName"]);
    }

    [Fact]
    public void ExportCsv_EscapesFieldsAndUsesBrazilianDecimalFormat()
    {
        var csv = MoodleGradingTools.BuildCorrectionsCsv(
        [
            new GradingCorrectionsCsvRow(
                "Ana; Silva",
                7.5m,
                $"Bom \"trabalho\"{Environment.NewLine}Continue assim",
                "gerado"),
            new GradingCorrectionsCsvRow("Bruno", null, null, "pendente")
        ]);

        Assert.StartsWith("nome;nota;feedback", csv, StringComparison.Ordinal);
        Assert.Contains($"\"Ana; Silva\";7,5;\"Bom \"\"trabalho\"\"{Environment.NewLine}Continue assim\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Bruno\";;\"\"", csv, StringComparison.Ordinal);
    }

    private static McpServerTool CreateTool(string methodName)
    {
        var method = typeof(MoodleGradingTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method {methodName} was not found.");
        var target = new MoodleGradingTools(null!, null!, null!);
        return McpServerTool.Create(
            method,
            target,
            new McpServerToolCreateOptions { SerializerOptions = JsonSerializerOptions.Default });
    }

    private static JsonObject ResolveArrayItemSchema(JsonElement schema, string propertyName)
    {
        var root = JsonNode.Parse(schema.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("The tool input schema is empty.");
        return root["properties"]?[propertyName]?["items"]?.AsObject()
            ?? throw new InvalidOperationException($"Schema for {propertyName}[] was not found: {root.ToJsonString()}");
    }

    private static JsonObject RequireOutputSchema(McpServerTool tool)
    {
        var schema = tool.ProtocolTool.OutputSchema
            ?? throw new InvalidOperationException($"Tool {tool.ProtocolTool.Name} does not expose an output schema.");
        return JsonNode.Parse(schema.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException($"Tool {tool.ProtocolTool.Name} has an empty output schema.");
    }

    private static void AssertAllowsNull(JsonNode? propertySchema, JsonElement fullSchema, string path) =>
        AssertAllowsNull(propertySchema, fullSchema.GetRawText(), path);

    private static void AssertAllowsNull(JsonNode? propertySchema, JsonObject fullSchema, string path) =>
        AssertAllowsNull(propertySchema, fullSchema.ToJsonString(), path);

    private static void AssertAllowsNull(JsonNode? propertySchema, string fullSchema, string path)
    {
        Assert.NotNull(propertySchema);
        var acceptsNull = propertySchema!["type"] is JsonArray types &&
                types.Any(type => string.Equals(type?.GetValue<string>(), "null", StringComparison.Ordinal)) ||
            propertySchema["anyOf"] is JsonArray anyOf &&
                anyOf.Any(option => string.Equals(option?["type"]?.GetValue<string>(), "null", StringComparison.Ordinal));

        Assert.True(
            acceptsNull,
            $"The schema for {path} must accept an explicit JSON null. Full schema: {fullSchema}");
    }
}
