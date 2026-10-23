using System.Net;
using System.Net.Http.Json;
using System.Linq;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;

namespace Moodle.Application.Tests.Integration;

public class McpJwtClaimsIntegrationTests : IClassFixture<McpTestWebApplicationFactory>
{
    internal const string JwtIssuer = "https://oauth.tests";
    internal const string JwtAudience = "https://oauth.tests/mcp";
    internal static readonly SymmetricSecurityKey JwtSigningKey = new(Encoding.UTF8.GetBytes("0123456789ABCDEF0123456789ABCDEF"));

    private readonly McpTestWebApplicationFactory _factory;

    public McpJwtClaimsIntegrationTests(McpTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Classifies_the_course_catalog_routes_with_a_stable_family()
    {
        using var client = _factory.CreateClient();
        var expectedRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/schools",
            "/api/schools/courses",
            "/api/courses/search",
            "/api/courses"
        };
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => expectedRoutes.Contains(endpoint.RoutePattern.RawText ?? string.Empty))
            .ToArray();

        Assert.Equal(expectedRoutes.Count, routes.Length);
        Assert.True(expectedRoutes.SetEquals(routes.Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)));
        Assert.All(routes, endpoint =>
        {
            var metadata = endpoint.Metadata.GetMetadata<AdmissionRouteMetadata>();
            Assert.NotNull(metadata);
            Assert.Equal(AdmissionTrafficClass.LocalRead, metadata!.TrafficClass);
            Assert.Equal("course_catalog", metadata.Family);
        });
    }

    [Fact]
    public void Uses_only_explicit_forwarded_header_trust_and_a_bounded_hop_count()
    {
        _ = _factory.CreateClient();
        var forwardedHeaders = _factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(1, forwardedHeaders.ForwardLimit);
        Assert.Contains(System.Net.IPNetwork.Parse("127.0.0.1/32"), forwardedHeaders.KnownIPNetworks);
        Assert.Contains(System.Net.IPNetwork.Parse("::1/128"), forwardedHeaders.KnownIPNetworks);
        Assert.DoesNotContain(System.Net.IPNetwork.Parse("0.0.0.0/0"), forwardedHeaders.KnownIPNetworks);
        Assert.Empty(forwardedHeaders.KnownProxies);
    }

    [Fact]
    public void Every_registered_http_endpoint_has_an_explicit_admission_classification()
    {
        using var client = _factory.CreateClient();
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .ToArray();
        var metadataByEndpoint = endpoints
            .Select(endpoint => (Endpoint: endpoint, Metadata: AdmissionEndpointClassifier.Classify(endpoint)))
            .ToArray();
        var unclassified = endpoints
            .Where(endpoint => AdmissionEndpointClassifier.Classify(endpoint) is null)
            .Select(DescribeEndpoint)
            .ToArray();

        Assert.True(unclassified.Length == 0,
            $"Unclassified HTTP endpoints ({unclassified.Length}/{endpoints.Length}):{Environment.NewLine}{string.Join(Environment.NewLine, unclassified)}");
        Assert.All(metadataByEndpoint
            .Where(item => item.Metadata?.TrafficClass is AdmissionTrafficClass.Operations or AdmissionTrafficClass.StaticContent),
            item => Assert.False(string.IsNullOrWhiteSpace(item.Metadata!.ExemptionReason)));
        Assert.False(string.IsNullOrWhiteSpace(AdmissionEndpointClassifier.StaticAssetExemptionReason));
    }

    [Fact]
    public void Endpoint_classification_covers_enabled_portal_feature_variants()
    {
        using var factory = new PortalFeaturesEnabledMcpTestWebApplicationFactory();
        using var client = factory.CreateClient();
        var unclassified = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => AdmissionEndpointClassifier.Classify(endpoint) is null)
            .Select(DescribeEndpoint)
            .ToArray();

        Assert.Empty(unclassified);
    }

    private static string DescribeEndpoint(Endpoint endpoint)
    {
        var methods = string.Join(',', endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []);
        var name = endpoint is RouteEndpoint route
            ? route.RoutePattern.RawText
            : endpoint.DisplayName;
        return $"{methods} {name ?? "<unnamed>"}";
    }

    [Fact]
    public async Task Deve_retornar_401_quando_api_key_estiver_ausente()
    {
        var auditSink = _factory.Services.GetRequiredService<TestAuthorizationAuditSink>();
        auditSink.Clear();
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("missing_api_key", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(auditSink.Requests, request => request.Reason == "missing_api_key");
    }

    [Fact]
    public async Task Deve_retornar_400_quando_payload_mcp_for_invalido()
    {
        var apiKey = await RegisterClientAsync(canWrite: true);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Mcp-Api-Key", apiKey);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_mcp_request", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON-RPC 2.0", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deve_listar_tools_mcp_com_aliases_em_ingles()
    {
        var apiKey = await RegisterClientAsync(canWrite: true);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);

        var payload = """
        {
          "jsonrpc": "2.0",
          "id": "tools-1",
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

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("list_my_courses", body, StringComparison.Ordinal);
        Assert.Contains("list_my_courses", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"search\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"fetch\"", body, StringComparison.Ordinal);
        Assert.Contains("search_courses", body, StringComparison.Ordinal);
        Assert.Contains("search_courses", body, StringComparison.Ordinal);
        Assert.Contains("get_course", body, StringComparison.Ordinal);
        Assert.Contains("get_course", body, StringComparison.Ordinal);
        Assert.Contains("list_course_participants", body, StringComparison.Ordinal);
        Assert.Contains("list_course_participants", body, StringComparison.Ordinal);
        Assert.Contains("list_course_students", body, StringComparison.Ordinal);
        Assert.Contains("list_course_students", body, StringComparison.Ordinal);
        Assert.Contains("list_course_groups", body, StringComparison.Ordinal);
        Assert.Contains("list_course_groups", body, StringComparison.Ordinal);
        Assert.Contains("get_group_members", body, StringComparison.Ordinal);
        Assert.Contains("get_group_members", body, StringComparison.Ordinal);
        Assert.Contains("list_course_contents", body, StringComparison.Ordinal);
        Assert.Contains("list_course_contents", body, StringComparison.Ordinal);
        Assert.Contains("get_course_module", body, StringComparison.Ordinal);
        Assert.Contains("get_course_module", body, StringComparison.Ordinal);
        Assert.Contains("list_course_resources", body, StringComparison.Ordinal);
        Assert.Contains("list_course_resources", body, StringComparison.Ordinal);
        Assert.Contains("list_course_files", body, StringComparison.Ordinal);
        Assert.Contains("list_course_files", body, StringComparison.Ordinal);
        Assert.Contains("list_course_pages", body, StringComparison.Ordinal);
        Assert.Contains("list_course_pages", body, StringComparison.Ordinal);
        Assert.Contains("list_course_urls", body, StringComparison.Ordinal);
        Assert.Contains("list_course_urls", body, StringComparison.Ordinal);
        Assert.Contains("audit_course_structure", body, StringComparison.Ordinal);
        Assert.Contains("audit_course_structure", body, StringComparison.Ordinal);
        Assert.Contains("list_course_activities", body, StringComparison.Ordinal);
        Assert.Contains("list_course_activities", body, StringComparison.Ordinal);
        Assert.Contains("get_course_activity", body, StringComparison.Ordinal);
        Assert.Contains("get_course_activity", body, StringComparison.Ordinal);
        Assert.Contains("list_course_assignments", body, StringComparison.Ordinal);
        Assert.Contains("list_course_assignments", body, StringComparison.Ordinal);
        Assert.Contains("get_assignment", body, StringComparison.Ordinal);
        Assert.Contains("get_assignment", body, StringComparison.Ordinal);
        Assert.Contains("list_course_quizzes", body, StringComparison.Ordinal);
        Assert.Contains("list_course_quizzes", body, StringComparison.Ordinal);
        Assert.Contains("get_quiz", body, StringComparison.Ordinal);
        Assert.Contains("get_quiz", body, StringComparison.Ordinal);
        Assert.Contains("list_course_scorms", body, StringComparison.Ordinal);
        Assert.Contains("list_course_scorms", body, StringComparison.Ordinal);
        Assert.Contains("list_activity_deadlines", body, StringComparison.Ordinal);
        Assert.Contains("list_activity_deadlines", body, StringComparison.Ordinal);
        Assert.Contains("list_assignment_submissions", body, StringComparison.Ordinal);
        Assert.Contains("list_assignment_submissions", body, StringComparison.Ordinal);
        Assert.Contains("get_student_submission", body, StringComparison.Ordinal);
        Assert.Contains("get_student_submission", body, StringComparison.Ordinal);
        Assert.Contains("list_pending_submissions", body, StringComparison.Ordinal);
        Assert.Contains("list_pending_submissions", body, StringComparison.Ordinal);
        Assert.Contains("list_late_submissions", body, StringComparison.Ordinal);
        Assert.Contains("list_late_submissions", body, StringComparison.Ordinal);
        Assert.Contains("list_submissions_awaiting_grading", body, StringComparison.Ordinal);
        Assert.Contains("list_submissions_awaiting_grading", body, StringComparison.Ordinal);
        Assert.Contains("get_submission_status", body, StringComparison.Ordinal);
        Assert.Contains("get_submission_status", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartPendingGradingRunSchema_ExposesOptionalExplicitSelectionAndIdempotencyKey()
    {
        var apiKey = await RegisterClientAsync(canWrite: true);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":"selection-schema","method":"tools/list","params":{}}
                """,
                Encoding.UTF8,
                "application/json")
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var parsed = ParseMcpResponseBody(await response.Content.ReadAsStringAsync());
        var tools = parsed?["result"]?["tools"]?.AsArray();
        var startTool = Assert.Single(tools?.Where(tool =>
            string.Equals(tool?["name"]?.ToString(), "start_pending_grading_run", StringComparison.Ordinal)) ?? []);
        var schema = startTool?["inputSchema"];
        var properties = schema?["properties"]?.AsObject();
        var submissionIdsSchema = properties?["submissionIds"];

        Assert.NotNull(properties);
        Assert.True(
            string.Equals("array", submissionIdsSchema?["type"]?.ToString(), StringComparison.Ordinal),
            submissionIdsSchema?.ToJsonString());
        Assert.True(
            string.Equals("string", submissionIdsSchema?["items"]?["type"]?.ToString(), StringComparison.Ordinal),
            submissionIdsSchema?.ToJsonString());
        var idempotencyKeyType = properties["idempotencyKey"]?["type"];
        Assert.True(
            string.Equals("string", idempotencyKeyType?.ToString(), StringComparison.Ordinal) ||
            idempotencyKeyType is JsonArray idempotencyKeyTypes &&
            idempotencyKeyTypes.Any(type => string.Equals(type?.ToString(), "string", StringComparison.Ordinal)),
            idempotencyKeyType?.ToJsonString());
        var required = schema?["required"]?.AsArray().Select(value => value?.ToString()).ToArray() ?? [];
        Assert.DoesNotContain("submissionIds", required);
        Assert.DoesNotContain("idempotencyKey", required);
    }

    [Fact]
    public async Task StartPendingGradingRun_RejectsExplicitNullSubmissionIdsBeforeInvocation()
    {
        var apiKey = await RegisterClientAsync(canWrite: true);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":"explicit-null-selection","method":"tools/call","params":{"name":"start_pending_grading_run","arguments":{"submissionIds":null}}}
                """,
                Encoding.UTF8,
                "application/json")
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var parsed = ParseMcpResponseBody(await response.Content.ReadAsStringAsync());
        var result = parsed?["result"];

        Assert.True(result?["isError"]?.GetValue<bool>());
        Assert.Contains("omita o campo", result?.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileB_ShouldHaveSameToolsAsProfileA()
    {
        var toolsA = await GetToolsListAsync(_factory, "Full");
        var toolsB = await GetToolsListAsync(_factory, "FullWithCoursesSkill");

        Assert.Equal(toolsA.Count, toolsB.Count);
        Assert.Equal(toolsA.OrderBy(name => name), toolsB.OrderBy(name => name));
    }

    [Fact]
    public async Task ProfileC_ShouldHideCourseWrapperToolsAndRetainStructuralAndUnrelatedTools()
    {
        var toolsFull = await GetToolsListAsync(_factory, "Full");
        var toolsC = await GetToolsListAsync(_factory, "SkillCoursesOptimized");

        Assert.True(toolsC.Count < toolsFull.Count);
        Assert.DoesNotContain("list_my_courses", toolsC);
        Assert.DoesNotContain("get_course", toolsC);
        Assert.Contains("search", toolsC);
        Assert.Contains("audit_course_structure", toolsC);
        Assert.Contains("start_pending_grading_run", toolsC);
    }

    [Theory]
    [InlineData("list_my_courses", """{"limite":5,"pagina":1,"moodleAlias":"goias"}""")]
    [InlineData("search_courses", """{"termo":"32786","limite":5,"moodleAlias":"goias"}""")]
    [InlineData("get_course", """{"courseId":"32786","moodleAlias":"goias"}""")]
    [InlineData("list_course_contents", """{"courseId":"32786","moodleAlias":"goias"}""")]
    public async Task ToolsCall_DeveConverterFalhaDeConexaoEmErroEstruturado(
        string toolName,
        string argumentsJson)
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMoodleUserResolver>();
                services.AddScoped<IMoodleUserResolver, ConnectionNotFoundMoodleUserResolver>();
            });
        });
        var apiKey = await RegisterClientAsync(canWrite: false, factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);
        var payload = $$"""
        {
          "jsonrpc": "2.0",
          "id": "stable-error-{{toolName}}",
          "method": "tools/call",
          "params": {
            "name": "{{toolName}}",
            "arguments": {{argumentsJson}}
          }
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
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"isError\":true", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MoodleErrorContract.ConnectionNotFound, body, StringComparison.Ordinal);
        Assert.Contains("\"auditId\":\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"auditId\":null", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INVALID_ARGUMENT", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MoodleErrorContract.NetworkError)]
    [InlineData(MoodleErrorContract.RequestTimeout)]
    [InlineData(MoodleErrorContract.PermissionDenied)]
    [InlineData(MoodleErrorContract.ValidationError)]
    public async Task ToolsCall_PreservesCanonicalReadErrorCategoriesAtMcpBoundary(string expectedErrorCode)
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMoodleUserResolver>();
                services.AddScoped<IMoodleUserResolver>(_ => new ConfiguredMoodleUserResolver(expectedErrorCode));
            });
        });
        var apiKey = await RegisterClientAsync(canWrite: false, factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);
        var calls = new (string Name, string Arguments)[]
        {
            ("list_my_courses", """{"limite":1,"pagina":1,"moodleAlias":"default"}"""),
            ("search_courses", """{"termo":"synthetic","limite":1,"moodleAlias":"default"}"""),
            ("get_course", """{"courseId":"course-1","moodleAlias":"default"}"""),
            ("list_course_contents", """{"courseId":"course-1","moodleAlias":"default"}"""),
        };

        foreach (var (toolName, arguments) in calls)
        {
            var payload = $$"""
            {
              "jsonrpc": "2.0",
              "id": "read-error-{{toolName}}",
              "method": "tools/call",
              "params": { "name": "{{toolName}}", "arguments": {{arguments}} }
            }
            """;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(sessionId)) request.Headers.Add("Mcp-Session-Id", sessionId);

            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();

            Assert.Contains("\"isError\":true", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expectedErrorCode, body, StringComparison.Ordinal);
            Assert.DoesNotContain("INVALID_ARGUMENT", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Synthetic read failure", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Deve_aceitar_api_key_quando_jwt_e_api_key_estiverem_habilitados()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["McpServerSecurity:RequireJwt"] = "true",
                    ["McpServerSecurity:RequireApiKey"] = "true",
                    ["OAuth:Issuer"] = JwtIssuer,
                    ["OAuth:Audience"] = JwtAudience
                });
            });
        });

        var apiKey = await RegisterClientAsync(canWrite: true, factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);

        Assert.False(string.IsNullOrWhiteSpace(sessionId));
    }

    [Fact]
    public async Task Deve_aceitar_jwt_valido_mesmo_com_api_key_invalida_quando_ambos_estiverem_habilitados()
    {
        var factory = BuildJwtFactory(requireApiKey: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateJwt(connectorClientId: "jwt-client"));
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", "api-key-invalida");
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);

        Assert.False(string.IsNullOrWhiteSpace(sessionId));
    }

    [Fact]
    public async Task Deve_aceitar_jwt_valido_com_vinculo_moodle()
    {
        var factory = BuildJwtFactory(requireApiKey: false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateJwt(connectorClientId: "jwt-client"));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);

        Assert.False(string.IsNullOrWhiteSpace(sessionId));
    }

    [Fact]
    public async Task Deve_usar_a_conexao_e_os_scopes_do_token_sem_exigir_grupo_de_acesso()
    {
        var factory = BuildJwtFactory(requireApiKey: false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            CreateJwt("jwt-client", deniedPlatformPermission: "tool.courses.view"));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);

        var payload = """
        {
          "jsonrpc": "2.0",
          "id": "token-authority-1",
          "method": "tools/call",
          "params": {
            "name": "list_my_courses",
            "arguments": { "limite": 1, "pagina": 1, "moodleAlias": "default" }
          }
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
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("platform_permission_denied", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("moodle_connection_not_linked", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deve_rejeitar_jwt_valido_sem_vinculo_moodle()
    {
        var factory = BuildJwtFactory(requireApiKey: false);
        var auditSink = factory.Services.GetRequiredService<TestAuthorizationAuditSink>();
        auditSink.Clear();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateJwt(connectorClientId: null, includeEmail: false));

        var response = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("moodle_connection_not_linked", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(auditSink.Requests, request => request.Reason == "moodle_connection_not_linked");
    }

    [Fact]
    public async Task Deve_rejeitar_jwt_invalido_quando_jwt_for_obrigatorio()
    {
        var factory = BuildJwtFactory(requireApiKey: false);
        var auditSink = factory.Services.GetRequiredService<TestAuthorizationAuditSink>();
        auditSink.Clear();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "token-invalido");

        var response = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("missing_or_invalid_jwt", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(auditSink.Requests, request => request.Reason == "missing_or_invalid_jwt");
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("resource_metadata=", challenge.Parameter, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deve_rejeitar_api_key_invalida_quando_api_key_for_obrigatoria()
    {
        var auditSink = _factory.Services.GetRequiredService<TestAuthorizationAuditSink>();
        auditSink.Clear();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", "api-key-invalida");

        var response = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_api_key", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(auditSink.Requests, request => request.Reason == "invalid_api_key");
    }

    [Fact]
    public async Task Deve_permitir_descoberta_de_tools_sem_token()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["McpServerSecurity:RequireJwt"] = "true",
                    ["McpServerSecurity:RequireApiKey"] = "false",
                    ["OAuth:Issuer"] = JwtIssuer,
                    ["OAuth:Audience"] = JwtAudience,
                    ["OAuth:ScopeName"] = "moodle-tests-audience"
                });
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);

        var payload = """
        {
          "jsonrpc": "2.0",
          "id": "tools-public-1",
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

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("list_my_courses", body, StringComparison.Ordinal);
        Assert.Contains("securitySchemes", body, StringComparison.Ordinal);
        Assert.Contains("oauth2", body, StringComparison.Ordinal);
        Assert.Contains("moodle-tests-audience", body, StringComparison.Ordinal);
        Assert.DoesNotContain("moodle-mcp-audience", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deve_permitir_descoberta_quando_cliente_envia_bearer_vazio()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["McpServerSecurity:RequireJwt"] = "true",
                    ["McpServerSecurity:RequireApiKey"] = "false",
                    ["OAuth:Issuer"] = JwtIssuer,
                    ["OAuth:Audience"] = JwtAudience
                });
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var response = await client.PostAsync(
            "/mcp",
            new StringContent(BuildInitializePayload("empty-bearer-init"), Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.Contains("Mcp-Session-Id"));
    }

    [Fact]
    public async Task Deve_anunciar_tools_e_scopes_necessarios_com_token_basico()
    {
        var factory = BuildJwtFactory(requireApiKey: false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            CreateJwt(
                connectorClientId: "jwt-client",
                scopes: "openid profile email offline_access moodle-mcp-audience"));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""
            {
              "jsonrpc": "2.0",
              "id": "tools-basic-token",
              "method": "tools/list",
              "params": {}
            }
            """, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(sessionId)) request.Headers.Add("Mcp-Session-Id", sessionId);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var tools = ParseMcpResponseBody(await response.Content.ReadAsStringAsync())?["result"]?["tools"]?.AsArray();
        Assert.NotNull(tools);
        foreach (var (name, requiredScope) in new[]
        {
            ("moodle_list_connections", "moodle.read"),
            ("list_my_courses", "moodle.read.courses"),
            ("list_course_activities", "moodle.read.activities"),
            ("moodle_prepare_write", "moodle.write"),
            ("start_pending_grading_run", "moodle.write.assignments.grade")
        })
        {
            var tool = Assert.Single(tools, tool => tool?["name"]?.GetValue<string>() == name);
            var scheme = Assert.Single(tool?["_meta"]?["securitySchemes"]?.AsArray() ?? []);
            Assert.Equal("oauth2", scheme?["type"]?.GetValue<string>());
            var scopes = scheme?["scopes"]?.AsArray().Select(scope => scope!.GetValue<string>()).ToArray() ?? [];
            Assert.Contains("moodle-mcp-audience", scopes);
            Assert.Contains(requiredScope, scopes);
        }
    }

    [Theory]
    [InlineData("moodle_list_connections", "moodle.read")]
    [InlineData("list_my_courses", "moodle.read.courses")]
    public async Task Deve_exigir_reautorizacao_para_chamar_tool_descobrivel_com_token_basico(
        string toolName, string requiredScope)
    {
        using var factory = BuildJwtFactory(requireApiKey: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            CreateJwt(connectorClientId: "jwt-client", scopes: "openid profile email offline_access moodle-mcp-audience"));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "basic-token-call",
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = toolName,
                    ["arguments"] = new JsonObject()
                }
            }.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(sessionId)) request.Headers.Add("Mcp-Session-Id", sessionId);

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = ParseMcpResponseBody(await response.Content.ReadAsStringAsync())?["result"];
        Assert.True(result?["isError"]?.GetValue<bool>());
        var challenge = Assert.Single(result?["_meta"]?["mcp/www_authenticate"]?.AsArray() ?? [])!.GetValue<string>();
        Assert.Contains("insufficient_scope", challenge, StringComparison.Ordinal);
        Assert.Contains(requiredScope, challenge, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deve_retornar_desafio_de_reautorizacao_quando_tool_exigir_scope_ausente()
    {
        var factory = BuildJwtFactory(requireApiKey: false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            CreateJwt(
                connectorClientId: "jwt-client",
                scopes: "openid profile email offline_access moodle-mcp-audience moodle.read moodle.read.assignments moodle.read.submissions"));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var sessionId = await InitializeMcpSessionAsync(client);
        await NotifyInitializedAsync(client, sessionId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""
            {
              "jsonrpc": "2.0",
              "id": "scope-challenge",
              "method": "tools/call",
              "params": {
                "name": "start_pending_grading_run",
                "arguments": {}
              }
            }
            """, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(sessionId)) request.Headers.Add("Mcp-Session-Id", sessionId);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains(MoodleErrorContract.PermissionDenied, body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mcp/www_authenticate", body, StringComparison.Ordinal);
        Assert.Contains("insufficient_scope", body, StringComparison.Ordinal);
        Assert.Contains("moodle.write.assignments.grade", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deve_aplicar_rate_limit_mcp_por_conector()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:McpPermitLimit"] = "1",
                    ["RateLimiting:WindowSeconds"] = "60"
                });
            });
        });

        var apiKey = await RegisterClientAsync(canWrite: true, factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var firstResponse = await client.PostAsync(
            "/mcp",
            new StringContent(BuildInitializePayload("rate-limit-1"), Encoding.UTF8, "application/json"));
        var secondResponse = await client.PostAsync(
            "/mcp",
            new StringContent(BuildInitializePayload("rate-limit-2"), Encoding.UTF8, "application/json"));

        firstResponse.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
    }

    [Fact]
    public async Task Deve_exercitar_limite_legado_app_auth_de_12_requisicoes_na_janela()
    {
        var metricsSink = new CapturingPlatformRequestMetricsSink();
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:WindowSeconds"] = "60",
                    ["RateLimiting:AppAuthPermitLimit"] = "12",
                    ["Testing:AppAuthPermitLimit"] = "12"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPlatformRequestMetricsSink>();
                services.AddSingleton<IPlatformRequestMetricsSink>(metricsSink);
            });
        });
        var client = factory.CreateClient();

        for (var requestNumber = 0; requestNumber < 12; requestNumber++)
        {
            var admitted = await client.GetAsync("/api/csrf");
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        var rejected = await client.GetAsync("/api/csrf");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("X-Correlation-ID"));
        Assert.NotNull(rejected.Headers.RetryAfter);
        var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("connector_rate_limited", error.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("connector", error.GetProperty("error").GetProperty("origin").GetString());
        Assert.Equal("session_control", error.GetProperty("error").GetProperty("family").GetString());
        Assert.Contains(metricsSink.Metrics, metric =>
            metric.StatusCode == StatusCodes.Status429TooManyRequests &&
            metric.FailureKind == "connector_rate_limited");
    }

    [Fact]
    public async Task Catalog_observation_pressure_does_not_block_the_login_flow()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Admission:IngressMode"] = "Observe",
                    ["Admission:LocalReadPermitLimitPerMinute"] = "1",
                    ["Admission:LocalReadBurst"] = "1",
                    ["RateLimiting:WindowSeconds"] = "60",
                    ["RateLimiting:AppAuthPermitLimit"] = "2",
                    ["Testing:AppAuthPermitLimit"] = "2"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMoodleCoursesGateway>();
                services.AddSingleton<IMoodleCoursesGateway, SyntheticMoodleCoursesGateway>();
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var observer = factory.Services.GetRequiredService<IngressAdmissionObserver>();
        var candidateDecisions = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var meterListener = new System.Diagnostics.Metrics.MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, observer.InstrumentationMeter) &&
                instrument.Name == "admission_candidate_decisions")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? family = null;
            string? decision = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "family") family = tag.Value?.ToString();
                if (tag.Key == "decision") decision = tag.Value?.ToString();
            }

            if (family == "course_catalog" && decision is not null) candidateDecisions.Add(decision);
        });
        meterListener.Start();

        var email = $"catalog-pressure-{Guid.NewGuid():N}@example.invalid";
        const string password = "synthetic-only-password";
        await RefreshCsrfTokenAsync(client);
        using var registration = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Synthetic Catalog Owner",
            email,
            password
        });
        Assert.True(registration.IsSuccessStatusCode, await registration.Content.ReadAsStringAsync());

        var firstCatalogRead = await client.GetAsync("/api/schools?request=1");
        var secondCatalogRead = await client.GetAsync("/api/schools?request=2");
        var thirdCatalogRead = await client.GetAsync("/api/schools?request=3");
        Assert.True(firstCatalogRead.IsSuccessStatusCode, await firstCatalogRead.Content.ReadAsStringAsync());
        Assert.True(secondCatalogRead.IsSuccessStatusCode, await secondCatalogRead.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.TooManyRequests, thirdCatalogRead.StatusCode);
        var limitedCatalog = await thirdCatalogRead.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("connector_rate_limited", limitedCatalog.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("course_catalog", limitedCatalog.GetProperty("error").GetProperty("family").GetString());
        Assert.Contains("admitted", candidateDecisions);
        Assert.Contains("would_reject", candidateDecisions);

        await RefreshCsrfTokenAsync(client);
        using var login = await client.PostAsJsonAsync("/api/account/login", new
        {
            email,
            password
        });

        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Registration_rate_limit_rejection_does_not_reveal_or_create_an_account()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:WindowSeconds"] = "60",
                    ["RateLimiting:AppAuthPermitLimit"] = "2",
                    ["Testing:AppAuthPermitLimit"] = "2"
                });
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var existingEmail = $"existing-{Guid.NewGuid():N}@example.invalid";
        var unknownEmail = $"unknown-{Guid.NewGuid():N}@example.invalid";
        const string password = "synthetic-only-password";

        await RefreshCsrfTokenAsync(client);
        using var registration = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Synthetic Existing Owner",
            email = existingEmail,
            password
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

        using var invalidRegistration = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "",
            email = "",
            password = ""
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidRegistration.StatusCode);

        using var duplicateRegistration = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Synthetic Duplicate Owner",
            email = existingEmail,
            password
        });
        using var unknownRegistration = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Synthetic Unknown Owner",
            email = unknownEmail,
            password
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, duplicateRegistration.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, unknownRegistration.StatusCode);
        var duplicateBody = await duplicateRegistration.Content.ReadFromJsonAsync<JsonElement>();
        var unknownBody = await unknownRegistration.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var property in new[] { "code", "origin", "family", "message" })
        {
            Assert.Equal(
                duplicateBody.GetProperty("error").GetProperty(property).GetString(),
                unknownBody.GetProperty("error").GetProperty(property).GetString());
        }
        Assert.Equal("connector_rate_limited", duplicateBody.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("authentication", duplicateBody.GetProperty("error").GetProperty("family").GetString());

        await RefreshCsrfTokenAsync(client);
        using var loginForRateLimitedAddress = await client.PostAsJsonAsync("/api/account/login", new
        {
            email = unknownEmail,
            password
        });
        Assert.Equal(HttpStatusCode.Unauthorized, loginForRateLimitedAddress.StatusCode);
    }

    [Fact]
    public async Task Public_login_quota_is_shared_by_normalized_account_across_client_ips()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:WindowSeconds"] = "60",
                    ["RateLimiting:AppAuthPermitLimit"] = "100",
                    ["RateLimiting:AccountAuthPermitLimit"] = "2",
                    ["RateLimiting:AccountAuthMaximumTrackedPartitions"] = "100",
                    ["Testing:AppAuthPermitLimit"] = "100"
                });
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
        var knownEmail = $"known-{Guid.NewGuid():N}@example.invalid";
        var unknownEmail = $"unknown-{Guid.NewGuid():N}@example.invalid";
        const string password = "synthetic-only-password";

        await RefreshCsrfTokenAsync(client);
        using var registration = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Synthetic Known Owner",
            email = knownEmail,
            password
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        await RefreshCsrfTokenAsync(client);

        var knownFirst = await PostLoginWithForwardedAddressAsync(client, knownEmail, "incorrect-password", "198.51.100.11");
        var knownSecond = await PostLegacyLoginWithForwardedAddressAsync(client, $"  {knownEmail.ToUpperInvariant()}  ", "incorrect-password", "198.51.100.12");
        var knownRejected = await PostLoginWithForwardedAddressAsync(client, knownEmail.ToUpperInvariant(), "incorrect-password", "198.51.100.13");
        Assert.Equal(HttpStatusCode.Unauthorized, knownFirst.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, knownSecond.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, knownRejected.StatusCode);
        Assert.NotNull(knownRejected.Headers.RetryAfter);

        var unknownFirst = await PostLoginWithForwardedAddressAsync(client, unknownEmail, password, "198.51.100.21");
        var unknownSecond = await PostLoginWithForwardedAddressAsync(client, unknownEmail.ToUpperInvariant(), password, "198.51.100.22");
        var unknownRejected = await PostLoginWithForwardedAddressAsync(client, unknownEmail, password, "198.51.100.23");
        Assert.Equal(HttpStatusCode.Unauthorized, unknownFirst.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownSecond.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, unknownRejected.StatusCode);

        var knownError = await knownRejected.Content.ReadAsStringAsync();
        var unknownError = await unknownRejected.Content.ReadAsStringAsync();
        Assert.Equal(knownError, unknownError);
        Assert.DoesNotContain(knownEmail, knownError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(unknownEmail, unknownError, StringComparison.OrdinalIgnoreCase);
        using var error = JsonDocument.Parse(knownError);
        Assert.Equal("connector_rate_limited", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("authentication", error.RootElement.GetProperty("error").GetProperty("family").GetString());

        var htmlRejected = await PostLegacyLoginWithForwardedAddressAsync(client, knownEmail, "incorrect-password", "198.51.100.14");
        Assert.Equal(HttpStatusCode.TooManyRequests, htmlRejected.StatusCode);
        Assert.Equal("text/html", htmlRejected.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(htmlRejected.Headers.RetryAfter);
    }

    [Fact]
    public async Task Registration_account_quota_rejects_before_creating_the_account()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:WindowSeconds"] = "60",
                    ["RateLimiting:AppAuthPermitLimit"] = "100",
                    ["RateLimiting:AccountAuthPermitLimit"] = "2",
                    ["RateLimiting:AccountAuthMaximumTrackedPartitions"] = "100",
                    ["Testing:AppAuthPermitLimit"] = "100"
                });
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var email = $"registration-quota-{Guid.NewGuid():N}@example.invalid";
        const string password = "synthetic-only-password";
        var limiter = factory.Services.GetRequiredService<PublicAccountRateLimiter>();
        Assert.True(limiter.TryAcquire(PublicAccountOperation.Registration, email, out _));
        Assert.True(limiter.TryAcquire(PublicAccountOperation.Registration, email, out _));

        await RefreshCsrfTokenAsync(client);
        using var rejectedRegistration = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Synthetic Limited Owner",
            email,
            password
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedRegistration.StatusCode);
        Assert.NotNull(rejectedRegistration.Headers.RetryAfter);
        var error = await rejectedRegistration.Content.ReadAsStringAsync();
        Assert.DoesNotContain(email, error, StringComparison.OrdinalIgnoreCase);

        await RefreshCsrfTokenAsync(client);
        using var login = await client.PostAsJsonAsync("/api/account/login", new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Sensitive_connection_validation_ids_share_one_legacy_rate_limit_partition()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:WindowSeconds"] = "60",
                    ["RateLimiting:AppAuthPermitLimit"] = "2",
                    ["Testing:AppAuthPermitLimit"] = "2"
                });
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var email = $"connection-limit-{Guid.NewGuid():N}@example.invalid";
        const string password = "synthetic-only-password";

        await RefreshCsrfTokenAsync(client);
        using var registration = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Synthetic Connection Owner",
            email,
            password
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        await RefreshCsrfTokenAsync(client);

        using var connection = await client.PostAsJsonAsync("/api/account/connect-moodle", new
        {
            moodleAlias = "synthetic",
            moodleBaseUrl = "https://moodle.tests",
            moodleUsername = "synthetic-user",
            moodlePassword = "synthetic-password",
            isDefault = true,
            canWrite = false
        });
        Assert.Equal(HttpStatusCode.OK, connection.StatusCode);

        var profile = await client.GetFromJsonAsync<JsonElement>("/api/account/me");
        var connectionId = Assert.Single(profile.GetProperty("moodleConnections").EnumerateArray())
            .GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionId));

        using var firstValidation = await client.PostAsync($"/api/connections/{connectionId}/validate", null);
        using var secondValidation = await client.PostAsync($"/api/connections/{connectionId}/validate", null);
        using var rateLimitedValidation = await client.PostAsync($"/api/connections/{connectionId}/validate", null);
        using var differentIdValidation = await client.PostAsync($"/api/connections/{Guid.NewGuid():D}/validate", null);

        Assert.Equal(HttpStatusCode.OK, firstValidation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondValidation.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimitedValidation.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, differentIdValidation.StatusCode);
        var limitedResponse = await rateLimitedValidation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("connector_rate_limited", limitedResponse.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("moodle_connections", limitedResponse.GetProperty("error").GetProperty("family").GetString());
    }

    [Fact]
    public async Task Password_and_api_key_rejections_do_not_apply_the_limited_mutations()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:WindowSeconds"] = "60",
                    ["RateLimiting:AppAuthPermitLimit"] = "2",
                    ["Testing:AppAuthPermitLimit"] = "2"
                });
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var email = $"sensitive-limit-{Guid.NewGuid():N}@example.invalid";
        const string password = "synthetic-only-password";
        await RefreshCsrfTokenAsync(client);

        using var registration = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Synthetic Sensitive Owner",
            email,
            password
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        await RefreshCsrfTokenAsync(client);

        using var connection = await client.PostAsJsonAsync("/api/account/connect-moodle", new
        {
            moodleAlias = "synthetic",
            moodleBaseUrl = "https://moodle.tests",
            moodleUsername = "synthetic-user",
            moodlePassword = "synthetic-password",
            isDefault = true,
            canWrite = false
        });
        Assert.Equal(HttpStatusCode.OK, connection.StatusCode);

        using var firstBadPassword = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = "incorrect-synthetic-password",
            newPassword = "replacement-synthetic-password"
        });
        using var secondBadPassword = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = "incorrect-synthetic-password",
            newPassword = "replacement-synthetic-password"
        });
        using var limitedPasswordChange = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = password,
            newPassword = "replacement-synthetic-password"
        });
        Assert.Equal(HttpStatusCode.BadRequest, firstBadPassword.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, secondBadPassword.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedPasswordChange.StatusCode);

        using var oldPasswordLogin = await client.PostAsJsonAsync("/api/account/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, oldPasswordLogin.StatusCode);

        using var firstRotation = await client.PostAsync("/api/account/api-key/rotate", null);
        using var secondRotation = await client.PostAsync("/api/account/api-key/rotate", null);
        using var limitedRotation = await client.PostAsync("/api/account/api-key/rotate", null);
        Assert.Equal(HttpStatusCode.OK, firstRotation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondRotation.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedRotation.StatusCode);

        var secondRotationBody = await secondRotation.Content.ReadFromJsonAsync<JsonElement>();
        var currentProfile = await client.GetFromJsonAsync<JsonElement>("/api/account/me");
        Assert.Equal(
            secondRotationBody.GetProperty("apiKey").GetString(),
            currentProfile.GetProperty("apiKey").GetString());
    }

    [Fact]
    public void Authentication_and_sensitive_account_routes_keep_their_explicit_rate_limit_policies()
    {
        using var client = _factory.CreateClient();
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToArray();
        var expectedRoutes = new[]
        {
            (Method: "POST", Path: "/api/account/register", TrafficClass: AdmissionTrafficClass.PublicAuthentication, Family: "authentication", Policy: "app-auth"),
            (Method: "POST", Path: "/api/account/login", TrafficClass: AdmissionTrafficClass.PublicAuthentication, Family: "authentication", Policy: "app-auth"),
            (Method: "POST", Path: "/api/account/password", TrafficClass: AdmissionTrafficClass.SensitiveOperation, Family: "account_security", Policy: "app-auth"),
            (Method: "POST", Path: "/api/account/api-key/rotate", TrafficClass: AdmissionTrafficClass.SensitiveOperation, Family: "account_security", Policy: "app-auth"),
            (Method: "POST", Path: "/api/connections/{id}/validate", TrafficClass: AdmissionTrafficClass.SensitiveOperation, Family: "moodle_connections", Policy: "app-auth"),
            (Method: "POST", Path: "/admin/connector-clients/register", TrafficClass: AdmissionTrafficClass.Administration, Family: "admin_api", Policy: "admin-api")
        };

        foreach (var route in expectedRoutes)
        {
            var endpoint = Assert.Single(endpoints, candidate =>
                string.Equals(candidate.RoutePattern.RawText, route.Path, StringComparison.Ordinal) &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(route.Method) == true);
            var classification = AdmissionEndpointClassifier.Classify(endpoint);
            var rateLimit = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();

            Assert.NotNull(classification);
            Assert.Equal(route.TrafficClass, classification!.TrafficClass);
            Assert.Equal(route.Family, classification.Family);
            Assert.NotNull(rateLimit);
            Assert.Equal(route.Policy, rateLimit!.PolicyName);
        }
    }

    [Theory]
    [InlineData("IngressMode")]
    [InlineData("MoodleEgressMode")]
    public void Deve_recusar_enforce_antes_de_a_politica_ser_implementada(string mode)
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"Admission:{mode}"] = "Enforce"
                });
            });
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("must be Legacy or Observe", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deve_expor_metadata_oauth_para_descoberta_do_chatgpt()
    {
        var originalAppDomain = Environment.GetEnvironmentVariable("APP_DOMAIN");
        Environment.SetEnvironmentVariable("APP_DOMAIN", null);

        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["McpServerSecurity:RequireJwt"] = "true",
                    ["McpServerSecurity:RequireApiKey"] = "false",
                    ["OAuth:Issuer"] = JwtIssuer,
                    ["OAuth:Audience"] = JwtAudience
                });
            });
        });

        try
        {
            var client = factory.CreateClient();

            var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);

            Assert.Equal("http://localhost/mcp", json.RootElement.GetProperty("resource").GetString());
            Assert.Equal(
                $"{JwtIssuer}/",
                json.RootElement.GetProperty("authorization_servers")[0].GetString());
            Assert.Contains(
                json.RootElement.GetProperty("scopes_supported").EnumerateArray(),
                scope => scope.GetString() == "openid");
            Assert.Contains(
                json.RootElement.GetProperty("scopes_supported").EnumerateArray(),
                scope => scope.GetString() == "moodle-mcp-audience");
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_DOMAIN", originalAppDomain);
        }
    }

    [Fact]
    public async Task Deve_informar_metadata_oauth_no_desafio_quando_jwt_estiver_ausente()
    {
        var originalAppDomain = Environment.GetEnvironmentVariable("APP_DOMAIN");
        Environment.SetEnvironmentVariable("APP_DOMAIN", null);

        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["McpServerSecurity:RequireJwt"] = "true",
                    ["McpServerSecurity:RequireApiKey"] = "false",
                    ["OAuth:Issuer"] = JwtIssuer,
                    ["OAuth:Audience"] = JwtAudience
                });
            });
        });

        try
        {
            var client = factory.CreateClient();

            var response = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var challenge = Assert.Single(response.Headers.WwwAuthenticate);
            Assert.Equal("Bearer", challenge.Scheme);
            Assert.Contains(
                "resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"",
                challenge.Parameter);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_DOMAIN", originalAppDomain);
        }
    }

    [Fact]
    public async Task Deve_retornar_desafio_oauth_mcp_quando_tool_for_chamada_sem_jwt()
    {
        var originalAppDomain = Environment.GetEnvironmentVariable("APP_DOMAIN");
        Environment.SetEnvironmentVariable("APP_DOMAIN", null);

        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["McpServerSecurity:RequireJwt"] = "true",
                    ["McpServerSecurity:RequireApiKey"] = "false",
                    ["OAuth:Issuer"] = JwtIssuer,
                    ["OAuth:Audience"] = JwtAudience
                });
            });
        });

        try
        {
            var client = factory.CreateClient();
            var payload = """
            {
              "jsonrpc": "2.0",
              "id": "call-auth-1",
              "method": "tools/call",
              "params": {
                "name": "list_my_courses",
                "arguments": {}
              }
            }
            """;

            var response = await client.PostAsync("/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("\"isError\":true", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mcp/www_authenticate", body, StringComparison.Ordinal);
            Assert.Contains("resource_metadata=\\\"http://localhost/.well-known/oauth-protected-resource/mcp\\\"", body);
            Assert.Contains("scope=\\\"openid", body);
            Assert.Contains("moodle-mcp-audience", body);
            Assert.DoesNotContain("moodle.write.assignments.grade", body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_DOMAIN", originalAppDomain);
        }
    }

    [Fact]
    public async Task Deve_expor_metadata_do_authorization_server_e_discovery_openid()
    {
        var client = _factory.CreateClient();

        var authorizationMetadataResponse = await client.GetAsync("/.well-known/oauth-authorization-server");
        authorizationMetadataResponse.EnsureSuccessStatusCode();
        var authorizationMetadataBody = await authorizationMetadataResponse.Content.ReadAsStringAsync();
        using var authorizationMetadata = JsonDocument.Parse(authorizationMetadataBody);

        var issuer = authorizationMetadata.RootElement.GetProperty("issuer").GetString();
        Assert.False(string.IsNullOrWhiteSpace(issuer));
        Assert.Equal("http://localhost/authorize", authorizationMetadata.RootElement.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("http://localhost/token", authorizationMetadata.RootElement.GetProperty("token_endpoint").GetString());
        Assert.Equal("http://localhost/.well-known/jwks", authorizationMetadata.RootElement.GetProperty("jwks_uri").GetString());
        Assert.Contains(
            authorizationMetadata.RootElement.GetProperty("scopes_supported").EnumerateArray(),
            scope => scope.GetString() == "offline_access");

        var openIdDiscoveryResponse = await client.GetAsync("/.well-known/openid-configuration");
        openIdDiscoveryResponse.EnsureSuccessStatusCode();
        var openIdDiscoveryBody = await openIdDiscoveryResponse.Content.ReadAsStringAsync();
        using var openIdDiscovery = JsonDocument.Parse(openIdDiscoveryBody);

        Assert.Equal(
            issuer,
            openIdDiscovery.RootElement.GetProperty("issuer").GetString());
        Assert.Contains(
            openIdDiscovery.RootElement.GetProperty("code_challenge_methods_supported").EnumerateArray(),
            method => method.GetString() == "S256");

        var jwksResponse = await client.GetAsync("/.well-known/jwks");
        jwksResponse.EnsureSuccessStatusCode();
        var jwksBody = await jwksResponse.Content.ReadAsStringAsync();
        using var jwks = JsonDocument.Parse(jwksBody);

        Assert.NotEmpty(jwks.RootElement.GetProperty("keys").EnumerateArray());
    }

    [Fact]
    public async Task Deve_cadastrar_logar_e_conectar_moodle_com_cookie_local()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });
        var email = $"professor-{Guid.NewGuid():N}@example.com";
        var password = "senha123";
        await RefreshCsrfTokenAsync(client);

        var weakPasswordResponse = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Professor Teste",
            email,
            password = "curta"
        });

        Assert.Equal(HttpStatusCode.BadRequest, weakPasswordResponse.StatusCode);

        var registerResponse = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Professor Teste",
            email,
            password
        });

        registerResponse.EnsureSuccessStatusCode();
        await RefreshCsrfTokenAsync(client);

        var firstProfile = await client.GetFromJsonAsync<JsonElement>("/api/account/me");
        Assert.Equal(email, firstProfile.GetProperty("email").GetString());
        Assert.False(firstProfile.GetProperty("hasMoodleConnected").GetBoolean());

        var teams = await client.GetFromJsonAsync<JsonElement>("/api/teams");
        Assert.True(teams.GetProperty("ok").GetBoolean());

        var permissionCatalog = await client.GetFromJsonAsync<JsonElement>("/api/permission-catalog");
        Assert.True(permissionCatalog.GetProperty("ok").GetBoolean());
        Assert.Contains("tool.connections.manage", permissionCatalog.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()));

        var permissionGroups = await client.GetFromJsonAsync<JsonElement>("/api/permission-groups");
        Assert.True(permissionGroups.GetProperty("ok").GetBoolean());

        var logoutResponse = await client.PostAsync("/auth/logout", null);
        logoutResponse.EnsureSuccessStatusCode();
        await RefreshCsrfTokenAsync(client);

        var loginResponse = await client.PostAsJsonAsync("/api/account/login", new
        {
            email,
            password
        });

        loginResponse.EnsureSuccessStatusCode();
        await RefreshCsrfTokenAsync(client);

        var connectResponse = await client.PostAsJsonAsync("/api/account/connect-moodle", new
        {
            moodleAlias = "Goias",
            moodleBaseUrl = "https://moodle.tests/ead?debug=true#fragment",
            moodleUsername = "professor.teste",
            moodlePassword = " senha-com-espaco-final ",
            isDefault = true,
            canWrite = true
        });

        connectResponse.EnsureSuccessStatusCode();
        var connectBody = await connectResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(connectBody.GetProperty("apiKey").GetString()));

        var connectedProfile = await client.GetFromJsonAsync<JsonElement>("/api/account/me");
        Assert.True(connectedProfile.GetProperty("hasMoodleConnected").GetBoolean());
        Assert.True(connectedProfile.GetProperty("hasApiKey").GetBoolean());
        var connection = Assert.Single(connectedProfile.GetProperty("moodleConnections").EnumerateArray());
        Assert.Equal("goias", connection.GetProperty("alias").GetString());
        Assert.Equal("https://moodle.tests/ead", connection.GetProperty("baseUrl").GetString());
        Assert.True(connection.GetProperty("canWrite").GetBoolean());

        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.True(session.GetProperty("data").GetProperty("authenticated").GetBoolean());
        Assert.Equal("Professor Teste", session.GetProperty("data").GetProperty("user").GetProperty("name").GetString());

        var connections = await client.GetFromJsonAsync<JsonElement>("/api/connections");
        var portalConnection = Assert.Single(connections.GetProperty("data").EnumerateArray());
        Assert.Equal("goias", portalConnection.GetProperty("connectionRef").GetString());
        Assert.Contains("write", portalConnection.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Login_get_preserva_o_retorno_oauth_local_no_formulario_unico()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        const string oauthReturnUrl = "/authorize?client_id=chatgpt&state=abc";

        var response = await client.GetAsync($"/auth/login?returnUrl={Uri.EscapeDataString(oauthReturnUrl)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/?returnUrl=%2Fauthorize%3Fclient_id%3Dchatgpt%26state%3Dabc",
            response.Headers.Location?.OriginalString);

        var externalResponse = await client.GetAsync("/auth/login?returnUrl=https%3A%2F%2Fattacker.example%2F");
        Assert.Equal("/", externalResponse.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_html_rejeita_return_url_externo()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
        var email = $"login-html-{Guid.NewGuid():N}@example.com";
        const string password = "senha-local-12345";
        await RefreshCsrfTokenAsync(client);

        var registerResponse = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Professor Login HTML",
            email,
            password
        });
        registerResponse.EnsureSuccessStatusCode();

        await RefreshCsrfTokenAsync(client);
        var logoutResponse = await client.PostAsync("/auth/logout", null);
        logoutResponse.EnsureSuccessStatusCode();

        var loginResponse = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["returnUrl"] = "//external.example/redirect"
        }));

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/", loginResponse.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Portal_privado_exige_sessao_cookie_e_preserva_bootstrap_publico()
    {
        var client = _factory.CreateClient();

        var privateResponse = await client.GetAsync("/api/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, privateResponse.StatusCode);
        var privateBody = await privateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("portal_session_required", privateBody.GetProperty("error").GetString());

        var gradingResponse = await client.GetAsync("/api/grading/batches");
        Assert.Equal(HttpStatusCode.Unauthorized, gradingResponse.StatusCode);

        var forumsResponse = await client.GetAsync("/api/courses/test/1/forums");
        Assert.Equal(HttpStatusCode.Unauthorized, forumsResponse.StatusCode);

        var messagesResponse = await client.GetAsync("/api/messages/conversations");
        Assert.Equal(HttpStatusCode.Unauthorized, messagesResponse.StatusCode);

        var statusResponse = await client.GetAsync("/api/status");
        statusResponse.EnsureSuccessStatusCode();

        var csrfResponse = await client.GetAsync("/api/csrf");
        csrfResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Respostas_https_encaminhadas_incluem_hsts()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal("max-age=31536000", response.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task Host_de_testes_nao_herda_o_limite_de_requisicoes_de_producao()
    {
        var client = _factory.CreateClient();

        for (var request = 0; request < 20; request++)
        {
            var response = await client.GetAsync("/api/csrf");
            response.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Rotas_legadas_do_portal_redirecionam_para_a_spa_canonica()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Features:AppV2Enabled"] = "true"
                });
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var appResponse = await client.GetAsync("/app.html");
        Assert.Equal(HttpStatusCode.Redirect, appResponse.StatusCode);
        Assert.Equal("/", appResponse.Headers.Location?.OriginalString);

        var authResponse = await client.GetAsync("/auth.html?tab=register&error=acesso negado");
        Assert.Equal(HttpStatusCode.Redirect, authResponse.StatusCode);
        Assert.Equal("/?tab=register&error=acesso%20negado", authResponse.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Token_oauth_do_mcp_nao_substitui_sessao_cookie_do_portal()
    {
        using var factory = BuildJwtFactory(requireApiKey: false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateJwt(connectorClientId: "jwt-client"));

        var response = await client.GetAsync("/api/account/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("portal_session_required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Api_administrativa_exige_chave_propria_e_nao_aceita_sessao_ou_bearer()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/admin/connector-clients/register", new
        {
            clientId = "admin-boundary-test",
            moodleAlias = "default",
            moodleBaseUrl = "https://moodle.tests",
            moodleUsername = "admin.test",
            moodlePassword = "senha-teste",
            moodleTarget = "default",
            isDefault = true,
            canWrite = false
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Deve_exigir_csrf_para_mutacoes_autenticadas_da_conta()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });
        await RefreshCsrfTokenAsync(client);

        var registerResponse = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Professor CSRF",
            email = $"csrf-{Guid.NewGuid():N}@example.com",
            password = "senha-local-12345"
        });
        registerResponse.EnsureSuccessStatusCode();
        await RefreshCsrfTokenAsync(client);

        var connectResponse = await client.PostAsJsonAsync("/api/account/connect-moodle", new
        {
            moodleAlias = "CSRF",
            moodleBaseUrl = "https://moodle.tests",
            moodleUsername = "professor.csrf",
            moodlePassword = "senha-moodle",
            isDefault = true,
            canWrite = false
        });
        connectResponse.EnsureSuccessStatusCode();

        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var missingTokenResponse = await client.PostAsync("/api/account/api-key/rotate", null);
        Assert.Equal(HttpStatusCode.BadRequest, missingTokenResponse.StatusCode);
        var missingToken = await missingTokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("csrf_invalid", missingToken.GetProperty("error").GetProperty("code").GetString());

        await RefreshCsrfTokenAsync(client);
        var rotateResponse = await client.PostAsync("/api/account/api-key/rotate", null);
        rotateResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Agenda_suspensa_retorna_404()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });
        await RefreshCsrfTokenAsync(client);

        var registerResponse = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Professor Agenda",
            email = $"agenda-{Guid.NewGuid():N}@example.com",
            password = "senha-local-12345"
        });
        registerResponse.EnsureSuccessStatusCode();
        await RefreshCsrfTokenAsync(client);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:agenda-criada
            DTSTART:20260824T120000Z
            SUMMARY:Reunião de planejamento
            END:VEVENT
            BEGIN:VEVENT
            UID:agenda-sem-inicio
            SUMMARY:Evento sem início
            END:VEVENT
            END:VCALENDAR
            """), "file", "agenda.ics");

        var response = await client.PostAsync("/api/agenda/import", form);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Tarefas_suspensas_retorna_404()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });
        await RefreshCsrfTokenAsync(client);

        var registerResponse = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Professor Tarefas",
            email = $"tasks-{Guid.NewGuid():N}@example.com",
            password = "senha-local-12345"
        });
        registerResponse.EnsureSuccessStatusCode();
        await RefreshCsrfTokenAsync(client);

        var createResponse = await client.PostAsJsonAsync("/api/tasks", new
        {
            title = "Conferir notas",
            description = "Validar os lançamentos pendentes.",
            status = "in_progress",
            priority = "high",
            actionType = "review",
            scheduleHint = "Até sexta-feira"
        });
        Assert.Equal(HttpStatusCode.NotFound, createResponse.StatusCode);
        var listResponse = await client.GetAsync("/api/tasks");
        Assert.Equal(HttpStatusCode.NotFound, listResponse.StatusCode);
    }

    private static async Task RefreshCsrfTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/csrf");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }

    private static async Task<HttpResponseMessage> PostLoginWithForwardedAddressAsync(
        HttpClient client,
        string email,
        string password,
        string forwardedAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/login")
        {
            Content = JsonContent.Create(new { email, password })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedAddress);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostLegacyLoginWithForwardedAddressAsync(
        HttpClient client,
        string email,
        string password,
        string forwardedAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = password
            })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedAddress);
        return await client.SendAsync(request);
    }

    private async Task<IReadOnlyList<string>> GetToolsListAsync(WebApplicationFactory<Program> factory, string exposureProfile)
    {
        var customFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MCP_EXPOSURE_PROFILE"] = exposureProfile,
                    ["McpServerSecurity:RequireApiKey"] = "true",
                    ["McpServerSecurity:RequireJwt"] = "false",
                    // The integration suite exercises the complete registered
                    // production surface. Production defaults remain fail-closed;
                    // this test host opts into write tools explicitly.
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
        Console.WriteLine("MCP response content type: " + response.Content.Headers.ContentType?.ToString());
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        // Avoid dumping potentially huge tool schemas to the test output; log only length for diagnostics.
        Console.WriteLine($"MCP raw body length: {body.Length}");

        var parsed = ParseMcpResponseBody(body);
        var tools = parsed?["result"]?["tools"]?.AsArray();
        if (tools == null)
        {
            return Array.Empty<string>();
        }

        var list = tools.Select(t => t? ["name"]?.ToString() ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        Console.WriteLine("MCP tools result: " + string.Join(",", list));
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

    private WebApplicationFactory<Program> BuildJwtFactory(bool requireApiKey)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["McpServerSecurity:RequireJwt"] = "true",
                    ["McpServerSecurity:RequireApiKey"] = requireApiKey.ToString().ToLowerInvariant(),
                    ["OAuth:Issuer"] = JwtIssuer,
                    ["OAuth:Audience"] = JwtAudience,
                    ["OAuth:RequireHttpsMetadata"] = "false"
                });
            });
        });
    }

    private static string CreateJwt(
        string? connectorClientId,
        bool includeEmail = true,
        string? deniedPlatformPermission = null,
        string? scopes = null)
    {
        var claims = new List<Claim>
        {
            new("sub", "jwt-user-1"),
            new("scope", scopes ?? "openid profile email offline_access moodle-mcp-audience " +
                "moodle.read moodle.write moodle.read.courses moodle.read.students moodle.read.groups " +
                "moodle.read.access moodle.read.contents moodle.read.resources moodle.read.activities " +
                "moodle.read.assignments moodle.read.submissions moodle.read.quizzes moodle.read.scorms " +
                "moodle.read.forums moodle.write.messages moodle.write.assignments.feedback " +
                "moodle.write.assignments.grade moodle.write.course_content moodle.write.forums"),
            new("platform_permission", "tool.assignments.view"),
            new("platform_permission", "tool.assignments.grade"),
            new("platform_permission", "tool.messages.view"),
            new("platform_permission", "tool.messages.send"),
            new("platform_permission", "tool.reports.view"),
            new("platform_permission", "tool.courses.view"),
            new("platform_permission", "tool.students.view"),
            new("platform_permission", "tool.classroom.view"),
            new("platform_permission", "tool.followup.view"),
            new("platform_permission", "tool.forums.view"),
            new("platform_permission", "tool.connections.manage"),
            new("platform_permission", "tool.memory.manage"),
            new("platform_permission", "tool.pedagogy.view")
        };

        if (includeEmail)
        {
            claims.Add(new Claim("email", "teacher@example.com"));
        }

        if (!string.IsNullOrWhiteSpace(deniedPlatformPermission))
        {
            claims.Add(new Claim("platform_permission_deny", deniedPlatformPermission));
        }

        if (!string.IsNullOrWhiteSpace(connectorClientId))
        {
            claims.Add(new Claim("connector_client_id", connectorClientId));
        }

        var token = new JwtSecurityToken(
            JwtIssuer,
            JwtAudience,
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(JwtSigningKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
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

internal sealed class SyntheticMoodleCoursesGateway : IMoodleCoursesGateway
{
    public Task<IReadOnlyList<CourseHierarchyNode>> GetMyCourseHierarchyAsync(
        string userExternalId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CourseHierarchyNode>>([]);

    public Task<PagedCourses> GetMyCoursesAsync(
        string userExternalId,
        int limit,
        int page,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PagedCourses([], 0, page, limit));

    public Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(
        string userExternalId,
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CourseSummary>>([]);

    public Task<CourseSummary?> GetMyCourseAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken) =>
        Task.FromResult<CourseSummary?>(null);
}

internal sealed class CapturingPlatformRequestMetricsSink : IPlatformRequestMetricsSink
{
    public List<PlatformRequestMetricEntity> Metrics { get; } = [];

    public bool TryEnqueue(PlatformRequestMetricEntity metric)
    {
        Metrics.Add(metric);
        return true;
    }
}

public class McpTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _oauthKeyStoragePath = Path.Combine(
        Path.GetTempPath(),
        "Moodle.Application.Tests",
        Guid.NewGuid().ToString("N"));

    protected virtual bool AppV2EnabledForTests => false;
    protected virtual bool CourseOverviewFeatureEnabledForTests => false;
    protected virtual bool ProfessionalAgendaEnabledForTests => false;
    protected virtual bool ProfessionalTasksEnabledForTests => false;
    protected virtual int McpPermitLimitForTests => 120;
    protected virtual string? PostgresConnectionStringForTests => null;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseContentRoot(ResolvePresentationContentRoot());
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServerSecurity:RequireJwt"] = "false",
                ["McpServerSecurity:RequireApiKey"] = "true",
                ["RateLimiting:McpPermitLimit"] = McpPermitLimitForTests.ToString(),
                ["UserClaims:UserIdClaim"] = "sub",
                ["UserClaims:MoodleUserIdClaim"] = "moodle_user_id",
                ["UserClaims:WritePermissionClaim"] = "scope",
                ["UserClaims:WritePermissionValue"] = "moodle.write",
                ["Postgres:ConnectionString"] = PostgresConnectionStringForTests
                    ?? "Host=localhost;Database=unused_in_tests;Username=unused;Password=unused",
                ["ConnectorSecrets:EncryptionKeyBase64"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=",
                ["ConnectorSecrets:TokenCacheMinutes"] = "10",
                ["AdminApi:HeaderName"] = "X-Admin-Api-Key",
                ["AdminApi:ApiKey"] = "admin-tests-key",
                ["OAuth:RequireHttpsMetadata"] = "false",
                ["OAuth:ScopeName"] = "moodle-mcp-audience",
                ["OAuth:KeyStoragePath"] = _oauthKeyStoragePath,
                ["MoodleApi:UseStubData"] = "true",
                ["MoodleApi:BaseUrl"] = "https://moodle.tests",
                ["MoodleProxy:UseStubData"] = "true",
                ["Features:MessagesWriteEnabled"] = "true",
                ["Features:ScheduledMessagesEnabled"] = "true",
                ["Features:AssignmentFeedbackWriteEnabled"] = "true",
                ["Features:AssignmentGradeWriteEnabled"] = "true",
                ["Features:UniversalMoodleWriteEnabled"] = "true",
                ["Features:CourseContentWriteEnabled"] = "true",
                ["Features:AppV2Enabled"] = AppV2EnabledForTests ? "true" : "false",
                ["Features:CourseDetailsOverviewV1Enabled"] = CourseOverviewFeatureEnabledForTests ? "true" : "false",
                ["Features:ProfessionalAgendaEnabled"] = ProfessionalAgendaEnabledForTests ? "true" : "false",
                ["Features:ProfessionalTasksEnabled"] = ProfessionalTasksEnabledForTests ? "true" : "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ConnectorDbContext>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<DbContextOptions<ConnectorDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ConnectorDbContext>>();
            services.RemoveAll<IMcpConnectorClientResolver>();
            services.RemoveAll<IConnectorClientRegistrationService>();
            services.RemoveAll<IMoodleCredentialsProvider>();
            services.RemoveAll<IAuthorizationAuditService>();
            services.RemoveAll<IMoodleCredentialValidator>();

            var postgresConnectionString = PostgresConnectionStringForTests;
            if (!string.IsNullOrWhiteSpace(postgresConnectionString))
            {
                services.AddDbContext<ConnectorDbContext>(options =>
                    options.UseNpgsql(postgresConnectionString));
            }
            else
            {
                var inMemoryProvider = new ServiceCollection()
                    .AddEntityFrameworkInMemoryDatabase()
                    .BuildServiceProvider();
                var databaseName = $"mcp-tests-{Guid.NewGuid():N}";
                services.AddDbContext<ConnectorDbContext>(options =>
                {
                    options.UseInMemoryDatabase(databaseName);
                    options.UseInternalServiceProvider(inMemoryProvider);
                });
            }
            services.AddSingleton<TestConnectorClientStore>();
            services.AddSingleton<TestAuthorizationAuditSink>();
            services.AddScoped<IMcpConnectorClientResolver, InMemoryConnectorClientResolver>();
            services.AddScoped<IConnectorClientRegistrationService, InMemoryConnectorClientRegistrationService>();
            services.AddScoped<IMoodleCredentialsProvider, InMemoryMoodleCredentialsProvider>();
            services.AddScoped<IAuthorizationAuditService, InMemoryAuthorizationAuditService>();
            services.AddScoped<IMoodleCredentialValidator, AlwaysValidMoodleCredentialValidator>();
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = string.Empty;
                options.MetadataAddress = string.Empty;
                options.RequireHttpsMetadata = false;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = McpJwtClaimsIntegrationTests.JwtIssuer
                };
                options.TokenValidationParameters.IssuerSigningKey = McpJwtClaimsIntegrationTests.JwtSigningKey;
                options.TokenValidationParameters.ValidIssuer = McpJwtClaimsIntegrationTests.JwtIssuer;
                options.TokenValidationParameters.ValidAudience = McpJwtClaimsIntegrationTests.JwtAudience;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(_oauthKeyStoragePath))
        {
            Directory.Delete(_oauthKeyStoragePath, recursive: true);
        }
    }

    private static string ResolvePresentationContentRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Moodle.Presentation");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("Nao foi possivel localizar o projeto Moodle.Presentation para os testes de integracao.");
    }
}

internal sealed class PortalFeaturesEnabledMcpTestWebApplicationFactory : McpTestWebApplicationFactory
{
    protected override bool AppV2EnabledForTests => true;
    protected override bool CourseOverviewFeatureEnabledForTests => true;
    protected override bool ProfessionalAgendaEnabledForTests => true;
    protected override bool ProfessionalTasksEnabledForTests => true;
}

public sealed class HighThroughputMcpTestWebApplicationFactory : McpTestWebApplicationFactory
{
    protected override int McpPermitLimitForTests => 10_000;
}

public sealed class HighThroughputPostgresMcpTestWebApplicationFactory : McpTestWebApplicationFactory
{
    private readonly string _connectionString;

    public HighThroughputPostgresMcpTestWebApplicationFactory(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A connection string de teste PostgreSQL é obrigatória.", nameof(connectionString))
            : connectionString;
    }

    protected override int McpPermitLimitForTests => 10_000;

    protected override string? PostgresConnectionStringForTests => _connectionString;
}

internal sealed class TestAuthorizationAuditSink
{
    private readonly object _gate = new();
    private readonly List<AuthorizationFailureAuditRequest> _requests = [];

    public IReadOnlyList<AuthorizationFailureAuditRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public void Add(AuthorizationFailureAuditRequest request)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _requests.Clear();
        }
    }
}

internal sealed class InMemoryAuthorizationAuditService(TestAuthorizationAuditSink sink) : IAuthorizationAuditService
{
    public Task RecordFailureAsync(AuthorizationFailureAuditRequest request, CancellationToken cancellationToken)
    {
        sink.Add(request);
        return Task.CompletedTask;
    }
}

internal sealed class TestConnectorClientStore
{
    public Dictionary<string, TestConnectorClient> ClientsById { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> ClientIdByApiKey { get; } = new(StringComparer.Ordinal);
}

internal sealed class TestConnectorClient
{
    public string ClientId { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public bool CanWrite { get; init; }
}

internal sealed class InMemoryConnectorClientRegistrationService(
    TestConnectorClientStore store,
    ConnectorDbContext dbContext) : IConnectorClientRegistrationService
{
    public async Task<RegisterConnectorClientResult> RegisterOrRotateAsync(RegisterConnectorClientRequest request, CancellationToken cancellationToken)
    {
        var clientId = request.ClientId.Trim();
        var replaced = store.ClientsById.ContainsKey(clientId);
        var apiKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        if (store.ClientsById.TryGetValue(clientId, out var previousClient))
        {
            store.ClientIdByApiKey.Remove(previousClient.ApiKey);
        }

        var client = new TestConnectorClient
        {
            ClientId = clientId,
            ApiKey = apiKey,
            CanWrite = request.CanWrite
        };

        store.ClientsById[clientId] = client;
        store.ClientIdByApiKey[apiKey] = clientId;

        var alias = string.IsNullOrWhiteSpace(request.MoodleAlias)
            ? "default"
            : request.MoodleAlias.Trim().ToLowerInvariant();
        var connectionId = $"{clientId}:{alias}";
        var entity = await dbContext.ConnectorClients.FindAsync([connectionId], cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (entity is null)
        {
            entity = new ConnectorClientCredentialEntity
            {
                Id = connectionId,
                ClientId = clientId,
                CreatedAtUtc = now
            };
            dbContext.ConnectorClients.Add(entity);
        }

        var shouldBeDefault = request.IsDefault ||
                              !await dbContext.ConnectorClients.AnyAsync(
                                  existing => existing.ClientId == clientId &&
                                              existing.IsActive &&
                                              existing.Id != connectionId,
                                  cancellationToken);
        if (shouldBeDefault)
        {
            var existingConnections = await dbContext.ConnectorClients
                .Where(existing => existing.ClientId == clientId && existing.Id != connectionId)
                .ToListAsync(cancellationToken);
            foreach (var existing in existingConnections)
            {
                existing.IsDefault = false;
            }
        }

        entity.ApiKeyHash = apiKey;
        entity.MoodleAlias = alias;
        entity.MoodleBaseUrl = NormalizeBaseUrl(request.MoodleBaseUrl);
        entity.MoodleUsernameEncrypted = request.MoodleUsername;
        entity.MoodlePasswordEncrypted = request.MoodlePassword;
        entity.MoodleTarget = string.IsNullOrWhiteSpace(request.MoodleTarget) ? "default" : request.MoodleTarget.Trim().ToLowerInvariant();
        entity.IsDefault = shouldBeDefault;
        entity.CanWrite = request.CanWrite;
        entity.IsActive = true;
        entity.UpdatedAtUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return new RegisterConnectorClientResult(clientId, connectionId, alias, apiKey, replaced);
    }

    private static string NormalizeBaseUrl(string moodleBaseUrl)
    {
        var uri = new Uri(moodleBaseUrl.Trim(), UriKind.Absolute);
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}

internal sealed class InMemoryConnectorClientResolver(TestConnectorClientStore store) : IMcpConnectorClientResolver
{
    public Task<ConnectorClientContext?> ResolveByApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (!store.ClientIdByApiKey.TryGetValue(apiKey, out var clientId) ||
            !store.ClientsById.TryGetValue(clientId, out var client))
        {
            return Task.FromResult<ConnectorClientContext?>(null);
        }

        return Task.FromResult<ConnectorClientContext?>(new ConnectorClientContext(client.ClientId, client.CanWrite));
    }
}

internal sealed class InMemoryMoodleCredentialsProvider(
    TestConnectorClientStore store,
    IHttpContextAccessor httpContextAccessor) : IMoodleCredentialsProvider
{
    public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken)
    {
        var clientId = httpContextAccessor.HttpContext?.User.FindFirst("connector_client_id")?.Value;
        var client = clientId is not null && store.ClientsById.TryGetValue(clientId, out var found)
            ? found
            : store.ClientsById.Values.FirstOrDefault();
        return Task.FromResult(new MoodleCredentials(
            client?.ClientId ?? "integration-client",
            $"{client?.ClientId ?? "integration-client"}:default",
            "default",
            "https://moodle.tests",
            "user",
            "pass",
            "default",
            client?.CanWrite ?? true));
    }
}

internal sealed class AlwaysValidMoodleCredentialValidator : IMoodleCredentialValidator
{
    public Task<bool> ValidateAsync(string moodleBaseUrl, string username, string password, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }
}

internal sealed class ConnectionNotFoundMoodleUserResolver : IMoodleUserResolver
{
    public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken) =>
        Task.FromException<long?>(new MoodleApiException(
            MoodleErrorContract.ConnectionNotFound,
            "Connection lookup failed in the integration-test boundary."));
}

internal sealed class ConfiguredMoodleUserResolver(string errorCode) : IMoodleUserResolver
{
    public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken)
    {
        Exception failure = string.Equals(errorCode, MoodleErrorContract.ValidationError, StringComparison.Ordinal)
            ? new ArgumentException("Synthetic invalid argument.")
            : new MoodleApiException(errorCode, "Synthetic read failure.");
        return Task.FromException<long?>(failure);
    }
}
