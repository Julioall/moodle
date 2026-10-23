using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MediatR;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Moodle.Application.Registry;
using Moodle.Application.Abstractions;
using Moodle.Application.Courses;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Domain.Registry;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Configuration;
using Moodle.Presentation.Tools;
using Moodle.Presentation.Tools.Portal;

namespace Moodle.Application.Tests.Tools.Courses;

public class MoodleCoursesToolsTests
{
    [Fact]
    public async Task Deve_usar_usuario_moodle_resolvido_na_consulta_de_cursos()
    {
        var mediator = new FakeMediator();
        var selection = new FakeMoodleConnectionSelection();
        var resolver = new FakeMoodleUserResolver(777);
        var sut = new MoodleCoursesTools(mediator, selection, resolver);

        var result = await sut.ListarMeusCursosAsync(3, 1, "goias");

        Assert.False(result.IsError ?? false);
        Assert.Equal("goias", selection.Alias);
        Assert.NotNull(mediator.LastListQuery);
        Assert.Equal("777", mediator.LastListQuery!.UserExternalId);
        Assert.Equal(3, mediator.LastListQuery.Limit);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("ok", structured.GetProperty("status").GetString());
        Assert.True(structured.TryGetProperty("data", out var data));
        Assert.Equal(1, data.GetProperty("total").GetInt32());
        var course = data.GetProperty("courses")[0];
        Assert.Equal("1", course.GetProperty("courseId").GetString());
        Assert.Equal("ID-1", course.GetProperty("idNumber").GetString());
        Assert.Equal("CURSO", course.GetProperty("shortName").GetString());
        Assert.Equal("Curso", course.GetProperty("fullName").GetString());
        Assert.Equal("Curso Display", course.GetProperty("displayName").GetString());
        Assert.Equal(10, course.GetProperty("categoryId").GetInt64());
        Assert.Equal("Categoria", course.GetProperty("categoryName").GetString());
        Assert.True(course.GetProperty("visible").GetBoolean());
        Assert.Equal("https://moodle.example/course/view.php?id=1", course.GetProperty("viewUrl").GetString());
        Assert.Equal("https://moodle.example/pluginfile.php/course.png", course.GetProperty("courseImage").GetString());
        Assert.Equal(75.5m, course.GetProperty("progress").GetDecimal());
        Assert.True(course.GetProperty("hasProgress").GetBoolean());
        Assert.False(course.GetProperty("isFavourite").GetBoolean());
        Assert.False(course.TryGetProperty("currentGrade", out _));
        Assert.False(course.TryGetProperty("nextDueAt", out _));
        Assert.False(course.TryGetProperty("pendingActivities", out _));
        Assert.True(structured.TryGetProperty("warnings", out _));
        Assert.True(structured.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public async Task Deve_falhar_quando_usuario_moodle_nao_for_resolvido()
    {
        var sut = new MoodleCoursesTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(null));

        var result = await sut.ListarMeusCursosAsync(cancellationToken: CancellationToken.None);

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("error", structured.GetProperty("status").GetString());
        Assert.Equal("Usuario nao autenticado para listar cursos.", structured.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Deve_retornar_lista_vazia_quando_usuario_nao_possuir_cursos()
    {
        var mediator = new FakeMediator { ReturnEmpty = true };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarMeusCursosAsync(cancellationToken: CancellationToken.None);

        Assert.False(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        Assert.Equal(0, data.GetProperty("total").GetInt32());
        Assert.Equal(0, data.GetProperty("courses").GetArrayLength());
    }

    [Fact]
    public async Task Deve_retornar_erro_controlado_quando_consulta_moodle_falhar()
    {
        var mediator = new FakeMediator { ThrowOnList = true };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarMeusCursosAsync(cancellationToken: CancellationToken.None);

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("error", structured.GetProperty("status").GetString());
        Assert.Equal("Nao foi possivel listar os cursos no Moodle neste momento.", structured.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Deve_buscar_cursos_por_termo()
    {
        var mediator = new FakeMediator();
        var selection = new FakeMoodleConnectionSelection();
        var sut = new MoodleCoursesTools(mediator, selection, new FakeMoodleUserResolver(777));

        var result = await sut.BuscarCursosAsync("seguranca", 4, "goias");

        Assert.False(result.IsError ?? false);
        Assert.Equal("goias", selection.Alias);
        Assert.Equal("seguranca", mediator.LastSearchQuery?.Query);
        Assert.Equal(4, mediator.LastSearchQuery?.Limit);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(1, structured.GetProperty("data").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Deve_consultar_curso_por_identificador()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ConsultarCursoAsync("CURSO");

        Assert.False(result.IsError ?? false);
        Assert.Equal("CURSO", mediator.LastGetCourseQuery?.CourseId);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("ok", structured.GetProperty("status").GetString());
        Assert.Equal("1", structured.GetProperty("data").GetProperty("course").GetProperty("courseId").GetString());
    }

    [Fact]
    public async Task GetCourse_ConsultaLiveQuandoSnapshotDoAliasNaoContemOCurso()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [],
            DateTimeOffset.UtcNow,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 0);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(
            snapshot,
            connectionCatalog: new FakeConnectionCatalog("SENAI"));
        var mediator = new FakeMediator();
        var selection = new FakeMoodleConnectionSelection();
        var sut = new MoodleCoursesTools(
            mediator,
            selection,
            new FakeMoodleUserResolver(777),
            fixture.Context);

        var result = await sut.ConsultarCursoAsync("11082", "SENAI");

        Assert.False(result.IsError ?? false);
        Assert.Equal("SENAI", selection.Alias);
        Assert.Equal("11082", mediator.LastGetCourseQuery?.CourseId);
    }

    [Fact]
    public async Task Deve_expor_search_no_formato_padrao_de_connector()
    {
        var mediator = new FakeMediator();
        var selection = new FakeMoodleConnectionSelection();
        var sut = new MoodleCoursesTools(
            mediator,
            selection,
            new FakeMoodleUserResolver(777));

        var result = await sut.SearchAsync("seguranca", moodleAlias: "SENAI");

        Assert.False(result.IsError ?? false);
        Assert.Equal("SENAI", selection.Alias);
        Assert.Equal("seguranca", mediator.LastSearchQuery?.Query);
        Assert.Equal(10, mediator.LastSearchQuery?.Limit);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var searchResult = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("1", searchResult.GetProperty("id").GetString());
        Assert.Equal("Curso Display", searchResult.GetProperty("title").GetString());
        Assert.Equal("https://moodle.example/course/view.php?id=1", searchResult.GetProperty("url").GetString());

        var content = Assert.Single(result.Content);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(content).Text;
        using var contentJson = JsonDocument.Parse(text);
        Assert.True(contentJson.RootElement.TryGetProperty("results", out _));
    }

    [Theory]
    [InlineData(nameof(MoodleCoursesTools.SearchAsync))]
    [InlineData(nameof(MoodleCoursesTools.FetchAsync))]
    public void Ferramentas_padrao_de_curso_expoem_alias_moodle(string methodName)
    {
        var method = typeof(MoodleCoursesTools).GetMethod(methodName);

        Assert.NotNull(method);
        Assert.Contains(method!.GetParameters(), parameter =>
            parameter.Name == "moodleAlias" && parameter.ParameterType == typeof(string));
    }

    [Fact]
    public async Task Deve_expor_fetch_no_formato_padrao_de_connector()
    {
        var mediator = new FakeMediator();
        var selection = new FakeMoodleConnectionSelection();
        var sut = new MoodleCoursesTools(
            mediator,
            selection,
            new FakeMoodleUserResolver(777));

        var result = await sut.FetchAsync("CURSO", moodleAlias: "SENAI");

        Assert.False(result.IsError ?? false);
        Assert.Equal("SENAI", selection.Alias);
        Assert.Equal("CURSO", mediator.LastGetCourseQuery?.CourseId);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("1", structured.GetProperty("id").GetString());
        Assert.Equal("Curso Display", structured.GetProperty("title").GetString());
        Assert.Contains("Curso: Curso", structured.GetProperty("text").GetString());
        Assert.Equal("https://moodle.example/course/view.php?id=1", structured.GetProperty("url").GetString());
        Assert.Equal("CURSO", structured.GetProperty("metadata").GetProperty("shortName").GetString());

        var content = Assert.Single(result.Content);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(content).Text;
        using var contentJson = JsonDocument.Parse(text);
        Assert.Equal("1", contentJson.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task FetchAsync_ConsultaLiveQuandoSnapshotDoAliasNaoContemOCurso()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [],
            DateTimeOffset.UtcNow,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 0);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(
            snapshot,
            connectionCatalog: new FakeConnectionCatalog("SENAI"));
        var mediator = new FakeMediator();
        var selection = new FakeMoodleConnectionSelection();
        var sut = new MoodleCoursesTools(
            mediator,
            selection,
            new FakeMoodleUserResolver(777),
            fixture.Context);

        var result = await sut.FetchAsync("11082", moodleAlias: "SENAI");

        Assert.False(result.IsError ?? false);
        Assert.Equal("SENAI", selection.Alias);
        Assert.Equal("11082", mediator.LastGetCourseQuery?.CourseId);
    }

    [Fact]
    public async Task Deve_retornar_erro_controlado_quando_curso_nao_for_encontrado()
    {
        var mediator = new FakeMediator { ReturnNullCourse = true };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ConsultarCursoAsync("inexistente");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("error", structured.GetProperty("status").GetString());
        Assert.Equal("Curso nao encontrado entre os cursos vinculados ao usuario.", structured.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Deve_incluir_metadados_de_paginacao_na_resposta()
    {
        var mediator = new FakeMediator { SimulatedTotal = 30, SimulatedPageSize = 10 };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarMeusCursosAsync(10, 2);

        Assert.False(result.IsError ?? false);
        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.Equal(30, data.GetProperty("total").GetInt32());
        Assert.Equal(2, data.GetProperty("page").GetInt32());
        Assert.Equal(3, data.GetProperty("total_pages").GetInt32());
        Assert.True(data.GetProperty("has_next_page").GetBoolean());
    }

    [Fact]
    public async Task Deve_repassar_pagina_corretamente_para_query()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        await sut.ListarMeusCursosAsync(10, 4);

        Assert.Equal(4, mediator.LastListQuery?.Page);
        Assert.Equal(10, mediator.LastListQuery?.Limit);
    }

    [Fact]
    public async Task Deve_incluir_hint_de_proxima_pagina_na_narracao()
    {
        var mediator = new FakeMediator { SimulatedTotal = 30, SimulatedPageSize = 10 };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarMeusCursosAsync(10, 1);

        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("Pagina 1 de 3", text);
        Assert.Contains("pagina=2", text);
    }

    [Fact]
    public async Task Deve_consultar_moodle_quando_snapshot_de_cursos_estiver_desatualizado()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso antigo")],
            DateTimeOffset.UtcNow.AddDays(-3),
            IsStale: true,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 1);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(snapshot);
        var mediator = new FakeMediator();
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777),
            fixture.Context);

        var result = await sut.ListarMeusCursosAsync(10, 1, "goias");

        Assert.False(result.IsError ?? false, ExtractMessage(result));
        Assert.NotNull(mediator.LastListQuery);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("live", structured.GetProperty("freshness").GetProperty("source").GetString());
        Assert.False(structured.GetProperty("freshness").GetProperty("stale").GetBoolean());
        Assert.Equal("1", structured.GetProperty("data").GetProperty("courses")[0].GetProperty("courseId").GetString());
    }

    [Fact]
    public async Task Deve_usar_snapshot_desatualizado_quando_consulta_live_falhar()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso antigo")],
            DateTimeOffset.UtcNow.AddDays(-3),
            IsStale: true,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 1);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(snapshot);
        var mediator = new FakeMediator { ThrowOnList = true, ThrowOnListTimeout = true };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777),
            fixture.Context);

        var result = await sut.ListarMeusCursosAsync(10, 1, "goias");

        Assert.False(result.IsError ?? false, ExtractMessage(result));
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("snapshot-1", structured.GetProperty("data").GetProperty("courses")[0].GetProperty("courseId").GetString());
        Assert.Equal("snapshot", structured.GetProperty("freshness").GetProperty("source").GetString());
        Assert.True(structured.GetProperty("freshness").GetProperty("stale").GetBoolean());
        Assert.Contains(
            structured.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()?.Contains("snapshot", StringComparison.OrdinalIgnoreCase) == true);
        Assert.False(structured.GetProperty("freshness").GetProperty("decisionSafe").GetBoolean());
    }

    [Fact]
    public async Task Deve_expor_cache_de_memoria_sem_marca_lo_como_live()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso antigo")],
            DateTimeOffset.UtcNow.AddDays(-3),
            IsStale: true,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 1);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(snapshot);
        var mediator = new FakeMediator { ListSource = CourseReadSource.MemoryCache };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777),
            fixture.Context);

        var result = await sut.ListarMeusCursosAsync(10, 1, "goias");

        Assert.False(result.IsError ?? false, ExtractMessage(result));
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("memory_cache", structured.GetProperty("freshness").GetProperty("source").GetString());
        Assert.False(structured.GetProperty("freshness").GetProperty("decisionSafe").GetBoolean());
        Assert.Contains(
            structured.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()?.Contains("cache", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Deve_indicar_snapshot_incompleto_quando_fallback_live_falhar()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso parcial")],
            DateTimeOffset.UtcNow,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: false,
            RecordCount: 1);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(snapshot);
        var mediator = new FakeMediator { ThrowOnList = true, ThrowOnListTimeout = true };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777),
            fixture.Context);

        var result = await sut.ListarMeusCursosAsync(10, 1, "goias");

        Assert.False(result.IsError ?? false, ExtractMessage(result));
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("snapshot", structured.GetProperty("freshness").GetProperty("source").GetString());
        Assert.False(structured.GetProperty("freshness").GetProperty("complete").GetBoolean());
        Assert.False(structured.GetProperty("freshness").GetProperty("decisionSafe").GetBoolean());
    }

    [Fact]
    public async Task Deve_limitar_tempo_da_consulta_live_e_servir_snapshot()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso antigo")],
            DateTimeOffset.UtcNow.AddDays(-3),
            IsStale: true,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 1);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(snapshot);
        var sut = new MoodleCoursesTools(
            new FakeMediator { WaitForListCancellation = true },
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777),
            fixture.Context,
            snapshotOptions: Options.Create(new MoodleSnapshotOptions { CourseLiveFallbackTimeoutSeconds = 1 }));

        var result = await sut.ListarMeusCursosAsync(10, 1, "goias");

        Assert.False(result.IsError ?? false, ExtractMessage(result));
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("snapshot", structured.GetProperty("freshness").GetProperty("source").GetString());
        Assert.Equal("snapshot-1", structured.GetProperty("data").GetProperty("courses")[0].GetProperty("courseId").GetString());
    }

    [Fact]
    public async Task Deve_retornar_timeout_controlado_quando_consulta_live_sem_snapshot_travar()
    {
        var sut = new MoodleCoursesTools(
            new FakeMediator { WaitForListCancellation = true },
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777),
            snapshotOptions: Options.Create(new MoodleSnapshotOptions { CourseLiveFallbackTimeoutSeconds = 1 }));

        var result = await sut.ListarMeusCursosAsync(10, 1);

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(MoodleErrorContract.RequestTimeout, structured.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Deve_continuar_com_leitura_live_quando_fila_de_snapshot_falhar()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso antigo")],
            DateTimeOffset.UtcNow.AddDays(-3),
            IsStale: true,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 1);
        var queue = new FakeSnapshotQueue { ThrowOnEnqueue = true };
        await using var fixture = await CourseSnapshotFixture.CreateAsync(snapshot, queue);
        var mediator = new FakeMediator { ListSource = CourseReadSource.Moodle };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777),
            fixture.Context);

        var result = await sut.ListarMeusCursosAsync(10, 1, "goias");

        Assert.False(result.IsError ?? false, ExtractMessage(result));
        Assert.NotNull(mediator.LastListQuery);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("live", structured.GetProperty("freshness").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Deve_preservar_erro_de_permissao_no_fallback_live()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso antigo")],
            DateTimeOffset.UtcNow.AddDays(-3),
            IsStale: true,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 1);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(snapshot);
        var mediator = new FakeMediator
        {
            ThrowOnList = true,
            ListException = new MoodleApiException(MoodleErrorContract.PermissionDenied, "sem permissao")
        };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777),
            fixture.Context);

        var result = await sut.ListarMeusCursosAsync(10, 1, "goias");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(MoodleErrorContract.PermissionDenied, structured.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Deve_preservar_cancelamento_explicito_do_chamador()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso")],
            DateTimeOffset.UtcNow,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 1);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(snapshot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sut = new MoodleCoursesTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777),
            fixture.Context);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ListarMeusCursosAsync(10, 1, "goias", cancellation.Token));
    }

    [Fact]
    public async Task Deve_servir_snapshot_fresco_sem_resolver_usuario_no_Moodle()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso atual")],
            DateTimeOffset.UtcNow,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 1);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(snapshot);
        var sut = new MoodleCoursesTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(error: new MoodleApiException(MoodleErrorContract.RequestTimeout, "Moodle indisponivel")),
            fixture.Context);

        var result = await sut.ListarMeusCursosAsync(10, 1, "goias");

        Assert.False(result.IsError ?? false, ExtractMessage(result));
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("snapshot", structured.GetProperty("freshness").GetProperty("source").GetString());
        Assert.Equal("snapshot-1", structured.GetProperty("data").GetProperty("courses")[0].GetProperty("courseId").GetString());
    }

    [Fact]
    public async Task Deve_ler_snapshot_com_alias_explicito_sem_validar_DNS_da_conexao()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso atual")],
            DateTimeOffset.UtcNow,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 1);
        var registry = new FakeConnectionRegistry(
            new Moodle.Domain.Registry.ConnectionInfo(Guid.NewGuid(), "goias", "https://moodle.example"))
        {
            ThrowOnResolve = true
        };
        await using var fixture = await CourseSnapshotFixture.CreateAsync(
            snapshot,
            connectionRegistry: registry,
            connectionCatalog: new FakeConnectionCatalog("goias"));
        var sut = new MoodleCoursesTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(error: new MoodleApiException(MoodleErrorContract.RequestTimeout, "Moodle indisponivel")),
            fixture.Context);

        var result = await sut.ListarMeusCursosAsync(10, 1, "goias");

        Assert.False(result.IsError ?? false, ExtractMessage(result));
        Assert.Equal(0, registry.ResolveCalls);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("snapshot", structured.GetProperty("freshness").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Deve_usar_snapshot_stale_quando_resolucao_do_usuario_tiver_timeout()
    {
        var snapshot = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
            [CreateCourseSummary("snapshot-1", "Curso antigo")],
            DateTimeOffset.UtcNow.AddDays(-3),
            IsStale: true,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: true,
            RecordCount: 1);
        await using var fixture = await CourseSnapshotFixture.CreateAsync(snapshot);
        var sut = new MoodleCoursesTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(error: new MoodleApiException(MoodleErrorContract.RequestTimeout, "Moodle lento")),
            fixture.Context);

        var result = await sut.ListarMeusCursosAsync(10, 1, "goias");

        Assert.False(result.IsError ?? false, ExtractMessage(result));
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("snapshot", structured.GetProperty("freshness").GetProperty("source").GetString());
        Assert.False(structured.GetProperty("freshness").GetProperty("decisionSafe").GetBoolean());
        Assert.Contains(
            structured.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()?.Contains("usuario", StringComparison.OrdinalIgnoreCase) == true ||
                warning.GetString()?.Contains("snapshot", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Nao_marca_consulta_desconhecida_como_live()
    {
        var sut = new MoodleCoursesTools(
            new FakeMediator { ListSource = CourseReadSource.Unknown },
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarMeusCursosAsync(10, 1);

        Assert.False(result.IsError ?? false, ExtractMessage(result));
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("unknown", structured.GetProperty("freshness").GetProperty("source").GetString());
        Assert.False(structured.GetProperty("freshness").GetProperty("decisionSafe").GetBoolean());
    }

    [Fact]
    public async Task Nao_deve_incluir_hint_na_narracao_quando_nao_houver_proxima_pagina()
    {
        var mediator = new FakeMediator { SimulatedTotal = 5, SimulatedPageSize = 10 };
        var sut = new MoodleCoursesTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarMeusCursosAsync(10, 1);

        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.DoesNotContain("Pagina", text);
        Assert.DoesNotContain("pagina=", text);
    }

    [Fact]
    public async Task ListarMeusCursosAsync_NuncaDeixaFalhaDeResolucaoEscaparAoMcp()
    {
        var sut = new MoodleCoursesTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(
                error: new MoodleApiException(
                    MoodleErrorContract.ConnectionNotFound,
                    "internal")));

        var result = await sut.ListarMeusCursosAsync(moodleAlias: "goias");

        Assert.True(result.IsError);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(MoodleErrorContract.ConnectionNotFound, structured.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("auditId").GetString()));
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("data").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task BuscarCursosAsync_DevolveConexaoDesativadaComContratoEstavel()
    {
        var sut = new MoodleCoursesTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(
                error: new MoodleApiException(
                    MoodleErrorContract.ConnectionDisabled,
                    "internal")));

        var result = await sut.BuscarCursosAsync("32786", moodleAlias: "goias");

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(MoodleErrorContract.ConnectionDisabled, structured.GetProperty("errorCode").GetString());
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ConsultarCursoAsync_ConverteExcecaoInesperadaSemExporTipo()
    {
        var sut = new MoodleCoursesTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(error: new InvalidOperationException("secret detail")));

        var result = await sut.ConsultarCursoAsync("32786", "goias");

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(MoodleErrorContract.Unexpected, structured.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("InvalidOperationException", structured.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", structured.GetRawText(), StringComparison.Ordinal);
    }

    private sealed class FakeMoodleConnectionSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private static string ExtractMessage(CallToolResult result) =>
        result.StructuredContent is JsonElement element && element.TryGetProperty("message", out var message)
            ? message.GetString() ?? "MCP error"
            : "MCP error";

    private static CourseSummary CreateCourseSummary(string courseId, string fullName) => new(
        courseId,
        "ID-" + courseId,
        "SHORT-" + courseId,
        fullName,
        fullName,
        10,
        "Categoria",
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero),
        true,
        "https://moodle.example/course/view.php?id=" + courseId,
        null,
        null,
        false,
        false,
        null);

    private sealed class CourseSnapshotFixture : IAsyncDisposable
    {
        private readonly ConnectorDbContext db;

        private CourseSnapshotFixture(
            ConnectorDbContext db,
            MoodleSnapshotToolContext context,
            FakeSnapshotQueue snapshotQueue)
        {
            this.db = db;
            Context = context;
            SnapshotQueue = snapshotQueue;
        }

        public MoodleSnapshotToolContext Context { get; }

        public FakeSnapshotQueue SnapshotQueue { get; }

        public static async Task<CourseSnapshotFixture> CreateAsync(
            MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>> courses,
            FakeSnapshotQueue? snapshotQueue = null,
            IConnectionRegistry? connectionRegistry = null,
            IMoodleConnectionCatalog? connectionCatalog = null)
        {
            var options = new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase("moodle-courses-tools-" + Guid.NewGuid().ToString("N"))
                .Options;
            var db = new ConnectorDbContext(options);
            var user = new UserAccountEntity
            {
                Id = Guid.NewGuid(),
                Name = "Professor",
                Email = "professor@example.com",
                PasswordHash = "hash",
                ConnectorClientId = "client-professor"
            };
            db.UserAccounts.Add(user);
            await db.SaveChangesAsync();

            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test"))
            };
            var identityResolver = new PortalMcpIdentityResolver(
                new FixedHttpContextAccessor { HttpContext = httpContext },
                db);
            var snapshotStore = new FakeSnapshotStore(courses);
            snapshotQueue ??= new FakeSnapshotQueue();
            connectionRegistry ??= new FakeConnectionRegistry(
                new Moodle.Domain.Registry.ConnectionInfo(Guid.NewGuid(), "goias", "https://moodle.example"));
            var context = new MoodleSnapshotToolContext(
                identityResolver,
                connectionRegistry,
                snapshotStore,
                snapshotQueue,
                connectionCatalog: connectionCatalog);

            return new CourseSnapshotFixture(db, context, snapshotQueue);
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class FakeConnectionRegistry(Moodle.Domain.Registry.ConnectionInfo connection) : IConnectionRegistry
    {
        public bool ThrowOnResolve { get; init; }

        public int ResolveCalls { get; private set; }

        public Task<Moodle.Domain.Registry.ConnectionInfo?> ResolveConnectionAsync(
            string? alias,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            if (ThrowOnResolve)
            {
                throw new InvalidOperationException("A resolucao por DNS nao deveria ser chamada.");
            }

            return Task.FromResult<Moodle.Domain.Registry.ConnectionInfo?>(string.IsNullOrWhiteSpace(alias) ||
                string.Equals(alias, connection.Alias, StringComparison.OrdinalIgnoreCase) ? connection : null);
        }
    }

    private sealed class FakeConnectionCatalog(params string[] aliases) : IMoodleConnectionCatalog
    {
        public Task<IReadOnlyList<string>> GetActiveAliasesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(aliases);
    }

    private sealed class FakeSnapshotStore(
        MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>> courses) : IMoodleSnapshotStore
    {
        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?> GetCoursesAsync(
            Guid ownerId, string connectionAlias, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?>(courses);

        public Task<MoodleSnapshotEnvelope<CourseContentsSummary>?> GetActivitiesAsync(
            Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<CourseContentsSummary>?>(null);

        public Task<MoodleSnapshotEnvelope<CourseParticipantsPage>?> GetStudentsAsync(
            Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<CourseParticipantsPage>?>(null);

        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?> GetGroupsAsync(
            Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?>(null);

        public Task<MoodleSnapshotEnvelope<T>?> GetAsync<T>(
            Guid ownerId, string connectionAlias, string dataset, string courseId = "", CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<T>?>(null);

        public Task SaveAsync<T>(
            Guid ownerId,
            string connectionAlias,
            string dataset,
            string courseId,
            T payload,
            string tier,
            bool frozen,
            bool complete,
            int recordCount,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Invalidate(Guid ownerId, string connectionAlias, string dataset, string courseId = "") { }
    }

    private sealed class FakeSnapshotQueue : IMoodleSnapshotSyncQueue
    {
        public bool ThrowOnEnqueue { get; init; }

        public bool Enqueue(MoodleSnapshotSyncRequest request) => true;

        public Task<bool> EnqueueAsync(
            MoodleSnapshotSyncRequest request,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnEnqueue)
            {
                throw new InvalidOperationException("Falha simulada ao enfileirar snapshot.");
            }

            return Task.FromResult(true);
        }
    }

    private sealed class FakeMoodleUserResolver(
        long? userId = null,
        Exception? error = null) : IMoodleUserResolver
    {
        public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken)
        {
            if (error is not null)
            {
                throw error;
            }

            return Task.FromResult(userId);
        }
    }

    private sealed class FakeMediator : IMediator
    {
        public ListMyCoursesQuery? LastListQuery { get; private set; }

        public SearchCoursesQuery? LastSearchQuery { get; private set; }

        public GetCourseQuery? LastGetCourseQuery { get; private set; }

        public bool ReturnEmpty { get; init; }

        public bool ThrowOnList { get; init; }

        public bool ThrowOnListTimeout { get; init; }

        public Exception? ListException { get; init; }

        public bool WaitForListCancellation { get; init; }

        public CourseReadSource ListSource { get; init; } = CourseReadSource.Moodle;

        public bool ReturnNullCourse { get; init; }

        public int SimulatedTotal { get; init; } = 1;

        public int SimulatedPageSize { get; init; } = 20;

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
            if (request is ListMyCoursesQuery list)
            {
                LastListQuery = list;
                if (WaitForListCancellation)
                {
                    return WaitForCancellationAsync<TResponse>(cancellationToken);
                }

                if (ThrowOnList)
                {
                    throw ListException ?? (ThrowOnListTimeout
                        ? new MoodleApiException(MoodleErrorContract.RequestTimeout, "Falha simulada.")
                        : new InvalidOperationException("Falha simulada."));
                }

                var course = CreateCourseSummary();
                IReadOnlyList<CourseSummary> courses = ReturnEmpty ? [] : [course];
                var effectiveTotal = ReturnEmpty ? 0 : SimulatedTotal;
                var paged = new PagedCourses(courses, effectiveTotal, list.Page, SimulatedPageSize, ListSource);
                return Task.FromResult((TResponse)(object)paged);
            }

            if (request is SearchCoursesQuery search)
            {
                LastSearchQuery = search;
                IReadOnlyList<CourseSummary> data = [CreateCourseSummary()];
                return Task.FromResult((TResponse)data);
            }

            if (request is GetCourseQuery getCourse)
            {
                LastGetCourseQuery = getCourse;
                return ReturnNullCourse
                    ? Task.FromResult<TResponse>(default!)
                    : Task.FromResult((TResponse)(object)CreateCourseSummary());
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            if (request is ListMyCoursesQuery list)
            {
                LastListQuery = list;
                if (WaitForListCancellation)
                {
                    return WaitForCancellationObjectAsync(cancellationToken);
                }

                if (ThrowOnList)
                {
                    throw ListException ?? (ThrowOnListTimeout
                        ? new MoodleApiException(MoodleErrorContract.RequestTimeout, "Falha simulada.")
                        : new InvalidOperationException("Falha simulada."));
                }

                var course = CreateCourseSummary();
                IReadOnlyList<CourseSummary> courses = ReturnEmpty ? [] : [course];
                var effectiveTotal = ReturnEmpty ? 0 : SimulatedTotal;
                var paged = new PagedCourses(courses, effectiveTotal, list.Page, SimulatedPageSize, ListSource);
                return Task.FromResult<object?>(paged);
            }

            if (request is SearchCoursesQuery search)
            {
                LastSearchQuery = search;
                IReadOnlyList<CourseSummary> data = [CreateCourseSummary()];
                return Task.FromResult<object?>(data);
            }

            if (request is GetCourseQuery getCourse)
            {
                LastGetCourseQuery = getCourse;
                return Task.FromResult<object?>(ReturnNullCourse ? null : CreateCourseSummary());
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<object?>();

        private static async Task<TResponse> WaitForCancellationAsync<TResponse>(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return default!;
        }

        private static async Task<object?> WaitForCancellationObjectAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        private static CourseSummary CreateCourseSummary()
        {
            return new CourseSummary(
                "1",
                "ID-1",
                "CURSO",
                "Curso",
                "Curso Display",
                10,
                "Categoria",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero),
                true,
                "https://moodle.example/course/view.php?id=1",
                "https://moodle.example/pluginfile.php/course.png",
                75.5m,
                true,
                false,
                new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero));
        }
    }
}
