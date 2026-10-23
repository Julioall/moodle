using System.Text.Json;
using System.Text.Json.Nodes;
using Moodle.Application.Abstractions;
using Moodle.Application.Registry;
using Moodle.Domain;
using Moodle.Domain.Registry;

namespace Moodle.Application.Tests.Registry;

public sealed class SafeReadExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldPassThroughPipeline_WhenOperationIsSafe()
    {
        var connectionId = Guid.NewGuid();
        var mockConnectionInfo = new ConnectionInfo(connectionId, "test-alias", "https://test.moodle");
        var safeOp = new MoodleOperation("safe_read_func", "cat", OperationType.Read, ToolRiskLevel.ReadOnly, OperationPolicy.Direct, "prof1");
        var creds = new MoodleCredentials("client", connectionId.ToString(), "test-alias", "https://test.moodle", "user1", "pass", "target", false);
        var snap = new CapabilitySnapshot(connectionId, "user1", new HashSet<string> { "safe_read_func" }, DateTimeOffset.UtcNow);

        var connectionReg = new FakeConnectionRegistry(mockConnectionInfo);
        var opReg = new FakeOperationRegistry(safeOp);
        var policy = new FakePolicyEngine(new PolicyEvaluationResult(PolicyDecision.Allow, "OK"));
        var credsProvider = new FakeCredentialsProvider(creds);
        var capReg = new FakeCapabilityRegistry(snap);
        var restClient = new FakeRestClient(JsonDocument.Parse("{\"key\":\"value\"}").RootElement);
        var normalizer = new FakeResponseNormalizer(JsonNode.Parse("{\"normalized\":\"true\"}"));

        var executor = new SafeReadExecutor(connectionReg, opReg, capReg, policy, normalizer, credsProvider, restClient);

        // Act
        var result = await executor.ExecuteAsync("safe_read_func", new Dictionary<string, object?>());

        // Assert
        Assert.NotNull(result);
        Assert.Equal("true", result!["normalized"]!.ToString());
        Assert.True(restClient.WasCalled);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectUnknownOperationBeforeCapabilityCall()
    {
        var connectionId = Guid.NewGuid();
        var connection = new ConnectionInfo(connectionId, "test-alias", "https://test.moodle");
        var capability = new FakeCapabilityRegistry(new CapabilitySnapshot(
            connectionId,
            "user1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow));
        var restClient = new FakeRestClient(JsonDocument.Parse("{}").RootElement);
        var executor = new SafeReadExecutor(
            new FakeConnectionRegistry(connection),
            new NullOperationRegistry(),
            capability,
            new PolicyEngine(),
            new FakeResponseNormalizer(JsonNode.Parse("{}")),
            new FakeCredentialsProvider(new MoodleCredentials(
                "client", connectionId.ToString(), "test-alias", "https://test.moodle", "user1", "pass", "target", false)),
            restClient);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("unknown_function", new Dictionary<string, object?>()));

        Assert.Contains("not registered", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(restClient.WasCalled);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRedirectControlledWrite()
    {
        var connectionId = Guid.NewGuid();
        var connection = new ConnectionInfo(connectionId, "test-alias", "https://test.moodle");
        var controlledWrite = new MoodleOperation(
            "mod_assign_save_grade",
            "assignment",
            OperationType.ControlledWrite,
            ToolRiskLevel.HumanConfirmedWrite,
            OperationPolicy.Aggregated,
            "controlled-write");
        var restClient = new FakeRestClient(JsonDocument.Parse("{}").RootElement);
        var executor = new SafeReadExecutor(
            new FakeConnectionRegistry(connection),
            new FakeOperationRegistry(controlledWrite),
            new FakeCapabilityRegistry(new CapabilitySnapshot(
                connectionId,
                "user1",
                new HashSet<string> { controlledWrite.OperationName },
                DateTimeOffset.UtcNow)),
            new PolicyEngine(),
            new FakeResponseNormalizer(JsonNode.Parse("{}")),
            new FakeCredentialsProvider(new MoodleCredentials(
                "client", connectionId.ToString(), "test-alias", "https://test.moodle", "user1", "pass", "target", false)),
            restClient);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(controlledWrite.OperationName, new Dictionary<string, object?>()));

        Assert.Contains("redirect", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(restClient.WasCalled);
    }

    private sealed class FakeConnectionRegistry(ConnectionInfo info) : IConnectionRegistry
    {
        public Task<ConnectionInfo?> ResolveConnectionAsync(string? alias, CancellationToken cancellationToken = default) => Task.FromResult<ConnectionInfo?>(info);
    }

    private sealed class FakeOperationRegistry(MoodleOperation op) : IOperationRegistry
    {
        public MoodleOperation? GetOperation(string operationName) => op;
        public IReadOnlyList<MoodleOperation> GetAllOperations() => [op];
    }

    private sealed class NullOperationRegistry : IOperationRegistry
    {
        public MoodleOperation? GetOperation(string operationName) => null;
        public IReadOnlyList<MoodleOperation> GetAllOperations() => [];
    }

    private sealed class FakePolicyEngine(PolicyEvaluationResult result) : IPolicyEngine
    {
        public PolicyEvaluationResult Evaluate(MoodleOperation? operation) => result;
    }

    private sealed class FakeCredentialsProvider(MoodleCredentials creds) : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(creds);
    }

    private sealed class FakeCapabilityRegistry(CapabilitySnapshot snap) : ICapabilityRegistry
    {
        public Task<CapabilitySnapshot> GetSnapshotAsync(ConnectionInfo connectionInfo, string userToken, CancellationToken cancellationToken = default) => Task.FromResult(snap);

        public void Invalidate(ConnectionInfo connectionInfo, string userToken)
        {
        }
    }

    private sealed class FakeResponseNormalizer(JsonNode? result) : IResponseNormalizer
    {
        public JsonNode? Normalize(string profileName, JsonNode? rawResponse, NormalizationContext? context = null) => result;
    }

    private sealed class FakeRestClient(JsonElement result) : IMoodleRestClient
    {
        public bool WasCalled { get; private set; }

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            return CallAsync(connection, functionName, parameters, true, cancellationToken);
        }

        public Task<JsonElement> CallAsync(MoodleCredentials credentials, string functionName, IReadOnlyDictionary<string, object?> parameters, bool allowServiceToken, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(result);
        }
    }
}
