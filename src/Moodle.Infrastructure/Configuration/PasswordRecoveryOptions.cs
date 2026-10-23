namespace Moodle.Infrastructure.Configuration;

/// <summary>
/// Senha temporária usada somente quando um administrador redefine uma conta.
/// Deve ser fornecida pela configuração de ambiente, nunca pelo cliente web.
/// </summary>
public sealed class PasswordRecoveryOptions
{
    public const string SectionName = "PasswordRecovery";
    public string DefaultPassword { get; init; } = string.Empty;
}
