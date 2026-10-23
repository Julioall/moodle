using System.Text.Json;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Submissions;
using Moodle.Domain;
using Moodle.Presentation.Tools;

namespace Moodle.Application.Tests.Tools.Submissions;

public class MoodleAssignmentSubmissionsToolsTests
{
    [Fact]
    public async Task Empty_late_filter_preserves_unknown_coverage_in_public_response()
    {
        var page = new AssignmentSubmissionsPage("123", "501", "11", "Tarefa sintética", 1, 20,
            AssignmentSubmissionFilter.Late, true, true, null, null, 0, false, [])
        {
            LatenessUnknownCount = 3,
            LatenessCoverageComplete = false,
            EvaluationUnknownCount = 2,
            EvaluationCoverageComplete = false
        };
        var sut = new MoodleAssignmentSubmissionsTools(new FakeMediator { PageOverride = page },
            new FakeMoodleConnectionSelection(), new FakeMoodleUserResolver(777));
        var result = await sut.ListarEntregasAtrasadasAsync("123", "501");
        var payload = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = payload.GetProperty("data");
        Assert.Equal(3, data.GetProperty("latenessUnknownCount").GetInt32());
        Assert.False(data.GetProperty("latenessCoverageComplete").GetBoolean());
        Assert.Equal(2, data.GetProperty("evaluationUnknownCount").GetInt32());
        Assert.False(data.GetProperty("evaluationCoverageComplete").GetBoolean());
        Assert.Equal(2, payload.GetProperty("warnings").GetArrayLength());
        Assert.Contains(payload.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()!.Contains("prazo efetivo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Known_deadline_states_report_partial_coverage_when_effective_deadline_is_missing()
    {
        var page = new AssignmentSubmissionsPage("123", "501", "11", "Tarefa sintética", 1, 20,
            AssignmentSubmissionFilter.All, true, true, null, null, 1, false, []);
        var sut = new MoodleAssignmentSubmissionsTools(new FakeMediator { PageOverride = page },
            new FakeMoodleConnectionSelection(), new FakeMoodleUserResolver(777));

        var result = await sut.ListarEntregasAtividadeAsync("123", "501");
        var payload = Assert.IsType<JsonElement>(result.StructuredContent);

        Assert.False(payload.GetProperty("data").GetProperty("latenessCoverageComplete").GetBoolean());
        Assert.Contains(payload.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()!.Contains("prazo efetivo", StringComparison.Ordinal));
        Assert.False(payload.GetProperty("freshness").GetProperty("complete").GetBoolean());
        Assert.False(payload.GetProperty("freshness").GetProperty("decisionSafe").GetBoolean());
    }

    [Fact]
    public async Task Deve_listar_entregas_sem_texto_integral_ou_nota()
    {
        var mediator = new FakeMediator();
        var selection = new FakeMoodleConnectionSelection();
        var sut = new MoodleAssignmentSubmissionsTools(mediator, selection, new FakeMoodleUserResolver(777));

        var result = await sut.ListarEntregasAtividadeAsync(
            "CURSO",
            "11",
            status: "entregues",
            moodleAlias: "goias");

        Assert.False(result.IsError ?? false);
        Assert.Equal("goias", selection.Alias);
        Assert.NotNull(mediator.LastListQuery);
        Assert.Equal("777", mediator.LastListQuery!.UserExternalId);
        Assert.Equal("11", mediator.LastListQuery.AssignmentId);
        Assert.Equal(AssignmentSubmissionFilter.Submitted, mediator.LastListQuery.Filter);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        var submission = data.GetProperty("submissions")[0];
        Assert.Equal("101", submission.GetProperty("userId").GetString());
        Assert.True(submission.GetProperty("submitted").GetBoolean());
        Assert.False(submission.GetProperty("late").GetBoolean());
        Assert.Equal("unknown", submission.GetProperty("latenessState").GetString());
        Assert.True(submission.GetProperty("hasOnlineText").GetBoolean());
        Assert.False(submission.TryGetProperty("content", out _));
        Assert.False(submission.TryGetProperty("submissionText", out _));
        Assert.False(submission.TryGetProperty("grade", out _));
    }

    [Fact]
    public async Task Deve_listar_pendentes_atrasadas_e_aguardando_correcao()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleAssignmentSubmissionsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        await sut.ListarEntregasPendentesAsync("CURSO", "11");
        Assert.Equal(AssignmentSubmissionFilter.NotSubmitted, mediator.LastListQuery!.Filter);
        Assert.True(mediator.LastListQuery.ExcludeFutureActivity);

        await sut.ListarEntregasAtrasadasAsync("CURSO", "11");
        Assert.Equal(AssignmentSubmissionFilter.Late, mediator.LastListQuery!.Filter);

        await sut.ListarEntregasAguardandoCorrecaoAsync("CURSO", "11");
        Assert.Equal(AssignmentSubmissionFilter.NeedsGrading, mediator.LastListQuery!.Filter);
    }

    [Fact]
    public async Task Deve_consultar_entrega_de_aluno()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleAssignmentSubmissionsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ConsultarEntregaAlunoAsync("CURSO", "11", "101");

        Assert.False(result.IsError ?? false);
        Assert.NotNull(mediator.LastStudentQuery);
        Assert.Equal("101", mediator.LastStudentQuery!.StudentId);

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var data = structured.GetProperty("data");
        Assert.Equal("submitted", data.GetProperty("submission").GetProperty("status").GetString());
        Assert.False(data.GetProperty("latenessCoverageComplete").GetBoolean());
        Assert.Contains(structured.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()!.Contains("prazo efetivo", StringComparison.Ordinal));
        Assert.False(structured.GetProperty("freshness").GetProperty("complete").GetBoolean());
        Assert.False(structured.GetProperty("freshness").GetProperty("decisionSafe").GetBoolean());
    }

    [Fact]
    public async Task Alias_de_status_de_submissao_delega_para_a_operacao_canonica()
    {
        var mediator = new FakeMediator();
        var sut = new MoodleAssignmentSubmissionsTools(
            mediator,
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var canonical = await sut.ConsultarEntregaAlunoAsync("CURSO", "11", "101");
        var alias = await sut.ConsultarStatusSubmissaoAsync("CURSO", "11", "101");

        Assert.Equal(canonical.IsError, alias.IsError);
        var canonicalData = Assert.IsType<JsonElement>(canonical.StructuredContent).GetProperty("data");
        var aliasData = Assert.IsType<JsonElement>(alias.StructuredContent).GetProperty("data");
        Assert.Equal(
            canonicalData.GetProperty("submission").GetProperty("userId").GetString(),
            aliasData.GetProperty("submission").GetProperty("userId").GetString());
        Assert.Equal(
            canonicalData.GetProperty("submission").GetProperty("status").GetString(),
            aliasData.GetProperty("submission").GetProperty("status").GetString());
        Assert.Equal("101", mediator.LastStudentQuery!.StudentId);
    }

    [Fact]
    public async Task Aluno_ausente_retorna_nao_encontrado_estruturado_sem_codigo_generico()
    {
        var sut = new MoodleAssignmentSubmissionsTools(
            new FakeMediator { ReturnNullStudent = true },
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ConsultarEntregaAlunoAsync("CURSO", "11", "nao-matriculado");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("error", structured.GetProperty("status").GetString());
        Assert.Equal(MoodleErrorContract.ResourceNotFound, structured.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("unexpected_connector_error", structured.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Falha_Moodle_na_consulta_individual_preserva_codigo_seguro()
    {
        var sut = new MoodleAssignmentSubmissionsTools(
            new FakeMediator { StudentException = new MoodleApiException(MoodleErrorContract.PermissionDenied, "internal Moodle detail") },
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ConsultarEntregaAlunoAsync("CURSO", "11", "101");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(MoodleErrorContract.PermissionDenied, structured.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("internal Moodle detail", Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(result.Content[0]).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Falha_inesperada_na_consulta_individual_e_correlacionavel_sem_vazar_detalhes()
    {
        var sut = new MoodleAssignmentSubmissionsTools(
            new FakeMediator { StudentException = new InvalidOperationException("private internal detail") },
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ConsultarEntregaAlunoAsync("CURSO", "11", "101");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(MoodleErrorContract.Unexpected, structured.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("auditId").GetString()));
        Assert.DoesNotContain("private internal detail", Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(result.Content[0]).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deve_rejeitar_status_invalido()
    {
        var sut = new MoodleAssignmentSubmissionsTools(
            new FakeMediator(),
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarEntregasAtividadeAsync("CURSO", "11", status: "desconhecido");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("error", structured.GetProperty("status").GetString());
        Assert.Equal("invalid_filter", structured.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Deve_retornar_erro_controlado_quando_moodle_falhar()
    {
        var sut = new MoodleAssignmentSubmissionsTools(
            new FakeMediator { ThrowOnList = true },
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarEntregasAtividadeAsync("CURSO", "11");

        Assert.True(result.IsError ?? false);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("unexpected_connector_error", structured.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Falha_de_transporte_preserva_categoria_de_rede_na_resposta_da_tool()
    {
        var sut = new MoodleAssignmentSubmissionsTools(
            new FakeMediator { ListException = new HttpRequestException("synthetic transport failure") },
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarEntregasAtividadeAsync("CURSO", "11");

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(MoodleErrorContract.NetworkError, structured.GetProperty("errorCode").GetString());
        Assert.Equal(MoodleErrorContract.NetworkError, structured.GetProperty("causeCode").GetString());
        Assert.DoesNotContain("synthetic transport failure",
            Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(result.Content[0]).Text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validacao_de_argumento_permanece_distinta_de_falha_de_transporte()
    {
        var sut = new MoodleAssignmentSubmissionsTools(
            new FakeMediator { ListException = new ArgumentException("synthetic invalid argument") },
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        var result = await sut.ListarEntregasAtividadeAsync("CURSO", "11");

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(MoodleErrorContract.ValidationError, structured.GetProperty("errorCode").GetString());
        Assert.Equal(MoodleErrorContract.ValidationError, structured.GetProperty("causeCode").GetString());
    }

    [Fact]
    public async Task Cancelamento_do_cliente_e_propaga_sem_classifica_lo_como_timeout()
    {
        var sut = new MoodleAssignmentSubmissionsTools(
            new FakeMediator { ListException = new OperationCanceledException("synthetic client cancellation") },
            new FakeMoodleConnectionSelection(),
            new FakeMoodleUserResolver(777));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ListarEntregasAtividadeAsync("CURSO", "11"));
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
        public ListAssignmentSubmissionsQuery? LastListQuery { get; private set; }

        public GetStudentSubmissionQuery? LastStudentQuery { get; private set; }

        public bool ThrowOnList { get; init; }
        public Exception? ListException { get; init; }

        public bool ReturnNullStudent { get; init; }

        public Exception? StudentException { get; init; }
        public AssignmentSubmissionsPage? PageOverride { get; init; }

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
            if (request is ListAssignmentSubmissionsQuery list)
            {
                LastListQuery = list;
                if (ListException is not null)
                {
                    throw ListException;
                }
                if (ThrowOnList)
                {
                    throw new InvalidOperationException("Falha simulada.");
                }

                return Task.FromResult((TResponse)(object)(PageOverride ?? CreatePage(list)));
            }

            if (request is GetStudentSubmissionQuery student)
            {
                LastStudentQuery = student;
                if (StudentException is not null)
                {
                    throw StudentException;
                }

                return Task.FromResult((TResponse)(object?) (ReturnNullStudent ? null : CreateSubmittedSummary())!);
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            if (request is ListAssignmentSubmissionsQuery list)
            {
                LastListQuery = list;
                if (ListException is not null)
                {
                    throw ListException;
                }
                if (ThrowOnList)
                {
                    throw new InvalidOperationException("Falha simulada.");
                }

                return Task.FromResult<object?>(CreatePage(list));
            }

            if (request is GetStudentSubmissionQuery student)
            {
                LastStudentQuery = student;
                if (StudentException is not null)
                {
                    throw StudentException;
                }

                return Task.FromResult<object?>(ReturnNullStudent ? null : CreateSubmittedSummary());
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<object?>();

        private static AssignmentSubmissionsPage CreatePage(ListAssignmentSubmissionsQuery query)
        {
            var summary = query.Filter switch
            {
                AssignmentSubmissionFilter.NotSubmitted => new AssignmentSubmissionSummary(
                    "102",
                    "Estudante 2",
                    SubmissionId: null,
                    "not_submitted",
                    GradingStatus: null,
                    Submitted: false,
                    Late: false,
                    NeedsGrading: false,
                    SubmittedAt: null,
                    ModifiedAt: null,
                    AttemptNumber: null,
                    FileCount: 0,
                    HasOnlineText: false),
                AssignmentSubmissionFilter.Late => CreateSubmittedSummary(late: true, needsGrading: false),
                AssignmentSubmissionFilter.NeedsGrading => CreateSubmittedSummary(late: false, needsGrading: true),
                _ => CreateSubmittedSummary()
            };

            return new AssignmentSubmissionsPage(
                "123",
                "501",
                "11",
                "Tarefa 1",
                query.Page,
                query.PageSize,
                query.Filter,
                query.IncludeLate,
                query.IncludeUngraded,
                query.Since,
                query.Before,
                Total: 1,
                HasMore: false,
                [summary]);
        }

        private static AssignmentSubmissionSummary CreateSubmittedSummary(
            bool late = false,
            bool needsGrading = false)
        {
            return new AssignmentSubmissionSummary(
                "101",
                "Estudante 1",
                "9001",
                "submitted",
                needsGrading ? "notgraded" : "graded",
                Submitted: true,
                late,
                needsGrading,
                SubmittedAt: new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero),
                ModifiedAt: new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero),
                AttemptNumber: 0,
                FileCount: 1,
                HasOnlineText: true);
        }
    }
}
