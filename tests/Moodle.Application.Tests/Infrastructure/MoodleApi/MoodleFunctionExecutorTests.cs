using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleFunctionExecutorTests
{
    [Fact]
    public async Task ExecuteReadAsync_ExecutaConsultaDePluginQuandoDescobertaPeloToken()
    {
        var profile = Profile(new MoodleFunctionDescriptor("local_plugin_get_data", MoodleFunctionRisk.Read, true));
        var restClient = new FakeRestClient();
        var sut = new MoodleFunctionExecutor(new FakeCatalog(profile), restClient, new FakeCredentialsProvider());

        var result = await sut.ExecuteReadAsync(
            "local_plugin_get_data", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal("local_plugin_get_data", result.Function);
        Assert.Equal(1, restClient.Calls);
    }

    [Fact]
    public async Task ExecuteReadAsync_ExecutaSomenteFuncaoCatalogadaComoLeitura()
    {
        var profile = Profile(new MoodleFunctionDescriptor("core_course_get_courses_by_field", MoodleFunctionRisk.Read, true));
        var restClient = new FakeRestClient();
        var sut = new MoodleFunctionExecutor(new FakeCatalog(profile), restClient, new FakeCredentialsProvider());

        var result = await sut.ExecuteReadAsync(
            "core_course_get_courses_by_field", new Dictionary<string, object?> { ["field"] = "id" }, CancellationToken.None);

        Assert.Equal("core_course_get_courses_by_field", result.Function);
        Assert.Equal(1, restClient.Calls);
    }

    [Fact]
    public async Task ExecuteReadAsync_AuditaSomenteMetadadosESuaDuracao()
    {
        var profile = Profile(new MoodleFunctionDescriptor("core_course_get_courses_by_field", MoodleFunctionRisk.Read, true));
        var auditLogs = new FakeAuditLogs();
        var sut = new MoodleFunctionExecutor(new FakeCatalog(profile), new FakeRestClient(), new FakeCredentialsProvider(), auditLogs);

        await sut.ExecuteReadAsync(
            "core_course_get_courses_by_field",
            new Dictionary<string, object?> { ["field"] = "id", ["value"] = "dado-confidencial" },
            CancellationToken.None);

        var audit = Assert.Single(auditLogs.Logs);
        Assert.DoesNotContain("dado-confidencial", audit.RequestSanitizedJson, StringComparison.Ordinal);
        Assert.Contains("connectionAlias", audit.RequestSanitizedJson, StringComparison.Ordinal);
        Assert.Equal("connection", audit.MoodleConnectionId);
        Assert.Equal("goias", audit.MoodleConnectionAlias);
        Assert.NotNull(audit.StartedAt);
        Assert.NotNull(audit.FinishedAt);
        Assert.NotNull(audit.DurationMs);
        using var response = JsonDocument.Parse(audit.ResponseSummaryJson);
        Assert.True(response.RootElement.TryGetProperty("durationMs", out var duration));
        Assert.True(duration.GetInt64() >= 0);
    }

    private static MoodleFunctionProfile Profile(MoodleFunctionDescriptor descriptor) => new(
        "connection", "goias", "Moodle", "4.5", 7, [descriptor], DateTimeOffset.UtcNow);

    private sealed class FakeCatalog(MoodleFunctionProfile profile) : IMoodleFunctionCatalog
    {
        public Task<MoodleFunctionProfile> GetCurrentAsync(bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(profile);
    }

    private sealed class FakeCredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials("client", "connection", "goias", "https://moodle.example", "user", "password", "goias", false));
    }

    private sealed class FakeRestClient : IMoodleRestClient
    {
        public int Calls { get; private set; }

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            Calls++;
            using var document = JsonDocument.Parse("{\"ok\":true}");
            return Task.FromResult(document.RootElement.Clone());
        }

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, bool allowServiceToken, CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, cancellationToken);
    }

    private sealed class FakeAuditLogs : IMoodleAuditLogRepository
    {
        public List<MoodleAuditLog> Logs { get; } = [];

        public Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(string correlationId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);

        public Task<int> CountByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(Guid batchJobId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);

        public Task<int> CountByBatchJobIdAsync(Guid batchJobId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
