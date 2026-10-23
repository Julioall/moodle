namespace Moodle.Infrastructure;

public sealed class MoodleApiOptions
{
    public const string SectionName = "MoodleApi";

    public string BaseUrl { get; init; } = string.Empty;

    public string ServiceToken { get; init; } = string.Empty;


    public string LoginService { get; init; } = "moodle_mobile_app";

    public bool AllowServiceTokenForReadOnlyQueries { get; init; } = false;

    // Hosts corporativos explicitamente aprovados que podem resolver para IPs
    // privados. A exceção é aplicada somente ao nome exato do host; HTTPS e
    // todas as demais validações de URL continuam obrigatórios.
    public string[] TrustedPrivateEndpointHosts { get; init; } = [];

    // Hosts that require a fresh TCP connection for every state-changing
    // request. Empty by default so existing installations keep their current
    // transport behavior until a host is explicitly enabled.
    public string[] FreshWriteConnectionHosts { get; init; } = [];

    public bool UseStubData { get; init; } = false;

    public int HttpTimeoutSeconds { get; init; } = 30;

    public int HttpRetryCount { get; init; } = 4;

    public int CircuitBreakerHandledEventsAllowedBeforeBreaking { get; init; } = 5;

    public int CircuitBreakerDurationSeconds { get; init; } = 30;
}
