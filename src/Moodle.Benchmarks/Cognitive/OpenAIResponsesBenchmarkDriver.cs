using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace Moodle.Benchmarks.Cognitive;

public sealed record ToolManifestSnapshot(
    int ToolCount,
    int ToolSchemaTokens,
    long ToolSchemaBytes,
    string ManifestHash);

/// <summary>
/// Benchmark driver using the MCP Streamable HTTP transport (POST /mcp).
/// The Moodle server uses .WithHttpTransport() which implements
/// the modern Streamable HTTP protocol — NOT the legacy SSE transport.
///
/// Protocol summary:
///   - Initialize: POST /mcp  { jsonrpc, id, method:"initialize", params:{ ... } }
///     → response contains { result: { ... } }
///     → server returns Mcp-Session-Id header
///   - Subsequent requests: POST /mcp with Mcp-Session-Id header
///   - tools/list: POST /mcp { jsonrpc, id, method:"tools/list" }
///   - tools/call:  POST /mcp { jsonrpc, id, method:"tools/call", params:{ name, arguments } }
/// </summary>
public sealed class OpenAIResponsesBenchmarkDriver : IBenchmarkAgentDriver
{
    private readonly ChatClient _chatClient;
    private readonly HttpClient _mcpClient;
    private readonly BenchmarkTelemetry? _telemetry;
    private readonly string _runId;
    private string? _sessionId;
    private bool _initialized;
    private BenchmarkScorer _scorer = new();
    private HashSet<string> _knownToolNames = new(StringComparer.OrdinalIgnoreCase);

    // Tools cached after first fetch — avoids 30× redundant tools/list calls per profile
    private JsonArray? _cachedMcpTools;
    private List<ChatTool>? _cachedChatTools;
    private int _cachedToolSchemaTokens;
    private string _cachedManifestHash = string.Empty;

    private const string McpEndpoint = "/mcp";
    private const int MaxRetries = 7; // bounded backoff for recoverable rate limits
    public static readonly string CommitSha = ResolveCommitSha();
    public const string BenchmarkVersion = "1.2.0";

    public OpenAIResponsesBenchmarkDriver(
        ChatClient chatClient,
        HttpClient mcpClient,
        BenchmarkTelemetry? telemetry = null,
        string? runId = null)
    {
        _chatClient = chatClient;
        _mcpClient = mcpClient;
        _telemetry = telemetry;
        _runId = runId ?? string.Empty;

        // MCP Streamable HTTP requires the client to advertise both media types.
        // Without this header the server returns 406 Not Acceptable.
        _mcpClient.DefaultRequestHeaders.Accept.Clear();
        _mcpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _mcpClient.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
    }

    // ------------------------------------------------------------------
    // Static helpers
    // ------------------------------------------------------------------

    private static string ResolveCommitSha()
    {
        var envSha = Environment.GetEnvironmentVariable("GIT_COMMIT_SHA")
            ?? Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(envSha)) return envSha.Trim();

        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            var sha = proc?.StandardOutput.ReadToEnd().Trim() ?? string.Empty;
            proc?.WaitForExit();
            return sha;
        }
        catch { return string.Empty; }
    }

    private static string ComputeManifestHash(JsonArray tools)
    {
        var json = tools.ToJsonString();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // MCP Streamable HTTP initialization
    // ------------------------------------------------------------------

    /// <summary>
    /// Initializes the MCP session via Streamable HTTP.
    /// After this call, _sessionId is set and all subsequent requests
    /// include the Mcp-Session-Id header.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "init-1",
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject()
                },
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "MoodleBench",
                    ["version"] = BenchmarkVersion
                }
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, McpEndpoint)
        {
            Content = JsonContent.Create(request)
        };

        // Apply session ID if we have one from a previous attempt
        if (!string.IsNullOrEmpty(_sessionId))
            httpRequest.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);

        var response = await _mcpClient.SendAsync(httpRequest, ct);

        // Capture session ID from response headers
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
        {
            _sessionId = sessionValues.FirstOrDefault();
            // Update default headers for subsequent requests
            _mcpClient.DefaultRequestHeaders.Remove("Mcp-Session-Id");
            if (!string.IsNullOrEmpty(_sessionId))
                _mcpClient.DefaultRequestHeaders.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"MCP initialize failed: HTTP {(int)response.StatusCode} — {body}");
        }

        // Send initialized notification
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized"
        };
        using var notifRequest = new HttpRequestMessage(HttpMethod.Post, McpEndpoint)
        {
            Content = JsonContent.Create(notification)
        };
        // Best effort — server may return 202 Accepted or 200
        _ = await _mcpClient.SendAsync(notifRequest, ct);

        _initialized = true;
    }

    // ------------------------------------------------------------------
    // MCP tool fetch (cached per driver instance)
    // ------------------------------------------------------------------

    private async Task<(JsonArray tools, int schemaTokens)> FetchMcpToolsAsync(CancellationToken ct)
    {
        // Return cached result after first successful fetch per profile
        if (_cachedMcpTools != null)
            return (_cachedMcpTools, _cachedToolSchemaTokens);

        await EnsureInitializedAsync(ct);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString(),
            ["method"] = "tools/list"
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, McpEndpoint)
        {
            Content = JsonContent.Create(request)
        };

        var response = await _mcpClient.SendAsync(httpRequest, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"MCP tools/list failed: HTTP {(int)response.StatusCode} — {body}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        JsonObject? responseJson;
        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var streamBody = await response.Content.ReadAsStringAsync(ct);
            responseJson = ParseFirstSseJson(streamBody);
        }
        else
        {
            responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        }

        var tools = responseJson?["result"]?["tools"]?.AsArray() ?? new JsonArray();
        // The model receives the complete MCP tool descriptor, not only the
        // input schema. Count name/description/schema together so the metric
        // reflects the actual schema surface sent to the model.
        var schemaTokens = tools.Sum(t => EstimateTokens(t?.ToJsonString() ?? string.Empty));

        // Cache for all subsequent tasks in this profile run
        _cachedMcpTools = tools;
        _cachedToolSchemaTokens = schemaTokens;
        _cachedManifestHash = ComputeManifestHash(tools);

        return (tools, schemaTokens);
    }

    /// <summary>
    /// Retrieves and measures the MCP catalog without making a model call.
    /// This is used by the deterministic schema-surface probe.
    /// </summary>
    public async Task<ToolManifestSnapshot> FetchToolManifestAsync(CancellationToken cancellationToken = default)
    {
        var (tools, schemaTokens) = await FetchMcpToolsAsync(cancellationToken);
        var schemaBytes = tools.Sum(t => Encoding.UTF8.GetByteCount(t?.ToJsonString() ?? string.Empty));
        return new ToolManifestSnapshot(tools.Count, schemaTokens, schemaBytes, _cachedManifestHash);
    }

    // ------------------------------------------------------------------
    // MCP tool call
    // ------------------------------------------------------------------

    private async Task<string> CallMcpToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString(),
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson)
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, McpEndpoint)
        {
            Content = JsonContent.Create(request)
        };

        var response = await _mcpClient.SendAsync(httpRequest, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"MCP tools/call failed for '{name}': HTTP {(int)response.StatusCode} — {body}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var streamBody = await response.Content.ReadAsStringAsync(ct);
            var parsed = ParseFirstSseJson(streamBody);
            return parsed?.ToJsonString() ?? streamBody;
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    // ------------------------------------------------------------------
    // Main benchmark run
    // ------------------------------------------------------------------

    public async Task<CognitiveTrace> RunAsync(BenchmarkTask task, BenchmarkProfile profile, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _telemetry?.TakeMoodleWebServiceCalls();
        _telemetry?.TakeLastConnectionAlias();

        // 1. Fetch tools — cached after first call, avoids 30× redundant fetches per profile
        var (mcpTools, toolSchemaTokens) = await FetchMcpToolsAsync(cancellationToken);
        var toolManifestHash = _cachedManifestHash;

        // Build known tool names and scorer once per profile
        if (_knownToolNames.Count == 0)
        {
            foreach (var t in mcpTools)
            {
                var n = t?["name"]?.ToString();
                if (!string.IsNullOrEmpty(n)) _knownToolNames.Add(n);
            }
            _scorer = new BenchmarkScorer(_knownToolNames);
        }

        // 2. Build ChatTools list once per profile (cached)
        if (_cachedChatTools == null)
        {
            _cachedChatTools = new List<ChatTool>();
            foreach (var mtool in mcpTools)
            {
                var name = mtool?["name"]?.ToString() ?? string.Empty;
                var description = mtool?["description"]?.ToString() ?? string.Empty;
                var inputSchema = mtool?["inputSchema"]?.AsObject();
                if (!string.IsNullOrEmpty(name) && inputSchema != null)
                {
                    var schemaBytes = JsonSerializer.SerializeToUtf8Bytes(inputSchema);
                    _cachedChatTools.Add(ChatTool.CreateFunctionTool(name, description, BinaryData.FromBytes(schemaBytes)));
                }
            }
        }

        // 3. Setup conversation. Plugin profiles receive the exact packaged
        // skill content, so the benchmark can prove a skill influenced a run.
        var skillBundle = LoadPluginSkillBundle(profile, task);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(BuildSystemPrompt(skillBundle)),
            new UserChatMessage(task.Prompt)
        };

        var options = new ChatCompletionOptions { Temperature = 1.0f };
        foreach (var tool in _cachedChatTools!)
            options.Tools.Add(tool);

        string? selectedTool = null;
        string? selectedOperation = null;
        string? selectedConnection = null;
        string? executedConnection = null;
        var arguments = new Dictionary<string, object>();
        string resultContent = string.Empty;
        var toolInvocations = new List<ToolInvocationTrace>();
        var aggregatedPromptTokens = 0;
        var aggregatedCompletionTokens = 0;
        var aggregatedCachedInputTokens = 0;
        var aggregatedReasoningTokens = 0;
        var modelCalls = 0;

        // 4. Agent loop (max 5 turns) with 429 retry
        for (int i = 0; i < 5; i++)
        {
            var completionTask = RetryOnRateLimitAsync(
                () => _chatClient.CompleteChatAsync(messages, options, cancellationToken),
                cancellationToken);
            var completion = await completionTask.WaitAsync(cancellationToken);
            modelCalls++;
            messages.Add(new AssistantChatMessage(completion));

            aggregatedPromptTokens += completion.Value.Usage?.InputTokenCount ?? 0;
            aggregatedCompletionTokens += completion.Value.Usage?.OutputTokenCount ?? 0;
            aggregatedCachedInputTokens += completion.Value.Usage?.InputTokenDetails?.CachedTokenCount ?? 0;
            aggregatedReasoningTokens += completion.Value.Usage?.OutputTokenDetails?.ReasoningTokenCount ?? 0;

            if (completion.Value.FinishReason == ChatFinishReason.ToolCalls)
            {
                foreach (var toolCall in completion.Value.ToolCalls)
                {
                    selectedTool ??= toolCall.FunctionName;
                    var argsStr = NormalizeConnectionInArguments(toolCall.FunctionArguments?.ToString() ?? "{}");
                    if (arguments.Count == 0 && !string.IsNullOrWhiteSpace(argsStr))
                        arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(argsStr) ?? new();

                    selectedConnection ??= ExtractConnectionFromArguments(arguments)
                        ?? InferConnectionFromPrompt(task.Prompt);
                    selectedOperation ??= ExtractCanonicalOperation(toolCall.FunctionName, argsStr);

                    var toolStart = Stopwatch.StartNew();
                    var toolResult = await CallMcpToolAsync(toolCall.FunctionName, argsStr, cancellationToken);
                    toolStart.Stop();
                    resultContent = toolResult;
                    executedConnection ??= ExtractConnectionFromToolResult(toolResult);

                    toolInvocations.Add(new ToolInvocationTrace(
                        ToolName: toolCall.FunctionName,
                        ArgumentsJson: argsStr,
                        ToolResult: toolResult,
                        LatencyMs: toolStart.ElapsedMilliseconds,
                        PromptTokens: completion.Value.Usage?.InputTokenCount ?? 0,
                        CompletionTokens: completion.Value.Usage?.OutputTokenCount ?? 0,
                        TotalTokens: completion.Value.Usage?.TotalTokenCount ?? 0
                    ));

                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                }
            }
            else
            {
                break;
            }
        }

        stopwatch.Stop();

        // 5. Build traces
        var routing = new RoutingTrace(
            SelectedSkill: skillBundle.SelectedNames,
            SelectedIntent: IntentMapper.ResolveOperation(selectedOperation ?? selectedTool ?? "none") ?? "unknown",
            SelectedOperation: selectedOperation ?? selectedTool ?? "none",
            SelectedConnection: selectedConnection,
            Arguments: arguments,
            ToolInvocations: toolInvocations
        );

        var execution = new ExecutionTrace(
            ConnectionId: Guid.Empty,
            RegistryOperation: selectedOperation ?? selectedTool ?? "none",
            PolicyDecision: "Allowed",
            MoodleCalls: _telemetry?.TakeMoodleWebServiceCalls() ?? 0,
            LatencyMs: stopwatch.ElapsedMilliseconds,
            PromptTokens: aggregatedPromptTokens,
            CompletionTokens: aggregatedCompletionTokens,
            TotalTokens: aggregatedPromptTokens + aggregatedCompletionTokens,
            ToolSchemaTokens: toolSchemaTokens,
            ToolManifestHash: toolManifestHash,
            SkillManifestHash: skillBundle.ManifestHash,
            BenchmarkVersion: BenchmarkVersion,
            CommitSha: CommitSha
            ,ModelCalls: modelCalls
            ,McpToolCalls: toolInvocations.Count
            ,CachedInputTokens: aggregatedCachedInputTokens
            ,UncachedInputTokens: Math.Max(0, aggregatedPromptTokens - aggregatedCachedInputTokens)
            ,ReasoningTokens: aggregatedReasoningTokens
            ,ExecutedConnection: _telemetry?.TakeLastConnectionAlias() ?? executedConnection
            ,RunId: _runId
        );

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

    private static string? ExtractConnectionFromToolResult(string result)
    {
        try
        {
            using var document = JsonDocument.Parse(result);
            return FindConnectionAlias(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindConnectionAlias(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("connectionAlias") || property.NameEquals("alias"))
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }

                var nested = FindConnectionAlias(property.Value);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindConnectionAlias(item);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Retry helper for 429 rate-limit responses
    // ------------------------------------------------------------------

    private static async Task<T> RetryOnRateLimitAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (ex.Message.Contains("429") || ex.Message.Contains("rate_limit"))
            {
                if (IsQuotaExhausted(ex))
                {
                    // Quota exhaustion is a permanent run blocker, not a
                    // recoverable rate limit. Retrying only delays the
                    // completeness verdict and wastes time/tokens.
                    throw;
                }

                if (attempt == MaxRetries - 1) throw;

                // Parse "Please try again in Xms" if present
                var match = System.Text.RegularExpressions.Regex.Match(
                    ex.Message, @"try again in (\d+)ms");
                var waitMs = match.Success ? int.Parse(match.Groups[1].Value) + 200 : 0;

                // OpenAI's TPM wait time is often calculated based on *requested* tokens, not total tokens!
                // So "try again in 80ms" is wrong for our 13k token prompts.
                // We MUST enforce the exponential delay if it's larger than OpenAI's suggestion.
                var waitTime = TimeSpan.FromMilliseconds(Math.Max(waitMs, delay.TotalMilliseconds));

                Console.Write($" [429 retry {attempt + 1}/{MaxRetries} wait {waitTime.TotalSeconds:F1}s]");
                await Task.Delay(waitTime, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60)); // exponential cap at 60s
            }
        }
        throw new InvalidOperationException("Unreachable");
    }

    private static bool IsQuotaExhausted(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("credit_balance_exhausted", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("quota_exceeded", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Static utilities
    // ------------------------------------------------------------------

    public static string? ExtractConnectionFromArguments(Dictionary<string, object> arguments)
    {
        foreach (var key in new[] { "moodleAlias", "alias", "connectionRef", "connection" })
        {
            if (arguments.TryGetValue(key, out var val) && val is string s && !string.IsNullOrWhiteSpace(s))
                return NormalizeConnectionAlias(s);
        }
        return null;
    }

    private static string? InferConnectionFromPrompt(string prompt)
    {
        if (prompt.Contains("SENAI", StringComparison.OrdinalIgnoreCase)) return "senai";
        if (prompt.Contains("FIEG",  StringComparison.OrdinalIgnoreCase)) return "fieg";
        return null;
    }

    private static int EstimateTokens(string text)
        => string.IsNullOrWhiteSpace(text) ? 0 : Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));

    private static string BuildSystemPrompt(BenchmarkSkillBundle skillBundle)
    {
        var prompt = "You are Moodle, a helpful assistant. Use tools to interact with Moodle.";
        if (skillBundle.IsEmpty) return prompt;

        return $"{prompt}\n\n" +
               "The following installed Moodle skills are authoritative workflow guidance for this request. " +
               "Follow them together with the available tool schemas.\n\n" +
               skillBundle.PromptSection;
    }

    private static string? ExtractCanonicalOperation(string toolName, string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var operation = FindStringProperty(document.RootElement, "functionName")
                ?? FindStringProperty(document.RootElement, "operation")
                ?? FindStringProperty(document.RootElement, "moodleFunction");
            return operation ?? toolName;
        }
        catch { return toolName; }
    }

    private static string? FindStringProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindStringProperty(property.Value, name);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindStringProperty(item, name);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static string NormalizeConnectionAlias(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("senai", StringComparison.Ordinal)) return "senai";
        if (normalized.Contains("fieg", StringComparison.Ordinal)) return "fieg";
        return normalized;
    }

    private static string NormalizeConnectionInArguments(string argumentsJson)
    {
        try
        {
            var node = JsonNode.Parse(argumentsJson);
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "moodleAlias", "alias", "connectionRef", "connection" })
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var alias)
                        && !string.IsNullOrWhiteSpace(alias))
                    {
                        obj[key] = NormalizeConnectionAlias(alias);
                    }
                }
            }
            return node?.ToJsonString() ?? argumentsJson;
        }
        catch { return argumentsJson; }
    }

    public static BenchmarkSkillBundle LoadPluginSkillBundle(
        BenchmarkProfile profile,
        BenchmarkTask task,
        string? repositoryRoot = null)
    {
        var skillNames = ResolveSkillNames(profile, task);
        if (skillNames.Count == 0)
        {
            return new BenchmarkSkillBundle([], string.Empty, string.Empty);
        }

        var repoRoot = repositoryRoot ?? FindRepositoryRoot();
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            throw new InvalidOperationException("Plugin skill benchmark requires a Moodle repository root.");
        }

        var prompt = new StringBuilder();
        var manifest = new StringBuilder();
        foreach (var skillName in skillNames)
        {
            var path = Path.Combine(repoRoot, "plugins", "moodle-connector", "skills", skillName, "SKILL.md");
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Packaged skill '{skillName}' was not found at '{path}'.");
            }

            var content = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException($"Packaged skill '{skillName}' is empty.");
            }

            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            manifest.Append(skillName).Append(':').Append(contentHash).Append('\n');
            prompt.Append("## Installed skill: ").Append(skillName)
                .Append(" (sha256: ").Append(contentHash).Append(")\n")
                .Append(content.Trim()).Append("\n\n");
        }

        var manifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()))).ToLowerInvariant();
        return new BenchmarkSkillBundle(skillNames, prompt.ToString().TrimEnd(), manifestHash);
    }

    private static IReadOnlyList<string> ResolveSkillNames(BenchmarkProfile profile, BenchmarkTask task)
    {
        if (!profile.UsePluginSkills) return [];

        return task.Category.ToLowerInvariant() switch
        {
            "assignments" => ["moodle-core", "moodle-assignments"],
            "students" => ["moodle-core", "moodle-students"],
            _ => ["moodle-core", "moodle-courses"]
        };
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Moodle.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "Moodle.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Parses the first JSON object from a text/event-stream body.
    /// SSE lines are: "data: {...}" or "event: message\ndata: {...}"
    /// </summary>
    private static JsonObject? ParseFirstSseJson(string sseBody)
    {
        foreach (var line in sseBody.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                var json = trimmed.Substring("data:".Length).Trim();
                if (!string.IsNullOrWhiteSpace(json) && json != "[DONE]")
                {
                    try { return JsonSerializer.Deserialize<JsonObject>(json); }
                    catch { /* try next line */ }
                }
            }
        }
        return null;
    }
}
