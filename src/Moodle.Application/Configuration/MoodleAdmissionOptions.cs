namespace Moodle.Application.Configuration;

public enum MoodleAdmissionMode
{
    Legacy,
    Observe,
    Enforce
}

/// <summary>
/// Required, explicit limits for the shared Moodle egress coordinator. Values
/// remain unset where the SPEC has no measured installation/process budget;
/// enforcement must not invent a limit from the legacy REST socket count.
/// </summary>
public sealed class MoodleAdmissionOptions
{
    public const string SectionName = "MoodleAdmission";

    public MoodleAdmissionMode Mode { get; set; } = MoodleAdmissionMode.Legacy;

    public int MaxConcurrentGlobal { get; set; }
    public int MaxConcurrentPerInstallation { get; set; }
    public int MaxConcurrentPerConnection { get; set; } = 2;

    public int QueueLimitGlobal { get; set; }
    public int QueueLimitPerInstallation { get; set; }
    public int QueueLimitPerConnection { get; set; } = 20;
    public int QueueWaitTimeoutSeconds { get; set; } = 10;
    public int MaximumTrackedConnections { get; set; } = 512;
    public int MaximumTrackedInstallations { get; set; } = 128;
    public int IdlePartitionExpirationMinutes { get; set; } = 30;

    public int? PermitsPerMinuteGlobal { get; set; }
    public int? GlobalBurst { get; set; }
    public int? PermitsPerMinutePerInstallation { get; set; }
    public int? InstallationBurst { get; set; }
    public int? PermitsPerMinutePerConnection { get; set; }
    public int? ConnectionBurst { get; set; }

    public int InteractiveGrantWeight { get; set; } = 3;
    public int BackgroundGrantWeight { get; set; } = 1;

    public bool HasValidCoordinatorProfile =>
        MaxConcurrentGlobal is >= 1 and <= 10_000 &&
        MaxConcurrentPerInstallation is >= 1 and <= 1_000 &&
        MaxConcurrentPerConnection is >= 1 and <= 256 &&
        QueueLimitGlobal is >= 1 and <= 100_000 &&
        QueueLimitPerInstallation is >= 1 and <= 10_000 &&
        QueueLimitPerConnection is >= 1 and <= 1_000 &&
        QueueWaitTimeoutSeconds is >= 1 and <= 300 &&
        MaximumTrackedConnections is >= 1 and <= 100_000 &&
        MaximumTrackedInstallations is >= 1 and <= 10_000 &&
        IdlePartitionExpirationMinutes is >= 1 and <= 1_440 &&
        InteractiveGrantWeight is >= 1 and <= 100 &&
        BackgroundGrantWeight is >= 1 and <= 100 &&
        IsValidRate(PermitsPerMinuteGlobal, GlobalBurst) &&
        IsValidRate(PermitsPerMinutePerInstallation, InstallationBurst) &&
        IsValidRate(PermitsPerMinutePerConnection, ConnectionBurst);

    private static bool IsValidRate(int? permitsPerMinute, int? burst) =>
        permitsPerMinute is null && burst is null ||
        permitsPerMinute is >= 1 and <= 1_000_000 && burst is >= 1 and <= 100_000;
}
