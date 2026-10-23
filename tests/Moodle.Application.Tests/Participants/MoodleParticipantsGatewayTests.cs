using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Participants;

public sealed class MoodleParticipantsGatewayTests
{
    [Theory]
    [InlineData("", ParticipantAccessEvidence.Unavailable)]
    [InlineData("\"lastcourseaccess\":null,", ParticipantAccessEvidence.Unavailable)]
    [InlineData("\"lastcourseaccess\":\"invalid\",", ParticipantAccessEvidence.Unavailable)]
    [InlineData("\"lastcourseaccess\":-1,", ParticipantAccessEvidence.Unavailable)]
    [InlineData("\"lastcourseaccess\":9223372036854775807,", ParticipantAccessEvidence.Unavailable)]
    [InlineData("\"lastcourseaccess\":0,", ParticipantAccessEvidence.Never)]
    [InlineData("\"lastcourseaccess\":\"0\",", ParticipantAccessEvidence.Never)]
    [InlineData("\"lastcourseaccess\":1700000000,", ParticipantAccessEvidence.Observed)]
    public async Task AccessEvidenceDistinguishesCourseFromPlatformAndAmbiguousNull(string fragment, ParticipantAccessEvidence expected)
    {
        var handler = new JsonHandler($$"""
            [{"id":1,"fullname":"Synthetic student",{{fragment}}"lastaccess":0,"roles":[],"groups":[]}]
            """);
        var participants = await CreateGateway(handler).GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.Active, 1, 20, true, false, null, default);
        var participant = Assert.Single(participants.Participants);
        Assert.Equal(expected, participant.LastCourseAccessEvidence);
        Assert.Equal(ParticipantAccessEvidence.Never, participant.LastAccessEvidence);
        Assert.Null(participant.LastAccessAt);
        if (expected == ParticipantAccessEvidence.Observed) Assert.NotNull(participant.LastCourseAccessAt);
        else Assert.Null(participant.LastCourseAccessAt);
    }

    [Fact]
    public void OldParticipantSnapshotsDefaultToUnknownAccessEvidence()
    {
        var participant = System.Text.Json.JsonSerializer.Deserialize<CourseParticipantSummary>(
            """{"userId":"1","fullName":"Synthetic student","roles":[],"groups":[],"lastAccessAt":null,"lastCourseAccessAt":null}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(ParticipantAccessEvidence.Unknown, participant.LastAccessEvidence);
        Assert.Equal(ParticipantAccessEvidence.Unknown, participant.LastCourseAccessEvidence);
    }

    [Fact]
    public async Task Solicita_roles_e_groups_e_preserva_grupos()
    {
        var handler = new JsonHandler("""
            [{"id":123,"fullname":"Aluno","suspended":false,"roles":[{"roleid":5,"shortname":"student","name":"Estudante"}],"groups":[{"id":9,"name":"Turma A"}]}]
            """, "[]");
        var sut = CreateGateway(handler);

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.All, 1, 20, false, false, null, CancellationToken.None);

        var body = Uri.UnescapeDataString(handler.LastRequestBody);
        Assert.Contains("roles", body);
        Assert.Contains("groups", body);
        Assert.DoesNotContain("token", handler.LastRequestUri!.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Turma A", Assert.Single(result.Participants).Groups[0].Name);
    }

    [Fact]
    public async Task Usa_onlyactive_do_Moodle_para_consultar_matriculas_ativas()
    {
        var handler = new JsonHandler(
            "[{\"id\":123,\"fullname\":\"Aluno\",\"suspended\":false,\"roles\":[],\"groups\":[]}]" );
        var sut = CreateGateway(handler);

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.Active, 1, 20, false, false, null, CancellationToken.None);

        var body = Uri.UnescapeDataString(handler.LastRequestBody);
        Assert.Contains("onlyactive", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[value]=1", body, StringComparison.OrdinalIgnoreCase);
        Assert.Single(result.Participants);
        Assert.Equal("active", result.Participants[0].EnrollmentStatus);
    }

    [Theory]
    [InlineData(25, false, 2)]
    [InlineData(25, true, 1)]
    [InlineData(100, false, 3)]
    [InlineData(100, true, 2)]
    [InlineData(500, false, 7)]
    [InlineData(500, true, 6)]
    public async Task Course_overview_gateway_respects_total_http_budget_for_synthetic_rosters(
        int studentCount,
        bool warmToken,
        int expectedBudgetCalls)
    {
        var participantHandler = new JsonHandler(CreateParticipantBatches(studentCount));
        var tokenHandler = new JsonHandler("""{"token":"test-token"}""");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var credentialsProvider = new FakeCredentialsProvider();
        var tokenProvider = CreateTokenProvider(tokenHandler, cache);
        if (warmToken)
        {
            await tokenProvider.GetAccessTokenAsync(
                await credentialsProvider.GetCurrentCredentialsAsync(CancellationToken.None),
                CancellationToken.None);
        }

        var sut = CreateBudgetGateway(participantHandler, tokenProvider, credentialsProvider);
        var tokenCallsBeforeRefresh = tokenHandler.RequestBodies.Count;

        CourseParticipantsPage result;
        using (MoodleOverviewStudentSyncBudget.Begin(8, TimeSpan.FromSeconds(60)))
        {
            result = await sut.GetCourseParticipantsAsync(
                "42", "10", ParticipantStatusFilter.Active, 1, 500, true, false, null, CancellationToken.None);
        }

        Assert.Equal(studentCount, result.Participants.Count);
        Assert.Equal(studentCount % 100 == 0 ? studentCount / 100 + 1 : 1, participantHandler.RequestBodies.Count);
        Assert.Equal(expectedBudgetCalls, participantHandler.RequestBodies.Count + tokenHandler.RequestBodies.Count - tokenCallsBeforeRefresh);
    }

    [Fact]
    public async Task Course_overview_status_fallback_and_cold_token_fit_exactly_eight_http_attempts()
    {
        var responses = new List<string>
        {
            """{"exception":"required_capability_exception","errorcode":"nopermissions","message":"Sem permissao"}"""
        };
        responses.AddRange(CreateParticipantBatches(500));
        var participantHandler = new JsonHandler(responses.ToArray());
        var tokenHandler = new JsonHandler("""{"token":"test-token"}""");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var credentialsProvider = new FakeCredentialsProvider();
        var tokenProvider = CreateTokenProvider(tokenHandler, cache);
        var sut = CreateBudgetGateway(participantHandler, tokenProvider, credentialsProvider);

        CourseParticipantsPage result;
        using (MoodleOverviewStudentSyncBudget.Begin(8, TimeSpan.FromSeconds(60)))
        {
            result = await sut.GetCourseParticipantsAsync(
                "42", "10", ParticipantStatusFilter.Active, 1, 500, true, false, null, CancellationToken.None);

            Assert.Throws<MoodleOverviewStudentSyncBudgetExceededException>(MoodleOverviewStudentSyncBudget.ReserveCall);
        }

        Assert.Equal(500, result.Participants.Count);
        Assert.True(result.ClassificationDiagnostics!.UsedStatusFilterFallback);
        Assert.Equal(7, participantHandler.RequestBodies.Count);
        Assert.Single(tokenHandler.RequestBodies);
    }

    [Fact]
    public async Task Filtra_grupo_localmente_quando_Moodle_rejeita_parametro_groupid()
    {
        var handler = new JsonHandler(
            "{\"exception\":\"invalid_parameter_exception\",\"errorcode\":\"invalidparameter\",\"message\":\"groupid nao suportado\"}",
            "[{\"id\":123,\"fullname\":\"Aluno\",\"suspended\":false,\"roles\":[],\"groups\":[{\"id\":9,\"name\":\"Turma A\"}]}]");
        var sut = CreateGateway(handler);

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.Active, 1, 20, false, false, "9", CancellationToken.None);

        Assert.Single(result.Participants);
        Assert.Equal("123", result.Participants[0].UserId);
        Assert.DoesNotContain("groupid", Uri.UnescapeDataString(handler.LastRequestBody), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Usa_onlysuspended_do_Moodle_mesmo_quando_campo_suspended_nao_esta_disponivel()
    {
        var handler = new JsonHandler("[{\"id\":123,\"fullname\":\"Aluno suspenso\",\"roles\":[],\"groups\":[]}]");
        var sut = CreateGateway(handler);

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.Suspended, 1, 20, false, false, null, CancellationToken.None);

        var body = Uri.UnescapeDataString(handler.LastRequestBody);
        Assert.Contains("onlysuspended", body, StringComparison.OrdinalIgnoreCase);
        var participant = Assert.Single(result.Participants);
        Assert.True(participant.Suspended);
        Assert.Equal("suspended", participant.EnrollmentStatus);
    }

    [Fact]
    public async Task Todos_combina_matriculas_ativas_e_suspensas_com_status_individual()
    {
        var sut = CreateGateway(new JsonHandler(
            "[{\"id\":1,\"fullname\":\"Ativo\",\"roles\":[],\"groups\":[]}]",
            "[{\"id\":2,\"fullname\":\"Suspenso\",\"roles\":[],\"groups\":[]}]"));

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.All, 1, 20, false, false, null, CancellationToken.None);

        Assert.Equal(2, result.Participants.Count);
        var active = Assert.Single(result.Participants, item => item.UserId == "1");
        var suspended = Assert.Single(result.Participants, item => item.UserId == "2");
        Assert.False(active.Suspended);
        Assert.Equal("active", active.EnrollmentStatus);
        Assert.True(suspended.Suspended);
        Assert.Equal("suspended", suspended.EnrollmentStatus);
    }

    [Fact]
    public async Task Usa_grupos_embutidos_quando_funcao_de_grupos_e_negada()
    {
        var handler = new JsonHandler(
            "{\"exception\":\"required_capability_exception\",\"errorcode\":\"nopermissions\",\"message\":\"Sem permissao\"}",
            "[{\"id\":123,\"fullname\":\"Aluno\",\"suspended\":false,\"roles\":[],\"groups\":[{\"id\":9,\"name\":\"Turma A\"}]}]");
        var sut = CreateGateway(handler);

        var result = await sut.GetCourseGroupsAsync("42", "10", CancellationToken.None);

        var group = Assert.Single(result);
        Assert.Equal("9", group.GroupId);
        Assert.Equal("10", group.CourseId);
        Assert.Equal("Turma A", group.Name);
    }

    [Fact]
    public async Task Inclui_participante_sem_roles_quando_students_only()
    {
        var sut = CreateGateway(new JsonHandler("""
            [{"id":123,"fullname":"Possivel aluno","suspended":false,"roles":[],"groups":[]}]
            """, "[]"));

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.All, 1, 20, true, false, null, CancellationToken.None);

        Assert.Single(result.Participants);
        Assert.NotNull(result.ClassificationDiagnostics);
        Assert.Equal(1, result.ClassificationDiagnostics.IncludedByFallbackCount);
        Assert.True(result.ClassificationDiagnostics.HasEmptyRoles);
        Assert.Equal(ParticipantClassificationMode.Fallback, result.ClassificationDiagnostics.Mode);
    }

    [Fact]
    public async Task Exclui_registro_roleless_sem_nome_da_lista_de_alunos()
    {
        var sut = CreateGateway(new JsonHandler("""
            [
              {"id":245956,"fullname":null,"suspended":false,"roles":[],"groups":[]},
              {"id":123,"fullname":"Aluno sem role","suspended":false,"roles":[],"groups":[]}
            ]
            """, "[]"));

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.Active, 1, 20, true, false, null, CancellationToken.None);

        var participant = Assert.Single(result.Participants);
        Assert.Equal("123", participant.UserId);
    }

    [Fact]
    public async Task Exclui_toda_role_preenchida_que_nao_seja_student()
    {
        var sut = CreateGateway(new JsonHandler("""
            [
              {"id":1,"fullname":"Professor","suspended":false,"roles":[{"roleid":3,"shortname":"editingteacher-go","name":"Professor - GO"}],"groups":[]},
              {"id":2,"fullname":"Monitora","suspended":false,"roles":[{"roleid":8,"shortname":"monitor_go","name":"Monitor - GO"}],"groups":[]},
              {"id":3,"fullname":"Aluna","suspended":false,"roles":[{"roleid":5,"shortname":"student","name":"Estudante"}],"groups":[]}
            ]
            """, "[]"));

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.All, 1, 20, true, false, null, CancellationToken.None);

        Assert.Equal("Aluna", Assert.Single(result.Participants).FullName);
        Assert.Equal(2, result.ClassificationDiagnostics!.ExcludedKnownStaffCount);
        Assert.Equal(0, result.ClassificationDiagnostics.IncludedByFallbackCount);
    }

    [Fact]
    public async Task Nao_sinaliza_grupo_vazio_de_equipe_como_grupo_vazio_de_aluno()
    {
        var sut = CreateGateway(new JsonHandler("""
            [
              {"id":1,"fullname":"Professor","suspended":false,"roles":[{"roleid":3,"shortname":"editingteacher","name":"Professor"}],"groups":[]},
              {"id":2,"fullname":"Aluna","suspended":false,"roles":[{"roleid":5,"shortname":"student","name":"Estudante"}],"groups":[{"id":9,"name":"Turma A"}]}
            ]
            """));

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.Active, 1, 20, false, false, null, CancellationToken.None);

        Assert.True(result.ClassificationDiagnostics!.HasEmptyGroups);
        Assert.False(result.ClassificationDiagnostics.HasEmptyStudentGroups);
    }

    [Fact]
    public async Task Usa_populacao_sem_filtro_quando_onlyactive_e_negado_e_preserva_alunos()
    {
        var sut = CreateGateway(new JsonHandler(
            """{"exception":"required_capability_exception","errorcode":"nopermissions","message":"Sem permissao"}""",
            """
            [
              {"id":1,"fullname":"Professor","suspended":false,"roles":[{"roleid":3,"shortname":"editingteacher-go","name":"Professor - GO"}],"groups":[]},
              {"id":2,"fullname":"Aluno ativo","roles":[{"roleid":5,"shortname":"student","name":"Estudante"}],"groups":[]},
              {"id":3,"fullname":"Aluno suspenso","suspended":true,"roles":[{"roleid":5,"shortname":"student","name":"Estudante"}],"groups":[]}
            ]
            """));

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.Active, 1, 20, true, false, null, CancellationToken.None);

        var participant = Assert.Single(result.Participants);
        Assert.Equal("2", participant.UserId);
        Assert.Equal("active", participant.EnrollmentStatus);
        Assert.True(result.ClassificationDiagnostics!.UsedStatusFilterFallback);
        Assert.Equal(1, result.ClassificationDiagnostics.ExcludedKnownStaffCount);
    }

    [Fact]
    public async Task Status_all_combina_populacoes_quando_filtros_sao_negados()
    {
        var sut = CreateGateway(new JsonHandler(
            """{"exception":"required_capability_exception","errorcode":"nopermissions","message":"Sem permissao"}""",
            """[{"id":2,"fullname":"Aluno ativo","suspended":false,"roles":[{"roleid":5,"shortname":"student","name":"Estudante"}],"groups":[]}]""",
            """{"exception":"required_capability_exception","errorcode":"nopermissions","message":"Sem permissao"}""",
            """[{"id":3,"fullname":"Aluno suspenso","suspended":true,"roles":[{"roleid":5,"shortname":"student","name":"Estudante"}],"groups":[]}]"""));

        var result = await sut.GetCourseParticipantsAsync(
            "42", "10", ParticipantStatusFilter.All, 1, 20, true, false, null, CancellationToken.None);

        Assert.Equal(2, result.Participants.Count);
        Assert.Contains(result.Participants, participant => participant.UserId == "2" && participant.EnrollmentStatus == "active");
        Assert.Contains(result.Participants, participant => participant.UserId == "3" && participant.EnrollmentStatus == "suspended");
        Assert.True(result.ClassificationDiagnostics!.UsedStatusFilterFallback);
    }

    [Fact]
    public async Task Busca_aluno_na_terceira_fatia_sem_reler_offsets_anteriores()
    {
        static string Batch(int first, int count) => System.Text.Json.JsonSerializer.Serialize(
            Enumerable.Range(first, count).Select(id => new
            {
                id,
                fullname = $"Aluno {id}",
                roles = new[] { new { roleid = 5, shortname = "student", name = "Estudante" } },
                groups = Array.Empty<object>()
            }));
        var handler = new JsonHandler(Batch(1, 100), Batch(101, 100), Batch(201, 1));
        var sut = CreateGateway(handler);

        var result = await sut.FindStudentAsync("42", "10", "201", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("201", result!.UserId);
        Assert.Equal(3, handler.RequestBodies.Count);
        var requestBodies = handler.RequestBodies.Select(Uri.UnescapeDataString).ToArray();
        Assert.Contains("options[0][value]=0", requestBodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("options[0][value]=100", requestBodies[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("options[0][value]=200", requestBodies[2], StringComparison.OrdinalIgnoreCase);
        Assert.All(requestBodies, body => Assert.Contains("onlyactive", body, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Busca_aluno_com_fallback_onlyactive_repetindo_o_mesmo_offset_e_usando_classificacao_existente()
    {
        const string unfilteredParticipants = """
            [
              {"id":1,"fullname":"Professor","roles":[{"roleid":3,"shortname":"editingteacher","name":"Professor"}],"groups":[]},
              {"id":2,"fullname":null,"roles":[],"groups":[]},
              {"id":3,"fullname":"Aluno sem papel explícito","roles":[],"groups":[]},
              {"id":4,"fullname":"Aluno suspenso","suspended":true,"roles":[{"roleid":5,"shortname":"student","name":"Estudante"}],"groups":[]}
            ]
            """;
        var handler = new JsonHandler(
            """{"exception":"required_capability_exception","errorcode":"nopermissions","message":"Sem permissao"}""",
            unfilteredParticipants);
        var sut = CreateGateway(handler);

        var result = await sut.FindStudentAsync("42", "10", "3", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("3", result!.UserId);
        Assert.Equal(2, handler.RequestBodies.Count);
        var requestBodies = handler.RequestBodies.Select(Uri.UnescapeDataString).ToArray();
        Assert.Contains("options[0][value]=0", requestBodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("options[0][value]=0", requestBodies[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("onlyactive", requestBodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onlyactive", requestBodies[1], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1")] // known staff
    [InlineData("2")] // role-less record without a name
    [InlineData("4")] // suspended when onlyactive is unavailable
    public async Task Busca_aluno_com_fallback_nao_retorna_staff_registro_incerto_sem_nome_ou_suspenso(string studentId)
    {
        const string unfilteredParticipants = """
            [
              {"id":1,"fullname":"Professor","roles":[{"roleid":3,"shortname":"editingteacher","name":"Professor"}],"groups":[]},
              {"id":2,"fullname":null,"roles":[],"groups":[]},
              {"id":3,"fullname":"Aluno sem papel explícito","roles":[],"groups":[]},
              {"id":4,"fullname":"Aluno suspenso","suspended":true,"roles":[{"roleid":5,"shortname":"student","name":"Estudante"}],"groups":[]}
            ]
            """;
        var handler = new JsonHandler(
            """{"exception":"required_capability_exception","errorcode":"nopermissions","message":"Sem permissao"}""",
            unfilteredParticipants);
        var sut = CreateGateway(handler);

        var result = await sut.FindStudentAsync("42", "10", studentId, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("options[0][value]=0", Uri.UnescapeDataString(handler.RequestBodies[0]), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("options[0][value]=0", Uri.UnescapeDataString(handler.RequestBodies[1]), StringComparison.OrdinalIgnoreCase);
    }

    private static MoodleParticipantsGateway CreateGateway(JsonHandler handler)
    {
        return new MoodleParticipantsGateway(
            Options.Create(new MoodleApiOptions()),
            new FakeCredentialsProvider(),
            new MoodleRestClient(
                new HttpClient(handler),
                new FakeTokenProvider(),
                NullLogger<MoodleRestClient>.Instance));
    }

    private static MoodleParticipantsGateway CreateBudgetGateway(
        JsonHandler handler,
        IMoodleAccessTokenProvider tokenProvider,
        IMoodleCredentialsProvider credentialsProvider) =>
        new(
            Options.Create(new MoodleApiOptions()),
            credentialsProvider,
            new MoodleRestClient(
                new HttpClient(handler),
                tokenProvider,
                NullLogger<MoodleRestClient>.Instance));

    private static MoodleAccessTokenProvider CreateTokenProvider(JsonHandler handler, IMemoryCache cache) =>
        new(
            new HttpClient(handler),
            cache,
            Options.Create(new MoodleApiOptions()),
            Options.Create(new ConnectorSecretsOptions { TokenCacheMinutes = 20 }),
            new FixedEndpointValidator(),
            NullLogger<MoodleAccessTokenProvider>.Instance);

    private static string[] CreateParticipantBatches(int studentCount)
    {
        var students = Enumerable.Range(1, studentCount)
            .Select(id => new
            {
                id,
                fullname = $"Aluno {id}",
                suspended = false,
                roles = new[] { new { roleid = 5, shortname = "student", name = "Estudante" } },
                groups = Array.Empty<object>(),
            })
            .ToArray();
        var responses = students
            .Chunk(100)
            .Select(batch => JsonSerializer.Serialize(batch))
            .ToList();
        if (studentCount % 100 == 0)
        {
            responses.Add("[]");
        }

        return responses.ToArray();
    }

    private sealed class JsonHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public JsonHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public Uri? LastRequestUri { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(LastRequestBody);
            var json = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeTokenProvider : IMoodleAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(
            MoodleCredentials connection,
            CancellationToken cancellationToken) =>
            Task.FromResult("token");

        public void Invalidate(MoodleCredentials connection)
        {
        }
    }

    private sealed class FakeCredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials(
                "client", "connection", "default", "https://moodle.example", "user", "password", "target", false));
    }

    private sealed class FixedEndpointValidator : IMoodleEndpointValidator
    {
        public Task<Uri> ValidateAsync(string baseUrl, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri("https://moodle.example"));
    }
}
