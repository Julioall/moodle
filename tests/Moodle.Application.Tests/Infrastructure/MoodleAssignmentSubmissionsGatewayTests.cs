using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAssignmentSubmissionsGatewayTests
{
    [Fact]
    public async Task Envia_ids_de_tarefa_em_blocos_de_cinquenta_normalizando_e_removendo_duplicatas()
    {
        var restClient = new CapturingBatchRestClient();
        var sut = new MoodleAssignmentSubmissionsGateway(
            Options.Create(new MoodleApiOptions()),
            new FakeCredentialsProvider(),
            restClient);
        var assignmentIds = Enumerable.Range(1, 51)
            .Select(id => id.ToString())
            .Prepend("001")
            .Append("invalid")
            .Append("0")
            .ToArray();

        var batches = await sut.GetAssignmentSubmissionsBatchAsync(
            "teacher-1",
            assignmentIds,
            status: null,
            since: null,
            before: null,
            CancellationToken.None);

        Assert.Equal([50, 1], restClient.Calls
            .Select(call => call.Parameters.Keys.Count(key => key.StartsWith("assignmentids[", StringComparison.Ordinal)))
            .ToArray());
        Assert.Equal("1", restClient.Calls[0].Parameters["assignmentids[0]"]?.ToString());
        Assert.Equal("50", restClient.Calls[0].Parameters["assignmentids[49]"]?.ToString());
        Assert.Equal("51", restClient.Calls[1].Parameters["assignmentids[0]"]?.ToString());
        Assert.Equal(51, batches.Count);
        Assert.Equal(51, batches.Select(batch => batch.AssignmentId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Recupera_somente_o_assignment_omitido_pela_resposta_em_lote()
    {
        var restClient = new OmittedSubmissionRestClient();
        var sut = new MoodleAssignmentSubmissionsGateway(
            Options.Create(new MoodleApiOptions()),
            new FakeCredentialsProvider(),
            restClient);

        var batches = await sut.GetAssignmentSubmissionsBatchAsync(
            "teacher-1",
            ["1", "2", "3"],
            status: null,
            since: null,
            before: null,
            CancellationToken.None);

        Assert.Equal(2, restClient.Calls.Count);
        Assert.Equal("2", restClient.Calls[1].Parameters["assignmentids[0]"]?.ToString());
        Assert.Single(restClient.Calls[1].Parameters);
        Assert.Equal(new[] { "1", "2", "3" }, batches.Select(batch => batch.AssignmentId).Order().ToArray());
    }

    [Fact]
    public async Task Repete_leitura_de_submissoes_quando_falha_de_rede_e_transitoria()
    {
        var restClient = new FlakyRestClient();
        var sut = new MoodleAssignmentSubmissionsGateway(
            Options.Create(new MoodleApiOptions()),
            new FakeCredentialsProvider(),
            restClient);

        var result = await sut.GetAssignmentSubmissionsAsync(
            "teacher-1",
            "123",
            status: null,
            since: null,
            before: null,
            CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(2, restClient.Calls);
    }

    private sealed class FlakyRestClient : IMoodleRestClient
    {
        public int Calls { get; private set; }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                throw new HttpRequestException("Falha transitoria simulada.");
            }

            using var document = JsonDocument.Parse(
                "{\"assignments\":[{\"assignmentid\":123,\"submissions\":[]}]}");
            return Task.FromResult(document.RootElement.Clone());
        }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, cancellationToken);

        public Task<JsonElement> CallWriteAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, cancellationToken);
    }

    private sealed class CapturingBatchRestClient : IMoodleRestClient
    {
        public List<(string FunctionName, IReadOnlyDictionary<string, object?> Parameters)> Calls { get; } = [];

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            Calls.Add((functionName, new Dictionary<string, object?>(parameters)));
            var assignmentIds = parameters
                .Where(pair => pair.Key.StartsWith("assignmentids[", StringComparison.Ordinal))
                .OrderBy(pair => int.Parse(pair.Key.AsSpan("assignmentids[".Length, pair.Key.Length - "assignmentids[".Length - 1)))
                .Select(pair => new
                {
                    assignmentid = int.Parse(pair.Value!.ToString()!),
                    submissions = Array.Empty<object>()
                });
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { assignments = assignmentIds }));
            return Task.FromResult(document.RootElement.Clone());
        }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, cancellationToken);

        public Task<JsonElement> CallWriteAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class OmittedSubmissionRestClient : IMoodleRestClient
    {
        public List<(string FunctionName, IReadOnlyDictionary<string, object?> Parameters)> Calls { get; } = [];

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            Calls.Add((functionName, new Dictionary<string, object?>(parameters)));
            int[] ids = Calls.Count == 1 ? [1, 3] : [2];
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                assignments = ids.Select(assignmentId => new
                {
                    assignmentid = assignmentId,
                    submissions = Array.Empty<object>()
                })
            }));
            return Task.FromResult(document.RootElement.Clone());
        }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, cancellationToken);

        public Task<JsonElement> CallWriteAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials(
                "client", "connection", "default", "https://moodle.example",
                "user", "password", "target", false));
    }
}
