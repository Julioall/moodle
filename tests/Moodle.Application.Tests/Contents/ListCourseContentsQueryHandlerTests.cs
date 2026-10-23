using Moodle.Application.Abstractions;
using Moodle.Application.Contents;
using Moodle.Domain;

namespace Moodle.Application.Tests.Contents;

public class ListCourseContentsQueryHandlerTests
{
    [Fact]
    public async Task Deve_resolver_curso_e_normalizar_filtro_antes_de_listar_conteudos()
    {
        var coursesGateway = new FakeCoursesGateway();
        var contentsGateway = new FakeContentsGateway();
        var sut = new ListCourseContentsQueryHandler(coursesGateway, contentsGateway);

        var result = await sut.Handle(
            new ListCourseContentsQuery(
                "usuario-42",
                "CURSO-1",
                [" Page ", "page"],
                IncludeHidden: true,
                OnlyWithFiles: false),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("usuario-42", coursesGateway.LastUserExternalId);
        Assert.Equal("CURSO-1", coursesGateway.LastCourseId);
        Assert.Equal("123", contentsGateway.LastCourseId);
        Assert.Equal(["page"], contentsGateway.LastModuleTypes);
        Assert.True(contentsGateway.LastIncludeHidden);
        Assert.False(contentsGateway.LastOnlyWithFiles);
    }

    [Fact]
    public async Task Deve_retornar_null_quando_curso_nao_estiver_vinculado()
    {
        var coursesGateway = new FakeCoursesGateway { ReturnNullCourse = true };
        var contentsGateway = new FakeContentsGateway();
        var sut = new ListCourseContentsQueryHandler(coursesGateway, contentsGateway);

        var result = await sut.Handle(
            new ListCourseContentsQuery("usuario-42", "inexistente", [], false, false),
            CancellationToken.None);

        Assert.Null(result);
        Assert.False(contentsGateway.WasCalled);
    }

    [Fact]
    public async Task Deve_consultar_modulo_por_cmid_ou_instance_id()
    {
        var contentsGateway = new FakeContentsGateway();
        var sut = new GetCourseModuleQueryHandler(new FakeCoursesGateway(), contentsGateway);

        var byModuleId = await sut.Handle(
            new GetCourseModuleQuery("usuario-42", "CURSO-1", "11"),
            CancellationToken.None);
        var byInstanceId = await sut.Handle(
            new GetCourseModuleQuery("usuario-42", "CURSO-1", "501"),
            CancellationToken.None);

        Assert.NotNull(byModuleId);
        Assert.NotNull(byInstanceId);
        Assert.Equal("Pagina 1", byModuleId!.Name);
        Assert.Equal("Pagina 1", byInstanceId!.Name);
        Assert.Equal(2, contentsGateway.CallCount);
        Assert.True(contentsGateway.LastIncludeHidden);
    }

    [Fact]
    public async Task Deve_auditar_secoes_vazias_e_modulos_sem_metadados()
    {
        var sut = new AuditCourseStructureQueryHandler(new FakeCoursesGateway(), new FakeContentsGateway());

        var audit = await sut.Handle(
            new AuditCourseStructureQuery("usuario-42", "CURSO-1", IncludeHidden: false),
            CancellationToken.None);

        Assert.NotNull(audit);
        Assert.Equal(2, audit!.SectionCount);
        Assert.Equal(1, audit.ModuleCount);
        Assert.Equal(1, audit.EmptySectionCount);
        Assert.Equal(1, audit.ModulesWithoutDatesCount);
        Assert.Contains(audit.Findings, finding => finding.Code == "empty_section");
        Assert.Contains(audit.Findings, finding => finding.Code == "module_without_dates");
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

    private sealed class FakeContentsGateway : IMoodleCourseContentsGateway
    {
        public bool WasCalled { get; private set; }

        public int CallCount { get; private set; }

        public string LastCourseId { get; private set; } = string.Empty;

        public IReadOnlyCollection<string> LastModuleTypes { get; private set; } = [];

        public bool LastIncludeHidden { get; private set; }

        public bool LastOnlyWithFiles { get; private set; }

        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId,
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            CallCount++;
            LastCourseId = courseId;
            LastModuleTypes = moduleTypes;
            LastIncludeHidden = includeHidden;
            LastOnlyWithFiles = onlyWithFiles;

            return Task.FromResult(CreateContents(courseId, moduleTypes, includeHidden, onlyWithFiles));
        }

        private static CourseContentsSummary CreateContents(
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles)
        {
            var module = new CourseModuleSummary(
                "11",
                "501",
                "page",
                "Pagina 1",
                "https://moodle.example/mod/page/view.php?id=11",
                true,
                true,
                "Descricao",
                null,
                [],
                []);

            return new CourseContentsSummary(
                courseId,
                moduleTypes.ToArray(),
                includeHidden,
                onlyWithFiles,
                [
                    new CourseSectionSummary("1", 1, "Topico 1", null, true, 1, false, [module]),
                    new CourseSectionSummary("2", 2, "Topico 2", null, true, 0, true, [])
                ]);
        }
    }
}
