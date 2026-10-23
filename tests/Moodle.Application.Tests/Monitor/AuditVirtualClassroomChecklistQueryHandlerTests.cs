using Moodle.Application.Abstractions;
using Moodle.Application.Monitor.Queries;
using Moodle.Domain;

namespace Moodle.Application.Tests.Monitor;

public sealed class AuditVirtualClassroomChecklistQueryHandlerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static CourseModuleSummary MakeModule(
        string id, string type, string name,
        IReadOnlyList<CourseModuleDate>? dates = null) =>
        new(ModuleId: id, InstanceId: null, ModuleType: type, Name: name,
            Url: null, Visible: true, UserVisible: true, Description: null,
            AvailabilityInfo: null,
            Dates: dates ?? [],
            Files: []);

    private static AuditVirtualClassroomChecklistQueryHandler CreateHandler(
        IReadOnlyList<CourseSectionSummary> sections)
    {
        var gateway = new FakeCourseContentsGateway(sections);
        var currentUser = new FakeCurrentUserGateway();
        return new AuditVirtualClassroomChecklistQueryHandler(gateway, currentUser);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_CompleteSala_KeyItemsAreOk()
    {
        var sections = new[]
        {
            new CourseSectionSummary("s1", 1, "Seção 1", null, true, 9, false,
            [
                MakeModule("m1", "resource", "Guia do Estudante"),
                MakeModule("m2", "resource", "Critérios de Certificação"),
                MakeModule("m3", "resource", "Cronograma e Plano de Estudo"),
                MakeModule("m4", "forum",    "Fórum de Apresentação"),
                MakeModule("m5", "forum",    "Fórum de Dúvidas Frequentes"),
                MakeModule("m6", "scorm",    "Conteúdo SCORM da SA"),
                MakeModule("m7", "assign",   "SA 1 - Situação de Aprendizagem",
                    [new CourseModuleDate("Due date", DateTimeOffset.UtcNow.AddDays(7))]),
            ])
        };

        var handler = CreateHandler(sections);
        var result = await handler.Handle(
            new AuditVirtualClassroomChecklistQuery("course1"), CancellationToken.None);

        Assert.Equal(9, result.TotalItems);

        // sala_visivel is always nao_verificavel
        var salaVisivel = result.Items.Single(i => i.ItemKey == "sala_visivel");
        Assert.Equal("nao_verificavel", salaVisivel.Status);

        var forumApresentacao = result.Items.Single(i => i.ItemKey == "forum_apresentacao");
        Assert.Equal("ok", forumApresentacao.Status);

        var forumDuvidas = result.Items.Single(i => i.ItemKey == "forum_duvidas");
        Assert.Equal("ok", forumDuvidas.Status);

        var scorm = result.Items.Single(i => i.ItemKey == "scorm_conteudo");
        Assert.Equal("ok", scorm.Status);

        var sa = result.Items.Single(i => i.ItemKey == "situacao_aprendizagem");
        Assert.Equal("ok", sa.Status);

        var datas = result.Items.Single(i => i.ItemKey == "datas_configuradas");
        Assert.Equal("ok", datas.Status);
    }

    [Fact]
    public async Task Handle_EmptyCourse_NoOkItems()
    {
        var sections = new[]
        {
            new CourseSectionSummary("s1", 1, "Seção vazia", null, true, 0, true, [])
        };

        var handler = CreateHandler(sections);
        var result = await handler.Handle(
            new AuditVirtualClassroomChecklistQuery("course1"), CancellationToken.None);

        Assert.Equal(0, result.OkCount);
        Assert.All(result.Items, i =>
            Assert.True(i.Status is "ausente" or "nao_verificavel"));
    }

    [Fact]
    public async Task Handle_AssignWithoutDates_DatasConfiguradasIsAusente()
    {
        var sections = new[]
        {
            new CourseSectionSummary("s1", 1, "Seção", null, true, 1, false,
            [
                MakeModule("m1", "assign", "SA 1", dates: [])  // no dates configured
            ])
        };

        var handler = CreateHandler(sections);
        var result = await handler.Handle(
            new AuditVirtualClassroomChecklistQuery("course1"), CancellationToken.None);

        var datas = result.Items.Single(i => i.ItemKey == "datas_configuradas");
        Assert.Equal("ausente", datas.Status);
        Assert.Contains("nenhuma com datas", datas.Observation);
    }

    [Fact]
    public async Task Handle_ForumWithoutMatchingName_IsIncompleto()
    {
        var sections = new[]
        {
            new CourseSectionSummary("s1", 1, "Seção", null, true, 1, false,
            [
                MakeModule("m1", "forum", "Avisos Gerais")  // does not match "apresentação"
            ])
        };

        var handler = CreateHandler(sections);
        var result = await handler.Handle(
            new AuditVirtualClassroomChecklistQuery("course1"), CancellationToken.None);

        var forumApresentacao = result.Items.Single(i => i.ItemKey == "forum_apresentacao");
        Assert.Equal("incompleto", forumApresentacao.Status);
    }

    [Fact]
    public async Task Handle_NoForums_ForumItemsAreAusente()
    {
        var sections = new[]
        {
            new CourseSectionSummary("s1", 1, "Seção", null, true, 1, false,
            [
                MakeModule("m1", "resource", "Documento qualquer")
            ])
        };

        var handler = CreateHandler(sections);
        var result = await handler.Handle(
            new AuditVirtualClassroomChecklistQuery("course1"), CancellationToken.None);

        var forumApresentacao = result.Items.Single(i => i.ItemKey == "forum_apresentacao");
        var forumDuvidas     = result.Items.Single(i => i.ItemKey == "forum_duvidas");

        Assert.Equal("ausente", forumApresentacao.Status);
        Assert.Equal("ausente", forumDuvidas.Status);
    }

    [Fact]
    public async Task Handle_PartialDates_DatasIsIncompleto()
    {
        var sections = new[]
        {
            new CourseSectionSummary("s1", 1, "Seção", null, true, 2, false,
            [
                MakeModule("m1", "assign", "SA 1",
                    [new CourseModuleDate("Due date", DateTimeOffset.UtcNow.AddDays(7))]),
                MakeModule("m2", "assign", "SA 2", dates: [])  // missing dates
            ])
        };

        var handler = CreateHandler(sections);
        var result = await handler.Handle(
            new AuditVirtualClassroomChecklistQuery("course1"), CancellationToken.None);

        var datas = result.Items.Single(i => i.ItemKey == "datas_configuradas");
        Assert.Equal("incompleto", datas.Status);
        Assert.Contains("1 de 2", datas.Observation);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────

    private sealed class FakeCourseContentsGateway(IReadOnlyList<CourseSectionSummary> sections)
        : IMoodleCourseContentsGateway
    {
        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId, string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden, bool onlyWithFiles,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CourseContentsSummary(courseId, [], false, false, sections));
    }

    private sealed class FakeCurrentUserGateway : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult(99L);
    }
}
