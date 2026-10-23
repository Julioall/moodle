using Moodle.Infrastructure;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Security;

internal static class ProductionSecuritySettingsValidator
{
    public static void Validate(
        string? environmentName,
        PostgresOptions? postgres,
        ConnectorSecretsOptions? secrets,
        AdminApiOptions? adminApi,
        string? mediatRLicenseKey = null)
    {
        var isDevLike = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
        if (isDevLike)
        {
            return;
        }

        var connectionString = postgres?.ConnectionString?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString) ||
            ContainsPlaceholder(connectionString, "placeholder") ||
            ContainsPlaceholder(connectionString, "replace-with") ||
            ContainsPlaceholder(connectionString, "change-me") ||
            connectionString.Contains("password=postgres", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("username=postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Segurança de Produção: Postgres:ConnectionString ausente, padrão ou placeholder.");
        }

        var encryptionKey = secrets?.EncryptionKeyBase64?.Trim();
        if (string.IsNullOrWhiteSpace(encryptionKey) ||
            ContainsPlaceholder(encryptionKey, "replace") ||
            ContainsPlaceholder(encryptionKey, "placeholder") ||
            !Is32ByteBase64(encryptionKey))
        {
            throw new InvalidOperationException("Segurança de Produção: ConnectorSecrets:EncryptionKeyBase64 deve conter Base64 válido de 32 bytes e não pode ser placeholder.");
        }

        const string sampleKey = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=";
        if (encryptionKey == sampleKey)
        {
            throw new InvalidOperationException("Segurança de Produção: ConnectorSecrets:EncryptionKeyBase64 não pode utilizar a chave AES de exemplo.");
        }

        var adminApiKey = adminApi?.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(adminApiKey) ||
            ContainsPlaceholder(adminApiKey, "replace") ||
            ContainsPlaceholder(adminApiKey, "placeholder") ||
            ContainsPlaceholder(adminApiKey, "change-me") ||
            ContainsPlaceholder(adminApiKey, "troque-este-valor"))
        {
            throw new InvalidOperationException("Segurança de Produção: AdminApi:ApiKey ausente, padrão ou placeholder.");
        }

        if (string.IsNullOrWhiteSpace(mediatRLicenseKey) ||
            ContainsPlaceholder(mediatRLicenseKey, "replace") ||
            ContainsPlaceholder(mediatRLicenseKey, "placeholder") ||
            ContainsPlaceholder(mediatRLicenseKey, "change-me"))
        {
            throw new InvalidOperationException("Segurança de Produção: MEDIATR_LICENSE_KEY ausente ou placeholder. Configure uma licença válida do MediatR.");
        }
    }

    private static bool ContainsPlaceholder(string value, string marker) =>
        value.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static bool Is32ByteBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
