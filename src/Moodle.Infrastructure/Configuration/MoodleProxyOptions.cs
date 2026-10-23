namespace Moodle.Infrastructure;

public sealed class MoodleProxyOptions
{
    public const string SectionName = "MoodleProxy";

    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public bool UseStubData { get; init; } = false;

    public int HttpTimeoutSeconds { get; init; } = 30;

    public int HttpRetryCount { get; init; } = 4;

    public int CircuitBreakerHandledEventsAllowedBeforeBreaking { get; init; } = 5;

    public int CircuitBreakerDurationSeconds { get; init; } = 30;
}
