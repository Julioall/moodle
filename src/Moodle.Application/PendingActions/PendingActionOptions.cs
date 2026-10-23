namespace Moodle.Application.PendingActions;

public enum PendingActionTtlClass
{
    Volatile = 0,
    Stable = 1
}

public sealed class PendingActionOptions
{
    public const string SectionName = "Moodle";

    // Mantido para compatibilidade com configuracoes legadas. Os fluxos
    // first-party devem usar ResolveExpiration com uma classe explicita.
    public int PendingActionExpirationMinutes { get; init; } = 15;

    public int VolatilePendingActionExpirationHours { get; init; } = 48;

    public int StablePendingActionExpirationHours { get; init; } = 168;

    public TimeSpan ResolveExpiration(PendingActionTtlClass ttlClass)
    {
        var hours = ttlClass switch
        {
            PendingActionTtlClass.Volatile => VolatilePendingActionExpirationHours,
            PendingActionTtlClass.Stable => StablePendingActionExpirationHours,
            _ => throw new ArgumentOutOfRangeException(nameof(ttlClass), ttlClass, "Classe de TTL de acao pendente invalida.")
        };

        return TimeSpan.FromHours(Math.Clamp(hours, 1, 24 * 30));
    }
}
