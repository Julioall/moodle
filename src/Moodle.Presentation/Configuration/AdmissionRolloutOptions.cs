namespace Moodle.Presentation.Configuration;

public enum AdmissionMode
{
    Legacy,
    Observe,
    Enforce
}

/// <summary>
/// Independent rollout switches for request admission and remote Moodle
/// admission. Enforce is rejected until the corresponding policy ships.
/// </summary>
public sealed class AdmissionRolloutOptions
{
    public const string SectionName = "Admission";

    public AdmissionMode IngressMode { get; init; } = AdmissionMode.Legacy;

    public AdmissionMode MoodleEgressMode { get; init; } = AdmissionMode.Legacy;

    /// <summary>
    /// Candidate profile for local catalog reads. These values are observed
    /// separately from the legacy ASP.NET rate-limit policy.
    /// </summary>
    public int LocalReadPermitLimitPerMinute { get; init; } = 120;

    public int LocalReadBurst { get; init; } = 40;

    public int MaximumTrackedPartitions { get; init; } = 10_000;

    public int IdlePartitionExpirationMinutes { get; init; } = 30;

    public bool IsSupportedInCurrentStage =>
        IsStageMode(IngressMode) && IsStageMode(MoodleEgressMode);

    public bool HasValidIngressProfile =>
        LocalReadPermitLimitPerMinute is >= 1 and <= 10_000 &&
        LocalReadBurst is >= 1 and <= 1_000 &&
        MaximumTrackedPartitions is >= 1 and <= 100_000 &&
        IdlePartitionExpirationMinutes is >= 1 and <= 1_440;

    private static bool IsStageMode(AdmissionMode mode) =>
        mode is AdmissionMode.Legacy or AdmissionMode.Observe;
}
