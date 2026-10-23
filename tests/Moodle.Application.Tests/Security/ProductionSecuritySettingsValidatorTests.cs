using Moodle.Infrastructure;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;

namespace Moodle.Application.Tests.Security;

public sealed class ProductionSecuritySettingsValidatorTests
{
    [Fact]
    public void Producao_aceita_configuracao_completa_e_nao_placeholder()
    {
        var exception = Record.Exception(() => ValidateProduction(
            new PostgresOptions { ConnectionString = "Host=postgres;Port=5432;Database=moodle;Username=connector;Password=valid-password" },
            new ConnectorSecretsOptions { EncryptionKeyBase64 = Convert.ToBase64String(new byte[32]) },
            new AdminApiOptions { ApiKey = "admin-api-key-for-production" },
            "valid-mediatr-license-key"));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("Host=PLACEHOLDER;Database=PLACEHOLDER;Username=PLACEHOLDER;Password=PLACEHOLDER")]
    [InlineData("Host=postgres;Database=moodle;Username=connector;Password=change-me")]
    [InlineData("")]
    public void Producao_rejeita_connection_string_ausente_ou_placeholder(string connectionString)
    {
        Assert.Throws<InvalidOperationException>(() => ValidateProduction(
            new PostgresOptions { ConnectionString = connectionString },
            ValidSecrets(),
            ValidAdmin()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("REPLACE_WITH_32_BYTE_BASE64_KEY")]
    [InlineData("not-base64")]
    [InlineData("MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=")]
    public void Producao_rejeita_chave_de_criptografia_invalida_ou_exemplo(string encryptionKey)
    {
        Assert.Throws<InvalidOperationException>(() => ValidateProduction(
            ValidPostgres(),
            new ConnectorSecretsOptions { EncryptionKeyBase64 = encryptionKey },
            ValidAdmin()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("replace-with-admin-api-key")]
    [InlineData("change-me")]
    public void Producao_rejeita_chave_admin_ausente_ou_placeholder(string apiKey)
    {
        Assert.Throws<InvalidOperationException>(() => ValidateProduction(
            ValidPostgres(),
            ValidSecrets(),
            new AdminApiOptions { ApiKey = apiKey }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("replace-with-mediatr-license-key")]
    [InlineData("placeholder")]
    public void Producao_rejeita_licenca_mediatr_ausente_ou_placeholder(string licenseKey)
    {
        Assert.Throws<InvalidOperationException>(() => ValidateProduction(
            ValidPostgres(),
            ValidSecrets(),
            ValidAdmin(),
            licenseKey));
    }

    [Fact]
    public void Development_e_Testing_permanecem_permissivos_para_o_bootstrap_local()
    {
        var placeholders = new PostgresOptions { ConnectionString = "Host=PLACEHOLDER" };
        var emptySecrets = new ConnectorSecretsOptions();
        var emptyAdmin = new AdminApiOptions();

        Assert.Null(Record.Exception(() => ProductionSecuritySettingsValidator.Validate("Development", placeholders, emptySecrets, emptyAdmin)));
        Assert.Null(Record.Exception(() => ProductionSecuritySettingsValidator.Validate("Testing", placeholders, emptySecrets, emptyAdmin)));
    }

    private static void ValidateProduction(
        PostgresOptions postgres,
        ConnectorSecretsOptions secrets,
        AdminApiOptions admin,
        string licenseKey = "valid-mediatr-license-key") =>
        ProductionSecuritySettingsValidator.Validate("Production", postgres, secrets, admin, licenseKey);

    private static PostgresOptions ValidPostgres() =>
        new() { ConnectionString = "Host=postgres;Port=5432;Database=moodle;Username=connector;Password=valid-password" };

    private static ConnectorSecretsOptions ValidSecrets() =>
        new() { EncryptionKeyBase64 = Convert.ToBase64String(new byte[32]) };

    private static AdminApiOptions ValidAdmin() =>
        new() { ApiKey = "admin-api-key-for-production" };
}
