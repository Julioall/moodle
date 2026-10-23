namespace Moodle.Presentation.Configuration;

public sealed class McpServerSecurityOptions
{
    public const string SectionName = "McpServerSecurity";

    public bool RequireJwt { get; init; } = false;

    public bool RequireApiKey { get; init; } = true;

    public string ApiKeyHeader { get; init; } = "X-Mcp-Api-Key";
}