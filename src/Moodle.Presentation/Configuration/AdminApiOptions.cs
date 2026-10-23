namespace Moodle.Presentation.Configuration;

public sealed class AdminApiOptions
{
    public const string SectionName = "AdminApi";

    public string HeaderName { get; init; } = "X-Admin-Api-Key";

    public string ApiKey { get; init; } = string.Empty;
}