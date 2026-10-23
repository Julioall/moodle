using System.Net.Http.Json;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;
using Xunit;

namespace Moodle.Application.Tests.Integration;

public class ToolExposureValidationTests : IClassFixture<McpTestWebApplicationFactory>
{
    private readonly McpTestWebApplicationFactory _factory;

    public ToolExposureValidationTests(McpTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ProfileC_RemovedSet_Equals_MetadataDrivenExpected()
    {
        // Tools for Profile A and B (should be identical)
        var toolsA = await GetToolsListAsync(_factory, "Full");
        var toolsB = await GetToolsListAsync(_factory, "FullWithCoursesSkill");

        Assert.Equal(toolsA.OrderBy(x => x), toolsB.OrderBy(x => x));

        // Tools for Profile C (optimized)
        var toolsC = await GetToolsListAsync(_factory, "SkillCoursesOptimized");

        Assert.True(toolsC.Count < toolsB.Count, "ProfileC should have fewer tools than ProfileB");

        // Ensure moodle_execute_read remains exposed
        Assert.Contains("moodle_execute_read", toolsC, StringComparer.OrdinalIgnoreCase);

        // Ensure controlled write primitives are present
        Assert.Contains("moodle_prepare_write", toolsC, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("moodle_confirm_write", toolsC, StringComparer.OrdinalIgnoreCase);

        // Ensure unrelated universal function listing remains exposed
        Assert.Contains("moodle_list_functions", toolsC, StringComparer.OrdinalIgnoreCase);

        // Compute expected hidden set from the ToolMetadataRegistry used by the app instance
        var customFactoryC = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MCP_EXPOSURE_PROFILE"] = "SkillCoursesOptimized",
                    ["McpServerSecurity:RequireApiKey"] = "true",
                    ["McpServerSecurity:RequireJwt"] = "false"
                });
            });
        });

        var registry = customFactoryC.Services.GetService<ToolMetadataRegistry>();
        Assert.NotNull(registry);

        var expectedHidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in registry!.Entries)
        {
            var name = entry.Key;
            var md = entry.Value;
            if (string.Equals(md.Family, "courses", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(md.Classification, "R1", StringComparison.OrdinalIgnoreCase) || string.Equals(md.Classification, "R2", StringComparison.OrdinalIgnoreCase))
                && md.Structural == false)
            {
                expectedHidden.Add(name);
            }
        }

        var removed = toolsA.Except(toolsC, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Assert the removed set exactly matches the metadata-driven expected set
        Assert.Equal(expectedHidden.OrderBy(x => x), removed.OrderBy(x => x));
    }

    [Fact]
    public async Task Production_exposes_only_the_registered_surface_and_keeps_controlled_writes_explicit()
    {
        var tools = await GetToolsListAsync(_factory, "Production");

        Assert.True(tools.Count <= 130, $"A exposição de produção não pode exceder o catálogo cognitivo: {tools.Count}.");
        Assert.Contains("moodle_execute_read", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("moodle_prepare_write", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("moodle_confirm_write", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("moodle_list_available_flows", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("moodle_diagnose_connection", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("moodle_list_functions", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("moodle_check_function", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("discover_grading_functions", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("execute_grading_discovery", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("list_all_gradable_submissions", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("start_pending_grading_run", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("prepare_ai_grading_batch", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("save_ai_grading_batch", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("export_grading_corrections_csv", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("create_batch_grade_launch_preview", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("confirm_batch_grade_launch", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("create_assisted_grading_batch", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("get_grading_batch_status", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("prepare_submission_grading", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("list_gradable_submissions", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("search", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("fetch", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("get_student_submission", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("get_submission_status", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("generate_course_grades_report", tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("export_course_grades_excel", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolve_planner_tags", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("create_tasks_for_references", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("create_task", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("create_agenda_event", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("get_student_completion", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("prepare_demo_action", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("confirm_demo_action", tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("future_unregistered_tool", tools, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Published_tool_metadata_does_not_suggest_unregistered_connection_aliases()
    {
        var productionContainers = RegisteredMcpToolContainers.AlwaysOn
            .Concat(RegisteredMcpToolContainers.GetEnabledContainers(
                new FeatureOptions
                {
                    MessagesWriteEnabled = true,
                    UniversalMoodleWriteEnabled = true,
                    UniversalMoodleFileDownloadEnabled = true
                },
                new AssignmentWriteFeatureOptions { AssignmentGradeWriteEnabled = true }));

        var publishedMetadata = productionContainers
            .SelectMany(container => container.GetMethods())
            .SelectMany(method => method
                .GetCustomAttributes<DescriptionAttribute>(inherit: true)
                .Select(attribute => attribute.Description)
                .Concat(method.GetParameters()
                    .SelectMany(parameter => parameter
                        .GetCustomAttributes<DescriptionAttribute>(inherit: true)
                        .Select(attribute => attribute.Description))))
            .ToArray();

        foreach (var forbiddenAlias in new[] { "goias", "ctm" })
        {
            Assert.DoesNotContain(
                publishedMetadata,
                description => description.Contains(forbiddenAlias, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Production_submission_matches_the_runtime_tools_list()
    {
        var runtimeTools = await GetToolsListAsync(_factory, "Production");
        var submissionPath = Path.Combine(AppContext.BaseDirectory, "chatgpt-app-submission.json");

        Assert.True(File.Exists(submissionPath), "chatgpt-app-submission.json must be available to integration tests.");

        var submission = JsonNode.Parse(await File.ReadAllTextAsync(submissionPath));
        var submissionTools = submission?["tools"]?.AsObject()
            .Select(entry => entry.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotNull(submissionTools);
        Assert.All(runtimeTools, name => Assert.Contains(name, submissionTools!, StringComparer.Ordinal));
    }

    [Fact]
    public void Production_submission_annotations_match_registered_tool_contracts()
    {
        var submissionTools = LoadSubmissionTools();
        var productionContainers = RegisteredMcpToolContainers.AlwaysOn
            .Concat(RegisteredMcpToolContainers.GetEnabledContainers(
                new FeatureOptions
                {
                    MessagesWriteEnabled = true,
                    UniversalMoodleWriteEnabled = true,
                    UniversalMoodleFileDownloadEnabled = true
                },
                new AssignmentWriteFeatureOptions { AssignmentGradeWriteEnabled = true }));
        var metadataRegistry = new ToolMetadataRegistry(RegisteredMcpToolContainers.All);
        var exposurePolicy = new CognitiveExposurePolicy(ToolExposureProfile.Production);
        var contracts = productionContainers
            .SelectMany(container => container.GetMethods())
            .SelectMany(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: true)
                .Cast<McpServerToolAttribute>())
            .Where(contract => !string.IsNullOrWhiteSpace(contract.Name))
            .Where(contract => metadataRegistry.TryGet(contract.Name!, out var metadata) &&
                               exposurePolicy.ShouldExpose(contract.Name!, metadata))
            .ToDictionary(contract => contract.Name!, StringComparer.Ordinal);

        Assert.Equal(97, contracts.Count);
        Assert.Contains("moodle_list_connections", contracts.Keys);
        Assert.Equal(
            contracts.Keys.OrderBy(name => name, StringComparer.Ordinal),
            submissionTools.Select(entry => entry.Key).OrderBy(name => name, StringComparer.Ordinal));

        foreach (var (name, contract) in contracts)
        {
            Assert.NotNull(contract.OutputSchemaType);

            var annotations = submissionTools[name]?["annotations"];
            Assert.NotNull(annotations);
            Assert.Equal(contract.ReadOnly, annotations!["readOnlyHint"]!.GetValue<bool>());
            Assert.Equal(contract.Destructive, annotations["destructiveHint"]!.GetValue<bool>());
            Assert.Equal(contract.Idempotent, annotations["idempotentHint"]!.GetValue<bool>());
            Assert.Equal(contract.OpenWorld, annotations["openWorldHint"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void Production_submission_tool_block_is_generated_from_registered_contracts()
    {
        var submission = LoadSubmission();

        Assert.True(
            JsonNode.DeepEquals(
                ChatGptSubmissionToolCatalog.CreateProductionTools(),
                submission["tools"]),
            "The tools block must be regenerated from McpServerToolAttribute contracts instead of edited by hand.");
    }

    [Fact]
    public void Submission_contains_the_required_review_metadata_and_cases()
    {
        var submission = LoadSubmission();
        var appInfo = submission["app_info"]?.AsObject();
        var testCases = submission["test_cases"]?.AsArray();
        var negativeTestCases = submission["negative_test_cases"]?.AsArray();
        var submissionTools = LoadSubmissionTools();

        Assert.NotNull(appInfo);
        Assert.False(string.IsNullOrWhiteSpace(appInfo!["display_name"]?.GetValue<string>()));
        var subtitle = appInfo["subtitle"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(subtitle));
        Assert.True(subtitle!.Length <= 30, "Submission subtitle must contain at most 30 characters.");
        Assert.Contains(appInfo["category"]?.GetValue<string>(), new[]
        {
            "BUSINESS", "COLLABORATION", "DESIGN", "DEVELOPER_TOOLS", "EDUCATION", "ENTERTAINMENT",
            "FINANCE", "FOOD", "LIFESTYLE", "NEWS", "PRODUCTIVITY", "SHOPPING", "TRAVEL"
        });

        Assert.NotNull(testCases);
        Assert.NotNull(negativeTestCases);
        Assert.Equal(5, testCases!.Count);
        Assert.Equal(3, negativeTestCases!.Count);

        foreach (var entry in submissionTools)
        {
            var annotations = entry.Value?["annotations"];
            var justifications = entry.Value?["justifications"];

            Assert.False(string.IsNullOrWhiteSpace(annotations?["title"]?.GetValue<string>()));
            Assert.NotNull(annotations?["readOnlyHint"]);
            Assert.NotNull(annotations?["openWorldHint"]);
            Assert.NotNull(annotations?["destructiveHint"]);
            Assert.False(string.IsNullOrWhiteSpace(justifications?["read_only_justification"]?.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(justifications?["open_world_justification"]?.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(justifications?["destructive_justification"]?.GetValue<string>()));
        }

        foreach (var testCase in testCases)
        {
            var toolName = testCase?["tools_triggered"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(toolName));
            Assert.Contains(toolName!, submissionTools.Select(entry => entry.Key), StringComparer.Ordinal);
        }

        Assert.All(negativeTestCases, testCase => Assert.Null(testCase?["tools_triggered"]));
    }

    [Fact]
    public void Write_permissions_delegate_the_coarse_scope_required_by_the_controlled_write_flow()
    {
        var scopes = ToolAuthorizationMapping.ScopesForPermissions([
            "tool.assignments.grade",
            "tool.messages.send"
        ]);

        Assert.Contains(MoodleScopePolicies.WriteAny, scopes);
        Assert.Contains(MoodleScopePolicies.WriteAssignmentsGrade, scopes);
        Assert.Contains(MoodleScopePolicies.WriteMessages, scopes);
    }

    [Theory]
    [InlineData("list_tasks")]
    [InlineData("list_agenda_events")]
    public async Task Portal_list_tools_accept_empty_arguments_without_invalid_argument(string toolName)
    {
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MCP_EXPOSURE_PROFILE"] = "Production",
                    ["McpServerSecurity:RequireApiKey"] = "true",
                    ["McpServerSecurity:RequireJwt"] = "false"
                });
            });
        });

        var apiKey = await RegisterClientAsync(canWrite: true, customFactory);
        var client = customFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);

        var payload = $$"""
        {
          "jsonrpc": "2.0",
          "id": "portal-list-{{toolName}}",
          "method": "tools/call",
          "params": {
            "name": "{{toolName}}",
            "arguments": {}
          }
        }
        """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
            request.Headers.Add("Mcp-Session-Id", sessionId);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("INVALID_ARGUMENT", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unexpected_connector_error", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("failed", true, false)]
    [InlineData("empty", false, false)]
    [InlineData("available", true, true)]
    public async Task Live_discovery_distinguishes_unknown_from_verified_capabilities(
        string discovery, bool exposesContents, bool exposesForumWrite)
    {
        var catalog = new ExposureFunctionCatalog(discovery);
        var tools = await GetToolsListAsync(_factory, "Production", catalog);

        Assert.True(catalog.Calls > 0, "The live capability filter must run, not the stub bypass.");
        Assert.Equal(exposesContents, tools.Contains("list_course_contents"));
        Assert.Equal(exposesForumWrite, tools.Contains("confirm_forum_post"));
        Assert.Contains("search", tools);
        Assert.DoesNotContain("moodle_diagnose_connection", tools);
        Assert.DoesNotContain("moodle_download_file", tools); // Its flag is still disabled.
        if (discovery == "failed")
        {
            Assert.Contains("get_course", tools);
            Assert.Contains("list_assignment_submissions", tools);
            Assert.Contains("list_course_participants", tools);
            Assert.Contains("get_student_gradebook", tools);
        }
    }

    [Theory]
    [InlineData("risk", true)]
    [InlineData("empty", false)]
    public async Task Production_exposes_risk_report_only_when_its_moodle_capability_is_available(
        string discovery, bool exposesRiskReport)
    {
        var catalog = new ExposureFunctionCatalog(discovery);
        var tools = await GetToolsListAsync(_factory, "Production", catalog);

        Assert.True(catalog.Calls > 0, "The live capability filter must run, not the stub bypass.");
        Assert.Equal(exposesRiskReport, tools.Contains("report_students_at_risk"));
    }

    private sealed class ExposureFunctionCatalog(string discovery) : IMoodleFunctionCatalog
    {
        public int Calls { get; private set; }

        public Task<MoodleFunctionProfile> GetCurrentAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            Calls++;
            if (discovery == "failed")
                throw new MoodleApiException(MoodleErrorContract.NetworkError, "Discovery unavailable.");
            string[] names = discovery switch
            {
                "available" => ["core_course_get_contents", "mod_forum_add_discussion"],
                "risk" => ["core_enrol_get_enrolled_users"],
                _ => []
            };
            return Task.FromResult(new MoodleFunctionProfile("connection", "default", null, null, null,
                names.Select(name => new MoodleFunctionDescriptor(name, MoodleFunctionRisk.Read, true)).ToArray(),
                DateTimeOffset.UtcNow));
        }
    }

    private async Task<IReadOnlyList<string>> GetToolsListAsync(WebApplicationFactory<Program> factory, string exposureProfile,
        IMoodleFunctionCatalog? functionCatalog = null)
    {
        var customFactory = factory.WithWebHostBuilder(builder =>
        {
            if (functionCatalog is not null)
            {
                builder.ConfigureTestServices(services =>
                    services.Replace(ServiceDescriptor.Singleton<IMoodleFunctionCatalog>(functionCatalog)));
            }
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MCP_EXPOSURE_PROFILE"] = exposureProfile,
                    ["MoodleApi:UseStubData"] = functionCatalog is null ? "true" : "false",
                    ["McpServerSecurity:RequireApiKey"] = "true",
                    ["McpServerSecurity:RequireJwt"] = "false",
                    // Explicitly opt into the complete write surface for this
                    // catalog test; deploy defaults remain fail-closed.
                    ["Features:MessagesWriteEnabled"] = "true",
                    ["Features:ScheduledMessagesEnabled"] = "true",
                    ["Features:AssignmentFeedbackWriteEnabled"] = "true",
                    ["Features:AssignmentGradeWriteEnabled"] = "true",
                    ["Features:UniversalMoodleWriteEnabled"] = "true",
                    ["Features:CourseContentWriteEnabled"] = "true"
                });
            });
        });

        var client = customFactory.CreateClient();
        var apiKey = await RegisterClientAsync(canWrite: true, customFactory);
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);

        var toolsClient = customFactory.CreateClient();
        toolsClient.DefaultRequestHeaders.Add("X-Mcp-Api-Key", apiKey);
        toolsClient.DefaultRequestHeaders.Accept.Clear();
        toolsClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        toolsClient.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var payload = """
        {
          "jsonrpc": "2.0",
          "id": "tools-list-profile",
          "method": "tools/list",
          "params": {}
        }
        """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }

        var response = await toolsClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        var parsed = ParseMcpResponseBody(body);
        var tools = parsed?["result"]?["tools"]?.AsArray();
        if (tools == null)
        {
            return Array.Empty<string>();
        }

        var list = tools.Select(t => t? ["name"]?.ToString() ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        return list;
    }

    private static JsonNode? ParseMcpResponseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            var dataLines = body
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line.Substring("data:".Length).Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            if (dataLines.Length == 0)
            {
                return null;
            }

            var combined = string.Join(string.Empty, dataLines);
            return JsonNode.Parse(combined);
        }
    }

    private static JsonObject LoadSubmission()
    {
        var submissionPath = Path.Combine(AppContext.BaseDirectory, "chatgpt-app-submission.json");
        Assert.True(File.Exists(submissionPath), "chatgpt-app-submission.json must be available to integration tests.");

        var submission = JsonNode.Parse(File.ReadAllText(submissionPath));
        var root = submission?.AsObject();
        Assert.NotNull(root);
        return root!;
    }

    private static JsonObject LoadSubmissionTools()
    {
        var submission = LoadSubmission();
        var tools = submission?["tools"]?.AsObject();
        Assert.NotNull(tools);
        return tools!;
    }

        private async Task<string> RegisterClientAsync(bool canWrite, WebApplicationFactory<Program>? factory = null)
        {
                var client = (factory ?? _factory).CreateClient();
                var payload = $$"""
                {
                    "clientId": "integration-{{Guid.NewGuid():N}}",
                    "moodleAlias": "default",
                    "moodleBaseUrl": "https://moodle.tests",
                    "moodleUsername": "usuario.teste",
                    "moodlePassword": "senha.teste",
                    "moodleTarget": "default",
                    "isDefault": true,
                    "canWrite": {{canWrite.ToString().ToLowerInvariant()}}
                }
                """;

                using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/connector-clients/register")
                {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Admin-Api-Key", "admin-tests-key");

                var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();

                var marker = "\"apiKey\":\"";
                var start = body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                Assert.True(start >= 0, "Resposta do cadastro nao retornou apiKey.");
                start += marker.Length;
                var end = body.IndexOf('"', start);
                Assert.True(end > start, "Formato de apiKey invalido no payload de cadastro.");

                return body[start..end];
        }

        private static async Task<string?> InitializeMcpSessionAsync(HttpClient client)
        {
                var initializePayload = BuildInitializePayload("init-1");

                var response = await client.PostAsync("/mcp", new StringContent(initializePayload, Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();

                if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIdValues))
                {
                        return sessionIdValues.FirstOrDefault();
                }

                return null;
        }

        private static string BuildInitializePayload(string id)
        {
                return $$"""
                {
                    "jsonrpc": "2.0",
                    "id": "{{id}}",
                    "method": "initialize",
                    "params": {
                        "protocolVersion": "2024-11-05",
                        "capabilities": {},
                        "clientInfo": {
                            "name": "integration-tests",
                            "version": "1.0.0"
                        }
                    }
                }
                """;
        }

        private static async Task NotifyInitializedAsync(HttpClient client, string? sessionId)
        {
                var payload = """
                {
                    "jsonrpc": "2.0",
                    "method": "notifications/initialized",
                    "params": {}
                }
                """;

                using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
                {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                        request.Headers.Add("Mcp-Session-Id", sessionId);
                }

                var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
        }
}
