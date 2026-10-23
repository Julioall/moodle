namespace Moodle.Domain.Registry;

public enum NormalizationMode
{
    Agent,
    Shadow
}

public sealed record NormalizationContext(
    NormalizationMode Mode = NormalizationMode.Agent,
    int MaxItems = 50,
    long MaxPayloadBytes = 500 * 1024
);
