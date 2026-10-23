using System.Collections.Generic;
using System.Linq;
using Moodle.Presentation.Configuration;

namespace Moodle.Benchmarks.Cognitive;

public sealed record SkillManifestEntry(
    string Name,
    string Version,
    string Hash);

/// <summary>
/// Aggregated metrics for a single benchmark profile run.
/// </summary>
public sealed record ProfileReport(
    ToolExposureProfile Profile,
    string ModelName,
    int TotalTasks,
    int SucceededTasks,
    int CriticalTasks,
    int CriticalSucceeded,

    // Accuracy metrics
    double IntentAccuracyRate,
    double RoutingAccuracyRate,
    double ConnectionAccuracyRate,
    double ParameterAccuracyRate,
    double ResultAccuracyRate,
    double PaginationAwarenessRate,

    // Token / cost metrics
    double AvgToolSchemaTokens,
    double AvgInputTokens,
    double AvgOutputTokens,
    double AvgToolCalls,
    double AvgMoodleWsCalls,

    // Latency
    long LatencyP50Ms,
    long LatencyP95Ms,
    long AvgLatencyMs,

    // Safety metrics
    int WrongConnectionExecutions,
    int UnsafeActions,
    double HallucinationRate,

    // Raw traces for gate evaluation
    IReadOnlyList<CognitiveTrace> Traces
)
{
    public double TaskSuccessRate => TotalTasks == 0 ? 0 : (double)SucceededTasks / TotalTasks * 100.0;
    public double CriticalTaskSuccessRate => CriticalTasks == 0 ? 100.0 : (double)CriticalSucceeded / CriticalTasks * 100.0;
    public double AvgModelCalls => Traces.Count == 0 ? 0 : Traces.Average(trace => trace.Execution.ModelCalls);
    public double AvgMcpToolCalls => Traces.Count == 0 ? 0 : Traces.Average(trace => trace.Execution.McpToolCalls);
    public double AvgCachedInputTokens => Traces.Count == 0 ? 0 : Traces.Average(trace => trace.Execution.CachedInputTokens);
    public double AvgUncachedInputTokens => Traces.Count == 0 ? 0 : Traces.Average(trace => trace.Execution.UncachedInputTokens);
    public double AvgReasoningTokens => Traces.Count == 0 ? 0 : Traces.Average(trace => trace.Execution.ReasoningTokens);
    public int WrongConnectionSelections => Traces.Count(trace => trace.Scoring.WrongConnectionSelectionDetected);
}

/// <summary>
/// Result of applying gates against the baseline profile (A).
/// </summary>
public sealed record GateResult(
    string GateName,
    string Description,
    bool Passed,
    string BaselineValue,
    string ProfileValue,
    string Threshold
);

/// <summary>
/// Full benchmark report comparing all three profiles.
/// </summary>
public sealed record BenchmarkReport(
    string RunId,
    string BenchmarkVersion,
    string CommitSha,
    string Model,
    ProfileReport ProfileA,
    ProfileReport ProfileB,
    ProfileReport ProfileC,
    IReadOnlyList<GateResult> GatesForProfileB,
    IReadOnlyList<GateResult> GatesForProfileC,
    bool ProfileBApproved,
    bool ProfileCApproved,
    string TaskSetHash = "",
    string ToolManifestHash = "",
    string SkillManifestHash = "",
    string RunConfiguration = "",
    IReadOnlyList<SkillManifestEntry>? SkillManifest = null
)
{
    public bool IsValid { get; init; } = true;
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
}
