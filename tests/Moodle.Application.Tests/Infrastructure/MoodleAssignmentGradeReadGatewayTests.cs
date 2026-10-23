using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAssignmentGradeReadGatewayTests
{
    [Fact]
    public async Task Le_notas_de_varios_assignments_em_uma_chamada_e_preserva_a_chave_da_atividade()
    {
        var restClient = new FakeRestClient("""
        {
          "assignments": [
            { "assignmentid": 116124, "grades": [{ "userid": 440752, "grade": "82.0" }] },
            { "assignmentid": 117487, "grades": [{ "userid": 440752, "grade": "91.0" }] }
          ]
        }
        """);
        var sut = new MoodleAssignmentGradeReadGateway(
            Options.Create(new MoodleApiOptions()),
            new FakeCredentialsProvider(),
            restClient);

        var batches = await sut.GetExistingGradesBatchAsync(
            "teacher-1",
            ["116124", "117487"],
            ["440752"],
            CancellationToken.None);

        Assert.Single(restClient.Calls);
        Assert.Equal("116124", restClient.Calls[0].Parameters["assignmentids[0]"]?.ToString());
        Assert.Equal("117487", restClient.Calls[0].Parameters["assignmentids[1]"]?.ToString());
        Assert.Equal(2, batches.Count);
        Assert.Equal(82m, batches.Single(batch => batch.AssignmentId == "116124").Grades["440752"].Grade);
        Assert.Equal(91m, batches.Single(batch => batch.AssignmentId == "117487").Grades["440752"].Grade);
    }

    [Fact]
    public async Task Reprocessa_somente_o_assignment_omitido_no_lote()
    {
        var restClient = new OmittedAssignmentRestClient();
        var sut = new MoodleAssignmentGradeReadGateway(
            Options.Create(new MoodleApiOptions()),
            new FakeCredentialsProvider(),
            restClient);

        var batches = await sut.GetExistingGradesBatchAsync(
            "teacher-1",
            ["116124", "117487"],
            ["440752"],
            CancellationToken.None);

        Assert.Equal(2, restClient.Calls.Count);
        Assert.Equal(2, batches.Count);
        Assert.Null(batches.Single(batch => batch.AssignmentId == "117487").ErrorCode);
        Assert.Equal(91m, batches.Single(batch => batch.AssignmentId == "117487").Grades["440752"].Grade);
    }

    [Fact]
    public async Task Repete_leitura_individual_apos_falha_transitoria_no_fallback_do_lote()
    {
        var restClient = new TransientThenSuccessRestClient(
            failCount: 2,
            """
            {
              "assignments": [
                { "assignmentid": 116124, "grades": [{ "userid": 440752, "grade": "82.0" }] }
              ]
            }
            """);
        var sut = new MoodleAssignmentGradeReadGateway(
            Options.Create(new MoodleApiOptions()),
            new FakeCredentialsProvider(),
            restClient);

        var batches = await sut.GetExistingGradesBatchAsync(
            "teacher-1",
            ["116124"],
            ["440752"],
            CancellationToken.None);

        var batch = Assert.Single(batches);
        Assert.Equal(3, restClient.Calls);
        Assert.Null(batch.ErrorCode);
        Assert.Equal(82m, batch.Grades["440752"].Grade);
    }

    [Fact]
    public async Task Mantem_nota_vazia_distinta_do_marcador_menos_um_do_Moodle()
    {
        var sut = new MoodleAssignmentGradeReadGateway(
            Options.Create(new MoodleApiOptions()),
            new FakeCredentialsProvider(),
            new FakeRestClient("""
            {
              "assignments": [{
                "assignmentid": 116124,
                "grades": [
                  { "userid": 440752, "grade": "" },
                  { "userid": 440730, "grade": "-1.00000" }
                ]
              }]
            }
            """));

        var grades = await sut.GetExistingGradesAsync(
            "teacher-1",
            "116124",
            ["440752", "440730"],
            CancellationToken.None);

        Assert.Null(grades["440752"].Grade);
        Assert.Equal(-1m, grades["440730"].Grade);
    }

    [Fact]
    public async Task Trata_aviso_sem_notas_como_lote_vazio_completo()
    {
        var sut = new MoodleAssignmentGradeReadGateway(
            Options.Create(new MoodleApiOptions()),
            new FakeCredentialsProvider(),
            new FakeRestClient("""
            {
              "assignments": [],
              "warnings": [
                { "item": "assignment", "itemid": 116124, "warningcode": "3", "message": "No grades found" }
              ]
            }
            """));

        var batches = await sut.GetExistingGradesBatchAsync(
            "teacher-1",
            ["116124"],
            ["440752"],
            CancellationToken.None);

        var batch = Assert.Single(batches);
        Assert.Equal("116124", batch.AssignmentId);
        Assert.Empty(batch.Grades);
        Assert.Null(batch.ErrorCode);
    }

        [Fact]
        public async Task Preserva_grader_e_timemodified_para_atividade_sem_nota()
        {
                var sut = new MoodleAssignmentGradeReadGateway(
                        Options.Create(new MoodleApiOptions()),
                        new FakeCredentialsProvider(),
                        new FakeRestClient("""
                        {
                            "assignments": [{
                                "assignmentid": 117487,
                                "grades": [
                                    { "userid": 440752, "grade": "-1.00000", "grader": 317295, "timemodified": 1787075877 },
                                    { "userid": 440739, "grade": "-1.00000", "grader": -1, "timemodified": 0 }
                                ]
                            }]
                        }
                        """));

                var grades = await sut.GetExistingGradesAsync(
                        "teacher-1",
                        "117487",
                        ["440752", "440739"],
                        CancellationToken.None);

                Assert.Equal(317295, grades["440752"].GraderId);
                Assert.Equal(1787075877, grades["440752"].TimeModified);
                Assert.Equal(-1, grades["440739"].GraderId);
        }

    private sealed class FakeRestClient(string json) : IMoodleRestClient
    {
        public List<(string FunctionName, IReadOnlyDictionary<string, object?> Parameters)> Calls { get; } = [];

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            Calls.Add((functionName, parameters));
            return Task.FromResult(Payload());
        }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            Calls.Add((functionName, parameters));
            return Task.FromResult(Payload());
        }

        private JsonElement Payload()
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class OmittedAssignmentRestClient : IMoodleRestClient
    {
        public List<IReadOnlyDictionary<string, object?>> Calls { get; } = [];

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) => CallAsync(connection, functionName, parameters, true, cancellationToken);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            Calls.Add(parameters);
            var json = Calls.Count == 1
                ? "{\"assignments\":[{\"assignmentid\":116124,\"grades\":[]}]}"
                : "{\"assignments\":[{\"assignmentid\":117487,\"grades\":[{\"userid\":440752,\"grade\":\"91\"}]}]}";
            using var document = JsonDocument.Parse(json);
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class TransientThenSuccessRestClient(int failCount, string json) : IMoodleRestClient
    {
        public int Calls { get; private set; }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) => CallAsync(connection, functionName, parameters, true, cancellationToken);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= failCount)
            {
                throw new HttpRequestException("Simulated Moodle connection reset.");
            }

            using var document = JsonDocument.Parse(json);
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class FakeCredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials(
                "client", "connection", "default", "https://moodle.example", "user", "password", "target", false));
    }
}
