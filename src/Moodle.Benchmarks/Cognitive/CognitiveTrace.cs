using System;
using System.Collections.Generic;

namespace Moodle.Benchmarks.Cognitive;

public sealed record ToolInvocationTrace(
    string ToolName,
    string ArgumentsJson,
    string ToolResult,
    long LatencyMs,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens
);

public sealed record RoutingTrace(
    string SelectedSkill,
    string SelectedIntent,
    string SelectedOperation,
    string? SelectedConnection,
    IReadOnlyDictionary<string, object> Arguments,
    IReadOnlyList<ToolInvocationTrace> ToolInvocations
);

public sealed record ExecutionTrace(
    Guid ConnectionId,
    string RegistryOperation,
    string PolicyDecision,
    int MoodleCalls,
    long LatencyMs,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int ToolSchemaTokens,
    string ToolManifestHash = "",
    string SkillManifestHash = "",
    string BenchmarkVersion = "1.1.0",
    string CommitSha = "",
    int ModelCalls = 0,
    int McpToolCalls = 0,
    int CachedInputTokens = 0,
    int UncachedInputTokens = 0,
    int ReasoningTokens = 0,
    string? ExecutedConnection = null,
    string RunId = ""
);

public sealed record ScoringTrace(
    bool IntentAccuracy,
    bool RoutingAccuracy,
    bool ConnectionAccuracy,
    bool ParameterAccuracy,
    bool ResultAccuracy,
    bool PaginationAwareness,
    bool OverallSuccess,
    FailureTaxonomy FailureReason,
    bool WrongConnectionDetected = false,
    bool HallucinationDetected = false,
    bool IsCriticalTask = false,
    bool WrongConnectionSelectionDetected = false,
    bool WrongConnectionExecutionDetected = false,
    bool UnsafeActionDetected = false
);

public sealed record CognitiveTrace(
    string TaskId,
    BenchmarkProfile Profile,
    string Prompt,
    string Model,
    RoutingTrace Routing,
    ExecutionTrace Execution,
    string ResultContent,
    ScoringTrace Scoring
);
