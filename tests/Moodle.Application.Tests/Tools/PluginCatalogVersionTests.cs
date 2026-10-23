using System.Text.Json;

namespace Moodle.Application.Tests.Tools;

public sealed class PluginCatalogVersionTests
{
    [Fact]
    public void Portable_plugin_release_is_newer_than_the_cached_catalog_and_configures_mcp()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var portable = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "plugins", "moodle-connector", "plugin.json")));
        using var mcp = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "plugins", "moodle-connector", "mcp.json")));
        using var submission = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "chatgpt-app-submission.json")));

        var portableVersion = portable.RootElement.GetProperty("version").GetString();
        var tools = submission.RootElement.GetProperty("tools");
        var mcpServer = mcp.RootElement.GetProperty("mcpServers").GetProperty("moodle");

        Assert.Contains("prepare_grading_packages", tools.EnumerateObject().Select(property => property.Name));
        Assert.Contains("get_grading_package_status", tools.EnumerateObject().Select(property => property.Name));
        Assert.Contains("import_grading_package_results", tools.EnumerateObject().Select(property => property.Name));
        Assert.Equal("streamable-http", mcpServer.GetProperty("type").GetString());
        Assert.Equal("https://novascript.com.br/mcp", mcpServer.GetProperty("url").GetString());
        Assert.False(portable.RootElement.GetProperty("extensions")
            .GetProperty("com.openai")
            .TryGetProperty("apps", out _));
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "plugins", "moodle-connector", ".app.json")));
        Assert.True(
            Version.Parse(portableVersion!).CompareTo(new Version(1, 0, 0)) > 0,
            "The portable plugin package version must be newer than the released 1.0.0 package.");
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

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
