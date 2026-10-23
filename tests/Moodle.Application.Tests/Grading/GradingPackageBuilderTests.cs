using System.Text;
using System.Text.Json;
using Moodle.Application.Grading.Packages;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingPackageBuilderTests
{
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void Build_DeduplicatesContextByContentNotArtifactId(bool sameContent, int expectedFiles)
    {
        var itemId = Guid.NewGuid();
        var context = Source(Guid.NewGuid(), itemId, "enunciado.pdf", "enunciado") with
        {
            IsContext = true,
            GradingItemId = null,
            Sha256 = new string('a', 64)
        };
        var other = context with
        {
            ArtifactId = Guid.NewGuid(),
            Sha256 = new string(sameContent ? 'a' : 'b', 64)
        };
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            CreatePackage(),
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [new GradingPackageSourceItem(itemId, Guid.NewGuid(), null, 101, "Ana", 9001, 0,
                null, "context-a", "submission-a", [Source(Guid.NewGuid(), itemId, "resposta.pdf", "entrega")])],
            [context, other], []));

        Assert.Equal(expectedFiles, result.Manifest.Activity.StatementFiles!.Count);
        Assert.Equal(expectedFiles, result.Entries.Count(entry => entry.Path.StartsWith("enunciado/", StringComparison.Ordinal)));
        Assert.Equal(expectedFiles, result.Files.Count(file => file.IsContext));
    }

    [Fact]
    public void Build_DistinguishesAttemptsAndMissingSubmissionIdsWithoutStudentNames()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            CreatePackage(),
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            ids.Select((id, index) => new GradingPackageSourceItem(id, Guid.NewGuid(), null,
                101, "Ana Silva", index == 2 ? null : 9001, index, null, "context-a", "submission-a",
                [Source(Guid.NewGuid(), id, "resposta.pdf", "entrega")])).ToArray(), [], []));

        var paths = result.Manifest.Items.SelectMany(item => item.Files).Select(file => file.Path).ToArray();
        Assert.Equal(3, paths.Distinct().Count());
        Assert.DoesNotContain(paths, path => path.Contains("Ana", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, path => path.StartsWith($"entregas/item_{ids[2]:N}/", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_PropagatesScopedStudentNameAndMarksUnavailableNameExplicitly()
    {
        var namedItemId = Guid.NewGuid();
        var unnamedItemId = Guid.NewGuid();
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            CreatePackage(),
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [
                new GradingPackageSourceItem(namedItemId, Guid.NewGuid(), null, 101, " Ana Silva ", 9001, 0,
                    null, "context-a", "submission-a", [Source(Guid.NewGuid(), namedItemId, "a.pdf", "A")]),
                new GradingPackageSourceItem(unnamedItemId, Guid.NewGuid(), null, 102, null, 9002, 0,
                    null, "context-a", "submission-b", [Source(Guid.NewGuid(), unnamedItemId, "b.pdf", "B")])
            ], [], []));

        var named = Assert.Single(result.Manifest.Items, item => item.GradingItemId == namedItemId);
        var unnamed = Assert.Single(result.Manifest.Items, item => item.GradingItemId == unnamedItemId);
        Assert.Equal("Ana Silva", named.StudentName);
        Assert.Equal("available", named.StudentNameStatus);
        Assert.Null(unnamed.StudentName);
        Assert.Equal("unavailable", unnamed.StudentNameStatus);
        var unnamedMember = Assert.Single(result.Members, member => member.GradingItemId == unnamedItemId);
        Assert.Null(unnamedMember.StudentDisplayName);

        using var json = JsonDocument.Parse(GradingPackageManifestHash.Serialize(result.Manifest));
        var unnamedJson = Assert.Single(
            json.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("gradingItemId").GetGuid() == unnamedItemId);
        Assert.Equal(JsonValueKind.Null, unnamedJson.GetProperty("studentName").ValueKind);
        Assert.Equal("unavailable", unnamedJson.GetProperty("studentNameStatus").GetString());
    }

    [Fact]
    public void ManifestHash_LegacyManifestWithoutStudentNameRemainsVerifiable()
    {
        var itemId = Guid.NewGuid();
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            CreatePackage(),
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [new GradingPackageSourceItem(itemId, Guid.NewGuid(), null, 101, null, 9001, 0,
                null, "context-a", "submission-a", [Source(Guid.NewGuid(), itemId, "a.pdf", "A")])],
            [], []));
        var legacyJson = GradingPackageManifestHash.Serialize(result.Manifest)
            .Replace(",\"studentName\":null", string.Empty, StringComparison.Ordinal)
            .Replace(",\"studentNameStatus\":\"unavailable\"", string.Empty, StringComparison.Ordinal);
        var legacyHash = GradingPackageManifestHash.ComputeSerializedJson(legacyJson);
        var restored = JsonSerializer.Deserialize<GradingPackageManifest>(legacyJson, GradingPackageContract.JsonOptions);

        Assert.NotNull(restored);
        Assert.Null(Assert.Single(restored!.Items).StudentName);
        Assert.Null(Assert.Single(restored.Items).StudentNameStatus);
        Assert.Equal(legacyHash, GradingPackageManifestHash.ComputeSerializedJson(legacyJson));
    }

    [Fact]
    public async Task Build_SeparatesHomonymsAndDuplicateFilenamesAndKeepsOnlineText()
    {
        var package = CreatePackage();
        var firstArtifact = Guid.NewGuid();
        var secondArtifact = Guid.NewGuid();
        var firstItem = Guid.NewGuid();
        var secondItem = Guid.NewGuid();
        var builder = new GradingPackageContentBuilder();

        var result = builder.Build(new GradingPackageBuildRequest(
            package,
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [
                new GradingPackageSourceItem(
                    firstItem, Guid.NewGuid(), null, 101, "José Silva", 9001, 0, null, "context-a", "submission-a",
                    [Source(firstArtifact, firstItem, "resposta.pdf", "PDF A")],
                    "<p>texto do primeiro aluno</p>", Guid.NewGuid()),
                new GradingPackageSourceItem(
                    secondItem, Guid.NewGuid(), null, 102, "Jose Silva", 9002, 0, null, "context-a", "submission-b",
                    [Source(secondArtifact, secondItem, "resposta.pdf", "PDF B")],
                    null)
            ],
            [],
            []));

        var paths = result.Entries.Select(entry => entry.Path).ToArray();
        Assert.Contains(paths, path => path.Contains("entregas/submission_9001/", StringComparison.Ordinal));
        Assert.Contains(paths, path => path.Contains("entregas/submission_9002/", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.Contains("silva", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, paths.Count(path => path.EndsWith("texto-online.html", StringComparison.Ordinal)));
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(result.Manifest.Items, item => item.GradingItemId == firstItem && item.Files.Count == 2);
        Assert.DoesNotContain(result.Manifest.Coverage.Issues, issue => issue.Code == "no_submission_evidence");
        Assert.Equal(2, result.Members.Count(member => member.Status == GradingEvidencePackageMemberStatus.Included));

        await using var stream = new MemoryStream();
        var writer = new Moodle.Infrastructure.GradingPackageArchiveWriter();
        var archive = await writer.WriteAsync(
            stream,
            result.Entries,
            new GradingPackageArchiveLimits(80, 80 * 1024, 20 * 1024, 10 * 1024),
            CancellationToken.None);

        Assert.Equal(result.Entries.Count, archive.EntryCount);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void Build_AcceptsOnlineTextAsSubmissionEvidenceWithoutAttachment()
    {
        var itemId = Guid.NewGuid();
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            CreatePackage(),
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [new GradingPackageSourceItem(
                itemId,
                Guid.NewGuid(),
                null,
                101,
                "Synthetic student",
                9001,
                0,
                null,
                "context-a",
                "submission-a",
                [],
                "<p>Resposta sintética digitada na atividade.</p>",
                Guid.NewGuid())],
            [],
            []));

        var member = Assert.Single(result.Members);
        var manifestItem = Assert.Single(result.Manifest.Items);
        Assert.Equal(GradingEvidencePackageMemberStatus.Included, member.Status);
        Assert.True(manifestItem.SubmissionEvidenceComplete);
        Assert.Contains(manifestItem.Files, file => file.OriginalName == "texto-online.html" && !file.IsContext);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "no_submission_evidence");
    }

    [Fact]
    public async Task Build_SanitizesArchiveUnsafeCharactersInUploadedFilenames()
    {
        var itemId = Guid.NewGuid();
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            CreatePackage(),
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [new GradingPackageSourceItem(
                itemId, Guid.NewGuid(), null, 101, "Ana", 9001, 0, null, "context-a", "submission-a",
                [Source(Guid.NewGuid(), itemId, "resposta:final.pdf", "entrega")])],
            [],
            []));

        await using var stream = new MemoryStream();
        var writer = new Moodle.Infrastructure.GradingPackageArchiveWriter();
        await writer.WriteAsync(
            stream,
            result.Entries,
            new GradingPackageArchiveLimits(80, 80 * 1024, 20 * 1024, 10 * 1024),
            CancellationToken.None);

        Assert.Contains(result.Entries, entry => entry.Path.EndsWith("resposta_final.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RejectsMissingOnlineTextArtifactInsteadOfInventingIdentity()
    {
        var itemId = Guid.NewGuid();
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            CreatePackage(),
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [new GradingPackageSourceItem(
                itemId, Guid.NewGuid(), null, 101, "Ana", 9001, 0, null, "context-a", "submission-a",
                [], "texto sem artifactId")],
            [],
            []));

        Assert.Contains(result.Issues, issue => issue.Code == "online_text_without_artifact_id");
        Assert.DoesNotContain(result.Entries, entry => entry.Path.EndsWith("texto-online.html", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ExposesContextArtifactMetadataInManifest()
    {
        var itemId = Guid.NewGuid();
        var contextArtifactId = Guid.NewGuid();
        var contextHash = new string('c', 64);
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            CreatePackage(),
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [new GradingPackageSourceItem(
                itemId, Guid.NewGuid(), null, 101, "Ana", 9001, 0, null, "context-a", "submission-a",
                [Source(Guid.NewGuid(), itemId, "resposta.pdf", "entrega")])],
            [new GradingPackageSourceFile(
                contextArtifactId,
                null,
                "enunciado.pdf",
                "application/pdf",
                contextHash,
                8,
                IsContext: true,
                (destination, _) => destination.WriteAsync("enunciado"u8.ToArray()).AsTask())],
            []));

        var statement = Assert.Single(result.Manifest.Activity.StatementFiles!);
        Assert.Equal(contextArtifactId, statement.ArtifactId);
        Assert.Equal(contextHash, statement.Sha256);
        Assert.Equal(8, statement.SizeBytes);
        Assert.Equal("application/pdf", statement.MimeType);
        Assert.True(statement.IsContext);
        Assert.Equal(result.Manifest.Activity.StatementPaths, [statement.Path]);
    }

    [Fact]
    public void ManifestHash_KeepsLegacyActivityWithoutStatementFilesCompatible()
    {
        var manifest = new GradingPackageManifest(
            GradingPackageContract.ManifestSchemaVersion,
            Guid.NewGuid(),
            1,
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [],
            new GradingPackageCoverage(0, 0, 0, []));

        var json = GradingPackageManifestHash.Serialize(manifest);

        Assert.DoesNotContain("statementFiles", json, StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<GradingPackageManifest>(
            json,
            GradingPackageContract.JsonOptions);
        Assert.NotNull(restored);
        Assert.Equal(
            GradingPackageManifestHash.Compute(manifest),
            GradingPackageManifestHash.Compute(restored!));
    }

    [Fact]
    public void Build_MarcaEntregaComoPendenteQuandoAnexoEsperadoFalha()
    {
        var itemId = Guid.NewGuid();
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            CreatePackage(),
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [new GradingPackageSourceItem(
                itemId, Guid.NewGuid(), null, 101, "Ana", 9001, 0, null, "context-a", "submission-a",
                [Source(Guid.NewGuid(), itemId, "resposta.pdf", "parte disponivel")],
                null,
                null,
                ExpectedSubmissionFileCount: 2,
                HasSubmissionArtifactFailure: true)],
            [],
            []));

        var manifestItem = Assert.Single(result.Manifest.Items);
        Assert.False(manifestItem.SubmissionEvidenceComplete);
        Assert.Equal(GradingEvidencePackageMemberStatus.Failed, Assert.Single(result.Members).Status);
        Assert.Contains(result.Issues, issue => issue.Code == "submission_evidence_incomplete");
    }

    [Fact]
    public void Build_MarcaEntregaComoPendenteQuandoContextoNaoEstaCompleto()
    {
        var itemId = Guid.NewGuid();
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            CreatePackage(),
            new GradingPackageActivity(10, 501, "1001", "numeric", 10m, [], [], null),
            [new GradingPackageSourceItem(
                itemId, Guid.NewGuid(), null, 101, "Ana", 9001, 0, null, "context-a", "submission-a",
                [Source(Guid.NewGuid(), itemId, "resposta.pdf", "entrega")],
                null,
                null,
                ExpectedSubmissionFileCount: 1,
                HasContextFailure: true)],
            [],
            []));

        var manifestItem = Assert.Single(result.Manifest.Items);
        Assert.False(manifestItem.ContextAvailable);
        Assert.Equal(GradingEvidencePackageMemberStatus.Failed, Assert.Single(result.Members).Status);
        Assert.Contains(result.Issues, issue => issue.Code == "context_unavailable");
    }

    private static GradingPackageSourceFile Source(Guid artifactId, Guid itemId, string name, string content) =>
        new(artifactId, itemId, name, "application/pdf", null, Encoding.UTF8.GetByteCount(content), false,
            (destination, _) => destination.WriteAsync(Encoding.UTF8.GetBytes(content)).AsTask());

    private static GradingEvidencePackage CreatePackage() =>
        GradingEvidencePackage.Create(
            "teacher-1", "connection-a", "client-a", "default", 10, 501, "1001", 1,
            new string('a', 64), new string('b', 64), now: DateTimeOffset.UtcNow);
}
