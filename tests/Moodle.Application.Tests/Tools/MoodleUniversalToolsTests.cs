using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Registry;
using Moodle.Application.Tools;
using Moodle.Domain.Registry;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Presentation.Tools;

namespace Moodle.Application.Tests.Tools;

public sealed class MoodleUniversalToolsTests
{
    [Fact]
    public async Task DiagnoseConnectionAsync_ExecutaSiteInfoAoVivoSemServiceToken()
    {
        var rest = new FakeRestClient(SiteInfo());
        var telemetry = new RecordingRuntimeTelemetry();
        var sut = CreateSut(new FakeCredentialsProvider(Connection()), rest, runtimeTelemetry: telemetry);

        var result = await sut.DiagnoseConnectionAsync("Goiás", forceRefresh: true);

        Assert.False(result.IsError);
        Assert.False(rest.LastAllowServiceToken);
        Assert.Equal(1, rest.Calls);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("ok", structured.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("auditId").GetString()));
        var data = structured.GetProperty("data");
        Assert.True(data.GetProperty("healthy").GetBoolean());
        Assert.Equal("goias", data.GetProperty("requestedAlias").GetString());
        Assert.Equal("https://ead.fieg.com.br", data.GetProperty("baseUrl").GetString());
        Assert.True(data.GetProperty("siteInfoSucceeded").GetBoolean());
        Assert.Equal("5.0.1", data.GetProperty("release").GetString());
        Assert.False(data.TryGetProperty("token", out _));
        Assert.DoesNotContain("password", structured.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["diagnostic_live"], telemetry.Phases);
    }

    [Fact]
    public async Task DiagnoseConnectionAsync_consults_live_even_when_force_refresh_is_false()
    {
        var rest = new FakeRestClient(SiteInfo());
        var telemetry = new RecordingRuntimeTelemetry();
        var sut = CreateSut(new FakeCredentialsProvider(Connection()), rest, runtimeTelemetry: telemetry);

        var first = await sut.DiagnoseConnectionAsync("goias", forceRefresh: false);
        var second = await sut.DiagnoseConnectionAsync("goias", forceRefresh: true);

        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.Equal(2, rest.Calls);
        Assert.Equal(["diagnostic_live", "diagnostic_live"], telemetry.Phases);
    }

    [Fact]
    public async Task DiagnoseConnectionAsync_reports_live_failure_as_unhealthy()
    {
        var failure = new MoodleApiException(
            MoodleErrorContract.NetworkError,
            "test-safe-failure",
            connectionId: "goias-connection",
            connectionAlias: "goias",
            stage: MoodleIntegrationStage.ResponseParsing);
        var telemetry = new RecordingRuntimeTelemetry();
        var sut = CreateSut(new FakeCredentialsProvider(Connection()), new FakeRestClient(SiteInfo(), failure), runtimeTelemetry: telemetry);

        var result = await sut.DiagnoseConnectionAsync("goias");

        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.False(data.GetProperty("healthy").GetBoolean());
        Assert.Equal(MoodleErrorContract.NetworkError, data.GetProperty("diagnosticErrorCode").GetString());
        Assert.Contains(telemetry.Events, item => item.Phase == "diagnostic_live" && item.Outcome == "failure");
    }

    [Fact]
    public async Task DiagnoseConnectionAsync_RelataCheckpointDeDescriptografiaSemLancar()
    {
        var failure = new MoodleApiException(
            MoodleErrorContract.TokenDecryptionFailed,
            "internal",
            connectionId: "goias-connection",
            connectionAlias: "goias",
            endpoint: "https://ead.fieg.com.br",
            stage: MoodleIntegrationStage.CredentialDecryption);
        var sut = CreateSut(new FakeCredentialsProvider(error: failure), new FakeRestClient(SiteInfo()));

        var result = await sut.DiagnoseConnectionAsync("goias");

        Assert.False(result.IsError);
        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.False(data.GetProperty("healthy").GetBoolean());
        Assert.True(data.GetProperty("connectionFound").GetBoolean());
        Assert.True(data.GetProperty("active").GetBoolean());
        Assert.True(data.GetProperty("urlValid").GetBoolean());
        Assert.True(data.GetProperty("credentialsPresent").GetBoolean());
        Assert.False(data.GetProperty("decryptionSucceeded").GetBoolean());
        Assert.False(data.GetProperty("tokenAvailable").GetBoolean());
        Assert.False(data.GetProperty("authenticationSucceeded").GetBoolean());
        Assert.Equal(
            MoodleErrorContract.TokenDecryptionFailed,
            data.GetProperty("diagnosticErrorCode").GetString());
    }

    [Fact]
    public async Task DiagnoseConnectionAsync_NaoMarcaEtapasPosterioresQuandoUrlForInvalida()
    {
        var failure = new MoodleApiException(
            MoodleErrorContract.NetworkError,
            "internal",
            connectionId: "goias-connection",
            connectionAlias: "goias",
            stage: MoodleIntegrationStage.UrlValidation);
        var sut = CreateSut(new FakeCredentialsProvider(error: failure), new FakeRestClient(SiteInfo()));

        var result = await sut.DiagnoseConnectionAsync("goias");

        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.True(data.GetProperty("connectionFound").GetBoolean());
        Assert.True(data.GetProperty("active").GetBoolean());
        Assert.False(data.GetProperty("urlValid").GetBoolean());
        Assert.False(data.GetProperty("credentialsPresent").GetBoolean());
        Assert.False(data.GetProperty("decryptionSucceeded").GetBoolean());
        Assert.False(data.GetProperty("tokenAvailable").GetBoolean());
        Assert.False(data.GetProperty("authenticationSucceeded").GetBoolean());
    }

    [Fact]
    public async Task ListFunctionsAsync_ExpoeTodasAsFuncoesDoToken()
    {
        var sut = CreateSut(
            new FakeCredentialsProvider(Connection()),
            new FakeRestClient(SiteInfo()),
            [
                new MoodleFunctionDescriptor("core_course_get_courses_by_field", MoodleFunctionRisk.Read, true),
                new MoodleFunctionDescriptor("mod_assign_save_grade", MoodleFunctionRisk.ControlledWrite, true),
                new MoodleFunctionDescriptor("local_plugin_secret", MoodleFunctionRisk.Unknown, true)
            ]);

        var result = await sut.ListFunctionsAsync();

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.True(structured.TryGetProperty("data", out var data), structured.GetRawText());
        Assert.Equal(3, data.GetArrayLength());
        Assert.Contains(data.EnumerateArray(), item => item.GetProperty("Name").GetString() == "core_course_get_courses_by_field");
        Assert.Contains(data.EnumerateArray(), item => item.GetProperty("Name").GetString() == "mod_assign_save_grade");
        Assert.Contains(data.EnumerateArray(), item => item.GetProperty("Name").GetString() == "local_plugin_secret");
    }

    [Fact]
    public async Task CheckFunctionAsync_ReturnsControlledFunctionWhenEnabledForToken()
    {
        var sut = CreateSut(
            new FakeCredentialsProvider(Connection()),
            new FakeRestClient(SiteInfo()),
            [new MoodleFunctionDescriptor("mod_assign_save_grade", MoodleFunctionRisk.ControlledWrite, true)]);

        var result = await sut.CheckFunctionAsync("mod_assign_save_grade");

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.True(structured.TryGetProperty("data", out var data), structured.GetRawText());
        Assert.Equal((int)MoodleFunctionRisk.ControlledWrite, data.GetProperty("Risk").GetInt32());
        Assert.True(data.GetProperty("IsAvailable").GetBoolean());
    }

    [Fact]
    public async Task ListAvailableFlowsAsync_ExpoeFreshnessDoCatalogoDeCapabilities()
    {
        var sut = CreateSut(
            new FakeCredentialsProvider(Connection()),
            new FakeRestClient(SiteInfo()),
            [new MoodleFunctionDescriptor("core_course_get_courses_by_field", MoodleFunctionRisk.Read, true)]);

        var result = await sut.ListAvailableFlowsAsync(forceRefresh: true);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var freshness = structured.GetProperty("freshness");
        Assert.Equal("live", freshness.GetProperty("source").GetString());
        Assert.Equal("capabilities", freshness.GetProperty("dataset").GetString());
        Assert.Equal("available_flows", freshness.GetProperty("recordType").GetString());
        Assert.True(freshness.GetProperty("complete").GetBoolean());
        Assert.True(freshness.GetProperty("decisionSafe").GetBoolean());
    }

    [Fact]
    public async Task ExecuteReadAsync_RedigePayloadPublicoRecursivamente()
    {
        var payload = JsonNode.Parse(
            """
            {
              "token": "token-real",
              "nested": { "privateAccessKey": "private-real" },
              "array": [{ "password": "password-real" }],
              "safe": "visible",
              "html": "<input name='sesskey' value='synthetic-html-canary'><p>Lesson</p>",
              "config": "const settings = {launch_token: 'synthetic-js-canary'}"
            }
            """);
        var sut = CreateSut(
            new FakeCredentialsProvider(Connection()),
            new FakeRestClient(SiteInfo()),
            safeReadExecutor: new FakeSafeReadExecutor(payload));
        using var parameters = JsonDocument.Parse("{}");

        var result = await sut.ExecuteReadAsync(
            "core_course_get_courses_by_field",
            parameters.RootElement,
            cancellationToken: CancellationToken.None);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var raw = structured.GetRawText();
        Assert.DoesNotContain("synthetic-html-canary", string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(item => item.Text)), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-js-canary", string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(item => item.Text)), StringComparison.Ordinal);
        Assert.DoesNotContain("token-real", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("private-real", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("password-real", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-html-canary", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-js-canary", raw, StringComparison.Ordinal);
        Assert.Contains("Lesson", raw, StringComparison.Ordinal);
        Assert.Contains("visible", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteReadAsync_ReportsPartialCoverageWhenAnOversizedTextIsWithheld()
    {
        var payload = new JsonObject
        {
            ["html"] = new string('x', 1_000_001) + "sesskey=synthetic-canary",
            ["deeplyEncoded"] = "token%2525253Dsynthetic-canary",
            ["safe"] = "Lesson"
        };
        var sut = CreateSut(new FakeCredentialsProvider(Connection()), new FakeRestClient(SiteInfo()), safeReadExecutor: new FakeSafeReadExecutor(payload));
        using var parameters = JsonDocument.Parse("{}");
        var result = await sut.ExecuteReadAsync("core_course_get_courses_by_field", parameters.RootElement);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.False(result.IsError);
        Assert.DoesNotContain("synthetic-canary", structured.GetRawText());
        Assert.Contains("Lesson", structured.GetRawText());
        Assert.Equal(MoodlePublicContentSanitizer.Withheld, structured.GetProperty("data").GetProperty("Payload").GetProperty("deeplyEncoded").GetString());
        Assert.NotEmpty(structured.GetProperty("warnings").EnumerateArray());
        Assert.Contains("Cobertura parcial", structured.GetProperty("warnings")[0].GetString());
    }

    private static MoodleUniversalTools CreateSut(
        IMoodleCredentialsProvider credentialsProvider,
        FakeRestClient restClient,
        IReadOnlyList<MoodleFunctionDescriptor>? descriptors = null,
        FakeSafeReadExecutor? safeReadExecutor = null,
        IMcpToolUsageTelemetry? runtimeTelemetry = null)
    {
        return new MoodleUniversalTools(
            new FakeCatalog(descriptors),
            safeReadExecutor ?? new FakeSafeReadExecutor(),
            new OperationRegistry(),
            new PolicyEngine(),
            new MoodleBusinessFlowRegistry(),
            credentialsProvider,
            new MoodleConnectionSelection(),
            restClient,
            NullLogger<MoodleUniversalTools>.Instance,
            runtimeTelemetry ?? new RecordingRuntimeTelemetry());
    }

    private static MoodleCredentials Connection() => new(
        "client",
        "goias-connection",
        "goias",
        "https://ead.fieg.com.br/?sensitive=query",
        "user",
        "password",
        "goias",
        false);

    private static JsonElement SiteInfo()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "sitename": "Moodle Goias",
              "release": "5.0.1",
              "userid": 847,
              "functions": [
                { "name": "core_webservice_get_site_info" },
                { "name": "core_course_get_courses_by_field" }
              ]
            }
            """);
        return document.RootElement.Clone();
    }

    private sealed class FakeCredentialsProvider(
        MoodleCredentials? connection = null,
        Exception? error = null) : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            error is null
                ? Task.FromResult(connection!)
                : Task.FromException<MoodleCredentials>(error);
    }

    private sealed class RecordingRuntimeTelemetry : IMcpToolUsageTelemetry
    {
        public List<(string Phase, string Outcome)> Events { get; } = [];
        public IReadOnlyList<string> Phases => Events.Select(item => item.Phase).ToArray();
        public void RecordInvocation(string toolName, string? canonicalOperation, string? compatibilityAliasOf,
            string exposureProfile, string outcome, string? errorCode, double durationMs) { }
        public void RecordRuntimePhase(string phase, string outcome, double durationMs) => Events.Add((phase, outcome));
    }

    private sealed class FakeRestClient(JsonElement payload, Exception? error = null) : IMoodleRestClient
    {
        public int Calls { get; private set; }
        public bool LastAllowServiceToken { get; private set; } = true;

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) => Task.FromResult(payload);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastAllowServiceToken = allowServiceToken;
            return error is null ? Task.FromResult(payload) : Task.FromException<JsonElement>(error);
        }
    }

    private sealed class FakeCatalog(IReadOnlyList<MoodleFunctionDescriptor>? descriptors = null) : IMoodleFunctionCatalog
    {
        public Task<MoodleFunctionProfile> GetCurrentAsync(
            bool forceRefresh,
            CancellationToken cancellationToken) => Task.FromResult(new MoodleFunctionProfile(
            "goias-connection",
            "goias",
            "Moodle Goias",
            "5.0.1",
            847,
            descriptors ??
            [
                new MoodleFunctionDescriptor(
                    "core_webservice_get_site_info",
                    MoodleFunctionRisk.Read,
                    true),
                new MoodleFunctionDescriptor(
                    "core_course_get_courses_by_field",
                    MoodleFunctionRisk.Read,
                    true)
            ],
            DateTimeOffset.UtcNow));
    }

    private sealed class FakeSafeReadExecutor(JsonNode? payload = null) : ISafeReadExecutor
    {
        public Task<System.Text.Json.Nodes.JsonNode?> ExecuteAsync(
            string functionName,
            Dictionary<string, object?> parameters,
            string? moodleAlias = null,
            NormalizationContext? context = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(payload);
    }
}
