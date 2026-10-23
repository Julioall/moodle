using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.Submissions;
using Moodle.Domain;

namespace Moodle.Application.Tests.Submissions;

public class ListAssignmentSubmissionsQueryHandlerTests
{
    [Theory]
    [InlineData(true, false, "late", 1, 0)]
    [InlineData(true, true, "on_time", 0, 0)]
    [InlineData(false, false, "unknown", 0, 1)]
    public async Task Late_filter_uses_official_due_and_observed_extension(
        bool complete, bool extended, string state, int total, int unknown)
    {
        var gateway = new FakeSubmissionsGateway
        {
            Records = [Submitted("9001", "101", DueAt.AddMinutes(1), "graded")],
            ExtensionsComplete = complete, ExtensionDueAt = extended ? DueAt.AddHours(1) : null,
            EffectiveDeadlinesComplete = true, EffectiveDueAt = DueAt
        };
        var settings = new FakeAssignmentSettingsGateway
        { Settings = new("501", 10) { DueAt = DueAt, DueAtKnown = true } };
        var handler = new ListAssignmentSubmissionsQueryHandler(new FakeCoursesGateway(),
            new FakeContentsGateway { Dates = [new CourseModuleDate("Cutoff", DueAt.AddDays(1))] },
            new FakeParticipantsGateway(), gateway, settings);
        var request = new ListAssignmentSubmissionsQuery("usuario-sintetico", "123", "11",
            AssignmentSubmissionFilter.Late, 1, 20, null, null, true, true);
        var result = await handler.Handle(request, CancellationToken.None);
        Assert.Equal(total, result!.Total);
        Assert.Equal(unknown, result.LatenessUnknownCount);
        Assert.Equal(complete, result.LatenessCoverageComplete);
        Assert.Equal(1, gateway.ExtensionReadCount);
        Assert.Equal(1, settings.CallCount);
        var all = await handler.Handle(request with { Filter = AssignmentSubmissionFilter.All }, CancellationToken.None);
        Assert.Equal(state, all!.Submissions.Single(row => row.UserId == "101").LatenessState);
    }

    private static readonly DateTimeOffset DueAt = new(2026, 6, 10, 23, 59, 0, TimeSpan.Zero);

    [Fact]
    public async Task Deve_listar_quem_entregou_tarefa()
    {
        var submissionsGateway = new FakeSubmissionsGateway
        {
            Records =
            [
                Submitted("9001", "101", new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero), "graded")
            ]
        };
        var sut = CreateHandler(submissionsGateway: submissionsGateway);

        var result = await sut.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "11",
                AssignmentSubmissionFilter.Submitted,
                Page: 1,
                PageSize: 20,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("501", submissionsGateway.LastAssignmentId);
        Assert.Equal("submitted", submissionsGateway.LastStatus);
        Assert.Equal(1, result!.Total);
        Assert.Equal("101", result.Submissions[0].UserId);
        Assert.True(result.Submissions[0].Submitted);
        Assert.False(result.Submissions[0].Late);
    }

    [Fact]
    public async Task Deve_listar_estudante_sem_entrega()
    {
        var submissionsGateway = new FakeSubmissionsGateway
        {
            Records =
            [
                Submitted("9001", "101", new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero), "graded")
            ]
        };
        var sut = CreateHandler(submissionsGateway: submissionsGateway);

        var result = await sut.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "501",
                AssignmentSubmissionFilter.NotSubmitted,
                Page: 1,
                PageSize: 20,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(submissionsGateway.LastStatus);
        Assert.Equal(2, result!.Total);
        Assert.All(result.Submissions, submission => Assert.False(submission.Submitted));
        Assert.Contains(result.Submissions, submission => submission.UserId == "102");
    }

    [Fact]
    public async Task Nao_inclui_submissao_de_usuario_fora_da_lista_de_estudantes()
    {
        var sut = CreateHandler(submissionsGateway: new FakeSubmissionsGateway
        {
            Records =
            [
                Submitted("9001", "101", new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero), "graded"),
                Submitted("9002", "teacher-1", new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero), "notgraded")
            ]
        });

        var result = await sut.Handle(
            new ListAssignmentSubmissionsQuery("usuario-42", "CURSO-1", "11", AssignmentSubmissionFilter.All,
                Page: 1, PageSize: 20, Since: null, Before: null, IncludeLate: true, IncludeUngraded: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Submissions, submission => submission.UserId == "teacher-1");
        Assert.All(result.Submissions, submission => Assert.NotNull(submission.FullName));
    }

    [Fact]
    public async Task Deve_identificar_entrega_atrasada()
    {
        var sut = CreateHandler(
            submissionsGateway: new FakeSubmissionsGateway
            {
                Records =
                [
                    Submitted("9001", "101", new DateTimeOffset(2026, 6, 11, 1, 0, 0, TimeSpan.Zero), "graded")
                ],
                EffectiveDeadlinesComplete = true,
                EffectiveDueAt = DueAt
            });

        var result = await sut.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "11",
                AssignmentSubmissionFilter.Late,
                Page: 1,
                PageSize: 20,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);

        Assert.NotNull(result);
        var submission = Assert.Single(result!.Submissions);
        Assert.Equal("101", submission.UserId);
        Assert.True(submission.Late);
    }

    [Fact]
    public async Task Deve_listar_entregas_aguardando_correcao()
    {
        var submissionsGateway = new FakeSubmissionsGateway
        {
            Records =
            [
                Submitted("9001", "101", new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero), "notgraded"),
                Submitted("9002", "102", new DateTimeOffset(2026, 6, 10, 11, 0, 0, TimeSpan.Zero), "graded"),
                Submitted("9003", "103", new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero), null)
            ]
        };
        var sut = CreateHandler(
            submissionsGateway: submissionsGateway,
            gradeReadGateway: new FakeAssignmentGradeReadGateway());

        var result = await sut.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "11",
                AssignmentSubmissionFilter.NeedsGrading,
                Page: 1,
                PageSize: 20,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(submissionsGateway.LastStatus);
        Assert.Equal(2, result!.Total);
        Assert.All(result.Submissions, submission => Assert.True(submission.NeedsGrading));
        Assert.DoesNotContain(result.Submissions, submission => submission.UserId == "102");
    }

    [Fact]
    public async Task Deve_manter_marcador_menos_um_do_Moodle_como_aguardando_correcao()
    {
        var sut = CreateHandler(
            submissionsGateway: new FakeSubmissionsGateway
            {
                Records =
                [
                    Submitted("9001", "101", new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero), "notgraded"),
                    Submitted("9002", "102", new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero), "notgraded"),
                    Submitted("9003", "103", new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero), "notgraded")
                ]
            },
            settingsGateway: new FakeAssignmentSettingsGateway
            {
                Settings = new AssignmentSettingsSummary("501", 0m, "Atividade sem nota", IsGradable: false)
            },
            gradeReadGateway: new FakeAssignmentGradeReadGateway
            {
                Grades = new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase)
                {
                    ["101"] = new("501", "101", Grade: null, HasGrade: false),
                    ["102"] = new("501", "102", Grade: -1m, HasGrade: false),
                    ["103"] = new("501", "103", Grade: 20m, HasGrade: true)
                }
            });

        var result = await sut.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "11",
                AssignmentSubmissionFilter.NeedsGrading,
                Page: 1,
                PageSize: 20,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Total);
        Assert.Equal(["101", "102"], result.Submissions.Select(submission => submission.UserId));
        Assert.All(result.Submissions, submission => Assert.True(submission.NeedsGrading));
    }

    [Fact]
    public async Task Deve_manter_aguardando_correcao_quando_atividade_usa_escala()
    {
        var sut = CreateHandler(
            submissionsGateway: new FakeSubmissionsGateway
            {
                Records =
                [
                    Submitted("9001", "101", new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero), "notgraded")
                ]
            },
            settingsGateway: new FakeAssignmentSettingsGateway
            {
                Settings = new AssignmentSettingsSummary("501", 0m, "Atividade com escala", IsGradable: true)
            },
            gradeReadGateway: new FakeAssignmentGradeReadGateway());

        var result = await sut.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "11",
                AssignmentSubmissionFilter.NeedsGrading,
                Page: 1,
                PageSize: 20,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(Assert.Single(result!.Submissions).NeedsGrading);
    }

    [Fact]
    public async Task Deve_paginar_respostas_grandes()
    {
        var sut = CreateHandler();

        var result = await sut.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "11",
                AssignmentSubmissionFilter.All,
                Page: 2,
                PageSize: 1,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Total);
        Assert.True(result.HasMore);
        Assert.Single(result.Submissions);
        Assert.Equal("102", result.Submissions[0].UserId);
    }

    [Fact]
    public async Task Deve_retornar_null_para_tarefa_inexistente()
    {
        var submissionsGateway = new FakeSubmissionsGateway();
        var sut = CreateHandler(
            contentsGateway: new FakeContentsGateway { ReturnWithoutAssignment = true },
            submissionsGateway: submissionsGateway);

        var result = await sut.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "999",
                AssignmentSubmissionFilter.All,
                Page: 1,
                PageSize: 20,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);

        Assert.Null(result);
        Assert.False(submissionsGateway.WasCalled);
    }

    [Fact]
    public async Task Deve_consultar_status_de_estudante_sem_entrega()
    {
        var participantsGateway = new FakeParticipantsGateway();
        var submissionsGateway = new FakeSubmissionsGateway();
        var settingsGateway = new FakeAssignmentSettingsGateway();
        var gradeReadGateway = new FakeAssignmentGradeReadGateway();
        var gradebookGateway = new FakeGradebookGateway();
        var sut = new GetStudentSubmissionQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            participantsGateway,
            submissionsGateway,
            settingsGateway,
            gradeReadGateway,
            gradebookGateway);

        var result = await sut.Handle(
            new GetStudentSubmissionQuery("usuario-42", "CURSO-1", "11", "102"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("102", result!.UserId);
        Assert.False(result.Submitted);
        Assert.Equal("not_submitted", result.Status);
        Assert.Equal(1, participantsGateway.TargetLookupCallCount);
        Assert.Equal(0, participantsGateway.CallCount);
        Assert.Equal(1, submissionsGateway.CallCount);
        Assert.Equal(0, settingsGateway.CallCount);
        Assert.Equal(0, gradeReadGateway.BulkReadCount);
        Assert.Equal(1, gradeReadGateway.IndividualReadCount);
        Assert.Equal("102", gradeReadGateway.LastSingleStudentId);
        Assert.Equal(0, gradebookGateway.BulkReadCount);
        Assert.Equal(1, gradebookGateway.IndividualReadCount);
        Assert.Equal("102", gradebookGateway.LastIndividualStudentId);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(500)]
    public async Task Consulta_individual_le_pagina_ate_o_aluno_e_limita_gradebook_e_notas(int participantCount)
    {
        var participants = Enumerable.Range(0, participantCount)
            .Select(index => Participant($"student-{index + 1:D4}", $"Aluno {index + 1:D4}"))
            .ToArray();
        var targetStudentId = $"student-{participantCount:D4}";
        var baselineParticipants = new FakeParticipantsGateway { CustomParticipants = participants };
        var baselineSubmissions = new FakeSubmissionsGateway();
        var baselineSettings = new FakeAssignmentSettingsGateway();
        var baselineGradeRead = new FakeAssignmentGradeReadGateway();
        var baselineGradebook = new FakeGradebookGateway();
        var baselineHandler = new ListAssignmentSubmissionsQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            baselineParticipants,
            baselineSubmissions,
            baselineSettings,
            baselineGradeRead,
            baselineGradebook);
        await baselineHandler.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "11",
                AssignmentSubmissionFilter.All,
                Page: 1,
                PageSize: 100,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);
        Assert.Equal((participantCount + 99) / 100, baselineParticipants.CallCount);
        Assert.Equal(1, baselineSubmissions.CallCount);
        Assert.Equal(1, baselineSettings.CallCount);
        Assert.Equal(1, baselineGradeRead.BulkReadCount);
        Assert.Equal(0, baselineGradeRead.IndividualReadCount);
        Assert.Equal(1, baselineGradebook.BulkReadCount);
        Assert.Equal(0, baselineGradebook.IndividualReadCount);

        var participantsGateway = new FakeParticipantsGateway { CustomParticipants = participants };
        var submissionsGateway = new FakeSubmissionsGateway();
        var settingsGateway = new FakeAssignmentSettingsGateway();
        var gradeReadGateway = new FakeAssignmentGradeReadGateway();
        var gradebookGateway = new FakeGradebookGateway();
        var sut = new GetStudentSubmissionQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            participantsGateway,
            submissionsGateway,
            settingsGateway,
            gradeReadGateway: gradeReadGateway,
            gradebookGateway: gradebookGateway);

        var result = await sut.Handle(
            new GetStudentSubmissionQuery("usuario-42", "CURSO-1", "11", targetStudentId),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, participantsGateway.TargetLookupCallCount);
        Assert.Equal(0, participantsGateway.CallCount);
        Assert.Equal(1, submissionsGateway.CallCount);
        Assert.Equal(0, settingsGateway.CallCount);
        Assert.Equal(1, gradeReadGateway.IndividualReadCount);
        Assert.Equal(targetStudentId, gradeReadGateway.LastSingleStudentId);
        Assert.Equal(0, gradeReadGateway.BulkReadCount);
        Assert.Equal(1, gradebookGateway.IndividualReadCount);
        Assert.Equal(targetStudentId, gradebookGateway.LastIndividualStudentId);
        Assert.Equal(0, gradebookGateway.BulkReadCount);
    }

    [Fact]
    public async Task Aluno_ausente_nao_dispara_submissoes_notas_ou_gradebook()
    {
        var baselineParticipants = new FakeParticipantsGateway
        {
            CustomParticipants = [Participant("101", "Estudante 1"), Participant("102", "Estudante 2")]
        };
        var baselineSubmissions = new FakeSubmissionsGateway();
        var baselineSettings = new FakeAssignmentSettingsGateway();
        var baselineGradeRead = new FakeAssignmentGradeReadGateway();
        var baselineGradebook = new FakeGradebookGateway();
        var baselineHandler = new ListAssignmentSubmissionsQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            baselineParticipants,
            baselineSubmissions,
            baselineSettings,
            baselineGradeRead,
            baselineGradebook);
        await baselineHandler.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "11",
                AssignmentSubmissionFilter.All,
                Page: 1,
                PageSize: 100,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);
        Assert.Equal(1, baselineSubmissions.CallCount);
        Assert.Equal(1, baselineSettings.CallCount);
        Assert.Equal(1, baselineGradeRead.BulkReadCount);
        Assert.Equal(1, baselineGradebook.BulkReadCount);

        var participantsGateway = new FakeParticipantsGateway
        {
            CustomParticipants = [Participant("101", "Estudante 1"), Participant("102", "Estudante 2")]
        };
        var submissionsGateway = new FakeSubmissionsGateway();
        var gradeReadGateway = new FakeAssignmentGradeReadGateway();
        var gradebookGateway = new FakeGradebookGateway();
        var sut = new GetStudentSubmissionQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            participantsGateway,
            submissionsGateway,
            gradeReadGateway: gradeReadGateway,
            gradebookGateway: gradebookGateway);

        var result = await sut.Handle(
            new GetStudentSubmissionQuery("usuario-42", "CURSO-1", "11", "nao-matriculado"),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, participantsGateway.TargetLookupCallCount);
        Assert.Equal(0, participantsGateway.CallCount);
        Assert.Equal(0, submissionsGateway.CallCount);
        Assert.Equal(0, gradeReadGateway.BulkReadCount + gradeReadGateway.IndividualReadCount);
        Assert.Equal(0, gradebookGateway.BulkReadCount + gradebookGateway.IndividualReadCount);
    }

    [Fact]
    public async Task Estudante_suspenso_permanece_fora_da_consulta_individual()
    {
        var participantsGateway = new FakeParticipantsGateway
        {
            CustomParticipants = [Participant("suspenso", "Aluno suspenso", suspended: true)]
        };
        var submissionsGateway = new FakeSubmissionsGateway();
        var sut = new GetStudentSubmissionQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            participantsGateway,
            submissionsGateway);

        var result = await sut.Handle(
            new GetStudentSubmissionQuery("usuario-42", "CURSO-1", "11", "suspenso"),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, participantsGateway.ReturnedParticipantCount);
        Assert.Equal(1, participantsGateway.TargetLookupCallCount);
        Assert.Equal(0, participantsGateway.CallCount);
        Assert.Equal(0, submissionsGateway.CallCount);
    }

    [Fact]
    public async Task Falha_parcial_de_grade_preserva_estado_unknown_na_consulta_individual()
    {
        var participantsGateway = new FakeParticipantsGateway
        {
            CustomParticipants = [Participant("101", "Estudante 1")]
        };
        var gradeReadGateway = new FakeAssignmentGradeReadGateway { ThrowOnRead = true };
        var gradebookGateway = new FakeGradebookGateway { ThrowOnRead = true };
        var submissionsGateway = new FakeSubmissionsGateway
        {
            Records = [Submitted("9001", "101", DueAt.AddHours(-1), gradingStatus: null, status: "unknown-status")]
        };
        var sut = new GetStudentSubmissionQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            participantsGateway,
            submissionsGateway,
            gradeReadGateway: gradeReadGateway,
            gradebookGateway: gradebookGateway);

        var result = await sut.Handle(
            new GetStudentSubmissionQuery("usuario-42", "CURSO-1", "11", "101"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(SubmissionEvaluationState.Unknown, result!.EvaluationState);
        Assert.False(result.NeedsGrading);
    }

    [Fact]
    public async Task Filtro_needs_grading_expoe_cobertura_desconhecida_sem_promover_a_pendencia()
    {
        var sut = CreateHandler(
            submissionsGateway: new FakeSubmissionsGateway
            {
                Records = [Submitted("submission-1", "101", DueAt.AddHours(-1), "notgraded")]
            },
            participantsGateway: new FakeParticipantsGateway
            {
                CustomParticipants = [Participant("101", "Estudante sintético")]
            },
            gradeReadGateway: new FakeAssignmentGradeReadGateway { ReturnMissingAssignment = true },
            gradebookGateway: new FakeGradebookGateway { ThrowOnRead = true });

        var result = await sut.Handle(
            new ListAssignmentSubmissionsQuery("usuario-sintetico", "CURSO-1", "501",
                AssignmentSubmissionFilter.NeedsGrading, 1, 20, null, null, true, true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.Submissions);
        Assert.Equal(0, result.Total);
        Assert.Equal(1, result.EvaluationUnknownCount);
        Assert.False(result.EvaluationCoverageComplete);
    }

    [Fact]
    public async Task Resumo_individual_equivale_a_linha_da_listagem_para_submissao_atrasada()
    {
        var submission = Submitted(
            "9001",
            "102",
            DueAt.AddHours(2),
            gradingStatus: "graded");
        var participantsGateway = new FakeParticipantsGateway();
        var submissionsGateway = new FakeSubmissionsGateway
        {
            Records = [submission], EffectiveDeadlinesComplete = true, EffectiveDueAt = DueAt
        };
        var gradeReadGateway = new FakeAssignmentGradeReadGateway();
        var gradebookGateway = new FakeGradebookGateway();
        var listHandler = new ListAssignmentSubmissionsQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            participantsGateway,
            submissionsGateway,
            gradeReadGateway: gradeReadGateway,
            gradebookGateway: gradebookGateway);
        var list = await listHandler.Handle(
            new ListAssignmentSubmissionsQuery(
                "usuario-42",
                "CURSO-1",
                "11",
                AssignmentSubmissionFilter.All,
                Page: 1,
                PageSize: 100,
                Since: null,
                Before: null,
                IncludeLate: true,
                IncludeUngraded: true),
            CancellationToken.None);
        var individualHandler = new GetStudentSubmissionQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            new FakeParticipantsGateway(),
            new FakeSubmissionsGateway
            {
                Records = [submission], EffectiveDeadlinesComplete = true, EffectiveDueAt = DueAt
            },
            gradeReadGateway: new FakeAssignmentGradeReadGateway(),
            gradebookGateway: new FakeGradebookGateway());

        var individual = await individualHandler.Handle(
            new GetStudentSubmissionQuery("usuario-42", "CURSO-1", "11", "102"),
            CancellationToken.None);

        Assert.NotNull(list);
        Assert.True(individual!.Late);
        Assert.Equal(Assert.Single(list!.Submissions, row => row.UserId == "102"), individual);
    }

    [Fact]
    public async Task Cancelamento_durante_paginacao_de_participantes_e_propaga()
    {
        using var cancellation = new CancellationTokenSource();
        var participantsGateway = new FakeParticipantsGateway { ThrowOnPage = 1 };
        var sut = new GetStudentSubmissionQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway(),
            participantsGateway,
            new FakeSubmissionsGateway());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.Handle(
            new GetStudentSubmissionQuery("usuario-42", "CURSO-1", "11", "101"),
            cancellation.Token));
    }

    [Fact]
    public async Task Atividade_inexistente_nao_consulta_participantes_ou_notas()
    {
        var participantsGateway = new FakeParticipantsGateway();
        var submissionsGateway = new FakeSubmissionsGateway();
        var gradeReadGateway = new FakeAssignmentGradeReadGateway();
        var gradebookGateway = new FakeGradebookGateway();
        var sut = new GetStudentSubmissionQueryHandler(
            new FakeCoursesGateway(),
            new FakeContentsGateway { ReturnWithoutAssignment = true },
            participantsGateway,
            submissionsGateway,
            gradeReadGateway: gradeReadGateway,
            gradebookGateway: gradebookGateway);

        var result = await sut.Handle(
            new GetStudentSubmissionQuery("usuario-42", "CURSO-1", "inexistente", "101"),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, participantsGateway.CallCount);
        Assert.Equal(0, submissionsGateway.CallCount);
        Assert.Equal(0, gradeReadGateway.BulkReadCount + gradeReadGateway.IndividualReadCount);
        Assert.Equal(0, gradebookGateway.BulkReadCount + gradebookGateway.IndividualReadCount);
    }

    [Fact]
    public async Task Curso_inacessivel_nao_consulta_conteudo_ou_participantes()
    {
        var contentsGateway = new FakeContentsGateway();
        var participantsGateway = new FakeParticipantsGateway();
        var sut = new GetStudentSubmissionQueryHandler(
            new FakeCoursesGateway { ReturnWithoutCourse = true },
            contentsGateway,
            participantsGateway,
            new FakeSubmissionsGateway());

        var result = await sut.Handle(
            new GetStudentSubmissionQuery("usuario-42", "CURSO-PRIVADO", "11", "101"),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, contentsGateway.CallCount);
        Assert.Equal(0, participantsGateway.CallCount);
    }

    private static ListAssignmentSubmissionsQueryHandler CreateHandler(
        FakeContentsGateway? contentsGateway = null,
        FakeSubmissionsGateway? submissionsGateway = null,
        FakeAssignmentSettingsGateway? settingsGateway = null,
        FakeAssignmentGradeReadGateway? gradeReadGateway = null,
        FakeParticipantsGateway? participantsGateway = null,
        FakeGradebookGateway? gradebookGateway = null)
    {
        return new ListAssignmentSubmissionsQueryHandler(
            new FakeCoursesGateway(),
            contentsGateway ?? new FakeContentsGateway(),
            participantsGateway ?? new FakeParticipantsGateway(),
            submissionsGateway ?? new FakeSubmissionsGateway(),
            settingsGateway,
            gradeReadGateway,
            gradebookGateway);
    }

    private static CourseParticipantSummary Participant(string userId, string fullName, bool suspended = false) =>
        new(userId, fullName, Email: null, Suspended: suspended, FirstAccessAt: null, LastAccessAt: null,
            LastCourseAccessAt: null, Roles: [], Groups: []);

    private static AssignmentSubmissionRecord Submitted(
        string submissionId,
        string userId,
        DateTimeOffset modifiedAt,
        string? gradingStatus,
        string status = "submitted")
    {
        return new AssignmentSubmissionRecord(
            submissionId,
            userId,
            status,
            gradingStatus,
            CreatedAt: modifiedAt.AddMinutes(-5),
            ModifiedAt: modifiedAt,
            AttemptNumber: 0,
            FileCount: 1,
            HasOnlineText: true);
    }

    private sealed class FakeCoursesGateway : IMoodleCoursesGateway
    {
        public bool ReturnWithoutCourse { get; init; }

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
            return Task.FromResult<CourseSummary?>(ReturnWithoutCourse ? null : new CourseSummary(
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
                null));
        }
    }

    private sealed class FakeContentsGateway : IMoodleCourseContentsGateway
    {
        public IReadOnlyList<CourseModuleDate>? Dates { get; init; }
        public bool ReturnWithoutAssignment { get; init; }

        public int CallCount { get; private set; }

        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId,
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var modules = ReturnWithoutAssignment
                ? Array.Empty<CourseModuleSummary>()
                :
                [
                    new CourseModuleSummary(
                        "11",
                        "501",
                        "assign",
                        "Tarefa 1",
                        "https://moodle.example/mod/assign/view.php?id=11",
                        true,
                        true,
                        "Descricao",
                        null,
                        Dates ?? [new CourseModuleDate("Entrega ate", DueAt)],
                        [])
                ];

            return Task.FromResult(new CourseContentsSummary(
                courseId,
                moduleTypes.ToArray(),
                includeHidden,
                onlyWithFiles,
                [new CourseSectionSummary("1", 1, "Topico 1", null, true, modules.Length, modules.Length == 0, modules)]));
        }
    }

    private sealed class FakeParticipantsGateway : IMoodleParticipantsGateway
    {
        private static readonly CourseParticipantSummary[] Participants =
        [
            Participant("101", "Estudante 1"),
            Participant("102", "Estudante 2"),
            Participant("103", "Estudante 3")
        ];

        public IReadOnlyList<CourseParticipantSummary>? CustomParticipants { get; init; }

        public int? ThrowOnPage { get; init; }

        public int CallCount { get; private set; }

        public int TargetLookupCallCount { get; private set; }

        public int ReturnedParticipantCount { get; private set; }

        public Task<CourseParticipantSummary?> FindStudentAsync(
            string userExternalId,
            string courseId,
            string studentId,
            CancellationToken cancellationToken)
        {
            TargetLookupCallCount++;
            if (ThrowOnPage == 1)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var participant = (CustomParticipants ?? Participants).FirstOrDefault(candidate =>
                candidate.Suspended != true &&
                string.Equals(candidate.UserId, studentId, StringComparison.OrdinalIgnoreCase));
            if (participant is not null)
            {
                ReturnedParticipantCount++;
            }

            return Task.FromResult(participant);
        }
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
            CallCount++;
            if (ThrowOnPage == page)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var available = CustomParticipants ?? Participants;
            if (statusFilter == ParticipantStatusFilter.Active)
            {
                available = available.Where(participant => participant.Suspended != true).ToArray();
            }

            var pageItems = available
                .Skip((Math.Max(1, page) - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            var hasMore = page * pageSize < available.Count;
            ReturnedParticipantCount += pageItems.Length;

            return Task.FromResult(new CourseParticipantsPage(
                courseId,
                page,
                pageSize,
                statusFilter,
                studentsOnly,
                includeEmail,
                hasMore,
                pageItems));
        }

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(
            string userExternalId,
            string courseId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

    }

    private sealed class FakeSubmissionsGateway : IMoodleAssignmentSubmissionsGateway
    {
        public bool ExtensionsComplete { get; init; } = true;
        public DateTimeOffset? ExtensionDueAt { get; init; }
        public bool EffectiveDeadlinesComplete { get; init; }
        public DateTimeOffset? EffectiveDueAt { get; init; }
        public int ExtensionReadCount { get; private set; }
        public Task<IReadOnlyList<AssignmentExtensionsBatch>> GetAssignmentExtensionsBatchAsync(
            string userExternalId, IReadOnlyCollection<string> assignmentIds, CancellationToken cancellationToken)
        {
            ExtensionReadCount++;
            return Task.FromResult<IReadOnlyList<AssignmentExtensionsBatch>>(assignmentIds.Select(id =>
                new AssignmentExtensionsBatch(id, Records.Select(record => record.UserId).Distinct()
                    .ToDictionary(id => id, _ => ExtensionDueAt), ExtensionsComplete)).ToArray());
        }

        public Task<IReadOnlyList<AssignmentEffectiveDeadlinesBatch>> GetAssignmentEffectiveDeadlinesBatchAsync(
            string userExternalId,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> assignmentUserIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<AssignmentEffectiveDeadlinesBatch>>(assignmentUserIds.Select(pair =>
            {
                var deadlines = pair.Value.ToDictionary(
                    id => id,
                    _ => new AssignmentEffectiveDeadline(EffectiveDueAt, EffectiveDeadlinesComplete),
                    StringComparer.OrdinalIgnoreCase);
                return new AssignmentEffectiveDeadlinesBatch(
                    pair.Key, deadlines, EffectiveDeadlinesComplete);
            }).ToArray());
        }

        public IReadOnlyList<AssignmentSubmissionRecord> Records { get; init; } = [];

        public bool WasCalled { get; private set; }

        public int CallCount { get; private set; }

        public string LastAssignmentId { get; private set; } = string.Empty;

        public string? LastStatus { get; private set; }

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
            string userExternalId,
            string assignmentId,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            CallCount++;
            LastAssignmentId = assignmentId;
            LastStatus = status;
            return Task.FromResult(Records);
        }
    }

    private sealed class FakeAssignmentSettingsGateway : IMoodleAssignmentSettingsGateway
    {
        public AssignmentSettingsSummary? Settings { get; init; }

        public int CallCount { get; private set; }

        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            string assignmentId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Settings);
        }
    }

    private sealed class FakeAssignmentGradeReadGateway : IMoodleAssignmentGradeReadGateway
    {
        public IReadOnlyDictionary<string, AssignmentExistingGrade> Grades { get; init; } =
            new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase);

        public bool ThrowOnRead { get; init; }
        public bool ReturnMissingAssignment { get; init; }

        public int BulkReadCount { get; private set; }

        public int IndividualReadCount { get; private set; }

        public string? LastSingleStudentId { get; private set; }

        public IReadOnlyCollection<string> LastBulkStudentIds { get; private set; } = [];

        public Task<AssignmentExistingGrade?> GetExistingGradeAsync(
            string userExternalId,
            string assignmentId,
            string studentId,
            CancellationToken cancellationToken)
        {
            IndividualReadCount++;
            LastSingleStudentId = studentId;
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Simulated grade read failure.");
            }

            return Task.FromResult(Grades.GetValueOrDefault(studentId));
        }

        public Task<IReadOnlyDictionary<string, AssignmentExistingGrade>> GetExistingGradesAsync(
            string userExternalId,
            string assignmentId,
            IReadOnlyCollection<string> studentIds,
            CancellationToken cancellationToken)
        {
            BulkReadCount++;
            LastBulkStudentIds = studentIds.ToArray();
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Simulated bulk grade read failure.");
            }

            return Task.FromResult(Grades);
        }

        public async Task<IReadOnlyList<AssignmentGradesBatch>> GetExistingGradesBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            IReadOnlyCollection<string> studentIds,
            CancellationToken cancellationToken)
        {
            if (ReturnMissingAssignment)
            {
                return [];
            }

            try
            {
                var grades = await GetExistingGradesAsync(
                    userExternalId,
                    assignmentIds.Single(),
                    studentIds,
                    cancellationToken);
                return [new AssignmentGradesBatch(assignmentIds.Single(), grades)];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return [new AssignmentGradesBatch(assignmentIds.Single(),
                    new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase),
                    "simulated_read_failure")];
            }
        }
    }

    private sealed class FakeGradebookGateway : IMoodleGradebookGateway
    {
        public bool ThrowOnRead { get; init; }

        public int IndividualReadCount { get; private set; }

        public int BulkReadCount { get; private set; }

        public string? LastIndividualStudentId { get; private set; }

        public IReadOnlyCollection<string> LastBulkStudentIds { get; private set; } = [];

        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId,
            string studentId,
            CancellationToken cancellationToken)
        {
            IndividualReadCount++;
            LastIndividualStudentId = studentId;
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Simulated individual gradebook failure.");
            }

            return Task.FromResult(new CourseGradebook(courseId, studentId, []));
        }

        public Task<CourseGradebookSnapshot> GetCourseGradebookAsync(
            string courseId,
            IReadOnlyCollection<string> studentIds,
            int? groupId,
            CancellationToken cancellationToken)
        {
            BulkReadCount++;
            LastBulkStudentIds = studentIds.ToArray();
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Simulated bulk gradebook failure.");
            }

            var gradebooks = studentIds.ToDictionary(
                studentId => studentId,
                studentId => new CourseGradebook(courseId, studentId, []),
                StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new CourseGradebookSnapshot(
                courseId,
                gradebooks,
                new GradebookSnapshotCoverage(
                    "fake",
                    studentIds.Count,
                    gradebooks.Count,
                    IsComplete: true,
                    Truncated: false,
                    MissingStudentIds: [],
                    Warnings: [])));
        }
    }
}
