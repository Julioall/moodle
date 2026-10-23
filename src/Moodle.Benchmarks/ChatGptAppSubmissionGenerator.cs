using System.Text.Json;
using System.Text.Json.Nodes;
using Moodle.Presentation.Configuration;

namespace Moodle.Benchmarks;

internal static class ChatGptAppSubmissionGenerator
{
    public static async Task<int> RunAsync(string[] args)
    {
        var checkOnly = args.Contains("--check", StringComparer.OrdinalIgnoreCase);
        var repositoryRoot = FindRepositoryRoot();
        var submissionPath = Path.Combine(repositoryRoot, "chatgpt-app-submission.json");
        var submission = JsonNode.Parse(await File.ReadAllTextAsync(submissionPath))?.AsObject()
            ?? throw new InvalidOperationException("chatgpt-app-submission.json deve conter um objeto JSON.");
        var generatedTools = ChatGptSubmissionToolCatalog.CreateProductionTools();

        if (JsonNode.DeepEquals(submission["tools"], generatedTools))
        {
            Console.WriteLine("Submission tool catalog is reproducible from the production MCP contracts.");
            return 0;
        }

        if (checkOnly)
        {
            Console.Error.WriteLine("chatgpt-app-submission.json tools differ from the production MCP contracts. Run scripts/generate-chatgpt-app-submission.ps1.");
            return 1;
        }

        submission["tools"] = generatedTools;
        await File.WriteAllTextAsync(
            submissionPath,
            submission.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine("Updated chatgpt-app-submission.json tools from the production MCP contracts.");
        return 0;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Moodle.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Não foi possível localizar a raiz do repositório Moodle.");
    }
}
