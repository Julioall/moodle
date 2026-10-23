using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Application.Grading.Packages;
using Moodle.Domain.Grading;
using Moodle.Presentation.Tools;
using Microsoft.Extensions.Options;

namespace Moodle.Application.Tests.Grading;

public sealed class ImportGradingPackageResultsTests
{
    [Theory]
    [InlineData("same", 2)]
    [InlineData("other_run", 1)]
    [InlineData("no_run", 1)]
    [InlineData("owner", 1)]
    [InlineData("connection", 1)]
    [InlineData("course", 1)]
    [InlineData("assignment", 1)]
    [InlineData("manifest_batch", 1)]
    [InlineData("context", 1)]
    [InlineData("instructions", 1)]
    [InlineData("client", 1)]
    [InlineData("alias", 1)]
    [InlineData("cancelled", 1)]
    [InlineData("cancelled_anchor", 1)]
    public async Task Imports_siblings_only_with_matching_scope_and_routes_each_actual_batch(string mismatch, int expectedSaved)
    {
        var runId = Guid.NewGuid();
        var fixture = CreateFixture(gradingRunId: mismatch == "no_run" ? null : runId, markReady: false);
        var sibling = AssistedGradingBatch.Create(mismatch == "course" ? 20 : 10,
            [mismatch == "assignment" ? 502 : 501], mismatch == "owner" ? "other" : "teacher-1", 321, 1,
            teacherInstructions: mismatch == "instructions" ? "Other instructions" : null,
            connectorClientId: mismatch == "client" ? "other" : null,
            connectionAlias: mismatch == "alias" ? "other" : null,
            moodleConnectionId: mismatch == "connection" ? "other" : null,
            gradingRunId: mismatch == "other_run" ? Guid.NewGuid() : mismatch == "no_run" ? null : runId);
        var item = AssistedGradingItem.Create(sibling.Id, sibling.CourseId, sibling.AssignmentIds[0], 9002, 102, 1, "Bia");
        item.MarkAwaitingAiAnalysis(null);
        if (mismatch == "cancelled") sibling.Cancel();
        if (mismatch == "cancelled_anchor") fixture.Repository.Batches.Single().Cancel();
        fixture.Repository.Batches.Add(sibling);
        fixture.Repository.Items.Add(item);
        foreach (var memberItem in fixture.Repository.Items)
        {
            fixture.PackageStore.Members.Add(GradingEvidencePackageMember.Create(fixture.Package.Id,
                memberItem.Id, memberItem.MoodleUserId, memberItem.StudentDisplayName, memberItem.SubmissionId,
                memberItem.AttemptNumber, null, memberItem.ContextHash, memberItem.SubmissionContentHash));
        }
        var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(fixture.Package.ManifestJson!, GradingPackageContract.JsonOptions)!;
        var siblingManifest = manifest.Items[0] with
        {
            GradingItemId = item.Id,
            BatchJobId = mismatch == "manifest_batch" ? fixture.Item.BatchId : sibling.Id,
            ContextHash = mismatch == "context" ? new string('c', 64) : null,
            Files = []
        };
        manifest = manifest with { Items = [manifest.Items[0], siblingManifest] };
        var hash = GradingPackageManifestHash.Compute(manifest);
        fixture.Package.MarkManifest(GradingPackageManifestHash.Serialize(manifest), hash, DateTimeOffset.UtcNow);
        fixture.Package.MarkReady("packages/test/package.zip", new string('a', 64), 10, 10, 1, 2, 2, 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var first = ResultDocument(fixture.Package.Id, hash, fixture.Item.Id, fixture.ArtifactId, 8m).Results[0];
        var document = new GradingPackageResultDocument(1, fixture.Package.Id, hash,
            [first, new GradingPackageResultItem(item.Id, siblingManifest.ContextHash, null, "needs_review", null,
                "Revisar entrega", [], [])]);
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(fixture.PackageStore, fixture.Repository,
            new FakeCurrentUser("teacher-1"), mediator);
        var command = new ImportGradingPackageResultsCommand(fixture.Package.Id, hash,
            JsonSerializer.Serialize(document, GradingPackageContract.JsonOptions));

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.Equal(expectedSaved, result.Saved);
        Assert.Equal(2 - expectedSaved, result.Rejected);
        Assert.Equal(expectedSaved, mediator.SaveCalls);
        if (expectedSaved == 2)
        {
            var repeated = await handler.Handle(command, CancellationToken.None);
            Assert.Equal(2, repeated.Unchanged);
            Assert.Equal(2, mediator.SaveCalls);
        }
        else
        {
            Assert.Contains(result.Errors, error => error.Code == "item_scope_mismatch");
            Assert.Equal(GradingItemStatus.AwaitingAiAnalysis,
                mismatch == "cancelled_anchor" ? fixture.Item.Status : item.Status);
            if (mismatch == "cancelled_anchor") Assert.Equal(GradingItemStatus.DraftReady, item.Status);
        }
    }

    [Fact]
    public async Task Importa_resultado_valido_e_repeticao_identica_sem_duplicar_rascunho()
    {
        var fixture = CreateFixture();
        var mediator = new FakeMediator(fixture.Repository);
        var expectedDraftVersionHash = GradingDraftVersionHash.Compute(fixture.Item);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var document = ResultDocument(fixture.Package.Id, fixture.ManifestHash, fixture.Item.Id, fixture.ArtifactId, 8m);

        var first = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(document, GradingPackageContract.JsonOptions)),
            CancellationToken.None);
        var second = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(document, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(1, first.Saved);
        Assert.True(first.Complete);
        Assert.Equal(1, second.Unchanged);
        Assert.True(second.Complete);
        Assert.Equal(0, second.Rejected);
        Assert.Equal(GradingEvidencePackageStatus.ResultsReceived, fixture.Package.Status);
        Assert.Equal(1, mediator.SaveCalls);
        Assert.Equal(
            expectedDraftVersionHash,
            mediator.LastExpectedDraftVersionHashes![fixture.Item.Id]);
    }

    [Fact]
    public async Task Importacao_rejeita_contexto_alterado_e_preserva_irmao_valido()
    {
        var fixture = CreateFixture(markReady: false);
        var staleItem = fixture.Item;
        var sibling = AssistedGradingItem.Create(staleItem.BatchId, 10, 501, 9002, 102, 1, "Bia");
        sibling.MarkAwaitingAiAnalysis(null);
        fixture.Repository.Items.Add(sibling);

        var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(
            fixture.Package.ManifestJson!,
            GradingPackageContract.JsonOptions)!;
        var originalItem = manifest.Items.Single();
        var siblingManifest = originalItem with
        {
            GradingItemId = sibling.Id,
            StudentId = sibling.MoodleUserId,
            StudentName = sibling.StudentDisplayName,
            SubmissionId = sibling.SubmissionId,
            Files = [],
            ExpectedSubmissionFileCount = 0,
            SubmissionEvidenceComplete = true
        };
        manifest = manifest with { Items = [originalItem, siblingManifest] };
        var manifestHash = GradingPackageManifestHash.Compute(manifest);
        fixture.Package.MarkManifest(GradingPackageManifestHash.Serialize(manifest), manifestHash, DateTimeOffset.UtcNow);
        fixture.Package.MarkReady(
            "packages/stale-context/package.zip",
            new string('c', 64),
            10,
            10,
            1,
            2,
            2,
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1));

        fixture.PackageStore.Members.Add(GradingEvidencePackageMember.Create(
            fixture.Package.Id,
            staleItem.Id,
            staleItem.MoodleUserId,
            staleItem.StudentDisplayName,
            staleItem.SubmissionId,
            staleItem.AttemptNumber,
            null,
            originalItem.ContextHash,
            originalItem.SubmissionContentHash));
        fixture.PackageStore.Members.Add(GradingEvidencePackageMember.Create(
            fixture.Package.Id,
            sibling.Id,
            sibling.MoodleUserId,
            sibling.StudentDisplayName,
            sibling.SubmissionId,
            sibling.AttemptNumber,
            null,
            siblingManifest.ContextHash,
            siblingManifest.SubmissionContentHash));

        var changedContext = GradingContextSnapshot.Create(
            staleItem.Id,
            staleItem.BatchId,
            new MoodleAssignmentReference(staleItem.CourseId, staleItem.AssignmentId, null),
            new MoodleSubmissionReference(staleItem.SubmissionId!.Value),
            new MoodleUserReference(staleItem.MoodleUserId),
            staleItem.AttemptNumber,
            1,
            "Atividade sintética",
            "Contexto atualizado",
            [],
            null,
            new GradingScaleSnapshot(20m, null, null, "numeric", 0m, "assignment_settings", "scale-v2"),
            [],
            [],
            new GradingExtractionSummary("missing", 0, false, 0, 0, null),
            new GradingEvidenceCoverage(0, 0, 0, 0, 0, 0, false),
            null,
            [],
            [],
            false);
        staleItem.RecordContextSnapshot(changedContext);

        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var staleResult = ResultDocument(fixture.Package.Id, manifestHash, staleItem.Id, fixture.ArtifactId, 8m).Results[0];
        var document = new GradingPackageResultDocument(
            1,
            fixture.Package.Id,
            manifestHash,
            [
                staleResult,
                new GradingPackageResultItem(
                    sibling.Id,
                    siblingManifest.ContextHash,
                    siblingManifest.SubmissionContentHash,
                    "needs_review",
                    null,
                    "Revisão manual necessária.",
                    [],
                    [new GradingPackageIssue("manual_review", "Entrega sintética para revisão manual.")])
            ]);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                manifestHash,
                JsonSerializer.Serialize(document, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(1, result.Rejected);
        Assert.Equal(1, result.Saved);
        Assert.Contains(result.Errors, error =>
            error.GradingItemId == staleItem.Id && error.Code == "current_content_changed");
        Assert.Equal(1, mediator.SaveCalls);
        Assert.Null(staleItem.SuggestedGrade);
        Assert.Equal(GradingItemStatus.DraftReady, sibling.Status);
    }

    [Fact]
    public async Task Importa_resultado_com_manifestoLegadoSemCampoStudentName()
    {
        var fixture = CreateFixture(markReady: false);
        var legacyJson = fixture.Package.ManifestJson!
            .Replace(",\"studentName\":\"Ana\"", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("studentName", legacyJson, StringComparison.Ordinal);
        var legacyHash = GradingPackageManifestHash.ComputeSerializedJson(legacyJson);
        fixture.Package.MarkManifest(legacyJson, legacyHash, DateTimeOffset.UtcNow);
        fixture.Package.MarkReady(
            "packages/legacy/package.zip",
            new string('a', 64),
            10,
            10,
            1,
            1,
            1,
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1));
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var resultDocument = ResultDocument(fixture.Package.Id, legacyHash, fixture.Item.Id, fixture.ArtifactId, 8m);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                legacyHash,
                JsonSerializer.Serialize(resultDocument, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(1, result.Saved);
        Assert.True(result.Complete);
        Assert.Equal(1, mediator.SaveCalls);
    }

    [Fact]
    public async Task Nao_substitui_rascunho_existente_com_resultado_conflitante()
    {
        var fixture = CreateFixture();
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var first = ResultDocument(fixture.Package.Id, fixture.ManifestHash, fixture.Item.Id, fixture.ArtifactId, 8m);
        await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(first, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        var conflicting = ResultDocument(fixture.Package.Id, fixture.ManifestHash, fixture.Item.Id, fixture.ArtifactId, 9m);
        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(conflicting, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(1, result.Rejected);
        Assert.Contains(result.Errors, error => error.Code == "conflicting_existing_draft");
        Assert.Equal(1, mediator.SaveCalls);
        Assert.Equal(8m, fixture.Item.SuggestedGrade);
    }

    [Fact]
    public async Task Pacote_antigo_nao_substitui_decisao_humana_ja_revisada()
    {
        var fixture = CreateFixture();
        fixture.Item.ApplyTeacherReview(
            9.5m,
            "Feedback revisado pelo professor.",
            "teacher-1",
            321,
            "approved",
            "Revisao humana mais recente.",
            maxGrade: 10m);
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var oldPackageResult = ResultDocument(
            fixture.Package.Id,
            fixture.ManifestHash,
            fixture.Item.Id,
            fixture.ArtifactId,
            7m);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(oldPackageResult, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(0, result.Saved);
        Assert.Equal(1, result.Rejected);
        Assert.Contains(result.Errors, error => error.Code == "item_not_importable");
        Assert.Equal(0, mediator.SaveCalls);
        Assert.Equal(GradingItemStatus.ReadyToCommit, fixture.Item.Status);
        Assert.Equal(9.5m, fixture.Item.FinalGrade);
        Assert.Equal("Feedback revisado pelo professor.", fixture.Item.FinalFeedback);
        Assert.Equal("Revisao humana mais recente.", fixture.Item.ReviewNotes);
    }

    [Fact]
    public async Task Importa_resultado_quando_arquivo_foi_persistido_pelo_worker()
    {
        var fixture = CreateFixture(markFileIncluded: false);
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var document = ResultDocument(
            fixture.Package.Id,
            fixture.ManifestHash,
            fixture.Item.Id,
            fixture.ArtifactId,
            8m);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(document, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(1, result.Saved);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(1, mediator.SaveCalls);
    }

    [Fact]
    public async Task Rejeita_evidencia_de_arquivo_persistido_mas_ausente_do_manifesto()
    {
        var fixture = CreateFixture();
        var strayArtifactId = Guid.NewGuid();
        var strayFile = GradingEvidencePackageFile.Create(
            fixture.Package.Id,
            fixture.Item.Id,
            strayArtifactId,
            "entregas/101/arquivo-fora-do-manifesto.txt",
            "arquivo-fora-do-manifesto.txt",
            "text/plain",
            new string('c', 64),
            12,
            false);
        strayFile.MarkIncluded();
        fixture.PackageStore.Files.Add(strayFile);
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(
                    ResultDocument(fixture.Package.Id, fixture.ManifestHash, fixture.Item.Id, strayArtifactId, 8m),
                    GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(1, result.Rejected);
        Assert.Contains(result.Errors, error => error.Code == "evidence_not_in_package");
        Assert.Equal(0, mediator.SaveCalls);
    }

    [Fact]
    public async Task Importa_needs_review_sem_nota_e_sem_evidencia_com_issue()
    {
        var fixture = CreateFixture();
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var document = new GradingPackageResultDocument(
            1,
            fixture.Package.Id,
            fixture.ManifestHash,
            [new GradingPackageResultItem(
                fixture.Item.Id,
                null,
                null,
                "needs_review",
                null,
                null,
                [],
                [new GradingPackageIssue("unreadable_submission", "A entrega precisa de revisao manual.")])]);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(document, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(1, result.Saved);
        Assert.Equal(1, result.Pending);
        Assert.Equal(0, result.Rejected);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task Importacao_parcial_mantem_item_ausente_pendente_e_nao_finaliza_pacote()
    {
        var fixture = CreateFixture();
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var document = new GradingPackageResultDocument(
            1,
            fixture.Package.Id,
            fixture.ManifestHash,
            []);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(document, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(0, result.Saved);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(1, result.Pending);
        Assert.False(result.Complete);
        var pending = Assert.Single(result.Items!);
        Assert.Equal(fixture.Item.Id, pending.GradingItemId);
        Assert.Equal("pending", pending.Status);
        Assert.Equal(GradingEvidencePackageStatus.Ready, fixture.Package.Status);
        Assert.Equal(0, mediator.SaveCalls);
    }

    [Fact]
    public async Task Importacao_isola_resultado_com_tipo_invalido_sem_bloquear_o_documento()
    {
        var fixture = CreateFixture();
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var resultsJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            packageId = fixture.Package.Id,
            manifestHash = fixture.ManifestHash,
            results = new[]
            {
                new
                {
                    gradingItemId = fixture.Item.Id,
                    contextHash = (string?)null,
                    submissionContentHash = (string?)null,
                    status = "graded",
                    grade = "nao-e-numero",
                    feedback = "Feedback presente.",
                    evidence = Array.Empty<object>(),
                    issues = Array.Empty<object>()
                }
            }
        }, GradingPackageContract.JsonOptions);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(fixture.Package.Id, fixture.ManifestHash, resultsJson),
            CancellationToken.None);

        Assert.Equal(0, result.Saved);
        Assert.Equal(1, result.Rejected);
        Assert.Contains(result.Errors, error => error.Code == "invalid_result_item");
        Assert.Equal(0, mediator.SaveCalls);
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, fixture.Item.Status);
    }

    [Fact]
    public async Task Importacao_rejeita_evidencia_nula_sem_lancar_excecao()
    {
        var fixture = CreateFixture();
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var resultsJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            packageId = fixture.Package.Id,
            manifestHash = fixture.ManifestHash,
            results = new[]
            {
                new
                {
                    gradingItemId = fixture.Item.Id,
                    contextHash = (string?)null,
                    submissionContentHash = (string?)null,
                    status = "graded",
                    grade = 8m,
                    feedback = "Feedback presente.",
                    evidence = new object?[] { null },
                    issues = Array.Empty<object>()
                }
            }
        }, GradingPackageContract.JsonOptions);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(fixture.Package.Id, fixture.ManifestHash, resultsJson),
            CancellationToken.None);

        Assert.Equal(0, result.Saved);
        Assert.Equal(1, result.Rejected);
        Assert.Contains(result.Errors, error => error.Code == "invalid_evidence_item");
        Assert.Equal(0, mediator.SaveCalls);
    }

    [Fact]
    public async Task Importacao_rejeita_nota_quando_manifesto_indica_evidencia_incompleta()
    {
        var fixture = CreateFixture(submissionEvidenceComplete: false);
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(
                    ResultDocument(fixture.Package.Id, fixture.ManifestHash, fixture.Item.Id, fixture.ArtifactId, 8m),
                    GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(0, result.Saved);
        Assert.Equal(1, result.Rejected);
        Assert.Contains(result.Errors, error => error.Code == "evidence_incomplete");
        Assert.Equal(0, mediator.SaveCalls);
    }

    [Fact]
    public async Task Importacao_rejeita_nota_com_evidencia_somente_do_enunciado()
    {
        var fixture = CreateFixture(markReady: false);
        var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(
            fixture.Package.ManifestJson!,
            GradingPackageContract.JsonOptions)!;
        var contextArtifactId = Guid.NewGuid();
        const string contextPath = "contexto/enunciado.pdf";
        var contextSha256 = new string('b', 64);
        var contextManifestFile = new GradingPackageManifestFile(
            contextArtifactId,
            contextPath,
            "enunciado.pdf",
            "application/pdf",
            contextSha256,
            64,
            true);
        var updatedManifest = manifest with
        {
            Activity = manifest.Activity with
            {
                StatementPaths = [contextPath],
                StatementFiles = [contextManifestFile]
            }
        };
        var manifestHash = GradingPackageManifestHash.Compute(updatedManifest);
        fixture.Package.MarkManifest(
            GradingPackageManifestHash.Serialize(updatedManifest),
            manifestHash,
            DateTimeOffset.UtcNow);
        fixture.Package.MarkReady(
            "packages/test/package.zip",
            new string('a', 64),
            10,
            10,
            1,
            1,
            1,
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1));
        var contextFile = GradingEvidencePackageFile.Create(
            fixture.Package.Id,
            fixture.Item.Id,
            contextArtifactId,
            contextPath,
            "enunciado.pdf",
            "application/pdf",
            contextSha256,
            64,
            isContext: true);
        contextFile.MarkIncluded();
        fixture.PackageStore.Files.Add(contextFile);
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var resultDocument = new GradingPackageResultDocument(
            1,
            fixture.Package.Id,
            manifestHash,
            [new GradingPackageResultItem(
                fixture.Item.Id,
                null,
                null,
                "graded",
                8m,
                "Feedback sintético.",
                [new GradingPackageResultEvidence(contextArtifactId, "orientação", "página 1")],
                [])]);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                manifestHash,
                JsonSerializer.Serialize(resultDocument, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(0, result.Saved);
        Assert.Equal(1, result.Rejected);
        Assert.Contains(result.Errors, error => error.Code == "submission_evidence_required");
        Assert.Equal(0, mediator.SaveCalls);
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, fixture.Item.Status);
    }

    [Fact]
    public async Task Importacao_rejeita_nota_sem_observacao_da_entrega_lida()
    {
        var fixture = CreateFixture();
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);
        var document = new GradingPackageResultDocument(
            1,
            fixture.Package.Id,
            fixture.ManifestHash,
            [new GradingPackageResultItem(
                fixture.Item.Id,
                null,
                null,
                "graded",
                8m,
                "Feedback sintético.",
                [new GradingPackageResultEvidence(fixture.ArtifactId, "  ", "página 1")],
                [])]);

        var result = await handler.Handle(
            new ImportGradingPackageResultsCommand(
                fixture.Package.Id,
                fixture.ManifestHash,
                JsonSerializer.Serialize(document, GradingPackageContract.JsonOptions)),
            CancellationToken.None);

        Assert.Equal(0, result.Saved);
        Assert.Equal(1, result.Rejected);
        Assert.Contains(result.Errors, error => error.Code == "evidence_observation_required");
        Assert.Equal(0, mediator.SaveCalls);
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, fixture.Item.Status);
    }

    [Fact]
    public async Task Rejeita_hash_de_manifesto_antes_de_qualquer_gravacao()
    {
        var fixture = CreateFixture();
        var mediator = new FakeMediator(fixture.Repository);
        var handler = new ImportGradingPackageResultsCommandHandler(
            fixture.PackageStore,
            fixture.Repository,
            new FakeCurrentUser("teacher-1"),
            mediator);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new ImportGradingPackageResultsCommand(fixture.Package.Id, new string('0', 64), "{}"),
            CancellationToken.None));

        Assert.Equal(0, mediator.SaveCalls);
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, fixture.Item.Status);
    }

    [Fact]
    public async Task Preparacao_particiona_itens_e_retorna_sem_esperar_download()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 2);
        repository.Batches.Add(batch);
        var firstItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        var secondItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 1, "Bia");
        firstItem.MarkAwaitingAiAnalysis(null);
        secondItem.MarkAwaitingAiAnalysis(null);
        repository.Items.Add(firstItem);
        repository.Items.Add(secondItem);
        var packageStore = new FakePackageStore();
        var handler = new PrepareGradingPackagesCommandHandler(
            repository,
            new FakeCurrentUser("teacher-1"),
            packageStore,
            new GradingPackagePreparationChannel(),
            Options.Create(new GradingLimitsOptions { MaxGradingPackageItems = 1 }));

        var result = await handler.Handle(
            new PrepareGradingPackagesCommand(batch.Id),
            CancellationToken.None);

        Assert.Equal(2, result.Packages.Count);
        Assert.Equal(2, packageStore.Packages.Count);
        Assert.Equal(2, packageStore.Members.Count);
        Assert.All(result.Packages, package => Assert.Equal("Queued", package.Status));
    }

    [Fact]
    public async Task Preparacao_de_pacote_parcial_reenfileira_somente_membros_pendentes()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 2);
        repository.Batches.Add(batch);
        var included = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        var pending = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 1, "Bia");
        included.MarkAwaitingAiAnalysis(null);
        pending.MarkAwaitingAiAnalysis(null);
        repository.Items.AddRange([included, pending]);

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            included.AssignmentId,
            null,
            [
                new GradingPackageItemIdentity(included.Id, included.ContextHash, included.SubmissionContentHash),
                new GradingPackageItemIdentity(pending.Id, pending.ContextHash, pending.SubmissionContentHash)
            ]);
        var previous = GradingEvidencePackage.Create(
            identity.OwnerSubject,
            identity.MoodleConnectionId,
            identity.ConnectorClientId,
            identity.ConnectionAlias,
            identity.CourseId,
            identity.AssignmentId,
            identity.Cmid,
            1,
            identity.PreparationKey,
            identity.ActivityKey,
            batch.Id);
        var now = DateTimeOffset.UtcNow;
        previous.MarkManifest("{}", new string('a', 64), now);
        previous.MarkPartiallyReady(
            "packages/previous.zip",
            new string('b', 64),
            100,
            100,
            5,
            2,
            1,
            1,
            1,
            now,
            now.AddHours(1));

        var packageStore = new FakePackageStore();
        packageStore.Packages.Add(previous);
        packageStore.Members.Add(GradingEvidencePackageMember.Create(
            previous.Id,
            included.Id,
            included.MoodleUserId,
            included.StudentDisplayName,
            included.SubmissionId,
            included.AttemptNumber,
            null,
            included.ContextHash,
            included.SubmissionContentHash));
        packageStore.Members[^1].MarkIncluded();
        packageStore.Members.Add(GradingEvidencePackageMember.Create(
            previous.Id,
            pending.Id,
            pending.MoodleUserId,
            pending.StudentDisplayName,
            pending.SubmissionId,
            pending.AttemptNumber,
            null,
            pending.ContextHash,
            pending.SubmissionContentHash));
        packageStore.Members[^1].MarkFailed("artifact_read_transient", "falha temporaria");

        var handler = new PrepareGradingPackagesCommandHandler(
            repository,
            new FakeCurrentUser("teacher-1"),
            packageStore,
            new GradingPackagePreparationChannel(),
            Options.Create(new GradingLimitsOptions { MaxGradingPackageItems = 10 }));

        var result = await handler.Handle(
            new PrepareGradingPackagesCommand(batch.Id),
            CancellationToken.None);

        Assert.Contains(previous.Id, result.ReusedPackageIds);
        Assert.DoesNotContain(included.Id, result.PendingItems);
        Assert.Contains(pending.Id, result.PendingItems);
        Assert.Contains(result.Packages, descriptor => descriptor.PackageId == previous.Id);
        var retryPackage = Assert.Single(packageStore.Packages, package => package.Id != previous.Id);
        Assert.Contains(result.Packages, descriptor => descriptor.PackageId == retryPackage.Id);
        Assert.Contains(packageStore.Members, member => member.PackageId == retryPackage.Id && member.GradingItemId == pending.Id);
        Assert.DoesNotContain(packageStore.Members, member => member.PackageId == retryPackage.Id && member.GradingItemId == included.Id);
    }

    [Fact]
    public async Task Preparacao_nao_reprocessa_item_que_ja_possui_rascunho()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 2);
        repository.Batches.Add(batch);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.SetDraft(8m, 0.9m, "Rascunho existente.");
        repository.Items.Add(item);
        var packageStore = new FakePackageStore();
        var handler = new PrepareGradingPackagesCommandHandler(
            repository,
            new FakeCurrentUser("teacher-1"),
            packageStore,
            new GradingPackagePreparationChannel(),
            Options.Create(new GradingLimitsOptions
            {
                MaxGradingPackageItems = 10,
                MaxGradingPackageEntries = 4
            }));

        var result = await handler.Handle(
            new PrepareGradingPackagesCommand(batch.Id),
            CancellationToken.None);

        Assert.Empty(result.Packages);
        Assert.DoesNotContain(item.Id, result.PendingItems);
        Assert.Empty(packageStore.Packages);
        Assert.Contains("Nenhum item validado", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task Preparacao_com_regeneracao_reconstroi_transporte_valido_sem_reabrir_item_com_rascunho()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1);
        repository.Batches.Add(batch);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.SetDraft(8m, 0.9m, "Rascunho preservado.");
        repository.Items.Add(item);

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            item.AssignmentId,
            null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);
        var previous = GradingEvidencePackage.Create(
            identity.OwnerSubject,
            identity.MoodleConnectionId,
            identity.ConnectorClientId,
            identity.ConnectionAlias,
            identity.CourseId,
            identity.AssignmentId,
            identity.Cmid,
            1,
            identity.PreparationKey,
            identity.ActivityKey,
            batch.Id);
        var now = DateTimeOffset.UtcNow;
        previous.MarkManifest("{}", new string('a', 64), now);
        previous.MarkReady(
            "packages/previous.zip",
            new string('b', 64),
            100,
            100,
            2,
            1,
            1,
            0,
            now,
            now.AddHours(1));
        var packageStore = new FakePackageStore();
        packageStore.Packages.Add(previous);
        var handler = new PrepareGradingPackagesCommandHandler(
            repository,
            new FakeCurrentUser("teacher-1"),
            packageStore,
            new GradingPackagePreparationChannel(),
            Options.Create(new GradingLimitsOptions { MaxGradingPackageItems = 10 }));

        var result = await handler.Handle(
            new PrepareGradingPackagesCommand(batch.Id, RegenerateTransport: true),
            CancellationToken.None);

        var replacement = Assert.Single(packageStore.Packages, package => package.Id != previous.Id);
        Assert.Contains(replacement.Id, result.Packages.Select(package => package.PackageId));
        Assert.Contains(packageStore.Members, member =>
            member.PackageId == replacement.Id && member.GradingItemId == item.Id);
        Assert.Equal(GradingEvidencePackageStatus.Ready, previous.Status);
        Assert.Equal("packages/previous.zip", previous.BlobKey);
        Assert.Equal(GradingItemStatus.DraftReady, item.Status);
        Assert.Equal(8m, item.SuggestedGrade);
    }

    [Fact]
    public async Task Status_expoe_manifesto_instrucoes_e_referencia_sem_vazar_para_outro_usuario()
    {
        var fixture = CreateFixture();
        var handler = new GetGradingPackageStatusQueryHandler(
            fixture.PackageStore,
            new FakeCurrentUser("teacher-1"));

        var status = await handler.Handle(
            new GetGradingPackageStatusQuery(fixture.Package.Id),
            CancellationToken.None);

        Assert.Equal(fixture.Package.Id, status.Descriptor.PackageId);
        Assert.NotNull(status.Manifest);
        Assert.Contains("Pacote de correção", status.Instructions);
        Assert.Equal($"moodle://grading-package/{fixture.Package.Id:D}", status.Descriptor.DownloadResourceUri);

        var unauthorized = new GetGradingPackageStatusQueryHandler(
            fixture.PackageStore,
            new FakeCurrentUser("other-user"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unauthorized.Handle(
            new GetGradingPackageStatusQuery(fixture.Package.Id),
            CancellationToken.None));
    }

    [Fact]
    public async Task Resource_do_pacote_exige_proprietario_e_devolve_zip_privado()
    {
        var fixture = CreateFixture();
        var resource = new MoodleGradingPackageResources(
            fixture.PackageStore,
            new FakeBlobStore(),
            new FakeCurrentUser("teacher-1"));

        var contents = await resource.ReadPackageAsync(fixture.Package.Id.ToString(), CancellationToken.None);
        var blob = Assert.IsType<ModelContextProtocol.Protocol.BlobResourceContents>(contents.Single());
        Assert.Equal("application/zip", blob.MimeType);

        var unauthorized = new MoodleGradingPackageResources(
            fixture.PackageStore,
            new FakeBlobStore(),
            new FakeCurrentUser("other-user"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unauthorized.ReadPackageAsync(
            fixture.Package.Id.ToString(),
            CancellationToken.None));
    }

    [Fact]
    public async Task Resource_do_pacote_rejeita_zip_com_hash_incorreto()
    {
        var fixture = CreateFixture();
        var resource = new MoodleGradingPackageResources(
            fixture.PackageStore,
            new FakeBlobStore(Encoding.UTF8.GetBytes("zip adulterado")),
            new FakeCurrentUser("teacher-1"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resource.ReadPackageAsync(
            fixture.Package.Id.ToString(),
            CancellationToken.None));

        Assert.Contains("integridade", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resource_do_pacote_rejeita_blob_acima_do_limite_de_leitura()
    {
        var fixture = CreateFixture();
        var blobStore = new FakeBlobStore();
        var resource = new MoodleGradingPackageResources(
            fixture.PackageStore,
            blobStore,
            new FakeCurrentUser("teacher-1"),
            Options.Create(new GradingLimitsOptions { MaxResourceBytes = 4 }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resource.ReadPackageAsync(
            fixture.Package.Id.ToString(),
            CancellationToken.None));

        Assert.Contains("limite", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, blobStore.OpenReadCalls);
    }

    [Fact]
    public async Task Resource_expirado_indica_regeneracao_sem_apagar_manifesto()
    {
        var fixture = CreateFixture();
        fixture.Package.MarkBlobExpired(DateTimeOffset.UtcNow);
        var resource = new MoodleGradingPackageResources(
            fixture.PackageStore,
            new FakeBlobStore(),
            new FakeCurrentUser("teacher-1"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resource.ReadPackageAsync(
            fixture.Package.Id.ToString(),
            CancellationToken.None));

        Assert.Contains("regenerateTransport=true", exception.Message, StringComparison.Ordinal);
        Assert.Contains("preservados", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(fixture.Package.ManifestJson);
    }

    [Fact]
    public async Task Status_nao_anuncia_uri_quando_tamanho_declarado_excede_limite_do_resource()
    {
        var fixture = CreateFixture();
        var handler = new GetGradingPackageStatusQueryHandler(
            fixture.PackageStore,
            new FakeCurrentUser("teacher-1"),
            new FakeBlobStore(),
            Options.Create(new GradingLimitsOptions { MaxResourceBytes = 4 }));

        var status = await handler.Handle(
            new GetGradingPackageStatusQuery(fixture.Package.Id),
            CancellationToken.None);

        Assert.Null(status.Descriptor.DownloadResourceUri);
        Assert.Contains(status.Warnings, warning => warning.Contains("limite", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("individual_mcp_resources", status.FallbackTransport);
    }

    [Fact]
    public async Task Status_oculta_uri_quando_blob_pronto_foi_removido()
    {
        var fixture = CreateFixture();
        var handler = new GetGradingPackageStatusQueryHandler(
            fixture.PackageStore,
            new FakeCurrentUser("teacher-1"),
            new MissingStatusBlobStore());

        var status = await handler.Handle(
            new GetGradingPackageStatusQuery(fixture.Package.Id),
            CancellationToken.None);

        Assert.Null(status.Descriptor.DownloadResourceUri);
        Assert.Contains(status.Warnings, warning => warning.Contains("armazenamento", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Status_indica_regeneracao_explicita_para_zip_expirado_sem_perder_rascunho()
    {
        var fixture = CreateFixture();
        fixture.Package.MarkBlobExpired(DateTimeOffset.UtcNow);
        var handler = new GetGradingPackageStatusQueryHandler(
            fixture.PackageStore,
            new FakeCurrentUser("teacher-1"));

        var status = await handler.Handle(
            new GetGradingPackageStatusQuery(fixture.Package.Id),
            CancellationToken.None);

        Assert.Null(status.Descriptor.DownloadResourceUri);
        Assert.Contains(status.Warnings, warning => warning.Contains("regenerateTransport=true", StringComparison.Ordinal));
        Assert.Contains(status.Warnings, warning => warning.Contains("preservados", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Status_preserva_fallback_quando_storage_recusa_leitura_do_blob()
    {
        var fixture = CreateFixture();
        var handler = new GetGradingPackageStatusQueryHandler(
            fixture.PackageStore,
            new FakeCurrentUser("teacher-1"),
            new UnauthorizedStatusBlobStore());

        var status = await handler.Handle(
            new GetGradingPackageStatusQuery(fixture.Package.Id),
            CancellationToken.None);

        Assert.Null(status.Descriptor.DownloadResourceUri);
        Assert.Equal("individual_mcp_resources", status.FallbackTransport);
        Assert.Contains(status.Warnings, warning => warning.Contains("permiss", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Status_nao_expoe_detalhe_do_storage_em_falha_de_io()
    {
        var fixture = CreateFixture();
        var handler = new GetGradingPackageStatusQueryHandler(
            fixture.PackageStore,
            new FakeCurrentUser("teacher-1"),
            new FailingStatusBlobStore());

        var status = await handler.Handle(
            new GetGradingPackageStatusQuery(fixture.Package.Id),
            CancellationToken.None);

        Assert.Null(status.Descriptor.DownloadResourceUri);
        var warning = Assert.Single(status.Warnings);
        Assert.Contains("temporariamente", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-grading-packages", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage-path-secret", warning, StringComparison.OrdinalIgnoreCase);
    }

    private static Fixture CreateFixture(bool markFileIncluded = true, bool submissionEvidenceComplete = true, Guid? gradingRunId = null, bool markReady = true)
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1, gradingRunId: gradingRunId);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.MarkAwaitingAiAnalysis(null);
        var artifactId = Guid.NewGuid();
        repository.Batches.Add(batch);
        repository.Items.Add(item);
        repository.Artifacts.Add(new GradingArtifact(
            artifactId,
            item.Id,
            "submission_file",
            "answer.txt",
            "text/plain",
            new string('a', 64),
            10,
            "succeeded",
            "answer",
            null,
            DateTimeOffset.UtcNow));

        var packageStore = new FakePackageStore();
        var identity = GradingPackageIdentity.Create(
            "teacher-1",
            null,
            null,
            null,
            10,
            501,
            null,
            [new GradingPackageItemIdentity(item.Id, null, null)]);
        var package = GradingEvidencePackage.Create(
            identity.OwnerSubject,
            identity.MoodleConnectionId,
            identity.ConnectorClientId,
            identity.ConnectionAlias,
            identity.CourseId,
            identity.AssignmentId,
            identity.Cmid,
            1,
            identity.PreparationKey,
            identity.ActivityKey,
            batch.Id);
        var manifest = new GradingPackageManifest(
            1,
            package.Id,
            1,
            new GradingPackageActivity(10, 501, null, "points", 10m, [], ["qualidade"], null),
            [new GradingPackageManifestItem(
                item.Id,
                batch.Id,
                null,
                item.MoodleUserId,
                item.StudentDisplayName,
                item.SubmissionId,
                item.AttemptNumber,
                null,
                null,
                null,
                [new GradingPackageManifestFile(artifactId, "entregas/101/answer.txt", "answer.txt", "text/plain", new string('a', 64), 10, false)],
                ExpectedSubmissionFileCount: 1,
                SubmissionEvidenceComplete: submissionEvidenceComplete)],
            new GradingPackageCoverage(1, 1, 0, []));
        var manifestHash = GradingPackageManifestHash.Compute(manifest);
        package.MarkManifest(GradingPackageManifestHash.Serialize(manifest), manifestHash, DateTimeOffset.UtcNow);
        var zipBytes = Encoding.UTF8.GetBytes("zip de teste");
        var zipHash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
        if (markReady) package.MarkReady("packages/test/package.zip", zipHash, zipBytes.LongLength, zipBytes.LongLength, 1, 1, 1, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var file = GradingEvidencePackageFile.Create(
            package.Id,
            item.Id,
            artifactId,
            "entregas/101/answer.txt",
            "answer.txt",
            "text/plain",
            new string('a', 64),
            10,
            false);
        if (markFileIncluded)
        {
            file.MarkIncluded();
        }
        packageStore.Packages.Add(package);
        packageStore.Files.Add(file);
        return new Fixture(repository, packageStore, package, manifestHash, item, artifactId);
    }

    private static GradingPackageResultDocument ResultDocument(Guid packageId, string manifestHash, Guid itemId, Guid artifactId, decimal grade) =>
        new(
            1,
            packageId,
            manifestHash,
            [new GradingPackageResultItem(
                itemId,
                null,
                null,
                "graded",
                grade,
                "Feedback individual baseado na entrega.",
                [new GradingPackageResultEvidence(artifactId, "observacao", "pagina 1")],
                [])]);

    private sealed record Fixture(
        FakeReviewRepository Repository,
        FakePackageStore PackageStore,
        GradingEvidencePackage Package,
        string ManifestHash,
        AssistedGradingItem Item,
        Guid ArtifactId);

    private sealed class FakeCurrentUser(string subject) : ICurrentUserContext
    {
        public string Subject { get; } = subject;
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes => [];
        public bool HasScope(string scope) => false;
    }

    private sealed class FakeMediator(FakeReviewRepository repository) : IMediator
    {
        public int SaveCalls { get; private set; }
        public IReadOnlyDictionary<Guid, string>? LastExpectedDraftVersionHashes { get; private set; }

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not SaveAiGradingBatchCommand save)
            {
                throw new NotSupportedException(request.GetType().Name);
            }

            SaveCalls++;
            LastExpectedDraftVersionHashes = save.ExpectedDraftVersionHashes;
            var updated = new List<SaveAiGradingBatchItemResult>();
            foreach (var input in save.Items)
            {
                var item = repository.Items.Single(item => item.Id == input.GradingItemId);
                Assert.Equal(item.BatchId, save.BatchJobId);
                Assert.Equal(save.Items.Count, save.ExpectedDraftVersionHashes!.Count);
                item.SetDraft(input.Nota, 0m, input.Feedback, maxGrade: 10m);
                updated.Add(new SaveAiGradingBatchItemResult(
                    item.Id,
                    item.Status.ToString(),
                    GradingDraftVersionHash.Compute(item),
                    item.SuggestedGrade,
                    item.DraftFeedback));
            }

            var result = new SaveAiGradingBatchResult(
                save.BatchJobId,
                updated.Count,
                0,
                0,
                save.Items.Count,
                [],
                "ok",
                updated);
            return Task.FromResult((TResponse)(object)result);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePackageStore : IGradingPackageStore
    {
        public List<GradingEvidencePackage> Packages { get; } = [];
        public List<GradingEvidencePackageMember> Members { get; } = [];
        public List<GradingEvidencePackageFile> Files { get; } = [];

        public Task<GradingEvidencePackage?> GetAsync(Guid packageId, CancellationToken cancellationToken) => Task.FromResult(Packages.SingleOrDefault(package => package.Id == packageId));
        public Task<GradingEvidencePackage?> FindReusableAsync(GradingPackageIdentity identity, CancellationToken cancellationToken)
        {
            var package = Packages.SingleOrDefault(item =>
                item.OwnerSubject == identity.OwnerSubject &&
                item.PreparationKey == identity.PreparationKey);
            if (package is not null &&
                package.Status is (GradingEvidencePackageStatus.Ready or
                    GradingEvidencePackageStatus.PartiallyReady or
                    GradingEvidencePackageStatus.ResultsReceived) &&
                (string.IsNullOrWhiteSpace(package.BlobKey) ||
                 package.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow))
            {
                return Task.FromResult<GradingEvidencePackage?>(null);
            }

            return Task.FromResult(package);
        }
        public async Task<GradingEvidencePackage> GetOrCreateAsync(GradingPackageIdentity identity, Guid? batchJobId, CancellationToken cancellationToken)
        {
            var existing = await FindReusableAsync(identity, cancellationToken);
            if (existing is not null) return existing;
            var version = Packages
                .Where(package => package.OwnerSubject == identity.OwnerSubject && package.ActivityKey == identity.ActivityKey)
                .Select(package => package.PackageVersion)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var package = GradingEvidencePackage.Create(identity.OwnerSubject, identity.MoodleConnectionId, identity.ConnectorClientId, identity.ConnectionAlias, identity.CourseId, identity.AssignmentId, identity.Cmid, version, identity.PreparationKey, identity.ActivityKey, batchJobId);
            Packages.Add(package);
            return package;
        }
        public Task<GradingEvidencePackage> CreateNextVersionAsync(GradingPackageIdentity identity, Guid? batchJobId, CancellationToken cancellationToken)
        {
            var version = Packages
                .Where(package => package.OwnerSubject == identity.OwnerSubject && package.ActivityKey == identity.ActivityKey)
                .Select(package => package.PackageVersion)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var package = GradingEvidencePackage.Create(identity.OwnerSubject, identity.MoodleConnectionId, identity.ConnectorClientId, identity.ConnectionAlias, identity.CourseId, identity.AssignmentId, identity.Cmid, version, identity.PreparationKey, identity.ActivityKey, batchJobId);
            Packages.Add(package);
            return Task.FromResult(package);
        }
        public Task AddMemberAsync(GradingEvidencePackageMember member, CancellationToken cancellationToken) { Members.Add(member); return Task.CompletedTask; }
        public Task<IReadOnlyList<GradingEvidencePackageMember>> ListMembersAsync(Guid packageId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingEvidencePackageMember>>(Members.Where(member => member.PackageId == packageId).ToArray());
        public Task AddFileAsync(GradingEvidencePackageFile file, CancellationToken cancellationToken) { Files.Add(file); return Task.CompletedTask; }
        public Task<IReadOnlyList<GradingEvidencePackageFile>> ListFilesAsync(Guid packageId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingEvidencePackageFile>>(Files.Where(file => file.PackageId == packageId).ToArray());
        public Task<bool> TryClaimAsync(Guid packageId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> RenewClaimAsync(Guid packageId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> ReleaseClaimAsync(Guid packageId, string workerId, DateTimeOffset now, string? errorCode, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<int> RecoverExpiredClaimsAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<IReadOnlyList<GradingEvidencePackage>> ListExpiredBlobsAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingEvidencePackage>>([]);
        public Task<IReadOnlyList<GradingEvidencePackage>> ListDueAsync(DateTimeOffset now, int maxPackages, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingEvidencePackage>>([]);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeBlobStore(byte[]? bytes = null) : IGradingPackageBlobStore
    {
        private readonly byte[] _bytes = bytes ?? Encoding.UTF8.GetBytes("zip de teste");
        public int OpenReadCalls { get; private set; }

        public Task<GradingPackageBlobDescriptor> WriteAsync(string blobKey, Func<Stream, CancellationToken, Task> writer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken)
        {
            OpenReadCalls++;
            return Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MissingStatusBlobStore : IGradingPackageBlobStore
    {
        public Task<GradingPackageBlobDescriptor> WriteAsync(string blobKey, Func<Stream, CancellationToken, Task> writer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) =>
            throw new FileNotFoundException("blob ausente", blobKey);

        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnauthorizedStatusBlobStore : IGradingPackageBlobStore
    {
        public Task<GradingPackageBlobDescriptor> WriteAsync(string blobKey, Func<Stream, CancellationToken, Task> writer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException("permissao insuficiente para ler o blob");

        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingStatusBlobStore : IGradingPackageBlobStore
    {
        public Task<GradingPackageBlobDescriptor> WriteAsync(string blobKey, Func<Stream, CancellationToken, Task> writer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) =>
            throw new IOException("storage-path-secret: C:\\private-grading-packages\\package.zip");

        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeReviewRepository : IGradingReviewRepository
    {
        public List<AssistedGradingBatch> Batches { get; } = [];
        public List<AssistedGradingItem> Items { get; } = [];
        public List<GradingArtifact> Artifacts { get; } = [];

        public Task AddBatchAsync(AssistedGradingBatch batch, CancellationToken cancellationToken) { Batches.Add(batch); return Task.CompletedTask; }
        public Task<AssistedGradingBatch?> GetBatchAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Batches.SingleOrDefault(batch => batch.Id == id));
        public Task AddItemAsync(AssistedGradingItem item, CancellationToken cancellationToken) { Items.Add(item); return Task.CompletedTask; }
        public Task AddArtifactAsync(GradingArtifact artifact, CancellationToken cancellationToken) { Artifacts.Add(artifact); return Task.CompletedTask; }
        public Task AddEvidenceAsync(GradingEvidence evidence, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AssistedGradingItem?> GetItemAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.Id == id));
        public Task<IReadOnlyList<AssistedGradingItem>> ListItemsByBatchAsync(Guid batchId, int page, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingItem>>(Items.Where(item => item.BatchId == batchId).Skip((page - 1) * pageSize).Take(pageSize).ToArray());
        public Task<int> CountItemsByBatchAsync(Guid batchId, CancellationToken cancellationToken) => Task.FromResult(Items.Count(item => item.BatchId == batchId));
        public Task<IReadOnlyList<GradingArtifact>> ListArtifactsByItemAsync(Guid gradingItemId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingArtifact>>(Artifacts.Where(artifact => artifact.GradingItemId == gradingItemId).ToArray());
        public Task<IReadOnlyList<GradingEvidence>> ListEvidenceByItemAsync(Guid gradingItemId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingEvidence>>([]);
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(GradingBatchStatus status, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>([]);
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>([]);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
