namespace Moodle.Presentation.Configuration;

public sealed class ConnectorRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public int WindowSeconds { get; init; } = 60;

    public int AppAuthPermitLimit { get; init; } = 12;

    public int AccountAuthPermitLimit { get; init; } = 12;

    public int AccountAuthMaximumTrackedPartitions { get; init; } = 10_000;

    public int AccountAuthIdleExpirationMinutes { get; init; } = 30;

    public int AdminApiPermitLimit { get; init; } = 30;

    public int McpPermitLimit { get; init; } = 120;
}
