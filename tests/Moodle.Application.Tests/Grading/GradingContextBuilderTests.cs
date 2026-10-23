using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingContextBuilderTests
{
    [Fact]
    public async Task BuildAsync_ComResourceOriginal_NaoBloqueiaQuandoExtracaoFalha()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "submission_file",
            "resposta.rtf",
            "text/rtf",
            "hash-rtf",
            SizeBytes: 4028,
            ExtractionStatus: ExtractionStatus.Failed,
            ExtractedTextRef: null,
            SummaryRef: "extract_failed",
            CreatedAt: DateTimeOffset.UtcNow,
            SourceUrl: "https://moodle.example/pluginfile.php/1/resposta.rtf"));

        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeRubric: false, IncludeSubmissionFiles: true),
            CancellationToken.None);

        var file = Assert.Single(context.AttachedFiles);
        Assert.True(file.OriginalResourceAvailable);
        Assert.DoesNotContain(
            context.Blockers,
            blocker => blocker.Contains("Submissão sem conteúdo legível", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_UsaSomenteTextoExtraidoJaPersistido()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        repository.Artifacts.AddRange(
        [
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "submission_file",
                "relatorio.pdf",
                "application/pdf",
                "hash-1",
                SizeBytes: 1024,
                ExtractionStatus: "succeeded",
                ExtractedTextRef: "Texto extraido salvo localmente com conteudo suficiente.",
                SummaryRef: null,
                DateTimeOffset.UtcNow),
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "submission_file",
                "imagem.png",
                "image/png",
                "hash-2",
                SizeBytes: 2048,
                ExtractionStatus: "failed",
                ExtractedTextRef: null,
                SummaryRef: null,
                DateTimeOffset.UtcNow)
        ]);
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions
            {
                MaxFilesPerSubmission = 1,
                MaxTextCharsPerSubmission = 12
            }),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeSubmissionFiles: true, TeacherInstructions: "Priorize clareza."),
            CancellationToken.None);

        Assert.Equal(item.Id, context.GradingItemId);
        var file = Assert.Single(context.AttachedFiles);
        Assert.Equal("relatorio.pdf", file.FileName);
        Assert.Equal("Texto extrai", file.ExtractedText);
        Assert.Equal(file.ExtractedText, context.SubmissionText);
        Assert.True(file.IsSupported);
        Assert.Equal(2, context.ArtifactReferences.Count);
        Assert.Contains(context.ArtifactReferences, artifact => artifact.ArtifactId == file.ArtifactId);
        Assert.Contains(context.ArtifactReferences, artifact => artifact.ExtractionStatus == "failed");
        Assert.Equal("Priorize clareza.", context.TeacherInstructions);
        Assert.Equal(1, repository.ListArtifactsCalls);
    }

    [Fact]
    public async Task BuildAsync_IgnoraDiagnosticoAntigoQuandoContextoFoiRecuperado()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 10, 501, 9001, 101, 0);
        repository.Artifacts.AddRange(
        [
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "assignment_context",
                "assignment-9001",
                null,
                null,
                null,
                ExtractionStatus.Failed,
                null,
                "context_fetch_failed",
                DateTimeOffset.UtcNow),
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "assignment_context",
                "orientacoes.pdf",
                "application/pdf",
                "hash-context",
                100,
                ExtractionStatus.Succeeded,
                "Enunciado recuperado com orientacoes suficientes.",
                "section:1;distance:1",
                DateTimeOffset.UtcNow)
        ]);
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeCourseMaterials: true),
            CancellationToken.None);

        Assert.DoesNotContain(context.Blockers, blocker => blocker.Contains("context_fetch_failed", StringComparison.OrdinalIgnoreCase));
        Assert.Null(context.AssignmentStatement);
        Assert.Contains("Enunciado recuperado", context.CourseMaterials);
    }

    [Fact]
    public async Task BuildAsync_MantemMateriaisDeCursoSemVinculoComoApoio()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 29972, 101112, 1178546, 356968, 0);
        repository.Artifacts.AddRange(
        [
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "assignment_context",
                "Calendario do curso.pdf",
                "application/pdf",
                "sha-calendar",
                SizeBytes: 100,
                ExtractionStatus: "succeeded",
                ExtractedTextRef: "Datas gerais e recados administrativos.",
                SummaryRef: null,
                CreatedAt: DateTimeOffset.UtcNow),
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "assignment_context",
                "Orientacoes SAP 01 - Etapa 1.pdf",
                "application/pdf",
                "sha-enunciado",
                SizeBytes: 200,
                ExtractionStatus: "succeeded",
                ExtractedTextRef: "Enunciado da atividade SAP 01 etapa 1 com criterios de entrega.",
                SummaryRef: null,
                CreatedAt: DateTimeOffset.UtcNow)
        ]);
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeSubmissionFiles: false, IncludeCourseMaterials: true),
            CancellationToken.None);

        Assert.Null(context.AssignmentStatement);
        Assert.Contains("Orientacoes SAP 01", context.CourseMaterials);
        Assert.Contains("Calendario do curso", context.CourseMaterials);
    }

    [Fact]
    public async Task BuildAsync_DescricaoOficialNaoPodeSerSubstituidaPorMaterialGenerico()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "assignment_context",
            "Web Aula - guia geral.pdf",
            "application/pdf",
            "sha-guide",
            200,
            ExtractionStatus.Succeeded,
            "Guia geral do curso sem instruções específicas para esta entrega.",
            "section:1;distance:0;score:20;reason:name_match",
            DateTimeOffset.UtcNow));
        var settingsGateway = new FakeMoodleAssignmentSettingsGateway
        {
            Result = new AssignmentSettingsSummary("501", 10m, "Tarefa", true, "Faça a atividade definida pelo professor.")
        };
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            settingsGateway,
            new HeuristicCriteriaGenerationService());
        await sut.PrepareBatchAsync(batch, [item], CancellationToken.None);

        var context = await sut.BuildAsync(item, new GradingContextOptions(IncludeCourseMaterials: true), CancellationToken.None);

        Assert.Equal("Faça a atividade definida pelo professor.", context.AssignmentStatement);
        Assert.Equal("numeric", context.GradingMode);
        Assert.DoesNotContain("Guia geral", context.AssignmentStatement);
    }

    [Fact]
    public async Task BuildAsync_WebAulaMuitoSemelhanteSemVinculoOficialPermaneceMaterialAuxiliar()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            includeCourseMaterials: true);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "assignment_context",
            "Web Aula SAP 01 etapa 1 — atividade e critérios de entrega.pdf",
            "application/pdf",
            "sha-web-aula",
            512,
            ExtractionStatus.Succeeded,
            "Enunciado SAP 01 etapa 1: elaborar a análise solicitada e apresentar os critérios de entrega.",
            "section:1;distance:0;score:99;reason:name_match",
            DateTimeOffset.UtcNow));
        var settingsGateway = new FakeMoodleAssignmentSettingsGateway
        {
            Result = new AssignmentSettingsSummary(
                "501",
                40m,
                "SAP 01 etapa 1 — análise aplicada",
                IsGradable: true,
                RawGrade: 40m)
        };
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            settingsGateway,
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeCourseMaterials: true),
            CancellationToken.None);

        Assert.Null(context.AssignmentStatement);
        Assert.Contains("Web Aula SAP 01", context.CourseMaterials, StringComparison.Ordinal);
        Assert.Null(context.Criteria);
        Assert.Contains(context.Blockers, blocker => blocker.Contains("Critérios, rubrica ou enunciado não informados", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_FontesOficiaisDivergentesBloqueiamAProposta()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(), item.Id, "assignment_context", "intro.html", "text/html", null, 20,
            ExtractionStatus.Succeeded, "Texto oficial retornado por course contents.",
            "assignment_description", DateTimeOffset.UtcNow));
        var settingsGateway = new FakeMoodleAssignmentSettingsGateway
        {
            Result = new AssignmentSettingsSummary("501", 10m, "Tarefa", true, "Outro enunciado oficial.")
        };
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            settingsGateway,
            new HeuristicCriteriaGenerationService());
        await sut.PrepareBatchAsync(batch, [item], CancellationToken.None);

        var context = await sut.BuildAsync(item, new GradingContextOptions(IncludeCourseMaterials: true), CancellationToken.None);

        Assert.Null(context.AssignmentStatement);
        Assert.Contains(context.Blockers, blocker => blocker.Contains("official_sources_conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_ExtraiCriteriosENotaMaximaDoContextoSelecionado()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 29972, 101112, 1178546, 356968, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "assignment_context",
            "Enunciado SAP 01 - Etapa 1.pdf",
            "application/pdf",
            "sha-enunciado",
            SizeBytes: 300,
            ExtractionStatus: "succeeded",
            ExtractedTextRef:
                """
                Enunciado da atividade SAP 01 - Etapa 1.
                Valor: 16 pontos.
                Critérios de avaliação:
                - Descrever o gerenciamento de eventos de TI.
                - Apresentar exemplos de incidentes e problemas.
                - Propor ações corretivas coerentes.
                """,
            SummaryRef: "official_intro_attachment",
            CreatedAt: DateTimeOffset.UtcNow));
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeSubmissionFiles: false, IncludeCourseMaterials: true),
            CancellationToken.None);

        Assert.Equal(16m, context.MaxGrade);
        Assert.Equal("unknown", context.GradingMode);
        Assert.Contains("gerenciamento de eventos", context.Criteria, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ações corretivas", context.Criteria, StringComparison.OrdinalIgnoreCase);
        // Sem submissao incluida, o blocker de submissao e esperado
        Assert.Contains(context.Blockers, blocker => blocker.Contains("Submissão não disponível", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(context.Blockers, b => b.Contains("Escala", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(context.Blockers, b => b.Contains("Critérios", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_ExtraiCriteriosEValorDeSapComCriteriosNaMesmaLinha()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 29972, 101112, 1178546, 356968, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "assignment_context",
            "SAP 01.pdf",
            "application/pdf",
            "sha-sap-01",
            SizeBytes: 4317,
            ExtractionStatus: "succeeded",
            ExtractedTextRef:
                """
                Situação de Aprendizagem 01
                Envio SAP 01 - Etapa 1
                Valor da atividade: 49 pontos
                Critérios de avaliação: organização do plano de gerenciamento; descrição do gerenciamento de eventos, incidentes e problemas; aderência às boas práticas de ITIL; clareza na proposta de ações corretivas.
                Produto esperado: Plano de Gerenciamento de Eventos, Incidentes e Problemas de TI.
                """,
            SummaryRef: "official_intro_attachment",
            CreatedAt: DateTimeOffset.UtcNow));
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeSubmissionFiles: false, IncludeCourseMaterials: true),
            CancellationToken.None);

        Assert.Equal(49m, context.MaxGrade);
        Assert.Contains("organização do plano de gerenciamento", context.Criteria, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gerenciamento de eventos", context.Criteria, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ITIL", context.Criteria, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Produto esperado", context.Criteria, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildAsync_ComArtefatoDeRubrica_PopulaRubricDescriptionENotaMaxima()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 29972, 101112, 1178546, 356968, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "rubric",
            "Rubrica SAP 01.pdf",
            "application/pdf",
            "sha-rubric",
            SizeBytes: 500,
            ExtractionStatus: "succeeded",
            ExtractedTextRef:
                """
                Rubrica de avaliação SAP 01.
                Valor da atividade: 20 pontos.
                Critério 1: Clareza na descrição dos processos ITIL.
                Critério 2: Coerência das ações corretivas propostas.
                """,
            SummaryRef: null,
            CreatedAt: DateTimeOffset.UtcNow));
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeSubmissionFiles: false, IncludeCourseMaterials: true),
            CancellationToken.None);

        Assert.NotNull(context.RubricDescription);
        Assert.Contains("Rubrica de avaliação", context.RubricDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(20m, context.MaxGrade);
        // Com rubricDescription preenchida, o blocker de critérios não deve disparar
        Assert.DoesNotContain(context.Blockers, b => b.Contains("Critérios", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_IncludeRubricFalse_NaoIncluiRubricaMesmoComMateriaisDeCurso()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 29972, 101112, 1178546, 356968, 0);
        repository.Artifacts.AddRange(
        [
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "rubric",
                "Rubrica.pdf",
                "application/pdf",
                "sha-rubric",
                SizeBytes: 200,
                ExtractionStatus: "succeeded",
                ExtractedTextRef: "Rubrica formal: clareza e coerencia.",
                SummaryRef: null,
                CreatedAt: DateTimeOffset.UtcNow),
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "assignment_context",
                "Enunciado.pdf",
                "application/pdf",
                "sha-context",
                SizeBytes: 300,
                ExtractionStatus: "succeeded",
                ExtractedTextRef: "Enunciado da atividade com orientacoes suficientes para analise.",
                SummaryRef: "official_intro_attachment",
                CreatedAt: DateTimeOffset.UtcNow)
        ]);
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(
                IncludeRubric: false,
                IncludeSubmissionFiles: false,
                IncludeCourseMaterials: true),
            CancellationToken.None);

        Assert.Null(context.RubricDescription);
        Assert.NotNull(context.AssignmentStatement);
        Assert.Contains("Enunciado da atividade", context.AssignmentStatement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildAsync_SomenteRubrica_MantemEnunciadoSemTratarComoMaterialDeCurso()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 29972, 101112, 1178546, 356968, 0);
        repository.Artifacts.AddRange(
        [
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "rubric",
                "Rubrica.pdf",
                "application/pdf",
                "sha-rubric",
                SizeBytes: 200,
                ExtractionStatus: "succeeded",
                ExtractedTextRef: "Rubrica formal: clareza e coerencia.",
                SummaryRef: null,
                CreatedAt: DateTimeOffset.UtcNow),
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "assignment_context",
                "Enunciado.pdf",
                "application/pdf",
                "sha-context",
                SizeBytes: 300,
                ExtractionStatus: "succeeded",
                ExtractedTextRef: "Enunciado da atividade com orientacoes suficientes para analise.",
                SummaryRef: "official_intro_attachment",
                CreatedAt: DateTimeOffset.UtcNow)
        ]);
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(
                IncludeRubric: true,
                IncludeSubmissionFiles: false,
                IncludeCourseMaterials: false),
            CancellationToken.None);

        Assert.NotNull(context.RubricDescription);
        Assert.NotNull(context.AssignmentStatement);
        Assert.Null(context.CourseMaterials);
    }

    [Fact]
    public async Task BuildAsync_QuandoCriteriosNaoEstruturados_UsaEnunciadoComoFallbackDeCriterios()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 29972, 101112, 1178546, 356968, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "assignment_context",
            "Orientacoes SAP 02.pdf",
            "application/pdf",
            "sha-sap-02",
            SizeBytes: 200,
            ExtractionStatus: "succeeded",
            ExtractedTextRef:
                """
                Situação de Aprendizagem 02 - Etapa 2.
                O aluno deve elaborar um plano de continuidade de negócios para uma empresa de médio porte.
                Deve considerar os principais riscos operacionais e as estratégias de mitigação adequadas.
                Valor: 12 pontos.
                """,
            SummaryRef: "official_intro_attachment",
            CreatedAt: DateTimeOffset.UtcNow));
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeSubmissionFiles: false, IncludeCourseMaterials: true),
            CancellationToken.None);

        // Sem header de critérios no texto, o enunciado completo deve ser usado como fallback
        Assert.NotNull(context.Criteria);
        Assert.Contains("plano de continuidade", context.Criteria, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12m, context.MaxGrade);
        // Com criteria preenchida pelo fallback, o blocker de critérios não deve disparar
        Assert.DoesNotContain(context.Blockers, b => b.Contains("Critérios", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_QuandoArquivosNaoIncluidos_NaoConsultaArtefatos()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeRubric: false, IncludeSubmissionFiles: false),
            CancellationToken.None);

        Assert.Empty(context.AttachedFiles);
        Assert.Null(context.SubmissionText);
        Assert.Equal(0, repository.ListArtifactsCalls);
    }

    [Fact]
    public async Task BuildAsync_CriteriosContaminados_FallbackGeraCriteriosLimpos()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 29972, 101112, 1178546, 356968, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "assignment_context",
            "SAP 01.pdf",
            "application/pdf",
            "sha-sap-01",
            SizeBytes: 4317,
            ExtractionStatus: "succeeded",
            ExtractedTextRef:
                """
                à distância - individual Resultados esperados: elaboração, em documento de texto, de um plano de gerenciamento de eventos, incidentes e problemas de TI;
                indicação das boas práticas de ITIL aplicáveis ao cenário proposto;
                apresentação de ações corretivas coerentes com o plano.
                Produto esperado: Plano de Gerenciamento.
                """,
            SummaryRef: "official_intro_attachment",
            CreatedAt: DateTimeOffset.UtcNow));
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            settingsGateway: new FakeMoodleAssignmentSettingsGateway(),
            criteriaGenerationService: new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeSubmissionFiles: false, IncludeCourseMaterials: true),
            CancellationToken.None);

        // O critério contaminado "à distância - individual Resultados esperados..." NÃO deve aparecer
        if (!string.IsNullOrWhiteSpace(context.Criteria))
        {
            Assert.DoesNotContain("à distância - individual", context.Criteria);
        }

        // Os critérios devem ser claros e avaliáveis
        Assert.NotNull(context.Criteria);
        Assert.NotEmpty(context.Criteria);
    }

    [Fact]
    public async Task BuildAsync_CriteriosBons_NaoAcionaFallback()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 29972, 101112, 1178546, 356968, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "assignment_context",
            "Enunciado SAP 01.pdf",
            "application/pdf",
            "sha-enunciado",
            SizeBytes: 300,
            ExtractionStatus: "succeeded",
            ExtractedTextRef:
                """
                Critérios de avaliação: organização do plano de gerenciamento; descrição do gerenciamento de eventos, incidentes e problemas; aderência às boas práticas de ITIL; clareza na proposta de ações corretivas.
                Produto esperado: Plano.
                """,
            SummaryRef: "official_intro_attachment",
            CreatedAt: DateTimeOffset.UtcNow));

        // Usando FakeCriteriaGenerationService que registra chamadas
        var fakeGenerator = new FakeCriteriaGenerationService();
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            settingsGateway: new FakeMoodleAssignmentSettingsGateway(),
            criteriaGenerationService: fakeGenerator);

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeSubmissionFiles: false, IncludeCourseMaterials: true),
            CancellationToken.None);

        // Os critérios heurísticos são bons, o fallback NÃO deveria ter sido acionado
        Assert.Equal(0, fakeGenerator.CallCount);
        Assert.NotNull(context.Criteria);
        Assert.Contains("organização do plano de gerenciamento", context.Criteria, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildAsync_ComFallback_PrivateNotesInformaCriteriosGerados()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 29972, 101112, 1178546, 356968, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "assignment_context",
            "SAP 01.pdf",
            "application/pdf",
            "sha-sap-01",
            SizeBytes: 200,
            ExtractionStatus: "succeeded",
            ExtractedTextRef:
                """
                à distância - individual Resultados esperados: elaboração, em documento de texto, de um plano de gerenciamento de eventos, incidentes e problemas de TI.
                Produto esperado: Plano de Gerenciamento.
                """,
            SummaryRef: "official_intro_attachment",
            CreatedAt: DateTimeOffset.UtcNow));
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            settingsGateway: new FakeMoodleAssignmentSettingsGateway(),
            criteriaGenerationService: new HeuristicCriteriaGenerationService());

        var context = await sut.BuildAsync(
            item,
            new GradingContextOptions(IncludeSubmissionFiles: false, IncludeCourseMaterials: true),
            CancellationToken.None);

        // As notas de geração de critérios devem ficar em CriteriaGenerationNotes (separadas de TeacherInstructions)
        Assert.Contains("gerados a partir do contexto", context.CriteriaGenerationNotes ?? "", StringComparison.OrdinalIgnoreCase);
        // TeacherInstructions deve permanecer null quando não há instrução real do professor
        Assert.Null(context.TeacherInstructions);
    }

    [Fact]
    public async Task BuildAsync_SemEscalaConhecida_NaoInventaCemPontos()
    {
        var repository = new FakeGradingReviewRepository();
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 10, 501, 9001, 101, 0);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "assignment_context",
            "enunciado.txt",
            "text/plain",
            "sha-1",
            SizeBytes: 100,
            ExtractionStatus: "succeeded",
            ExtractedTextRef: "Elabore uma resposta fundamentada.",
            SummaryRef: null,
            CreatedAt: DateTimeOffset.UtcNow));

        var context = await new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway(),
            new HeuristicCriteriaGenerationService())
            .BuildAsync(item, new GradingContextOptions(IncludeCourseMaterials: true), CancellationToken.None);

        Assert.Null(context.MaxGrade);
    }

    [Fact]
    public async Task BuildAsync_MarcaEscalaDesconhecidaQuandoSettingsEGradebookDivergem()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var gradebook = new CourseGradebook("10", "101", [
            new GradebookItem(
                "grade-item-77",
                "Atividade",
                "mod",
                "assign",
                null,
                null,
                null,
                0m,
                20m,
                null,
                null,
                null,
                null,
                null,
                null,
                "501",
                ScaleId: 0,
                GradeType: 1)
        ]);
        var sut = new GradingContextBuilder(
            repository,
            Options.Create(new GradingLimitsOptions()),
            new HeuristicAssignmentContextSelectionService(),
            new FakeMoodleAssignmentSettingsGateway
            {
                Result = new AssignmentSettingsSummary("501", 10m, "Atividade", IsGradable: true, RawGrade: 10m)
            },
            new HeuristicCriteriaGenerationService(),
            gradebookGateway: new FakeMoodleGradebookGateway(gradebook));
        await sut.PrepareBatchAsync(batch, [item], CancellationToken.None);

        var context = await sut.BuildAsync(item, new GradingContextOptions(), CancellationToken.None);

        Assert.Equal("unknown", context.GradingMode);
        Assert.Equal("conflict", context.GradeScaleSource);
        Assert.Null(context.ConfirmedMaximumGrade);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("à distância - individual momento atividade", true)]
    [InlineData("modalidade a distancia individual", true)]
    [InlineData("Resultados esperados: elaboração de um plano de gerenciamento", true)]
    [InlineData("Produto esperado: Plano de Gerenciamento de TI", true)]
    [InlineData("Situação de Aprendizagem 01\nElaborar plano de TI", true)]
    [InlineData("elaborar um plano de gerenciamento de eventos de TI", false)]
    [InlineData("organização do plano de gerenciamento\ndescrição do gerenciamento de eventos\naderência às boas práticas", false)]
    public void AreCriteriaLowQuality_DetectaCorretamente(string? criteria, bool expectedLowQuality)
    {
        var result = GradingContextBuilder.AreCriteriaLowQuality(criteria);
        Assert.Equal(expectedLowQuality, result);
    }

    private sealed class FakeCriteriaGenerationService : ICriteriaGenerationService
    {
        public int CallCount { get; private set; }

        public Task<CriteriaGenerationResult> GenerateAsync(
            CriteriaGenerationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new CriteriaGenerationResult(
                Source: "fake",
                MaxPoints: request.MaxGrade,
                Confidence: 0.5m,
                Criteria: [new GeneratedCriterion("C1", "Critério fake para teste", request.MaxGrade, null)],
                Warnings: [],
                PrivateNotesToTeacher: "Critérios gerados por fake service."));
        }
    }

    private sealed class FakeGradingReviewRepository : IGradingReviewRepository
    {
        public List<GradingArtifact> Artifacts { get; } = [];

        public int ListArtifactsCalls { get; private set; }

        public Task AddBatchAsync(AssistedGradingBatch batch, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<AssistedGradingBatch?> GetBatchAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult<AssistedGradingBatch?>(null);

        public Task AddItemAsync(AssistedGradingItem item, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AddArtifactAsync(GradingArtifact artifact, CancellationToken cancellationToken)
        {
            Artifacts.Add(artifact);
            return Task.CompletedTask;
        }

        public Task AddEvidenceAsync(GradingEvidence evidence, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<AssistedGradingItem?> GetItemAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult<AssistedGradingItem?>(null);

        public Task<IReadOnlyList<AssistedGradingItem>> ListItemsByBatchAsync(
            Guid batchId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AssistedGradingItem>>([]);

        public Task<int> CountItemsByBatchAsync(Guid batchId, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<IReadOnlyList<GradingArtifact>> ListArtifactsByItemAsync(
            Guid gradingItemId,
            CancellationToken cancellationToken)
        {
            ListArtifactsCalls++;
            return Task.FromResult<IReadOnlyList<GradingArtifact>>(
                Artifacts.Where(artifact => artifact.GradingItemId == gradingItemId).ToArray());
        }

        public Task<IReadOnlyList<GradingEvidence>> ListEvidenceByItemAsync(
            Guid gradingItemId,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GradingEvidence>>([]);

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(
            GradingBatchStatus status, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());        public Task SaveChangesAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeMoodleAssignmentSettingsGateway : IMoodleAssignmentSettingsGateway
    {
        public AssignmentSettingsSummary? Result { get; init; }

        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId, string courseId, string assignmentId, CancellationToken cancellationToken)
            => Task.FromResult(Result);
    }

    private sealed class FakeMoodleGradebookGateway(CourseGradebook gradebook) : IMoodleGradebookGateway
    {
        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId,
            string studentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(gradebook);

        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId,
            string studentId,
            GradebookReadOptions readOptions,
            CancellationToken cancellationToken) =>
            Task.FromResult(gradebook);
    }
}
