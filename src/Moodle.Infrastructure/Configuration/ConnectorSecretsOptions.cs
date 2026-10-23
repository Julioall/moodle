namespace Moodle.Infrastructure;

public sealed class ConnectorSecretsOptions
{
    public const string SectionName = "ConnectorSecrets";

    public string EncryptionKeyBase64 { get; init; } = string.Empty;

    public int TokenCacheMinutes { get; init; } = 20;
}