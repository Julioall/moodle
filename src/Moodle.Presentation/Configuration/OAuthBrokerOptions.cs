namespace Moodle.Presentation.Configuration;

public sealed class OAuthBrokerOptions
{
    public const string SectionName = "OAuth";

    public string Issuer { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public string ChatGptClientId { get; init; } = "moodle";

    public string ChatGptRedirectUri { get; init; } = string.Empty;

    public string ScopeName { get; init; } = "moodle-mcp-audience";

    public bool RequireHttpsMetadata { get; init; } = true;

    public int AccessTokenMinutes { get; init; } = 60;

    public int RefreshTokenDays { get; init; } = 30;

    public string KeyStoragePath { get; init; } = "App_Data/oauth";

    public int CertificateYears { get; init; } = 5;
}
