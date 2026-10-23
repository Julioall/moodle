using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;
using System.Text.Json;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAssignmentSettingsGatewayTests
{
    [Fact]
    public async Task MissingGradeStillReturnsOfficialContentAndInvalidDatesRemainUnknown()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new ConnectionAwareRestClient { OverridePayload = """
            {"courses":[{"assignments":[{"id":5001,"intro":"Official content","duedate":-1,
            "cutoffdate":9223372036854775807,"introfiles":[{"filename":"unavailable.png"}]}]}]}
            """ };
        var gateway = new MoodleAssignmentSettingsGateway(Options.Create(new MoodleApiOptions()), new SwitchingCredentialsProvider(), rest, cache);
        var result = await gateway.GetAssignmentSettingsAsync("synthetic", "101", "5001", CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("Official content", result.Description);
        Assert.Null(result.RawGrade);
        Assert.False(result.DueAtKnown);
        Assert.False(result.CutoffAtKnown);
        Assert.False(result.IntroFilesComplete);
        Assert.Empty(result.IntroFiles!);
    }

    [Fact]
    public async Task BulkAndDetailPreserveOfficialFieldsWithZeroAndMissingDatesDistinct()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new ConnectionAwareRestClient { OverridePayload = """
            {"courses":[{"id":101,"assignments":[{"id":5001,"cmid":9001,"grade":100,"intro":"<p>Official</p>",
            "allowsubmissionsfromdate":1700000000,"duedate":0,"cutoffdate":1700000300,
            "introfiles":[{"filename":"image.png","fileurl":"https://moodle.example/pluginfile.php/1/image.png"}],"introattachments":[]},
            {"id":5002,"cmid":9002,"grade":0,"intro":"Empty dates"}]}]}
            """ };
        var gateway = new MoodleAssignmentSettingsGateway(Options.Create(new MoodleApiOptions()), new SwitchingCredentialsProvider(), rest, cache);
        var bulk = await gateway.GetCourseAssignmentSettingsAsync("synthetic", "101", CancellationToken.None);
        var first = bulk["5001"];
        Assert.True(first.DescriptionPresent);
        Assert.Equal(9001L, first.CourseModuleId);
        Assert.Single(first.IntroFiles!);
        Assert.Empty(first.IntroAttachments!);
        Assert.True(first.OpenAtKnown);
        Assert.True(first.DueAtKnown);
        Assert.Null(first.DueAt);
        Assert.Equal(1700000300, first.CutoffAt!.Value.ToUnixTimeSeconds());
        Assert.False(bulk["5002"].DueAtKnown);
        Assert.Null(bulk["5002"].IntroFilesComplete);
        var detail = await gateway.GetAssignmentSettingsAsync("synthetic", "101", "5001", CancellationToken.None);
        Assert.Equal(first, detail);
        Assert.Equal(1, rest.CallCount);
    }

    [Fact]
    public async Task ParseNoSubmissionsComoTriStateELeituraFreshIgnoraCacheDoCurso()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new ConnectionAwareRestClient { OverridePayload = """
            {"courses":[{"id":101,"assignments":[
              {"id":5001,"nosubmissions":0},{"id":5002,"nosubmissions":1},{"id":5003,"nosubmissions":"unknown"}]}]}
            """ };
        var gateway = new MoodleAssignmentSettingsGateway(Options.Create(new MoodleApiOptions()), new SwitchingCredentialsProvider(), rest, cache);

        var cached = await gateway.GetCourseAssignmentSettingsAsync("synthetic", "101", CancellationToken.None);
        var fresh = await gateway.GetCourseAssignmentSettingsFreshAsync("synthetic", "101", CancellationToken.None);

        Assert.False(cached["5001"].NoSubmissions);
        Assert.True(cached["5002"].NoSubmissions);
        Assert.Null(cached["5003"].NoSubmissions);
        Assert.Equal(cached["5002"], fresh["5002"]);
        Assert.Equal(2, rest.CallCount);
    }

    [Fact]
    public async Task BulkDoesNotOverwriteCanonicalInstanceWhenModuleIdsCollideOrIncludeOtherCourses()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new ConnectionAwareRestClient { OverridePayload = """
            {"courses":[{"id":101,"assignments":[{"id":1,"cmid":2,"grade":10},{"id":2,"cmid":1,"grade":20}]},
                        {"id":102,"assignments":[{"id":1,"cmid":2,"grade":999}]}]}
            """ };
        var gateway = new MoodleAssignmentSettingsGateway(Options.Create(new MoodleApiOptions()), new SwitchingCredentialsProvider(), rest, cache);
        var bulk = await gateway.GetCourseAssignmentSettingsAsync("synthetic", "101", CancellationToken.None);
        Assert.Equal(10m, bulk["1"].RawGrade);
        Assert.Equal(20m, bulk["2"].RawGrade);
        Assert.Equal(2, bulk.Count);
    }

    [Fact]
    public async Task StubSemEscalaNaoFabricaMaximoNumerico()
    {
        var gateway = new MoodleAssignmentSettingsGateway(
            Options.Create(new MoodleApiOptions { UseStubData = true }),
            credentialsProvider: null!,
            restClient: null!,
            new MemoryCache(new MemoryCacheOptions()));

        var result = await gateway.GetAssignmentSettingsAsync(
            "teacher",
            "101",
            "5001",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0m, result!.MaxGrade);
    }

    [Fact]
    public async Task CacheDeConfiguracaoIsolaConexaoEUsuario()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var credentials = new SwitchingCredentialsProvider();
        var restClient = new ConnectionAwareRestClient();
        var gateway = new MoodleAssignmentSettingsGateway(
            Options.Create(new MoodleApiOptions { UseStubData = false }),
            credentials,
            restClient,
            cache);

        credentials.Current = Credentials("client-a", "connection-a", "teacher-a");
        var first = await gateway.GetAssignmentSettingsAsync("teacher-a", "101", "5001", CancellationToken.None);

        credentials.Current = Credentials("client-b", "connection-b", "teacher-b");
        var second = await gateway.GetAssignmentSettingsAsync("teacher-b", "101", "5001", CancellationToken.None);

        Assert.Equal(10m, first!.MaxGrade);
        Assert.Equal(20m, second!.MaxGrade);
        Assert.Equal(2, restClient.CallCount);
    }

    [Fact]
    public async Task ParsePreservaEscalaAssinadaEAnexosOficiaisComCobertura()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ConnectionAwareRestClient
        {
            OverridePayload = """
                {"courses":[{"assignments":[{"id":5001,"cmid":9001,"grade":-7,"intro":"Enunciado oficial","introattachments":[{"filename":"enunciado.pdf","mimetype":"application/pdf","filesize":128,"fileurl":"https://moodle.example/pluginfile.php/11/mod_assign/introattachment/0/enunciado.pdf?token=synthetic-token","isexternalfile":false}]}]}]}
                """
        };
        var gateway = new MoodleAssignmentSettingsGateway(
            Options.Create(new MoodleApiOptions { UseStubData = false }),
            new SwitchingCredentialsProvider(),
            restClient,
            cache);

        var result = await gateway.GetAssignmentSettingsAsync("teacher-a", "101", "5001", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0m, result!.MaxGrade);
        Assert.Equal(-7m, result.RawGrade);
        Assert.Equal(7L, result.GradeScaleId);
        Assert.True(result.IsGradable);
        Assert.Equal("Enunciado oficial", result.Description);
        Assert.True(result.IntroAttachmentsComplete);
        var attachment = Assert.Single(result.IntroAttachments!);
        Assert.Equal("enunciado.pdf", attachment.FileName);
        Assert.Equal(128L, attachment.FileSize);
        Assert.Contains("synthetic-token", attachment.FileUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CampoDeAnexosOmitidoPermaneceCoberturaDesconhecida()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ConnectionAwareRestClient
        {
            OverridePayload = """
                {"courses":[{"assignments":[{"id":5001,"grade":0,"intro":"Enunciado oficial"}]}]}
                """
        };
        var gateway = new MoodleAssignmentSettingsGateway(
            Options.Create(new MoodleApiOptions { UseStubData = false }),
            new SwitchingCredentialsProvider(),
            restClient,
            cache);

        var result = await gateway.GetAssignmentSettingsAsync("teacher-a", "101", "5001", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0m, result!.RawGrade);
        Assert.False(result.IsGradable);
        Assert.Null(result.IntroAttachments);
        Assert.Null(result.IntroAttachmentsComplete);
    }

    [Fact]
    public async Task ListaDeAnexosOficiaisVaziaConfirmaCoberturaSemAnexos()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ConnectionAwareRestClient
        {
            OverridePayload = """
                {"courses":[{"assignments":[{"id":5001,"grade":40,"intro":"Enunciado oficial","introattachments":[]}]}]}
                """
        };
        var gateway = new MoodleAssignmentSettingsGateway(
            Options.Create(new MoodleApiOptions { UseStubData = false }),
            new SwitchingCredentialsProvider(),
            restClient,
            cache);

        var result = await gateway.GetAssignmentSettingsAsync("teacher-a", "101", "5001", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.IntroAttachments!);
        Assert.True(result.IntroAttachmentsComplete);
    }

    [Fact]
    public async Task NegacaoDePermissaoContinuaSendoErroEnaoListaVazia()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ConnectionAwareRestClient
        {
            Failure = new MoodleApiException(
                MoodleErrorContract.PermissionDenied,
                "permissao negada")
        };
        var gateway = new MoodleAssignmentSettingsGateway(
            Options.Create(new MoodleApiOptions { UseStubData = false }),
            new SwitchingCredentialsProvider(),
            restClient,
            cache);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            gateway.GetAssignmentSettingsAsync("teacher-a", "101", "5001", CancellationToken.None));

        Assert.Equal(MoodleErrorContract.PermissionDenied, error.ErrorCode);
        Assert.Equal(1, restClient.CallCount);
    }

    [Fact]
    public async Task LeituraFrescaIgnoraCacheAntigoEAtualizaValorArmazenado()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ConnectionAwareRestClient();
        var gateway = new MoodleAssignmentSettingsGateway(
            Options.Create(new MoodleApiOptions { UseStubData = false }),
            new SwitchingCredentialsProvider(),
            restClient,
            cache);

        var first = await gateway.GetAssignmentSettingsAsync("teacher-a", "101", "5001", CancellationToken.None);
        restClient.OverridePayload = """
            {"courses":[{"assignments":[{"id":5001,"grade":20}]}]}
            """;
        var refreshed = await gateway.GetAssignmentSettingsFreshAsync("teacher-a", "101", "5001", CancellationToken.None);
        var cachedAfterRefresh = await gateway.GetAssignmentSettingsAsync("teacher-a", "101", "5001", CancellationToken.None);

        Assert.Equal(10m, first!.MaxGrade);
        Assert.Equal(20m, refreshed!.MaxGrade);
        Assert.Equal(20m, cachedAfterRefresh!.MaxGrade);
        Assert.Equal(2, restClient.CallCount);
    }

    [Fact]
    public async Task LeRubricaAtivaAssociadaAoCmidEConservaFaixaOficial()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ConnectionAwareRestClient
        {
            OverridePayload = """
                {"areas":[{"cmid":9001,"contextid":99,"component":"mod_assign","areaname":"submissions","activemethod":"rubric","definitions":[{"id":700,"method":"rubric","name":"Rubrica sintetica","description":"Rubrica oficial","status":20,"rubric_criteria":[{"id":11,"sortorder":0,"description":"Precisao","levels":[{"id":101,"score":0,"definition":"Insuficiente"},{"id":102,"score":5,"definition":"Completa"}]},{"id":12,"sortorder":1,"description":"Analise","levels":[{"id":201,"score":0,"definition":"Ausente"},{"id":202,"score":10,"definition":"Consistente"}]}]}]}],"warnings":[]}
                """
        };
        var gateway = new MoodleAssignmentSettingsGateway(
            Options.Create(new MoodleApiOptions { UseStubData = false }),
            new SwitchingCredentialsProvider(),
            restClient,
            cache);

        var rubric = await gateway.GetActiveRubricFreshAsync("teacher-a", 9001, CancellationToken.None);

        Assert.NotNull(rubric);
        Assert.Equal("active", rubric!.AssociationStatus);
        Assert.Equal(700, rubric.DefinitionId);
        Assert.Equal(9001L, rubric.CourseModuleId);
        Assert.Equal("submissions", rubric.AreaName);
        Assert.Equal("rubric", rubric.Method);
        Assert.Equal(0m, rubric.MinimumPoints);
        Assert.Equal(15m, rubric.MaximumPoints);
        Assert.Matches("^[a-f0-9]{64}$", rubric.DefinitionVersion!);
        Assert.Equal(2, rubric.Criteria!.Count);
        Assert.Equal(5m, rubric.Criteria[0].MaximumPoints);
        Assert.Equal("Insuficiente", rubric.Criteria[0].Levels[0].Description);
        Assert.Equal("core_grading_get_definitions", Assert.Single(restClient.FunctionNames));
        Assert.Equal(9001L, Assert.IsType<long[]>(restClient.LastParameters!["cmids"])[0]);
        Assert.Equal("submissions", restClient.LastParameters["areaname"]);
        Assert.Equal(true, restClient.LastParameters["activeonly"]);
    }

    [Fact]
    public async Task VersaoDaRubricaNaoDependeDaOrdemDeArraysNaResposta()
    {
        const string firstPayload = """
            {"areas":[{"cmid":9001,"component":"mod_assign","areaname":"submissions","activemethod":"rubric","definitions":[{"id":700,"method":"rubric","name":"Rubrica sintetica","description":"Rubrica oficial","rubric_criteria":[{"id":11,"description":"Precisao","levels":[{"id":101,"score":0,"definition":"Insuficiente"},{"id":102,"score":5,"definition":"Completa"}]},{"id":12,"description":"Analise","levels":[{"id":201,"score":0,"definition":"Ausente"},{"id":202,"score":10,"definition":"Consistente"}]}]}]}],"warnings":[]}
            """;
        const string reorderedPayload = """
            {"areas":[{"cmid":9001,"component":"mod_assign","areaname":"submissions","activemethod":"rubric","definitions":[{"id":700,"method":"rubric","name":"Rubrica sintetica","description":"Rubrica oficial","rubric_criteria":[{"id":12,"description":"Analise","levels":[{"id":202,"score":10,"definition":"Consistente"},{"id":201,"score":0,"definition":"Ausente"}]},{"id":11,"description":"Precisao","levels":[{"id":102,"score":5,"definition":"Completa"},{"id":101,"score":0,"definition":"Insuficiente"}]}]}]}],"warnings":[]}
            """;

        async Task<string?> ReadVersionAsync(string payload)
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var gateway = new MoodleAssignmentSettingsGateway(
                Options.Create(new MoodleApiOptions { UseStubData = false }),
                new SwitchingCredentialsProvider(),
                new ConnectionAwareRestClient { OverridePayload = payload },
                cache);
            return (await gateway.GetActiveRubricFreshAsync("teacher-a", 9001, CancellationToken.None))?.DefinitionVersion;
        }

        var first = await ReadVersionAsync(firstPayload);
        var reordered = await ReadVersionAsync(reorderedPayload);

        Assert.NotNull(first);
        Assert.Equal(first, reordered);
    }

    [Fact]
    public async Task RubricaInativaNaoEConfundidaComDefinicaoAssociada()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ConnectionAwareRestClient
        {
            OverridePayload = """
                {"areas":[{"cmid":9001,"contextid":99,"component":"mod_assign","areaname":"submissions","activemethod":"guide","definitions":[{"id":700,"method":"guide","name":"Guia","status":20}]}],"warnings":[]}
                """
        };
        var gateway = new MoodleAssignmentSettingsGateway(
            Options.Create(new MoodleApiOptions { UseStubData = false }),
            new SwitchingCredentialsProvider(),
            restClient,
            cache);

        var rubric = await gateway.GetActiveRubricFreshAsync("teacher-a", 9001, CancellationToken.None);

        Assert.NotNull(rubric);
        Assert.Equal("no_active_rubric", rubric!.AssociationStatus);
        Assert.Equal("guide", rubric.Method);
        Assert.Null(rubric.MaximumPoints);
    }

    [Fact]
    public async Task RubricaComAvisoDeAcessoNaoViraResultadoVazioConfiavel()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ConnectionAwareRestClient
        {
            OverridePayload = """
                {"areas":[],"warnings":[{"item":"module","itemid":9001,"warningcode":"1","message":"synthetic access warning"}]}
                """
        };
        var gateway = new MoodleAssignmentSettingsGateway(
            Options.Create(new MoodleApiOptions { UseStubData = false }),
            new SwitchingCredentialsProvider(),
            restClient,
            cache);

        var rubric = await gateway.GetActiveRubricFreshAsync("teacher-a", 9001, CancellationToken.None);

        Assert.NotNull(rubric);
        Assert.Equal("unavailable", rubric!.AssociationStatus);
        Assert.Null(rubric.DefinitionId);
    }

    private static MoodleCredentials Credentials(string clientId, string connectionId, string username) =>
        new(clientId, connectionId, connectionId, "https://moodle.example", username, "password", "moodle", false);

    private sealed class SwitchingCredentialsProvider : IMoodleCredentialsProvider
    {
        public MoodleCredentials Current { get; set; } = Credentials("client-a", "connection-a", "teacher-a");

        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);
    }

    private sealed class ConnectionAwareRestClient : IMoodleRestClient
    {
        public int CallCount { get; private set; }
        public List<string> FunctionNames { get; } = [];
        public IReadOnlyDictionary<string, object?>? LastParameters { get; private set; }
        public string? OverridePayload { get; set; }

        public Exception? Failure { get; init; }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, allowServiceToken: false, cancellationToken);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            CallCount++;
            FunctionNames.Add(functionName);
            LastParameters = parameters;
            if (Failure is not null)
            {
                throw Failure;
            }
            if (OverridePayload is not null)
            {
                using var overrideDocument = JsonDocument.Parse(OverridePayload);
                return Task.FromResult(overrideDocument.RootElement.Clone());
            }

            var grade = connection.ConnectionId == "connection-a" ? 10 : 20;
            using var document = JsonDocument.Parse($"{{\"courses\":[{{\"assignments\":[{{\"id\":5001,\"grade\":{grade}}}]}}]}}");
            return Task.FromResult(document.RootElement.Clone());
        }
    }
}
