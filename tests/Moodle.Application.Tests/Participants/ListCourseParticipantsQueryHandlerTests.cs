using Moodle.Application.Abstractions;
using Moodle.Application.Participants;
using Moodle.Domain;

namespace Moodle.Application.Tests.Participants;

public class ListCourseParticipantsQueryHandlerTests
{
    [Fact]
    public async Task Deve_resolver_curso_e_listar_participantes_quando_paginacao_valida()
    {
        var coursesGateway = new FakeCoursesGateway();
        var participantsGateway = new FakeParticipantsGateway();
        var sut = new ListCourseParticipantsQueryHandler(coursesGateway, participantsGateway);

        var result = await sut.Handle(
            new ListCourseParticipantsQuery(
                "usuario-42",
                "CURSO-1",
                ParticipantStatusFilter.Active,
                Page: 1,
                PageSize: 999,
                StudentsOnly: true,
                IncludeEmail: false),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("usuario-42", coursesGateway.LastUserExternalId);
        Assert.Equal("CURSO-1", coursesGateway.LastCourseId);
        Assert.Equal("123", participantsGateway.LastCourseId);
        Assert.Equal(1, participantsGateway.LastPage);
        Assert.Equal(50, participantsGateway.LastPageSize);
        Assert.True(participantsGateway.LastStudentsOnly);
        Assert.False(participantsGateway.LastIncludeEmail);
    }

    [Fact]
    public async Task Deve_retornar_null_quando_curso_nao_estiver_vinculado_ao_usuario()
    {
        var coursesGateway = new FakeCoursesGateway { ReturnNullCourse = true };
        var participantsGateway = new FakeParticipantsGateway();
        var sut = new ListCourseParticipantsQueryHandler(coursesGateway, participantsGateway);

        var result = await sut.Handle(
            new ListCourseParticipantsQuery(
                "usuario-42",
                "inexistente",
                ParticipantStatusFilter.Active,
                Page: 1,
                PageSize: 20,
                StudentsOnly: false,
                IncludeEmail: false),
            CancellationToken.None);

        Assert.Null(result);
        Assert.False(participantsGateway.WasCalled);
    }

    [Fact]
    public async Task Deve_lancar_ArgumentOutOfRangeException_quando_pagina_for_menor_que_1()
    {
        var coursesGateway = new FakeCoursesGateway();
        var participantsGateway = new FakeParticipantsGateway();
        var sut = new ListCourseParticipantsQueryHandler(coursesGateway, participantsGateway);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.Handle(
            new ListCourseParticipantsQuery(
                "usuario-42",
                "CURSO-1",
                ParticipantStatusFilter.Active,
                Page: 0,
                PageSize: 20,
                StudentsOnly: true,
                IncludeEmail: false),
            CancellationToken.None));
    }

    [Fact]
    public async Task Deve_resolver_curso_antes_de_listar_grupos()
    {
        var coursesGateway = new FakeCoursesGateway();
        var participantsGateway = new FakeParticipantsGateway();
        var sut = new ListCourseGroupsQueryHandler(coursesGateway, participantsGateway);

        var result = await sut.Handle(
            new ListCourseGroupsQuery("usuario-42", "CURSO-1"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("123", participantsGateway.LastCourseId);
    }

    [Fact]
    public async Task Deve_resolver_curso_e_repassar_grupo_ao_listar_membros()
    {
        var coursesGateway = new FakeCoursesGateway();
        var participantsGateway = new FakeParticipantsGateway();
        var sut = new ListGroupMembersQueryHandler(coursesGateway, participantsGateway);

        var result = await sut.Handle(
            new ListGroupMembersQuery(
                "usuario-42",
                "CURSO-1",
                "99",
                ParticipantStatusFilter.All,
                Page: 2,
                PageSize: 10,
                IncludeEmail: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("123", participantsGateway.LastCourseId);
        Assert.Equal("99", participantsGateway.LastGroupId);
        Assert.Equal(2, participantsGateway.LastPage);
        Assert.Equal(10, participantsGateway.LastPageSize);
        Assert.False(participantsGateway.LastStudentsOnly);
        Assert.True(participantsGateway.LastIncludeEmail);
    }

    private sealed class FakeCoursesGateway : IMoodleCoursesGateway
    {
        public string LastUserExternalId { get; private set; } = string.Empty;

        public string LastCourseId { get; private set; } = string.Empty;

        public bool ReturnNullCourse { get; init; }

        public Task<PagedCourses> GetMyCoursesAsync(string userExternalId, int limit, int page, CancellationToken cancellationToken) { throw new NotSupportedException(); }

        public Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(
            string userExternalId,
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<CourseSummary?> GetMyCourseAsync(
            string userExternalId,
            string courseId,
            CancellationToken cancellationToken)
        {
            LastUserExternalId = userExternalId;
            LastCourseId = courseId;

            return Task.FromResult(ReturnNullCourse ? null : CreateCourseSummary());
        }

        private static CourseSummary CreateCourseSummary()
        {
            return new CourseSummary(
                "123",
                "ID-123",
                "CURSO-1",
                "Curso 1",
                "Curso 1",
                10,
                "Categoria",
                null,
                null,
                true,
                null,
                null,
                null,
                null,
                null,
                null);
        }
    }

    private sealed class FakeParticipantsGateway : IMoodleParticipantsGateway
    {
        public bool WasCalled { get; private set; }

        public string LastCourseId { get; private set; } = string.Empty;

        public string? LastGroupId { get; private set; }

        public int LastPage { get; private set; }

        public int LastPageSize { get; private set; }

        public bool LastStudentsOnly { get; private set; }

        public bool LastIncludeEmail { get; private set; }
    public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This legacy fixture does not support limited previews.");

        public Task<CourseParticipantsPage> GetCourseParticipantsAsync(
            string userExternalId,
            string courseId,
            ParticipantStatusFilter statusFilter,
            int page,
            int pageSize,
            bool studentsOnly,
            bool includeEmail,
            string? groupId,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastCourseId = courseId;
            LastGroupId = groupId;
            LastPage = page;
            LastPageSize = pageSize;
            LastStudentsOnly = studentsOnly;
            LastIncludeEmail = includeEmail;

            return Task.FromResult(new CourseParticipantsPage(
                courseId,
                page,
                pageSize,
                statusFilter,
                studentsOnly,
                includeEmail,
                HasMore: false,
                [CreateParticipant()]));
        }

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(
            string userExternalId,
            string courseId,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastCourseId = courseId;

            IReadOnlyList<CourseGroupSummary> groups =
            [
                new("99", courseId, "Grupo A", "G-A")
            ];

            return Task.FromResult(groups);
        }

        private static CourseParticipantSummary CreateParticipant()
        {
            return new CourseParticipantSummary(
                "777",
                "Aluno Teste",
                null,
                false,
                null,
                null,
                null,
                [new CourseParticipantRole("5", "student", "Estudante")],
                [new CourseParticipantGroup("99", "Grupo A")]);
        }
    }
}
