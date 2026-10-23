using Moodle.Application.Configuration;
using Moodle.Application.Grading.Packages;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingPackagePlanningTests
{
    [Fact]
    public void SplitPackageItems_counts_identical_context_once_across_students()
    {
        var items = CreateItems(3);
        var artifacts = items.ToDictionary(item => item.Id,
            item => (IReadOnlyList<GradingArtifact>)[new GradingArtifact(
                Guid.NewGuid(), item.Id, "assignment_context", "enunciado.pdf", "application/pdf",
                new string('a', 64), 500_000, "succeeded", null, null, DateTimeOffset.UtcNow)]);
        var chunks = PrepareGradingPackagesCommandHandler.SplitPackageItems(items, artifacts,
            new GradingLimitsOptions
            {
                MaxGradingPackageZipMb = 1,
                MaxGradingPackageUncompressedMb = 1,
                MaxGradingPackageEntries = 7,
                MaxTextCharsPerSubmission = 1
            });

        Assert.Equal(3, Assert.Single(chunks).Count);
    }

    [Fact]
    public void SplitPackageItems_respeita_limite_de_entradas_antes_de_baixar_os_arquivos()
    {
        var items = CreateItems(3);
        var artifactsByItem = items.ToDictionary(
            item => item.Id,
            item => (IReadOnlyList<GradingArtifact>)[
                CreateArtifact(item.Id, sizeBytes: 128),
            ]);
        var options = new GradingLimitsOptions
        {
            MaxGradingPackageItems = 20,
            MaxGradingPackageEntries = 5,
            MaxGradingPackageZipMb = 1,
            MaxGradingPackageUncompressedMb = 1,
            MaxFileSizeMb = 1,
            MaxTextCharsPerSubmission = 1
        };

        var chunks = PrepareGradingPackagesCommandHandler.SplitPackageItems(
            items,
            artifactsByItem,
            options);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.Single(chunk));
    }

    [Fact]
    public void SplitPackageItems_respeita_orcamento_de_bytes_mesmo_com_poucas_entradas()
    {
        var items = CreateItems(2);
        var artifactsByItem = items.ToDictionary(
            item => item.Id,
            item => (IReadOnlyList<GradingArtifact>)[
                CreateArtifact(item.Id, sizeBytes: 700_000),
            ]);
        var options = new GradingLimitsOptions
        {
            MaxGradingPackageItems = 20,
            MaxGradingPackageEntries = 20,
            MaxGradingPackageZipMb = 1,
            MaxGradingPackageUncompressedMb = 1,
            MaxFileSizeMb = 1,
            MaxTextCharsPerSubmission = 1
        };

        var chunks = PrepareGradingPackagesCommandHandler.SplitPackageItems(
            items,
            artifactsByItem,
            options);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, chunk => Assert.Single(chunk));
    }

    [Fact]
    public void SplitPackageItems_nao_reserva_contexto_de_itens_fora_do_grupo()
    {
        var items = CreateItems(2);
        var unrelatedItemId = Guid.NewGuid();
        var artifactsByItem = new Dictionary<Guid, IReadOnlyList<GradingArtifact>>
        {
            [unrelatedItemId] =
            [
                CreateArtifact(
                    unrelatedItemId,
                    artifactType: "assignment_context",
                    sizeBytes: 983_040)
            ]
        };
        var options = new GradingLimitsOptions
        {
            MaxGradingPackageItems = 20,
            MaxGradingPackageEntries = 20,
            MaxGradingPackageZipMb = 1,
            MaxGradingPackageUncompressedMb = 1,
            MaxFileSizeMb = 1,
            MaxTextCharsPerSubmission = 1
        };

        var chunks = PrepareGradingPackagesCommandHandler.SplitPackageItems(
            items,
            artifactsByItem,
            options);

        Assert.Single(chunks);
        Assert.Equal(items, chunks[0]);
    }

    [Fact]
    public void SplitPackageItems_estima_texto_persistido_pelo_tamanho_real()
    {
        var items = CreateItems(2);
        var artifactsByItem = items.ToDictionary(
            item => item.Id,
            item => (IReadOnlyList<GradingArtifact>)[
                new GradingArtifact(
                    Guid.NewGuid(),
                    item.Id,
                    "assignment_context",
                    "enunciado.md",
                    "text/markdown",
                    new string('a', 64),
                    SizeBytes: null,
                    "succeeded",
                    new string('x', 128),
                    null,
                    DateTimeOffset.UtcNow)
            ]);
        var options = new GradingLimitsOptions
        {
            MaxGradingPackageItems = 20,
            MaxGradingPackageEntries = 20,
            MaxGradingPackageZipMb = 1,
            MaxGradingPackageUncompressedMb = 1,
            MaxFileSizeMb = 1,
            MaxTextCharsPerSubmission = 1
        };

        var chunks = PrepareGradingPackagesCommandHandler.SplitPackageItems(
            items,
            artifactsByItem,
            options);

        Assert.Single(chunks);
        Assert.Equal(items, chunks[0]);
    }

    [Fact]
    public void SplitPackageItems_mantem_reserva_conservadora_para_binario_com_texto_extraido()
    {
        var items = CreateItems(2);
        var artifactsByItem = items.ToDictionary(
            item => item.Id,
            item => (IReadOnlyList<GradingArtifact>)[
                new GradingArtifact(
                    Guid.NewGuid(),
                    item.Id,
                    "assignment_context",
                    "enunciado.pdf",
                    "application/pdf",
                    new string('a', 64),
                    SizeBytes: null,
                    "succeeded",
                    new string('x', 128),
                    null,
                    DateTimeOffset.UtcNow,
                    "https://moodle.example/enunciado.pdf")
            ]);
        var options = new GradingLimitsOptions
        {
            MaxGradingPackageItems = 20,
            MaxGradingPackageEntries = 20,
            MaxGradingPackageZipMb = 1,
            MaxGradingPackageUncompressedMb = 1,
            MaxFileSizeMb = 1,
            MaxTextCharsPerSubmission = 1
        };

        var chunks = PrepareGradingPackagesCommandHandler.SplitPackageItems(
            items,
            artifactsByItem,
            options);

        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void SplitPackageItems_trata_tipos_de_artifact_sem_diferenciar_maiusculas()
    {
        var items = CreateItems(3);
        var artifactsByItem = items.ToDictionary(
            item => item.Id,
            item => (IReadOnlyList<GradingArtifact>)[
                CreateArtifact(item.Id, sizeBytes: 128, artifactType: "SUBMISSION_FILE"),
            ]);
        var options = new GradingLimitsOptions
        {
            MaxGradingPackageItems = 20,
            MaxGradingPackageEntries = 5,
            MaxGradingPackageZipMb = 1,
            MaxGradingPackageUncompressedMb = 1,
            MaxFileSizeMb = 1,
            MaxTextCharsPerSubmission = 1
        };

        var chunks = PrepareGradingPackagesCommandHandler.SplitPackageItems(
            items,
            artifactsByItem,
            options);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.Single(chunk));
    }

    [Fact]
    public void SplitPackageItems_reserva_contexto_com_tipo_legado_em_maiusculas()
    {
        var items = CreateItems(2);
        var artifactsByItem = items.ToDictionary(
            item => item.Id,
            item => (IReadOnlyList<GradingArtifact>)[
                CreateArtifact(item.Id, sizeBytes: 128, artifactType: "ASSIGNMENT_CONTEXT"),
            ]);
        var options = new GradingLimitsOptions
        {
            MaxGradingPackageItems = 20,
            MaxGradingPackageEntries = 5,
            MaxGradingPackageZipMb = 1,
            MaxGradingPackageUncompressedMb = 1,
            MaxFileSizeMb = 1,
            MaxTextCharsPerSubmission = 1
        };

        var chunks = PrepareGradingPackagesCommandHandler.SplitPackageItems(
            items,
            artifactsByItem,
            options);

        Assert.Equal(2, chunks.Count);
    }

    private static IReadOnlyList<AssistedGradingItem> CreateItems(int count)
    {
        var batchId = Guid.NewGuid();
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var item = AssistedGradingItem.Create(
                    batchId,
                    courseId: 10,
                    assignmentId: 501,
                    submissionId: 9000 + index,
                    moodleUserId: 100 + index,
                    attemptNumber: 1,
                    studentDisplayName: $"Aluno {index}");
                item.MarkAwaitingAiAnalysis(null);
                return item;
            })
            .ToArray();
    }

    private static GradingArtifact CreateArtifact(
        Guid itemId,
        long sizeBytes,
        string artifactType = "submission_file") =>
        new(
            Guid.NewGuid(),
            itemId,
            artifactType,
            "answer.bin",
            "application/octet-stream",
            new string('a', 64),
            sizeBytes,
            "succeeded",
            null,
            null,
            DateTimeOffset.UtcNow,
            "https://moodle.test/file");
}
