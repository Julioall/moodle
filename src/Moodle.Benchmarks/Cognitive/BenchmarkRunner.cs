using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Moodle.Benchmarks.Cognitive;

public sealed class BenchmarkRunner
{
    private readonly BenchmarkScorer _scorer = new();

    public async Task<CognitiveTrace> RunTaskAsync(BenchmarkProfile profile, BenchmarkTask task)
    {
        // 1. In a real environment, this invokes the Agent (e.g. Semantic Kernel or Claude Desktop API)
        // For the scaffolding, we simulate an agent's trace.
        var stopwatch = Stopwatch.StartNew();

        // Mocking an agent response:
        var selectedSkill = "moodle-courses";
        var selectedIntent = task.ExpectedIntent;
        var selectedOperation = task.AllowedOperations.Count > 0 ? task.AllowedOperations[0] : "unknown";

        var routing = new RoutingTrace(
            SelectedSkill: selectedSkill,
            SelectedIntent: selectedIntent,
            SelectedOperation: selectedOperation,
            SelectedConnection: null,
            Arguments: new System.Collections.Generic.Dictionary<string, object>(),
            ToolInvocations: Array.Empty<ToolInvocationTrace>()
        );

        // 2. Mocking execution metrics:
        var execution = new ExecutionTrace(
            ConnectionId: Guid.NewGuid(),
            RegistryOperation: selectedOperation,
            PolicyDecision: "Allowed",
            MoodleCalls: 1,
            LatencyMs: 150,
            PromptTokens: 0,
            CompletionTokens: 0,
            TotalTokens: 0,
            ToolSchemaTokens: 0
        );

        stopwatch.Stop();

        var resultContent = "{ \"items\": [], \"truncated\": false }";

        // 3. Score the trace
        var scoring = _scorer.Score(task, routing, execution, resultContent);

        return new CognitiveTrace(
            TaskId: task.Id,
            Profile: profile,
            Prompt: task.Prompt,
            Model: profile.ModelName,
            Routing: routing,
            Execution: execution,
            ResultContent: resultContent,
            Scoring: scoring
        );
    }
}
