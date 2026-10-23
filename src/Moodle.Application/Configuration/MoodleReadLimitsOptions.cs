namespace Moodle.Application.Configuration;

/// <summary>
/// Optional admission limits for idempotent MCP reads outside the grading
/// tool family. The gate remains disabled until a measured canary enables it.
/// </summary>
public sealed class MoodleReadLimitsOptions
{
    public const string SectionName = "MoodleReadLimits";

    public bool Enabled { get; set; }
    /// <summary>
    /// Enables one bounded Retry-After retry for HTTP 429 responses from
    /// idempotent MCP reads. Keep disabled until a controlled read canary.
    /// </summary>
    public bool Retry429Enabled { get; set; }
    public int MaxConcurrentPerConnection { get; set; } = 2;
    public int MaxConcurrentForumPostReads { get; set; } = 1;
    public int QueueLimitPerConnection { get; set; } = 20;
    public int QueueWaitTimeoutSeconds { get; set; } = 10;
    public int MaxTrackedConnections { get; set; } = 512;
}
