using Moodle.Application.Abstractions;
using Moodle.Application.Activities;
using Moodle.Domain;

namespace Moodle.Application.Tests.Activities;

public class ListCourseActivitiesQueryHandlerTests
{
    [Fact]
    public async Task ListDetailAndDeadlinesUseSameOfficialCourseBatch()
    {
        var settings = new OfficialSettings();
        var contents = new FakeContentsGateway { ReturnActivityWithoutDates = true };
        var list = await new ListCourseActivitiesQueryHandler(new FakeCoursesGateway(), contents, settings)
            .Handle(new ListCourseActivitiesQuery("synthetic", "CURSO", ["assign"], false), CancellationToken.None);
        Assert.Equal(1, settings.Calls);
        var detail = await new GetCourseActivityQueryHandler(new FakeCoursesGateway(), contents, settings)
            .Handle(new GetCourseActivityQuery("synthetic", "CURSO", "11", ["assign"]), CancellationToken.None);
        Assert.Equal(2, settings.Calls);
        Assert.Equal(list!.Activities[0].Assignment, detail!.Assignment);
        Assert.Equal("Official", detail.Description);
        Assert.Equal(settings.Due, detail.DueAt);
        var deadlines = await new ListActivityDeadlinesQueryHandler(new FakeCoursesGateway(), contents, settings)
            .Handle(new ListActivityDeadlinesQuery("synthetic", "CURSO", ["assign"], false), CancellationToken.None);
        Assert.Equal(3, settings.Calls);
        Assert.Equal(settings.Due, deadlines!.Deadlines[0].DueAt);
        Assert.True(deadlines.Deadlines[0].DueAtKnown);
        Assert.Equal("assignment_settings", deadlines.Deadlines[0].DateSource);
    }

    private sealed class OfficialSettings : IMoodleAssignmentSettingsGateway
    {
        public int Calls { get; private set; }
        public DateTimeOffset Due { get; } = DateTimeOffset.UnixEpoch.AddDays(20);
        public Task<IReadOnlyDictionary<string, AssignmentSettingsSummary>> GetCourseAssignmentSettingsAsync(string userExternalId, string courseId, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("123", courseId);
            return Task.FromResult<IReadOnlyDictionary<string, AssignmentSettingsSummary>>(new Dictionary<string, AssignmentSettingsSummary>
            {
                ["501"] = new("501", 100, Description: "Official", RawGrade: 100, IntroAttachments: [], IntroAttachmentsComplete: true)
                {
                    DescriptionPresent = true, CourseModuleId = 11, IntroFiles = [], IntroFilesComplete = true,
                    DueAt = Due, DueAtKnown = true, OpenAtKnown = true, CutoffAtKnown = true
                }
            });
        }
        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(string userExternalId, string courseId, string assignmentId, CancellationToken cancellationToken) => throw new InvalidOperationException("No individual read allowed");
    }

    [Fact]
    public async Task Deve_resolver_curso_e_listar_atividades_filtradas()
    {
        var coursesGateway = new FakeCoursesGateway();
        var contentsGateway = new FakeContentsGateway();
        var sut = new ListCourseActivitiesQueryHandler(coursesGateway, contentsGateway);

        var result = await sut.Handle(
            new ListCourseActivitiesQuery(
                "usuario-42",
                "CURSO-1",
                [" Assign ", "assign"],
                IncludeHidden: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("usuario-42", coursesGateway.LastUserExternalId);
        Assert.Equal("CURSO-1", coursesGateway.LastCourseId);
        Assert.Equal("123", contentsGateway.LastCourseId);
        Assert.Equal(["assign"], contentsGateway.LastModuleTypes);
        Assert.True(contentsGateway.LastIncludeHidden);
        Assert.Equal(1, result!.Total);
        Assert.Equal("assign", result.Activities[0].ActivityType);
        Assert.True(result.Activities[0].HasDeadline);
    }

    [Fact]
    public async Task Deve_contar_atividades_sem_datas_e_sem_prazo()
    {
        var contentsGateway = new FakeContentsGateway { ReturnActivityWithoutDates = true };
        var sut = new ListCourseActivitiesQueryHandler(new FakeCoursesGateway(), contentsGateway);

        var result = await sut.Handle(
            new ListCourseActivitiesQuery("usuario-42", "CURSO-1", CourseActivityModuleTypes.All, IncludeHidden: false),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.WithoutDatesCount);
        Assert.Equal(1, result.WithoutDeadlineCount);
        Assert.False(result.Activities[0].HasDates);
        Assert.False(result.Activities[0].HasDeadline);
    }

    [Fact]
    public async Task Deve_retornar_null_quando_curso_nao_estiver_vinculado()
    {
        var contentsGateway = new FakeContentsGateway();
        var sut = new ListCourseActivitiesQueryHandler(
            new FakeCoursesGateway { ReturnNullCourse = true },
            contentsGateway);

        var result = await sut.Handle(
            new ListCourseActivitiesQuery("usuario-42", "inexistente", CourseActivityModuleTypes.All, IncludeHidden: false),
            CancellationToken.None);

        Assert.Null(result);
        Assert.False(contentsGateway.WasCalled);
    }

    [Fact]
    public async Task Deve_consultar_atividade_por_cmid_ou_instance_id()
    {
        var contentsGateway = new FakeContentsGateway();
        var sut = new GetCourseActivityQueryHandler(new FakeCoursesGateway(), contentsGateway);

        var byModuleId = await sut.Handle(
            new GetCourseActivityQuery("usuario-42", "CURSO-1", "11", CourseActivityModuleTypes.Assignments),
            CancellationToken.None);
        var byInstanceId = await sut.Handle(
            new GetCourseActivityQuery("usuario-42", "CURSO-1", "501", CourseActivityModuleTypes.Assignments),
            CancellationToken.None);

        Assert.NotNull(byModuleId);
        Assert.NotNull(byInstanceId);
        Assert.Equal("Tarefa 1", byModuleId!.Name);
        Assert.Equal("Tarefa 1", byInstanceId!.Name);
        Assert.Equal(2, contentsGateway.CallCount);
        Assert.True(contentsGateway.LastIncludeHidden);
    }

    [Fact]
    public async Task Deve_listar_prazos_de_atividades()
    {
        var sut = new ListActivityDeadlinesQueryHandler(new FakeCoursesGateway(), new FakeContentsGateway());

        var result = await sut.Handle(
            new ListActivityDeadlinesQuery("usuario-42", "CURSO-1", CourseActivityModuleTypes.All, IncludeHidden: false),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Total);
        Assert.NotNull(result.Deadlines[0].DueAt);
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

        public bool ReturnActivityWithoutDates { get; init; }

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

            return Task.FromResult(CreateContents(courseId, moduleTypes, includeHidden, ReturnActivityWithoutDates));
        }

        private static CourseContentsSummary CreateContents(
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool withoutDates)
        {
            var module = new CourseModuleSummary(
                "11",
                "501",
                "assign",
                "Tarefa 1",
                "https://moodle.example/mod/assign/view.php?id=11",
                true,
                true,
                "Descricao",
                null,
                withoutDates
                    ? []
                    :
                    [
                        new CourseModuleDate("Entrega ate", new DateTimeOffset(2026, 6, 10, 23, 59, 0, TimeSpan.Zero))
                    ],
                []);

            return new CourseContentsSummary(
                courseId,
                moduleTypes.ToArray(),
                includeHidden,
                OnlyWithFiles: false,
                [new CourseSectionSummary("1", 1, "Topico 1", null, true, 1, false, [module])]);
        }
    }
}
