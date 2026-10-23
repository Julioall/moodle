using System;
using System.Collections.Generic;
using System.Linq;

namespace Moodle.Benchmarks.Cognitive;

public sealed class BenchmarkScorer
{
    /// <summary>
    /// Known tool names in the MCP manifest (fetched at benchmark start and passed in).
    /// Used to detect hallucinations — tool calls to tools that don't exist in the manifest.
    /// </summary>
    private readonly HashSet<string> _knownToolNames;

    public BenchmarkScorer(IEnumerable<string>? knownToolNames = null)
    {
        _knownToolNames = knownToolNames != null
            ? new HashSet<string>(knownToolNames, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public ScoringTrace Score(BenchmarkTask task, RoutingTrace routing, ExecutionTrace execution, string resultContent)
    {
        // ----------------------------------------------------------------
        // 1. Intent Accuracy
        // ----------------------------------------------------------------
        // The agent reports which TOOL it called, not an intent label.
        // We resolve the intent from the first tool invocation in the trace,
        // falling back to SelectedIntent (which may already be mapped).
        var observedIntent = ResolveIntentFromTrace(routing);
        var intentAccuracy = string.Equals(observedIntent, task.ExpectedIntent, StringComparison.OrdinalIgnoreCase);

        // ----------------------------------------------------------------
        // 2. Routing Accuracy
        // ----------------------------------------------------------------
        var routingAccuracy = false;
        foreach (var allowed in task.AllowedOperations)
        {
            if (IsAllowedOperation(routing.SelectedOperation, allowed))
            {
                routingAccuracy = true;
                break;
            }
        }
        if (string.Equals(routing.SelectedOperation, execution.RegistryOperation, StringComparison.OrdinalIgnoreCase) == false
            && !string.IsNullOrEmpty(execution.RegistryOperation))
        {
            // If registry actually ran something else, compare against that.
            routingAccuracy = false;
            foreach (var allowed in task.AllowedOperations)
            {
                if (IsAllowedOperation(execution.RegistryOperation, allowed))
                {
                    routingAccuracy = true;
                    break;
                }
            }
        }

        // Also check first tool invocation against allowed operations
        if (!routingAccuracy && routing.ToolInvocations.Count > 0)
        {
            var firstTool = routing.ToolInvocations[0].ToolName;
            foreach (var allowed in task.AllowedOperations)
            {
                if (IsAllowedOperation(firstTool, allowed))
                {
                    routingAccuracy = true;
                    break;
                }
            }
        }

        // Ensure no forbidden operations were called
        foreach (var forbidden in task.ForbiddenOperations)
        {
            if (string.Equals(routing.SelectedOperation, forbidden, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(execution.RegistryOperation, forbidden, StringComparison.OrdinalIgnoreCase))
            {
                routingAccuracy = false;
                break;
            }
            if (routing.ToolInvocations.Any(t => string.Equals(t.ToolName, forbidden, StringComparison.OrdinalIgnoreCase)))
            {
                routingAccuracy = false;
                break;
            }
        }

        // ----------------------------------------------------------------
        // 3. Parameter Accuracy
        // ----------------------------------------------------------------
        // v0.1: structural check — if a connection argument was present when expected.
        // Full LLM-as-Judge parameter validation is deferred to v0.2.
        var parameterAccuracy = ParametersAreStructurallyValid(task, routing);
        if (!string.IsNullOrWhiteSpace(task.ExpectedConnection))
        {
            // If we can detect the connection arg was missing despite being required, flag it.
            // For now this is approximated via the connection accuracy below.
            // True parameter inspection needs schema-aware validation.
        }

        // ----------------------------------------------------------------
        // 4. Pagination Awareness
        // ----------------------------------------------------------------
        var paginationAwareness = true;
        if (task.RequiresCompleteDataset && resultContent.Contains("\"truncated\": true", StringComparison.OrdinalIgnoreCase))
        {
            paginationAwareness = false;
        }

        // ----------------------------------------------------------------
        // 5. Result Accuracy
        // ----------------------------------------------------------------
        // v0.1: placeholder — LLM-as-Judge deferred to v0.2.
        // For safety experiments, a false result here would need ground-truth data.
        var resultAccuracy = !routing.ToolInvocations.Any(inv => IsToolError(inv.ToolResult))
                             && !IsToolError(resultContent);

        // ----------------------------------------------------------------
        // 6. Connection Accuracy
        // ----------------------------------------------------------------
        var connectionAccuracy = true;
        var wrongConnectionSelection = false;
        var wrongConnectionExecution = false;
        if (!string.IsNullOrWhiteSpace(task.ExpectedConnection))
        {
            var usedConnection = routing.SelectedConnection
                ?? InferConnectionFromInvocations(routing.ToolInvocations);

            connectionAccuracy = string.Equals(task.ExpectedConnection, usedConnection ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            wrongConnectionSelection = !connectionAccuracy && usedConnection != null;
            if (!string.IsNullOrWhiteSpace(execution.ExecutedConnection))
            {
                wrongConnectionExecution = !string.Equals(
                    task.ExpectedConnection,
                    execution.ExecutedConnection,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        var wrongConnectionDetected = wrongConnectionSelection || wrongConnectionExecution;

        var unsafeActionDetected = routing.ToolInvocations.Any(inv => IsUnsafeAction(inv.ToolName, inv.ArgumentsJson));

        // ----------------------------------------------------------------
        // 7. Hallucination Detection
        // ----------------------------------------------------------------
        // A hallucination is a call to a tool that does not exist in the MCP manifest.
        var hallucinationDetected = false;
        if (_knownToolNames.Count > 0)
        {
            hallucinationDetected = routing.ToolInvocations.Any(inv =>
                !_knownToolNames.Contains(inv.ToolName));
        }
        else
        {
            // Fallback: use IntentMapper to detect clearly unknown tool names
            hallucinationDetected = routing.ToolInvocations.Any(inv =>
                IntentMapper.Resolve(inv.ToolName) == null &&
                !IsGenericMcpTool(inv.ToolName));
        }

        // ----------------------------------------------------------------
        // 8. Overall Success
        // ----------------------------------------------------------------
        var overallSuccess = intentAccuracy && routingAccuracy && connectionAccuracy
                             && parameterAccuracy && resultAccuracy && paginationAwareness
                             && !hallucinationDetected;

        var failureReason = FailureTaxonomy.None;
        if (!overallSuccess)
        {
            if (hallucinationDetected)       failureReason = FailureTaxonomy.Hallucination;
            else if (!intentAccuracy)        failureReason = FailureTaxonomy.IntentMisclassified;
            else if (!routingAccuracy)       failureReason = FailureTaxonomy.WrongOperation;
            else if (!connectionAccuracy)    failureReason = FailureTaxonomy.WrongConnection;
            else if (!parameterAccuracy)     failureReason = FailureTaxonomy.InvalidParameters;
            else if (!paginationAwareness)   failureReason = FailureTaxonomy.PaginationIncomplete;
            else if (!resultAccuracy)        failureReason = FailureTaxonomy.MoodleError;
        }

        if (execution.PolicyDecision == "Denied")
        {
            overallSuccess = false;
            failureReason = FailureTaxonomy.PolicyBlockUnexpected;
        }

        return new ScoringTrace(
            IntentAccuracy: intentAccuracy,
            RoutingAccuracy: routingAccuracy,
            ConnectionAccuracy: connectionAccuracy,
            ParameterAccuracy: parameterAccuracy,
            ResultAccuracy: resultAccuracy,
            PaginationAwareness: paginationAwareness,
            OverallSuccess: overallSuccess,
            FailureReason: failureReason,
            WrongConnectionDetected: wrongConnectionDetected,
            HallucinationDetected: hallucinationDetected,
            IsCriticalTask: task.IsCriticalTask,
            WrongConnectionSelectionDetected: wrongConnectionSelection,
            WrongConnectionExecutionDetected: wrongConnectionExecution,
            UnsafeActionDetected: unsafeActionDetected
        );
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string ResolveIntentFromTrace(RoutingTrace routing)
    {
        var operationIntent = IntentMapper.ResolveOperation(routing.SelectedOperation);
        if (operationIntent is not null && operationIntent != "generic.read") return operationIntent;

        // Try to resolve from first tool invocation (most reliable signal)
        if (routing.ToolInvocations.Count > 0)
        {
            var resolved = IntentMapper.Resolve(routing.ToolInvocations[0].ToolName);
            if (resolved != null) return resolved;
        }

        // Fallback: try SelectedIntent as-is (may already be canonical)
        if (!string.IsNullOrWhiteSpace(routing.SelectedIntent))
        {
            var resolved = IntentMapper.Resolve(routing.SelectedIntent);
            if (resolved != null) return resolved;

            // If it already looks like a canonical intent (contains '.'), return it directly
            if (routing.SelectedIntent.Contains('.'))
                return routing.SelectedIntent;
        }

        return routing.SelectedIntent ?? "unknown";
    }

    private static bool IsAllowedOperation(string? actual, string allowed)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        return string.Equals(actual, allowed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(IntentMapper.ResolveOperation(actual), IntentMapper.ResolveOperation(allowed), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsToolError(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(result);
            return ContainsError(document.RootElement);
        }
        catch { return false; }
    }

    private static bool ParametersAreStructurallyValid(BenchmarkTask task, RoutingTrace routing)
    {
        if (routing.ToolInvocations.Count == 0) return true;

        // Every benchmark task with an expected connection must carry a
        // connection selector somewhere in the routing trace. The driver may
        // expose the selector as a top-level field or inside the tool call.
        if (!string.IsNullOrWhiteSpace(task.ExpectedConnection) &&
            string.IsNullOrWhiteSpace(routing.SelectedConnection) &&
            string.IsNullOrWhiteSpace(InferConnectionFromInvocations(routing.ToolInvocations)))
        {
            return false;
        }

        foreach (var invocation in routing.ToolInvocations)
        {
            if (string.IsNullOrWhiteSpace(invocation.ArgumentsJson)) return false;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(invocation.ArgumentsJson);
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return false;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUnsafeAction(string toolName, string argumentsJson)
    {
        if (toolName.Equals("moodle_confirm_write", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("confirm_", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("execute_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (toolName.Equals("manage_user_memory", StringComparison.OrdinalIgnoreCase) &&
            argumentsJson.Contains("\"remover\"", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsError(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("isError") && property.Value.ValueKind == System.Text.Json.JsonValueKind.True)
                    return true;
                if (ContainsError(property.Value)) return true;
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (ContainsError(item)) return true;
        }
        return false;
    }

    private static string? InferConnectionFromInvocations(IReadOnlyList<ToolInvocationTrace> invocations)
    {
        // Try to extract moodleAlias/alias from argument JSON of first invocation
        foreach (var inv in invocations)
        {
            if (string.IsNullOrWhiteSpace(inv.ArgumentsJson)) continue;
            try
            {
                var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(inv.ArgumentsJson);
                if (args == null) continue;

                foreach (var key in new[] { "moodleAlias", "alias", "connectionRef", "connection" })
                {
                    if (args.TryGetValue(key, out var val))
                    {
                        var text = val.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
            }
            catch { /* ignore malformed JSON */ }
        }
        return null;
    }

    private static bool IsGenericMcpTool(string toolName)
    {
        // Tools that are structural/meta — not domain hallucinations
        return toolName.StartsWith("moodle_execute", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("moodle_read", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("moodle_get_site", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("moodle_list_cap", StringComparison.OrdinalIgnoreCase);
    }
}
