namespace Moodle.Infrastructure.Configuration;

public sealed class MoodleCallDiagnosticsOptions
{
    public const string SectionName = "MoodleCallDiagnostics";

    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    public int WriteTimeoutMilliseconds { get; set; } = 1000;
    public int CleanupIntervalHours { get; set; } = 24;
    public int CleanupBatchSize { get; set; } = 500;
    public int MaxCleanupBatchesPerRun { get; set; } = 20;

    public bool IsValid() =>
        RetentionDays is >= 1 and <= 3650 &&
        WriteTimeoutMilliseconds is >= 25 and <= 2000 &&
        CleanupIntervalHours is >= 1 and <= 168 &&
        CleanupBatchSize is >= 10 and <= 10000 &&
        MaxCleanupBatchesPerRun is >= 1 and <= 1000;
}
