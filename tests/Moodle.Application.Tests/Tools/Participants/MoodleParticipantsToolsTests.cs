using System.Text.Json;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Participants;
using Moodle.Domain;
using Moodle.Presentation.Tools;

namespace Moodle.Application.Tests.Tools.Participants;

public class MoodleParticipantsToolsTests
{
    [Fact]
    public async Task Deve_listar_participantes_com_usuario_moodle_resolvido_e_sem_email_por_padrao()
    {
        var mediator = new FakeMediator();
        var selection = new FakeMoodleConnectionSelection();
        var sut = new MoodleParticipantsTools(mediator, selection, new FakeMoodleUserResolver(777));

        var result = await sut.ListarParticipantesCursoAsync("CURSO", 2, 10, "todos", moodleAlias: "goias");

        Assert.False(result.IsError ?? false);
        Assert.Equal("goias", selection.Alias);
        Assert.NotNull(mediator.LastParticipantsQuery);
        Assert.Equal("777", mediator.LastParticipantsQuery!.UserExternalId);
        Assert.Equal("CURSO", mediator.LastParticipantsQuery.CourseId);
        Assert.Equal(2, mediator.LastParticipantsQuery.Page);
        Assert.Equal(10, mediator.LastParticipantsQuery.PageSize);
        Assert.False(mediator.LastParticipantsQuery.IncludeEmail);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("ok", structured.GetProperty("status").GetString());
        var data = structured.GetProperty("data");
        Assert.Equal("123", data.GetProperty("courseId").GetString());
        Assert.Equal("all", data.GetProperty("status").GetString());
        Assert.False(data.GetProperty("studentsOnly").GetBoolean());
        Assert.False(data.GetProperty("includeEmail").GetBoolean());
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        var participant = data.GetProperty("participants")[0];
        Assert.Equal("777", participant.GetProperty("userId").GetString());
        Assert.Equal("Aluno Teste", participant.GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, participant.GetProperty("email").ValueKind);
        Assert.Equal("unknown", participant.GetProperty("enrollmentStatus").GetString());
        Assert.Equal("student", participant.GetProperty("roles")[0].GetProperty("shortName").GetString());
        Assert.Equal("Grupo A", participant.GetProperty("groups")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Deve_listar_alunos_com_flag_students_only()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleParticipantsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarAlunosCursoAsync("CURSO", incluirEmail: true);

        Assert.False(result.IsError ?? false);
        Assert.NotNull(mediator.LastParticipantsQuery);
        Assert.True(mediator.LastParticipantsQuery!.StudentsOnly);
        Assert.True(mediator.LastParticipantsQuery.IncludeEmail);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        Assert.True(data.GetProperty("studentsOnly").GetBoolean());
        Assert.True(data.GetProperty("includeEmail").GetBoolean());
        Assert.Equal("aluno@example.com", data.GetProperty("participants")[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task Deve_retornar_erro_quando_status_for_invalido()
    {
        var sut = new MoodleParticipantsTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarParticipantesCursoAsync("CURSO", status: "bloqueados");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("error", structured.GetProperty("status").GetString());
        Assert.Equal("Filtro de status invalido. Use ativos, suspensos ou todos.", structured.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Deve_retornar_erro_quando_usuario_moodle_nao_for_resolvido()
    {
        var sut = new MoodleParticipantsTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(null));

        var result = await sut.ListarParticipantesCursoAsync("CURSO");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("Usuario nao autenticado para consultar participantes.", structured.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Deve_retornar_erro_quando_curso_nao_for_encontrado()
    {
        var mediator = new FakeMediator { ReturnNullParticipants = true };
        var sut = new MoodleParticipantsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarParticipantesCursoAsync("inexistente");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("Curso nao encontrado entre os cursos vinculados ao usuario.", structured.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Deve_retornar_lista_vazia_quando_curso_nao_tiver_alunos()
    {
        var mediator = new FakeMediator { ReturnEmptyParticipants = true };
        var sut = new MoodleParticipantsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarAlunosCursoAsync("CURSO");

        Assert.False(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        Assert.Equal(0, data.GetProperty("count").GetInt32());
        Assert.Equal(0, data.GetProperty("participants").GetArrayLength());
        Assert.NotEqual(0, structured.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task Deve_alertar_quando_alunos_forem_incluidos_por_fallback()
    {
        var mediator = new FakeMediator { ReturnFallbackDiagnostics = true };
        var sut = new MoodleParticipantsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarAlunosCursoAsync("CURSO");

        Assert.False(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var warnings = structured.GetProperty("warnings");
        Assert.Contains(warnings.EnumerateArray(), warning =>
            warning.GetString()!.Contains("fallback", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings.EnumerateArray(), warning =>
            warning.GetString()!.Contains("roles", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings.EnumerateArray(), warning =>
            warning.GetString()!.Contains("grupos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Nao_alerta_grupos_vazios_de_equipe_ao_listar_todos_os_participantes()
    {
        var mediator = new FakeMediator { ReturnFallbackDiagnostics = true };
        var sut = new MoodleParticipantsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarParticipantesCursoAsync("CURSO");

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.DoesNotContain(structured.GetProperty("warnings").EnumerateArray(), warning =>
            warning.GetString()!.Contains("alunos sem grupos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Deve_distinguir_pagina_vazia_fora_do_intervalo()
    {
        var mediator = new FakeMediator { ReturnEmptyParticipants = true };
        var sut = new MoodleParticipantsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarAlunosCursoAsync("CURSO", pagina: 3);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Contains(structured.GetProperty("warnings").EnumerateArray(), warning =>
            warning.GetString()!.Contains("pagina", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Deve_rejeitar_pagina_menor_que_um(int pagina)
    {
        var mediator = new FakeMediator();
        var sut = new MoodleParticipantsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarAlunosCursoAsync("CURSO", pagina: pagina);

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("invalid_page", structured.GetProperty("errorCode").GetString());
        Assert.Null(mediator.LastParticipantsQuery);
    }

    [Fact]
    public async Task Deve_indicar_que_a_primeira_pagina_nao_tem_proxima_quando_tem_menos_registros_que_o_limite()
    {
        var sut = new MoodleParticipantsTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarAlunosCursoAsync("CURSO", pagina: 1, tamanhoPagina: 20);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.False(data.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Deve_retornar_erro_controlado_quando_moodle_negar_consulta_de_participantes()
    {
        var mediator = new FakeMediator { ThrowOnParticipants = true };
        var sut = new MoodleParticipantsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarParticipantesCursoAsync("CURSO");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("Nao foi possivel listar participantes no Moodle neste momento.", structured.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Deve_mapear_filtro_suspensos()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleParticipantsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarParticipantesCursoAsync("CURSO", status: "suspensos");

        Assert.False(result.IsError ?? false);
        Assert.NotNull(mediator.LastParticipantsQuery);
        Assert.Equal(ParticipantStatusFilter.Suspended, mediator.LastParticipantsQuery!.StatusFilter);
    }

    [Fact]
    public async Task Deve_listar_grupos_do_curso()
    {
        var mediator = new FakeMediator();
        var selection = new FakeMoodleConnectionSelection();
        var sut = new MoodleParticipantsTools(mediator, selection, new FakeMoodleUserResolver(777));

        var result = await sut.ListarGruposCursoAsync("CURSO", "goias");

        Assert.False(result.IsError ?? false);
        Assert.Equal("goias", selection.Alias);
        Assert.NotNull(mediator.LastGroupsQuery);
        Assert.Equal("CURSO", mediator.LastGroupsQuery!.CourseId);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.Equal("99", data.GetProperty("groups")[0].GetProperty("groupId").GetString());
    }

    [Fact]
    public async Task Deve_consultar_membros_de_grupo()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleParticipantsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ConsultarMembrosGrupoAsync("CURSO", "99", status: "ativos");

        Assert.False(result.IsError ?? false);
        Assert.NotNull(mediator.LastGroupMembersQuery);
        Assert.Equal("99", mediator.LastGroupMembersQuery!.GroupId);
        Assert.Equal(ParticipantStatusFilter.Active, mediator.LastGroupMembersQuery.StatusFilter);
    }

    private sealed class FakeMoodleConnectionSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class FakeMoodleUserResolver(long? userId) : IMoodleUserResolver
    {
        public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(userId);
        }
    }

    private sealed class FakeMediator : IMediator
    {
        public ListCourseParticipantsQuery? LastParticipantsQuery { get; private set; }

        public ListCourseGroupsQuery? LastGroupsQuery { get; private set; }

        public ListGroupMembersQuery? LastGroupMembersQuery { get; private set; }

        public bool ReturnNullParticipants { get; init; }

        public bool ReturnEmptyParticipants { get; init; }

        public bool ReturnFallbackDiagnostics { get; init; }

        public bool ThrowOnParticipants { get; init; }

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => Task.CompletedTask;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ListCourseParticipantsQuery participants)
            {
                LastParticipantsQuery = participants;
                if (ThrowOnParticipants)
                {
                    throw new InvalidOperationException("Falha simulada.");
                }

                return ReturnNullParticipants
                    ? Task.FromResult<TResponse>(default!)
                    : Task.FromResult((TResponse)(object)CreateParticipantsPage(
                        participants.Page,
                        participants.IncludeEmail,
                        participants.StudentsOnly,
                        participants.StatusFilter,
                        ReturnEmptyParticipants,
                        ReturnFallbackDiagnostics));
            }

            if (request is ListCourseGroupsQuery groups)
            {
                LastGroupsQuery = groups;
                IReadOnlyList<CourseGroupSummary> data = [new("99", "123", "Grupo A", "G-A")];
                return Task.FromResult((TResponse)data);
            }

            if (request is ListGroupMembersQuery members)
            {
                LastGroupMembersQuery = members;
                return Task.FromResult((TResponse)(object)CreateParticipantsPage(
                    members.Page,
                    members.IncludeEmail,
                    studentsOnly: false,
                    members.StatusFilter));
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            if (request is ListCourseParticipantsQuery participants)
            {
                LastParticipantsQuery = participants;
                if (ThrowOnParticipants)
                {
                    throw new InvalidOperationException("Falha simulada.");
                }

                return Task.FromResult<object?>(
                    ReturnNullParticipants
                        ? null
                        : CreateParticipantsPage(
                            participants.Page,
                            participants.IncludeEmail,
                            participants.StudentsOnly,
                            participants.StatusFilter,
                            ReturnEmptyParticipants,
                            ReturnFallbackDiagnostics));
            }

            if (request is ListCourseGroupsQuery groups)
            {
                LastGroupsQuery = groups;
                IReadOnlyList<CourseGroupSummary> data = [new("99", "123", "Grupo A", "G-A")];
                return Task.FromResult<object?>(data);
            }

            if (request is ListGroupMembersQuery members)
            {
                LastGroupMembersQuery = members;
                return Task.FromResult<object?>(CreateParticipantsPage(
                    members.Page,
                    members.IncludeEmail,
                    studentsOnly: false,
                    members.StatusFilter));
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<object?>();

        private static CourseParticipantsPage CreateParticipantsPage(
            int page,
            bool includeEmail,
            bool studentsOnly,
            ParticipantStatusFilter statusFilter,
            bool empty = false,
            bool fallbackDiagnostics = false)
        {
            return new CourseParticipantsPage(
                "123",
                Page: page,
                PageSize: 20,
                statusFilter,
                studentsOnly,
                includeEmail,
                HasMore: false,
                empty ? [] : [CreateParticipant(includeEmail, statusFilter)],
                fallbackDiagnostics
                    ? new ParticipantClassificationDiagnostics(
                        1, 0, 1, 0, HasEmptyRoles: true, HasEmptyGroups: true,
                        ParticipantClassificationMode.Fallback)
                    : null);
        }

        private static CourseParticipantSummary CreateParticipant(bool includeEmail, ParticipantStatusFilter statusFilter)
        {
            return new CourseParticipantSummary(
                "777",
                "Aluno Teste",
                includeEmail ? "aluno@example.com" : null,
                false,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 31, 13, 0, 0, TimeSpan.Zero),
                [new CourseParticipantRole("5", "student", "Estudante")],
                [new CourseParticipantGroup("99", "Grupo A")],
                statusFilter switch
                {
                    ParticipantStatusFilter.Active => "active",
                    ParticipantStatusFilter.Suspended => "suspended",
                    _ => "unknown"
                });
        }
    }
}
