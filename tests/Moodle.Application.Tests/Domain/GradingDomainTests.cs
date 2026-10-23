using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Domain;

public sealed class GradingDomainTests
{
    [Fact]
    public void AssistedGradingBatch_Create_IniciaContadoresEStatus()
    {
        var batch = AssistedGradingBatch.Create(
            courseId: 10,
            assignmentIds: [501, 502],
            createdBySubject: "teacher-1",
            createdByMoodleUserId: 321,
            totalItems: 25);

        Assert.NotEqual(Guid.Empty, batch.Id);
        Assert.Equal(10, batch.CourseId);
        Assert.Equal([501, 502], batch.AssignmentIds);
        Assert.Equal("teacher-1", batch.CreatedBySubject);
        Assert.Equal(GradingBatchStatus.Pending, batch.Status);
        Assert.Equal(25, batch.TotalItems);
        Assert.Equal(0, batch.ProcessedItems);
        Assert.Equal(0, batch.ReadyItems);
        Assert.Equal(0, batch.BlockedItems);
        Assert.Equal(0, batch.FailedItems);
    }

    [Fact]
    public void AssistedGradingBatch_Create_PersisteConfiguracaoDoProfessorComValoresNormalizados()
    {
        var batch = AssistedGradingBatch.Create(
            courseId: 10,
            assignmentIds: [501],
            createdBySubject: "teacher-1",
            createdByMoodleUserId: 321,
            totalItems: 1,
            teacherInstructions: "  Valorize clareza e evidencias.  ",
            priority: " HIGH ");

        Assert.Equal("Valorize clareza e evidencias.", batch.TeacherInstructions);
        Assert.Equal("high", batch.Priority);
        Assert.True(batch.IncludeRubric);
        Assert.True(batch.IncludeSubmissionFiles);
        Assert.False(batch.IncludeCourseMaterials);
    }

    [Fact]
    public void AssistedGradingBatch_Create_PersisteContextoDeExecucaoSemSegredos()
    {
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            1,
            connectorClientId: " client-1 ",
            connectionAlias: " default ");

        Assert.Equal("client-1", batch.ConnectorClientId);
        Assert.Equal("default", batch.ConnectionAlias);
    }

    [Fact]
    public void AssistedGradingBatch_Create_RejeitaConfiguracaoInvalida()
    {
        Assert.Throws<ArgumentException>(() => AssistedGradingBatch.Create(
            10, [501], "teacher-1", 321, 1, priority: "urgent"));
        Assert.Throws<ArgumentException>(() => AssistedGradingBatch.Create(
            10, [501], "teacher-1", 321, 1, teacherInstructions: new string('x', 8001)));
    }

    [Fact]
    public void AssistedGradingItem_ApplyTeacherReview_SeparaNotaSugeridaDeNotaFinal()
    {
        var item = AssistedGradingItem.Create(
            batchId: Guid.NewGuid(),
            courseId: 10,
            assignmentId: 501,
            submissionId: 9001,
            moodleUserId: 101,
            attemptNumber: 0);

        item.SetDraft(
            suggestedGrade: 7.5m,
            confidence: 0.82m,
            draftFeedback: "Feedback sugerido.",
            privateNotesToTeacher: "Observacao privada do rascunho.");

        item.ApplyTeacherReview(
            finalGrade: 8.0m,
            finalFeedback: "Feedback revisado pelo professor.",
            reviewedBySubject: "teacher-1",
            reviewedByMoodleUserId: 321);

        Assert.Equal(7.5m, item.SuggestedGrade);
        Assert.Equal("Observacao privada do rascunho.", item.PrivateNotesToTeacher);
        Assert.Equal(8.0m, item.FinalGrade);
        Assert.Equal("Feedback revisado pelo professor.", item.FinalFeedback);
        Assert.Equal(GradingReviewStatus.Reviewed, item.ReviewStatus);
        Assert.Equal(GradingItemStatus.ReadyToCommit, item.Status);
        Assert.Equal(GradingCommitStatus.Pending, item.CommitStatus);
        Assert.Equal("teacher-1", item.ReviewedBySubject);
        Assert.NotNull(item.ReviewedAt);
    }

    [Fact]
    public void AssistedGradingItem_MarkAnalysisFailed_MarcaFalhaSemCommitMoodle()
    {
        var item = AssistedGradingItem.Create(
            batchId: Guid.NewGuid(),
            courseId: 10,
            assignmentId: 501,
            submissionId: 9001,
            moodleUserId: 101,
            attemptNumber: 0);

        item.MarkAnalysisFailed("Erro ao montar contexto.");

        Assert.Equal(GradingItemStatus.Failed, item.Status);
        Assert.Equal(GradingCommitStatus.NotReady, item.CommitStatus);
        Assert.Equal(0m, item.Confidence);
        Assert.Contains("Erro ao montar contexto", item.DraftFeedback);
        Assert.Equal(item.DraftFeedback, item.PrivateNotesToTeacher);
    }

    [Fact]
    public void AssistedGradingItem_MarkProcessingStage_PersisteCheckpointTecnico()
    {
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 10, 501, 9001, 101, 0);
        var at = DateTimeOffset.UtcNow;

        item.MarkProcessingStage(GradingProcessingStage.Analysis, at);

        Assert.Equal(GradingProcessingStage.Analysis, item.ProcessingStage);
        Assert.Equal(at, item.ProcessingStageUpdatedAt);
        Assert.Throws<ArgumentException>(() => item.MarkProcessingStage("unknown", at));
    }

    [Fact]
    public void AssistedGradingItem_RejectsGradeAboveKnownScale()
    {
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 10, 501, 9001, 101, 0);

        var draftEx = Assert.Throws<ArgumentOutOfRangeException>(() =>
            item.SetDraft(11m, 0.8m, "Rascunho.", maxGrade: 10m));
        Assert.Equal("suggestedGrade", draftEx.ParamName);

        item.SetDraft(8m, 0.8m, "Rascunho.", maxGrade: 10m);
        var reviewEx = Assert.Throws<ArgumentOutOfRangeException>(() =>
            item.ApplyTeacherReview(10.1m, "Feedback.", "teacher-1", 321, maxGrade: 10m));
        Assert.Equal("finalGrade", reviewEx.ParamName);
    }

    [Fact]
    public void GradingContext_HasMinimumContext_FicaFalseQuandoHaBloqueadoresMesmoComArquivo()
    {
        var context = GradingContext.Build(
            gradingItemId: Guid.NewGuid(),
            batchId: Guid.NewGuid(),
            courseId: "10",
            assignmentId: "501",
            submissionId: "9001",
            studentId: "101",
            assignmentStatement: null,
            criteria: null,
            rubricDescription: null,
            maxGrade: null,
            gradeScale: null,
            submissionText: null,
            attachedFiles:
            [
                new GradingFileInfo(
                    "entrega.pdf",
                    "application/pdf",
                    FileSizeBytes: 1200,
                    Sha256: "hash-1",
                    ExtractedText: "Texto extraido previamente.",
                    IsSupported: true)
            ],
            courseMaterials: null,
            teacherInstructions: null);

        Assert.NotEmpty(context.Blockers);
        Assert.False(context.HasMinimumContext);
    }

    [Fact]
    public void GradingContext_PreservaBloqueadorDeFalhaDeContextoSemMensagemGenericaDuplicada()
    {
        var context = GradingContext.Build(
            gradingItemId: Guid.NewGuid(),
            batchId: Guid.NewGuid(),
            courseId: "10",
            assignmentId: "501",
            submissionId: "9001",
            studentId: "101",
            assignmentStatement: null,
            criteria: null,
            rubricDescription: null,
            maxGrade: null,
            gradeScale: null,
            submissionText: "Resposta da submissao.",
            attachedFiles: [],
            courseMaterials: null,
            teacherInstructions: null,
            additionalBlockers: ["O contexto da atividade nao pode ser recuperado (context_fetch_failed)."]);

        Assert.Single(context.Blockers);
        Assert.Contains("context_fetch_failed", context.Blockers[0]);
    }

    [Fact]
    public void GradingContext_DistingueFalhaDeDownloadDeFormatoNaoSuportado()
    {
        var context = GradingContext.Build(
            gradingItemId: Guid.NewGuid(),
            batchId: Guid.NewGuid(),
            courseId: "10",
            assignmentId: "501",
            submissionId: "9001",
            studentId: "101",
            assignmentStatement: "Enunciado.",
            criteria: "Critérios.",
            rubricDescription: null,
            maxGrade: 10m,
            gradeScale: null,
            submissionText: null,
            attachedFiles:
            [
                new GradingFileInfo(
                    "EXTRA 03.docx",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    100,
                    null,
                    null,
                    IsSupported: false,
                    ExtractionStatus: "failed")
            ],
            courseMaterials: null,
            teacherInstructions: null);

        var blocker = Assert.Single(context.Blockers);
        Assert.Contains("download ou a extração", blocker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("formatos não suportados", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssistedGradingItem_ScheduleProcessingRetry_MantemItemPendenteSemApagarContexto()
    {
        var item = AssistedGradingItem.Create(Guid.NewGuid(), 10, 501, 9001, 101, 0);
        item.MarkAnalysisFailed("Falha temporária de leitura.");
        var nextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(1);

        item.ScheduleProcessingRetry("Moodle indisponível; nova tentativa agendada.", nextAttemptAt);

        Assert.Equal(GradingItemStatus.Pending, item.Status);
        Assert.Equal(GradingCommitStatus.NotReady, item.CommitStatus);
        Assert.Equal(GradingProcessingStage.Pending, item.ProcessingStage);
        Assert.Equal("Moodle indisponível; nova tentativa agendada.", item.PrivateNotesToTeacher);
        Assert.Equal(nextAttemptAt, item.NextAttemptAt);
    }
}
