using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Moodle.Benchmarks.Cognitive;

/// <summary>
/// Renders a human-readable Markdown report comparing Profile A × B × C.
/// </summary>
public static class BenchmarkReportRenderer
{
    public static string RenderMarkdown(BenchmarkReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# MoodleBench — Courses A × B × C");
        sb.AppendLine();
        sb.AppendLine($"> **Run ID**: `{report.RunId}`  ");
        sb.AppendLine($"> **Model**: `{report.Model}`  ");
        sb.AppendLine($"> **Benchmark Version**: `{report.BenchmarkVersion}`  ");
        if (!string.IsNullOrWhiteSpace(report.CommitSha))
            sb.AppendLine($"> **Commit**: `{report.CommitSha[..Math.Min(7, report.CommitSha.Length)]}`  ");
        sb.AppendLine($"> **Generated**: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        sb.AppendLine("## Reproducibility");
        sb.AppendLine();
        sb.AppendLine($"- TaskSetHash: `{report.TaskSetHash}`");
        sb.AppendLine($"- ToolManifestHash: `{report.ToolManifestHash}`");
        sb.AppendLine($"- SkillManifestHash: `{report.SkillManifestHash}`");
        sb.AppendLine($"- Skill manifest: {string.Join(", ", report.SkillManifest?.Select(skill => $"`{skill.Name}` ({skill.Version}, `{skill.Hash}`)") ?? [])}");
        sb.AppendLine();

        // ----------------------------------------------------------------
        // Profile labels
        // ----------------------------------------------------------------
        sb.AppendLine("## Profiles");
        sb.AppendLine();
        sb.AppendLine("| Profile | Exposure | Description |");
        sb.AppendLine("|---------|----------|-------------|");
        sb.AppendLine("| **A** | Full (111 tools) | Baseline - todas as tools registradas |");
        sb.AppendLine("| **B** | Full + courses SKILL | Baseline + SKILL hint, wrappers ainda visíveis |");
        sb.AppendLine("| **C** | SKILL focused | Wrappers R1/R2 de Courses escondidas |");
        sb.AppendLine();

        // ----------------------------------------------------------------
        // Accuracy metrics table
        // ----------------------------------------------------------------
        sb.AppendLine("## Accuracy Metrics");
        sb.AppendLine();
        sb.AppendLine("| Métrica | Profile A | Profile B | Profile C |");
        sb.AppendLine("|---------|-----------|-----------|-----------|");
        AppendAccuracyRow(sb, "TaskSuccess", report.ProfileA.TaskSuccessRate, report.ProfileB.TaskSuccessRate, report.ProfileC.TaskSuccessRate, "%");
        AppendAccuracyRow(sb, "CriticalTaskSuccess", report.ProfileA.CriticalTaskSuccessRate, report.ProfileB.CriticalTaskSuccessRate, report.ProfileC.CriticalTaskSuccessRate, "%");
        AppendAccuracyRow(sb, "IntentAccuracy", report.ProfileA.IntentAccuracyRate, report.ProfileB.IntentAccuracyRate, report.ProfileC.IntentAccuracyRate, "%");
        AppendAccuracyRow(sb, "RoutingAccuracy", report.ProfileA.RoutingAccuracyRate, report.ProfileB.RoutingAccuracyRate, report.ProfileC.RoutingAccuracyRate, "%");
        AppendAccuracyRow(sb, "ConnectionAccuracy", report.ProfileA.ConnectionAccuracyRate, report.ProfileB.ConnectionAccuracyRate, report.ProfileC.ConnectionAccuracyRate, "%");
        AppendAccuracyRow(sb, "ParameterAccuracy", report.ProfileA.ParameterAccuracyRate, report.ProfileB.ParameterAccuracyRate, report.ProfileC.ParameterAccuracyRate, "%");
        AppendAccuracyRow(sb, "ResultAccuracy", report.ProfileA.ResultAccuracyRate, report.ProfileB.ResultAccuracyRate, report.ProfileC.ResultAccuracyRate, "%");
        AppendAccuracyRow(sb, "PaginationAwareness", report.ProfileA.PaginationAwarenessRate, report.ProfileB.PaginationAwarenessRate, report.ProfileC.PaginationAwarenessRate, "%");
        sb.AppendLine();

        // ----------------------------------------------------------------
        // Token / cost / latency table
        // ----------------------------------------------------------------
        sb.AppendLine("## Efficiency Metrics");
        sb.AppendLine();
        sb.AppendLine("| Métrica | Profile A | Profile B | Profile C | Δ B vs A | Δ C vs B |");
        sb.AppendLine("|---------|-----------|-----------|-----------|----------|----------|");
        AppendEfficiencyRow(sb, "ToolSchemaTokens (avg)", report.ProfileA.AvgToolSchemaTokens, report.ProfileB.AvgToolSchemaTokens, report.ProfileC.AvgToolSchemaTokens);
        AppendEfficiencyRow(sb, "InputTokens (avg)", report.ProfileA.AvgInputTokens, report.ProfileB.AvgInputTokens, report.ProfileC.AvgInputTokens);
        AppendEfficiencyRow(sb, "OutputTokens (avg)", report.ProfileA.AvgOutputTokens, report.ProfileB.AvgOutputTokens, report.ProfileC.AvgOutputTokens);
        AppendEfficiencyRow(sb, "ToolCalls (avg)", report.ProfileA.AvgToolCalls, report.ProfileB.AvgToolCalls, report.ProfileC.AvgToolCalls);
        AppendEfficiencyRow(sb, "ModelCalls (avg)", report.ProfileA.AvgModelCalls, report.ProfileB.AvgModelCalls, report.ProfileC.AvgModelCalls);
        AppendEfficiencyRow(sb, "McpToolCalls (avg)", report.ProfileA.AvgMcpToolCalls, report.ProfileB.AvgMcpToolCalls, report.ProfileC.AvgMcpToolCalls);
        AppendEfficiencyRow(sb, "CachedInputTokens (avg)", report.ProfileA.AvgCachedInputTokens, report.ProfileB.AvgCachedInputTokens, report.ProfileC.AvgCachedInputTokens);
        AppendEfficiencyRow(sb, "UncachedInputTokens (avg)", report.ProfileA.AvgUncachedInputTokens, report.ProfileB.AvgUncachedInputTokens, report.ProfileC.AvgUncachedInputTokens);
        AppendEfficiencyRow(sb, "ReasoningTokens (avg)", report.ProfileA.AvgReasoningTokens, report.ProfileB.AvgReasoningTokens, report.ProfileC.AvgReasoningTokens);
        AppendEfficiencyRow(sb, "MoodleWsCalls (avg)", report.ProfileA.AvgMoodleWsCalls, report.ProfileB.AvgMoodleWsCalls, report.ProfileC.AvgMoodleWsCalls);
        AppendEfficiencyRow(sb, "Latency p50 (ms)", report.ProfileA.LatencyP50Ms, report.ProfileB.LatencyP50Ms, report.ProfileC.LatencyP50Ms);
        AppendEfficiencyRow(sb, "Latency p95 (ms)", report.ProfileA.LatencyP95Ms, report.ProfileB.LatencyP95Ms, report.ProfileC.LatencyP95Ms);
        AppendEfficiencyRow(sb, "Latency avg (ms)", report.ProfileA.AvgLatencyMs, report.ProfileB.AvgLatencyMs, report.ProfileC.AvgLatencyMs);
        sb.AppendLine();

        // ----------------------------------------------------------------
        // Safety metrics
        // ----------------------------------------------------------------
        sb.AppendLine("## Safety Metrics");
        sb.AppendLine();
        sb.AppendLine("| Métrica | Profile A | Profile B | Profile C |");
        sb.AppendLine("|---------|-----------|-----------|-----------|");
        sb.AppendLine($"| WrongConnectionSelection | {report.ProfileA.WrongConnectionSelections} | {report.ProfileB.WrongConnectionSelections} | {report.ProfileC.WrongConnectionSelections} |");
        sb.AppendLine($"| WrongConnectionExecution | {report.ProfileA.WrongConnectionExecutions} | {report.ProfileB.WrongConnectionExecutions} | {report.ProfileC.WrongConnectionExecutions} |");
        sb.AppendLine($"| UnsafeActions | {report.ProfileA.UnsafeActions} | {report.ProfileB.UnsafeActions} | {report.ProfileC.UnsafeActions} |");
        sb.AppendLine($"| HallucinationRate | {report.ProfileA.HallucinationRate:F1}% | {report.ProfileB.HallucinationRate:F1}% | {report.ProfileC.HallucinationRate:F1}% |");
        sb.AppendLine();

        // ----------------------------------------------------------------
        // Gates - Profile B
        // ----------------------------------------------------------------
        sb.AppendLine("## Gate Evaluation - Profile B vs Baseline A");
        sb.AppendLine();
        RenderGates(sb, report.GatesForProfileB);
        sb.AppendLine();
        sb.AppendLine($"### Veredicto Profile B: **{(report.ProfileBApproved ? "? APPROVED" : "? REJECTED")}**");
        sb.AppendLine();

        // ----------------------------------------------------------------
        // Gates - Profile C
        // ----------------------------------------------------------------
        sb.AppendLine("## Gate Evaluation - Profile C vs Baseline B");
        sb.AppendLine();
        RenderGates(sb, report.GatesForProfileC);
        sb.AppendLine();
        sb.AppendLine($"### Veredicto Profile C: **{(report.ProfileCApproved ? "✅ APPROVED — Wrappers de Courses podem ser removidas" : "❌ REJECTED — Wrappers de Courses NÃO podem ser removidas ainda")}**");
        sb.AppendLine();

        // ----------------------------------------------------------------
        // Failed tasks detail
        // ----------------------------------------------------------------
        AppendFailedTasksDetail(sb, "A", report.ProfileA.Traces);
        AppendFailedTasksDetail(sb, "B", report.ProfileB.Traces);
        AppendFailedTasksDetail(sb, "C", report.ProfileC.Traces);

        // ----------------------------------------------------------------
        // Conclusion
        // ----------------------------------------------------------------
        sb.AppendLine("## Conclusão");
        sb.AppendLine();
        if (report.ProfileCApproved)
        {
            sb.AppendLine("Profile C passou todos os gates. A hipótese central foi confirmada:");
            sb.AppendLine();
            sb.AppendLine("> **É seguro esconder as wrappers R1/R2 de Courses e depender de SKILL + Registry + SafeReadExecutor.**");
            sb.AppendLine();
            sb.AppendLine("**Próximos passos:**");
            sb.AppendLine("1. Completar Live Shadow de Courses (operações restantes)");
            sb.AppendLine("2. Marcar wrappers aprovadas como `Deprecated`");
            sb.AppendLine("3. Repetir processo para domínio Assignments");
        }
        else
        {
            sb.AppendLine("Profile C **não** passou todos os gates. As wrappers de Courses ainda são necessárias.");
            sb.AppendLine();
            sb.AppendLine("**Próximos passos:**");
            sb.AppendLine("1. Analisar tasks que falharam no Profile C vs B");
            sb.AppendLine("2. Identificar se o problema é SKILL, normalização ou paginação");
            sb.AppendLine("3. Corrigir e re-executar MoodleBench");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void AppendAccuracyRow(StringBuilder sb, string metric, double a, double b, double c, string unit)
    {
        sb.AppendLine($"| {metric} | {a:F1}{unit} | {b:F1}{unit} | {c:F1}{unit} |");
    }

    private static void AppendEfficiencyRow(StringBuilder sb, string metric, double a, double b, double c)
    {
        var deltaB = a == 0 ? 0 : (b - a) / a * 100.0;
        var deltaC = b == 0 ? 0 : (c - b) / b * 100.0;
        var deltaBText = deltaB >= 0 ? $"+{deltaB:F1}%" : $"{deltaB:F1}%";
        var deltaCText = deltaC >= 0 ? $"+{deltaC:F1}%" : $"{deltaC:F1}%";
        sb.AppendLine($"| {metric} | {a:F1} | {b:F1} | {c:F1} | {deltaBText} | {deltaCText} |");
    }

    private static void RenderGates(StringBuilder sb, IReadOnlyList<GateResult> gates)
    {
        sb.AppendLine("| Gate | Threshold | Explicit Baseline | Candidate | Result |");
        sb.AppendLine("|------|-----------|------------|-----------|--------|");
        foreach (var gate in gates)
        {
            var icon = gate.Passed ? "?" : "?";
            sb.AppendLine($"| {gate.Description} | {gate.Threshold} | {gate.BaselineValue} | {gate.ProfileValue} | {icon} |");
        }
    }

    private static void AppendFailedTasksDetail(StringBuilder sb, string profileLabel, IReadOnlyList<CognitiveTrace> traces)
    {
        var failed = traces.Where(t => !t.Scoring.OverallSuccess).ToList();
        if (failed.Count == 0) return;

        sb.AppendLine($"## Falhas - Profile {profileLabel}");
        sb.AppendLine();
        sb.AppendLine("| Task ID | Reason | Selection | Execution | Hallucination |");
        sb.AppendLine("|---------|--------|-----------|-----------|---------------|");
        foreach (var trace in failed)
        {
            sb.AppendLine($"| `{trace.TaskId}` | {trace.Scoring.FailureReason} | {(trace.Scoring.WrongConnectionSelectionDetected ? "? Wrong" : "? OK")} | {(trace.Scoring.WrongConnectionExecutionDetected ? "? Wrong" : "? OK")} | {(trace.Scoring.HallucinationDetected ? "?? Yes" : "No")} |");
        }
        sb.AppendLine();
    }
}
