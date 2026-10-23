using System.Text.Json;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Contents;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Presentation.Tools;

namespace Moodle.Application.Tests.Tools.Contents;

public class MoodleCourseContentsToolsTests
{
    [Fact]
    public async Task Deve_listar_conteudos_com_filtro_de_tipo_e_usuario_moodle_resolvido()
    {
        var mediator = new FakeMediator();
        var selection = new FakeMoodleConnectionSelection();
        var sut = new MoodleCourseContentsTools(mediator, selection, new FakeMoodleUserResolver(777));

        var result = await sut.ListarConteudosCursoAsync("CURSO", "page", incluirOcultos: true, moodleAlias: "goias");

        Assert.False(result.IsError ?? false);
        Assert.Equal("goias", selection.Alias);
        Assert.NotNull(mediator.LastContentsQuery);
        Assert.Equal("777", mediator.LastContentsQuery!.UserExternalId);
        Assert.Equal("CURSO", mediator.LastContentsQuery.CourseId);
        Assert.Equal(["page"], mediator.LastContentsQuery.ModuleTypes);
        Assert.True(mediator.LastContentsQuery.IncludeHidden);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("ok", structured.GetProperty("status").GetString());
        var data = structured.GetProperty("data");
        Assert.Equal("123", data.GetProperty("courseId").GetString());
        Assert.Equal(2, data.GetProperty("sectionCount").GetInt32());
        Assert.Equal(1, data.GetProperty("moduleCount").GetInt32());
        var module = data.GetProperty("sections")[0].GetProperty("modules")[0];
        Assert.Equal("11", module.GetProperty("moduleId").GetString());
        Assert.Equal("page", module.GetProperty("moduleType").GetString());
        Assert.DoesNotContain("token=", module.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deve_rejeitar_tipo_de_modulo_invalido()
    {
        var sut = new MoodleCourseContentsTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarConteudosCursoAsync("CURSO", "glossary");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("error", structured.GetProperty("status").GetString());
        Assert.Equal("invalid_filter", structured.GetProperty("errorCode").GetString());
        Assert.Equal("Tipo de modulo invalido. Use resource, page, url, book, folder, label, assign, quiz, scorm ou forum.", structured.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Deve_retornar_lista_vazia_quando_curso_nao_tiver_modulos()
    {
        var mediator = new FakeMediator { ReturnEmptyContents = true };
        var sut = new MoodleCourseContentsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarConteudosCursoAsync("CURSO");

        Assert.False(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        Assert.Equal(0, data.GetProperty("moduleCount").GetInt32());
    }

    [Fact]
    public async Task Deve_retornar_erro_controlado_quando_moodle_negar_conteudos()
    {
        var mediator = new FakeMediator { ThrowOnContents = true };
        var sut = new MoodleCourseContentsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarConteudosCursoAsync("CURSO");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("Nao foi possivel listar conteudos no Moodle neste momento.", structured.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Deve_consultar_modulo_do_curso()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleCourseContentsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ConsultarModuloCursoAsync("CURSO", "11");

        Assert.False(result.IsError ?? false);
        Assert.NotNull(mediator.LastModuleQuery);
        Assert.Equal("11", mediator.LastModuleQuery!.ModuleId);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("Pagina 1", structured.GetProperty("data").GetProperty("module").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Deve_filtrar_lista_de_arquivos_sem_baixar_conteudo()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleCourseContentsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarArquivosCursoAsync("CURSO");

        Assert.False(result.IsError ?? false);
        Assert.NotNull(mediator.LastContentsQuery);
        Assert.True(mediator.LastContentsQuery!.OnlyWithFiles);
    }

    [Fact]
    public async Task Deve_auditar_estrutura_do_curso()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleCourseContentsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.AuditarEstruturaCursoAsync("CURSO");

        Assert.False(result.IsError ?? false);
        Assert.NotNull(mediator.LastAuditQuery);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        Assert.Equal(1, data.GetProperty("emptySectionCount").GetInt32());
        Assert.Equal("empty_section", data.GetProperty("findings")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Deve_repetir_auditoria_quando_falha_de_rede_e_transitoria()
    {
        var mediator = new FakeMediator { AuditTransientFailures = 1 };
        var sut = new MoodleCourseContentsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.AuditarEstruturaCursoAsync("CURSO");

        Assert.False(result.IsError ?? false);
        Assert.Equal(2, mediator.AuditCalls);
    }

    [Fact]
    public async Task ListarConteudosCursoAsync_NuncaDeixaFalhaDeAliasEscaparAoMcp()
    {
        var sut = new MoodleCourseContentsTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new ThrowingMoodleUserResolver(new MoodleApiException(
                MoodleErrorContract.ConnectionNotFound,
                "internal")));

        var result = await sut.ListarConteudosCursoAsync("32786", moodleAlias: "goias");

        Assert.True(result.IsError);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(MoodleErrorContract.ConnectionNotFound, structured.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("auditId").GetString()));
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("data").ValueKind);
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

    private sealed class ThrowingMoodleUserResolver(Exception error) : IMoodleUserResolver
    {
        public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken) =>
            throw error;
    }

    private sealed class FakeMediator : IMediator
    {
        public ListCourseContentsQuery? LastContentsQuery { get; private set; }

        public GetCourseModuleQuery? LastModuleQuery { get; private set; }

        public AuditCourseStructureQuery? LastAuditQuery { get; private set; }

        public bool ReturnEmptyContents { get; init; }

        public bool ThrowOnContents { get; init; }

        public int AuditTransientFailures { get; init; }

        public int AuditCalls { get; private set; }

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
            if (request is ListCourseContentsQuery contents)
            {
                LastContentsQuery = contents;
                if (ThrowOnContents)
                {
                    throw new InvalidOperationException("Falha simulada.");
                }

                return Task.FromResult((TResponse)(object)CreateContents(ReturnEmptyContents));
            }

            if (request is GetCourseModuleQuery module)
            {
                LastModuleQuery = module;
                return Task.FromResult((TResponse)(object)CreateModule());
            }

            if (request is AuditCourseStructureQuery audit)
            {
                LastAuditQuery = audit;
                AuditCalls++;
                if (AuditCalls <= AuditTransientFailures)
                {
                    throw new MoodleApiException(MoodleErrorContract.NetworkError, "Falha transitoria simulada.");
                }
                return Task.FromResult((TResponse)(object)CreateAudit());
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            if (request is ListCourseContentsQuery contents)
            {
                LastContentsQuery = contents;
                if (ThrowOnContents)
                {
                    throw new InvalidOperationException("Falha simulada.");
                }

                return Task.FromResult<object?>(CreateContents(ReturnEmptyContents));
            }

            if (request is GetCourseModuleQuery module)
            {
                LastModuleQuery = module;
                return Task.FromResult<object?>(CreateModule());
            }

            if (request is AuditCourseStructureQuery audit)
            {
                LastAuditQuery = audit;
                AuditCalls++;
                if (AuditCalls <= AuditTransientFailures)
                {
                    throw new MoodleApiException(MoodleErrorContract.NetworkError, "Falha transitoria simulada.");
                }
                return Task.FromResult<object?>(CreateAudit());
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<object?>();

        private static CourseContentsSummary CreateContents(bool empty)
        {
            var sections = empty
                ? [new CourseSectionSummary("1", 1, "Topico 1", null, true, 0, true, [])]
                : new CourseSectionSummary[]
                {
                    new("1", 1, "Topico 1", null, true, 1, false, [CreateModule()]),
                    new("2", 2, "Topico 2", null, true, 0, true, [])
                };

            return new CourseContentsSummary("123", ["page"], IncludeHidden: false, OnlyWithFiles: false, sections);
        }

        private static CourseModuleSummary CreateModule()
        {
            return new CourseModuleSummary(
                "11",
                "501",
                "page",
                "Pagina 1",
                MoodleContentUrlSanitizer.Sanitize("https://moodle.example/mod/page/view.php?id=11&token=secret"),
                true,
                true,
                "Descricao",
                null,
                [new CourseModuleDate("Disponivel ate", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))],
                [new CourseModuleFile("file", "aula.pdf", "/", 1000, "application/pdf", "https://moodle.example/pluginfile.php/aula.pdf", false)]);
        }

        private static CourseStructureAuditSummary CreateAudit()
        {
            return new CourseStructureAuditSummary(
                "123",
                SectionCount: 2,
                ModuleCount: 1,
                EmptySectionCount: 1,
                ModulesWithoutDescriptionCount: 0,
                ModulesWithoutDatesCount: 0,
                [new CourseStructureFinding("empty_section", "info", "Secao sem modulos: Topico 2.", "2", null, null)]);
        }
    }
}
