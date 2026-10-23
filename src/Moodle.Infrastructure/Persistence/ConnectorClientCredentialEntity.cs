namespace Moodle.Infrastructure;

public sealed class ConnectorClientCredentialEntity
{
    public string Id { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string? ApiKeyHash { get; set; }

    public string MoodleAlias { get; set; } = "default";

    public string MoodleBaseUrl { get; set; } = string.Empty;

    public string MoodleUsernameEncrypted { get; set; } = string.Empty;

    public string MoodlePasswordEncrypted { get; set; } = string.Empty;

    public string MoodleTarget { get; set; } = "default";

    public bool IsDefault { get; set; }

    public bool CanWrite { get; set; }

    public bool IsActive { get; set; } = true;

    public string ValidationStatus { get; set; } = "unknown";

    public DateTimeOffset? LastValidatedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
