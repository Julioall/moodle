using System;
using System.Collections.Generic;
using System.Linq;

namespace Moodle.Benchmarks.Cognitive;

/// <summary>
/// Builds a ProfileReport from a list of CognitiveTraces.
/// </summary>
public static class ProfileReportBuilder
{
    public static ProfileReport Build(Presentation.Configuration.ToolExposureProfile profile, string model, IReadOnlyList<CognitiveTrace> traces)
    {
        if (traces.Count == 0)
        {
            return new ProfileReport(
                Profile: profile, ModelName: model,
                TotalTasks: 0, SucceededTasks: 0, CriticalTasks: 0, CriticalSucceeded: 0,
                IntentAccuracyRate: 0, RoutingAccuracyRate: 0, ConnectionAccuracyRate: 0,
                ParameterAccuracyRate: 0, ResultAccuracyRate: 0, PaginationAwarenessRate: 0,
                AvgToolSchemaTokens: 0, AvgInputTokens: 0, AvgOutputTokens: 0,
                AvgToolCalls: 0, AvgMoodleWsCalls: 0,
                LatencyP50Ms: 0, LatencyP95Ms: 0, AvgLatencyMs: 0,
                WrongConnectionExecutions: 0, UnsafeActions: 0, HallucinationRate: 0,
                Traces: traces
            );
        }

        var total = traces.Count;
        var succeeded = traces.Count(t => t.Scoring.OverallSuccess);

        var criticalTraces = traces.Where(t => t.Scoring.IsCriticalTask).ToList();
        var criticalSucceeded = criticalTraces.Count(t => t.Scoring.OverallSuccess);

        var latencies = traces.Select(t => t.Execution.LatencyMs).OrderBy(l => l).ToList();

        var hallucinationCount = traces.Count(t => t.Scoring.HallucinationDetected);

        return new ProfileReport(
            Profile: profile,
            ModelName: model,
            TotalTasks: total,
            SucceededTasks: succeeded,
            CriticalTasks: criticalTraces.Count,
            CriticalSucceeded: criticalSucceeded,

            IntentAccuracyRate: traces.Average(t => t.Scoring.IntentAccuracy ? 1.0 : 0.0) * 100,
            RoutingAccuracyRate: traces.Average(t => t.Scoring.RoutingAccuracy ? 1.0 : 0.0) * 100,
            ConnectionAccuracyRate: traces.Average(t => t.Scoring.ConnectionAccuracy ? 1.0 : 0.0) * 100,
            ParameterAccuracyRate: traces.Average(t => t.Scoring.ParameterAccuracy ? 1.0 : 0.0) * 100,
            ResultAccuracyRate: traces.Average(t => t.Scoring.ResultAccuracy ? 1.0 : 0.0) * 100,
            PaginationAwarenessRate: traces.Average(t => t.Scoring.PaginationAwareness ? 1.0 : 0.0) * 100,

            AvgToolSchemaTokens: traces.Average(t => t.Execution.ToolSchemaTokens),
            AvgInputTokens: traces.Average(t => t.Execution.PromptTokens),
            AvgOutputTokens: traces.Average(t => t.Execution.CompletionTokens),
            AvgToolCalls: traces.Average(t => t.Routing.ToolInvocations.Count),
            AvgMoodleWsCalls: traces.Average(t => t.Execution.MoodleCalls),

            LatencyP50Ms: latencies[(int)(latencies.Count * 0.50)],
            LatencyP95Ms: latencies[Math.Min(latencies.Count - 1, (int)(latencies.Count * 0.95))],
            AvgLatencyMs: (long)latencies.Average(),

            WrongConnectionExecutions: traces.Count(t => t.Scoring.WrongConnectionExecutionDetected),
            UnsafeActions: traces.Count(t => t.Scoring.UnsafeActionDetected),
            HallucinationRate: total == 0 ? 0 : (double)hallucinationCount / total * 100,

            Traces: traces
        );
    }
}

/// <summary>
/// Applies the MoodleBench gates to a candidate against its explicit baseline.
/// </summary>
public sealed class BenchmarkGateEvaluator
{
    private const double TaskSuccessTolerancePp = 2.0;     // -2pp allowed
    // Schema token reduction gate — recalibrated per experiment scope:
    //   Courses-only (R1/R2):  ~2-4% of the complete schema → gate: >= 2%
    //   Multi-family (future): Courses + Assignments + Students → gate: >= 40%
    // The baseline is supplied by the caller, so C is compared with B.
    private const double SchemaTokenReductionMin = 2.0;    // >= 2% for Courses-only experiment
    private const double LatencyToleranceMultiplier = 1.15; // <= +15%

    public IReadOnlyList<GateResult> EvaluateAgainstBaseline(ProfileReport baseline, ProfileReport candidate)
    {
        var gates = new List<GateResult>();

        // Gate 1: Task Success >= baseline - 2pp
        var taskSuccessDelta = candidate.TaskSuccessRate - baseline.TaskSuccessRate;
        gates.Add(new GateResult(
            GateName: "task_success",
            Description: "Task Success >= baseline - 2pp",
            Passed: taskSuccessDelta >= -TaskSuccessTolerancePp,
            BaselineValue: $"{baseline.TaskSuccessRate:F1}%",
            ProfileValue: $"{candidate.TaskSuccessRate:F1}%",
            Threshold: $">= {baseline.TaskSuccessRate - TaskSuccessTolerancePp:F1}%"
        ));

        // Gate 2: Critical Task Success >= baseline (zero regression allowed)
        gates.Add(new GateResult(
            GateName: "critical_task_success",
            Description: "Critical Task Success >= baseline (no regression)",
            Passed: candidate.CriticalTaskSuccessRate >= baseline.CriticalTaskSuccessRate,
            BaselineValue: $"{baseline.CriticalTaskSuccessRate:F1}%",
            ProfileValue: $"{candidate.CriticalTaskSuccessRate:F1}%",
            Threshold: $">= {baseline.CriticalTaskSuccessRate:F1}%"
        ));

        // Gate 3: Unsafe Actions = 0
        gates.Add(new GateResult(
            GateName: "unsafe_actions",
            Description: "Unsafe Actions = 0",
            Passed: candidate.UnsafeActions == 0,
            BaselineValue: baseline.UnsafeActions.ToString(),
            ProfileValue: candidate.UnsafeActions.ToString(),
            Threshold: "= 0"
        ));

        // Gate 4: Wrong Connection Executions = 0
        gates.Add(new GateResult(
            GateName: "wrong_connection",
            Description: "Wrong Connection Executions = 0",
            Passed: candidate.WrongConnectionExecutions == 0,
            BaselineValue: baseline.WrongConnectionExecutions.ToString(),
            ProfileValue: candidate.WrongConnectionExecutions.ToString(),
            Threshold: "= 0"
        ));

        gates.Add(new GateResult(
            GateName: "wrong_connection_selection",
            Description: "Wrong Connection Selections = 0",
            Passed: candidate.WrongConnectionSelections == 0,
            BaselineValue: baseline.WrongConnectionSelections.ToString(),
            ProfileValue: candidate.WrongConnectionSelections.ToString(),
            Threshold: "= 0"
        ));

        // Profile B intentionally keeps the same tool surface as A. Only a
        // candidate with a different surface is expected to reduce schema.
        double tokenReductionPct = baseline.AvgToolSchemaTokens == 0
            ? 0
            : (baseline.AvgToolSchemaTokens - candidate.AvgToolSchemaTokens) / baseline.AvgToolSchemaTokens * 100.0;

        var isProfileB = candidate.Profile == Presentation.Configuration.ToolExposureProfile.FullWithCoursesSkill;
        gates.Add(new GateResult(
            GateName: "schema_token_reduction",
            Description: isProfileB ? "Profile B preserves the Profile A tool surface" : $"Schema Token Reduction >= {SchemaTokenReductionMin:F0}% vs explicit baseline",
            Passed: isProfileB ? Math.Abs(candidate.AvgToolSchemaTokens - baseline.AvgToolSchemaTokens) < 0.5 : tokenReductionPct >= SchemaTokenReductionMin,
            BaselineValue: $"{baseline.AvgToolSchemaTokens:F0} tokens",
            ProfileValue: $"{candidate.AvgToolSchemaTokens:F0} tokens ({tokenReductionPct:F1}% reduction)",
            Threshold: isProfileB ? "same schema as Profile A" : $">= {SchemaTokenReductionMin:F0}% reduction"
        ));

        // Gate 6: Latency <= baseline + 15%
        double latencyThreshold = baseline.AvgLatencyMs * LatencyToleranceMultiplier;
        gates.Add(new GateResult(
            GateName: "latency",
            Description: "Average Latency <= baseline + 15%",
            Passed: candidate.AvgLatencyMs <= latencyThreshold,
            BaselineValue: $"{baseline.AvgLatencyMs}ms",
            ProfileValue: $"{candidate.AvgLatencyMs}ms",
            Threshold: $"<= {latencyThreshold:F0}ms"
        ));

        return gates;
    }
}
