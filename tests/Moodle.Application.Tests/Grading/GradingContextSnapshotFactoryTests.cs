using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingContextSnapshotFactoryTests
{
    [Fact]
    public void Create_PersisteEscalaConfirmadaComFingerprintNoSchemaV3()
    {
        var batch = AssistedGradingBatch.Create(42, [501], "teacher-1", 3001, 1);
        var item = AssistedGradingItem.Create(batch.Id, 42, 501, 7001, 3001, 0);
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "42",
            "501",
            "7001",
            "3001",
            assignmentStatement: "Enunciado.",
            criteria: "Critério.",
            rubricDescription: null,
            maxGrade: 20m,
            gradeScale: null,
            submissionText: "Entrega.",
            attachedFiles: [],
            courseMaterials: null,
            teacherInstructions: null,
            gradingMode: "numeric",
            confirmedMinimumGrade: 2m,
            confirmedMaximumGrade: 20m,
            gradeScaleId: 0,
            gradeScaleSource: "activity_gradebook",
            gradeScaleVersion: new string('a', 64));

        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());

        Assert.Equal("3", snapshot.SchemaVersion);
        Assert.Equal("numeric", snapshot.GradingScale!.GradingMode);
        Assert.Equal(2m, snapshot.GradingScale.MinimumGrade);
        Assert.Equal(20m, snapshot.GradingScale.MaximumGrade);
        Assert.Equal(0, snapshot.GradingScale.ScaleId);
        Assert.Equal("activity_gradebook", snapshot.GradingScale.Source);
        Assert.Equal(new string('a', 64), snapshot.GradingScale.Version);
        Assert.Equal(snapshot.ContextHash, GradingContextSnapshot.ComputeCanonicalPayloadHash(
            GradingContextSnapshot.SerializeCanonicalPayload(snapshot)));
    }

    [Fact]
    public void Create_NaoTrataMaximoIndicativoComoEscalaConfirmada()
    {
        var batch = AssistedGradingBatch.Create(42, [501], "teacher-1", 3001, 1);
        var item = AssistedGradingItem.Create(batch.Id, 42, 501, 7001, 3001, 0);
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "42",
            "501",
            "7001",
            "3001",
            assignmentStatement: "Valor: 16 pontos.",
            criteria: "Critério.",
            rubricDescription: null,
            maxGrade: 16m,
            gradeScale: null,
            submissionText: "Entrega.",
            attachedFiles: [],
            courseMaterials: null,
            teacherInstructions: null,
            gradingMode: "unknown");

        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());

        Assert.Equal(16m, context.MaxGrade);
        Assert.Equal("unknown", snapshot.GradingScale!.GradingMode);
        Assert.Null(snapshot.GradingScale.MaximumGrade);
    }

    [Fact]
    public void Create_PreservaHashCoberturaEReferenciaDoArtifact()
    {
        var batch = AssistedGradingBatch.Create(42, [501], "teacher-1", 3001, 1);
        var item = AssistedGradingItem.Create(batch.Id, 42, 501, 7001, 3001, 0);
        var artifactId = Guid.NewGuid();
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "42",
            "501",
            "7001",
            "3001",
            assignmentStatement: "Descreva a solução proposta.",
            criteria: "Descrever a solução\nJustificar as escolhas",
            rubricDescription: "Rubrica formal",
            maxGrade: 10m,
            gradeScale: null,
            submissionText: "Texto da entrega.",
            attachedFiles:
            [
                new GradingFileInfo(
                    "entrega.pdf",
                    "application/pdf",
                    120,
                    "sha-1",
                    "Texto da entrega.",
                    true,
                    artifactId,
                    "submission_file",
                    "succeeded",
                    SourceCharacterCount: 300,
                    IsTruncated: true,
                    Source: new GradingSourceMetadata("assignsubmission_file", 7001, 3, "Entrega", 0))
            ],
            courseMaterials: null,
            teacherInstructions: "Priorize clareza.");

        var snapshot = GradingContextSnapshotFactory.Create(
            item,
            context,
            new GradingContextOptions(
                IncludeRubric: true,
                IncludeSubmissionFiles: true,
                IncludeCourseMaterials: false,
                TeacherInstructions: "Priorize clareza."));

        Assert.Equal(item.Id, snapshot.ItemId);
        Assert.Equal(batch.Id, snapshot.BatchId);
        Assert.Equal(501, snapshot.Assignment.AssignmentId);
        Assert.Equal(7001, snapshot.Submission?.SubmissionId);
        Assert.Equal("Priorize clareza.", snapshot.TeacherInstructions);
        Assert.Equal(GradingContextSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Matches("^[a-f0-9]{64}$", snapshot.ContextHash);
        Assert.True(snapshot.Coverage.IsPartial);
        Assert.Equal(artifactId, Assert.Single(snapshot.Artifacts).ArtifactId);
        Assert.True(Assert.Single(snapshot.Artifacts).IsTruncated);
    }

    [Fact]
    public void RecordContextSnapshot_PersisteSomenteIdentidadeVerificadaNoItem()
    {
        var batch = AssistedGradingBatch.Create(42, [501], "teacher-1", 3001, 1);
        var item = AssistedGradingItem.Create(batch.Id, 42, 501, 7001, 3001, 0);
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "42",
            "501",
            "7001",
            "3001",
            assignmentStatement: "Enunciado.",
            criteria: "Critério avaliável.",
            rubricDescription: null,
            maxGrade: 10m,
            gradeScale: null,
            submissionText: "Entrega legível.",
            attachedFiles: [],
            courseMaterials: null,
            teacherInstructions: null);
        var snapshot = GradingContextSnapshotFactory.Create(
            item,
            context,
            new GradingContextOptions());

        item.RecordContextSnapshot(snapshot);

        Assert.Equal(snapshot.Version, item.ContextVersion);
        Assert.Equal(snapshot.ContextHash, item.ContextHash);
        Assert.Equal(snapshot.ContextStatus, item.ContextStatus);
    }
}
