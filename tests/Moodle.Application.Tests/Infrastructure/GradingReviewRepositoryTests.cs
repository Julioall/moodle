using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class GradingReviewRepositoryTests
{
    [Fact]
    public async Task ReconciliationOfGradingPublication_RequiresAnItemIdAndDoesNotResolveTheWholeAction()
    {
        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            PayloadJson = "{}",
            PreviewJson = "{}",
            ConfirmationText = "CONFIRMAR",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        action.Authorize("teacher-1", DateTimeOffset.UtcNow);
        action.MarkExecutionUnknown("Resultado remoto incerto.");
        var pendingRepository = new SingleActionPendingMoodleActionRepository(action);
        var currentUser = new ReconciliationCurrentUser("teacher-1", "moodle.write.assignments.grade");
        var sut = new MoodleWriteReconciliationService(pendingRepository, currentUser);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.ReconcileAsync(action.Id, "auto", CancellationToken.None));

        Assert.Equal(PendingActionStatus.ExecutionUnknown, action.Status);
        Assert.Empty(pendingRepository.Audits);
    }

    [Fact]
    public async Task GetBatchesAsync_ReturnsExistingBatchesByIdAndDeduplicatesInput()
    {
        await using var dbContext = CreateDbContext();
        var repository = new GradingReviewRepository(dbContext);
        var batches = Enumerable.Range(0, 20)
            .Select(index => AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1))
            .ToArray();
        dbContext.GradingBatches.AddRange(batches);
        await dbContext.SaveChangesAsync();
        var requestedIds = batches.Select(batch => batch.Id)
            .Append(batches[0].Id)
            .Append(Guid.NewGuid())
            .ToArray();

        var result = await repository.GetBatchesAsync(requestedIds, CancellationToken.None);

        Assert.Equal(20, result.Count);
        Assert.All(batches, batch => Assert.Same(batch, result[batch.Id]));
    }

    [Fact]
    public async Task GradingRun_AgrupaSublotesEPermanecePersistido()
    {
        await using var dbContext = CreateDbContext();
        IGradingReviewRepository repository = new GradingReviewRepository(dbContext);
        var run = GradingRun.Create("teacher-1", 321, "connection-1", "client-1", "alias-1", "10");
        var first = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1, gradingRunId: run.Id);
        var second = AssistedGradingBatch.Create(10, [502], "teacher-1", 321, 1, gradingRunId: run.Id);

        await repository.AddGradingRunAsync(run, CancellationToken.None);
        await repository.AddBatchAsync(first, CancellationToken.None);
        await repository.AddBatchAsync(second, CancellationToken.None);
        await repository.SaveChangesAsync(CancellationToken.None);

        var loaded = await repository.GetGradingRunAsync(run.Id, CancellationToken.None);
        var children = await repository.ListBatchesByGradingRunAsync(run.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("connection-1", loaded!.MoodleConnectionId);
        Assert.Equal([first.Id, second.Id], children.Select(batch => batch.Id));
    }

    [Fact]
    public async Task GradingRun_RecuperaSubloteOrfaoPelaLinhagemIdempotenteESinalizaCoberturaEsperada()
    {
        await using var dbContext = CreateDbContext();
        IGradingReviewRepository repository = new GradingReviewRepository(dbContext);
        var run = GradingRun.Create("teacher-1");
        run.SetExpectedCoverage(expectedItemCount: 2, expectedBatchCount: 1);
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 2,
            // Simula uma linha antiga/interrompida que persistiu a chave de
            // idempotencia, mas perdeu a FK de agregacao.
            idempotencyKey: $"pending-run:{run.Id:N}:course:10:assignment:501:chunk:0");
        var first = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var second = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 0);

        await repository.AddGradingRunAsync(run, CancellationToken.None);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(first, CancellationToken.None);
        await repository.AddItemAsync(second, CancellationToken.None);
        await repository.SaveChangesAsync(CancellationToken.None);

        var children = await repository.ListBatchesByGradingRunAsync(run.Id, CancellationToken.None);
        var items = await repository.ListItemsByGradingRunAsync(
            run.Id,
            page: 1,
            pageSize: 10,
            status: null,
            cancellationToken: CancellationToken.None);
        var count = await repository.CountItemsByGradingRunAsync(
            run.Id,
            status: null,
            cancellationToken: CancellationToken.None);
        var loadedRun = await repository.GetGradingRunAsync(run.Id, CancellationToken.None);

        Assert.Equal([batch.Id], children.Select(child => child.Id));
        Assert.Equal([first.Id, second.Id], items.Select(item => item.Id));
        Assert.Equal(2, count);
        Assert.Equal(2, loadedRun!.ExpectedItemCount);
        Assert.Equal(1, loadedRun.ExpectedBatchCount);
    }

    [Fact]
    public async Task SubmissionMatch_RecuperaHandlesDaCorrecaoEFiltraPeloProprietario()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var run = GradingRun.Create("teacher-1", 321, "connection-1", "client-1", "alias-1", "10");
        var ownedBatch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1, gradingRunId: run.Id,
            moodleConnectionId: "connection-1", connectorClientId: "client-1", connectionAlias: "alias-1");
        var ownedItem = AssistedGradingItem.Create(ownedBatch.Id, 10, 501, 9001, 101, 0);
        var otherBatch = AssistedGradingBatch.Create(10, [501], "teacher-2", 322, 1,
            moodleConnectionId: "connection-1", connectorClientId: "client-1", connectionAlias: "alias-1");
        var otherItem = AssistedGradingItem.Create(otherBatch.Id, 10, 501, 9001, 101, 0);

        dbContext.GradingRuns.Add(run);
        dbContext.GradingBatches.AddRange(ownedBatch, otherBatch);
        dbContext.GradingItems.AddRange(ownedItem, otherItem);
        await dbContext.SaveChangesAsync();

        var matches = await store.FindSubmissionMatchesAsync(
            9001,
            10,
            501,
            "connection-1",
            "client-1",
            "alias-1",
            "teacher-1",
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(ownedItem.Id, match.GradingItemId);
        Assert.Equal(ownedBatch.Id, match.BatchJobId);
        Assert.Equal(run.Id, match.GradingRunId);
        Assert.Equal("teacher-1", match.CreatedBySubject);
        Assert.Equal(GradingRunStatus.Preparing, match.RunStatus);
    }

    [Fact]
    public async Task SubmissionMatch_MantemBarreiraParaPublicacaoEmLoteCancelado()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var publishedBatch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            1,
            moodleConnectionId: "connection-1");
        var publishedItem = AssistedGradingItem.Create(publishedBatch.Id, 10, 501, 9001, 101, 0);
        publishedItem.ApplyTeacherReview(9m, "Publicado.", "teacher-1", 321);
        publishedItem.MarkCommitSucceeded();
        publishedBatch.Cancel();

        var unknownBatch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            1,
            moodleConnectionId: "connection-1");
        var unknownItem = AssistedGradingItem.Create(unknownBatch.Id, 10, 501, 9002, 102, 0);
        unknownItem.ApplyTeacherReview(8m, "A reconciliar.", "teacher-1", 321);
        unknownItem.MarkCommitExecutionUnknown("timeout");
        unknownBatch.Cancel();

        dbContext.GradingBatches.AddRange(publishedBatch, unknownBatch);
        dbContext.GradingItems.AddRange(publishedItem, unknownItem);
        await dbContext.SaveChangesAsync();

        var matches = await store.FindSubmissionMatchesAsync(
            9001,
            10,
            501,
            "connection-1",
            null,
            null,
            "teacher-1",
            CancellationToken.None);
        var unknownMatches = await store.FindSubmissionMatchesAsync(
            9002,
            10,
            501,
            "connection-1",
            null,
            null,
            "teacher-1",
            CancellationToken.None);

        Assert.Equal(publishedItem.Id, Assert.Single(matches).GradingItemId);
        Assert.Equal(unknownItem.Id, Assert.Single(unknownMatches).GradingItemId);
    }

    [Fact]
    public async Task PublicationClaims_MantemAlvosNaoConflitantesQuandoParteDoLoteEstaOcupada()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var connectionKey = "connection-claims";
        var occupied = new GradingPublicationClaimEntity
        {
            PublicationId = Guid.NewGuid(),
            GradingItemId = Guid.NewGuid(),
            ConnectionKey = connectionKey,
            AssignmentId = 501,
            MoodleUserId = 101,
            AttemptNumber = 1,
            Status = "Authorized",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        dbContext.GradingPublicationClaims.Add(occupied);
        await dbContext.SaveChangesAsync();

        var freeItemId = Guid.NewGuid();
        var results = await store.TryClaimPublicationTargetsAsync(
            Guid.NewGuid(),
            connectionKey,
            [
                new GradingPublicationClaimRequest(Guid.NewGuid(), 501, 101, 1),
                new GradingPublicationClaimRequest(freeItemId, 501, 202, 1)
            ],
            DateTimeOffset.UtcNow.AddMinutes(15),
            CancellationToken.None);

        Assert.False(results.Single(result => result.GradingItemId != freeItemId).Claimed);
        Assert.True(results.Single(result => result.GradingItemId == freeItemId).Claimed);
        Assert.Contains(dbContext.GradingPublicationClaims,
            claim => claim.ConnectionKey == connectionKey && claim.MoodleUserId == 202 && claim.Status == "AwaitingConfirmation");
    }

    [Fact]
    public async Task PublicationClaims_VinculoAoPendingActionProtegePreviewAutorizadaContraExpiracao()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var action = new Moodle.Domain.PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            PayloadJson = "{}",
            PreviewJson = "{}",
            ConfirmationText = "CONFIRMAR",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        var publicationId = Guid.NewGuid();
        dbContext.PendingMoodleActions.Add(action);
        dbContext.GradingPublicationClaims.Add(new GradingPublicationClaimEntity
        {
            PublicationId = publicationId,
            GradingItemId = Guid.NewGuid(),
            ConnectionKey = "connection-binding",
            AssignmentId = 501,
            MoodleUserId = 101,
            AttemptNumber = 1,
            Status = "AwaitingConfirmation",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await dbContext.SaveChangesAsync();

        await store.BindPublicationClaimsAsync(publicationId, action.Id, CancellationToken.None);
        action.Authorize("teacher-1", DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync();

        var result = await store.TryClaimPublicationTargetsAsync(
            Guid.NewGuid(),
            "connection-binding",
            [new GradingPublicationClaimRequest(Guid.NewGuid(), 501, 101, 1)],
            DateTimeOffset.UtcNow.AddMinutes(15),
            CancellationToken.None);

        Assert.False(Assert.Single(result).Claimed);
        Assert.Equal("publication_target_busy", result[0].ConflictCode);
        Assert.Equal("AwaitingConfirmation", Assert.Single(dbContext.GradingPublicationClaims).Status);
    }

    [Fact]
    public async Task PublicationClaims_MesmaPublicacaoPodeReivindicarNovamenteAposFalhaParcial()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var publicationId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        dbContext.GradingPublicationClaims.Add(new GradingPublicationClaimEntity
        {
            PublicationId = publicationId,
            GradingItemId = itemId,
            ConnectionKey = "connection-retry",
            AssignmentId = 501,
            MoodleUserId = 101,
            AttemptNumber = 1,
            Status = "Authorized",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        await dbContext.SaveChangesAsync();

        var result = await store.TryClaimPublicationTargetsAsync(
            publicationId,
            "connection-retry",
            [new GradingPublicationClaimRequest(itemId, 501, 101, 1)],
            DateTimeOffset.UtcNow.AddMinutes(15),
            CancellationToken.None);

        Assert.True(Assert.Single(result).Claimed);
        Assert.Single(dbContext.GradingPublicationClaims);
        Assert.Equal("Authorized", dbContext.GradingPublicationClaims.Single().Status);
    }

    [Fact]
    public async Task PublicationClaims_PreservaResultadoPorItemAoLiberarMutex()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var publicationId = Guid.NewGuid();
        var succeededItemId = Guid.NewGuid();
        var failedItemId = Guid.NewGuid();
        dbContext.GradingPublicationClaims.AddRange(
            new GradingPublicationClaimEntity
            {
                PublicationId = publicationId,
                GradingItemId = succeededItemId,
                ConnectionKey = "connection-outcome",
                AssignmentId = 501,
                MoodleUserId = 101,
                AttemptNumber = 1,
                Status = "Authorized",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
            },
            new GradingPublicationClaimEntity
            {
                PublicationId = publicationId,
                GradingItemId = failedItemId,
                ConnectionKey = "connection-outcome",
                AssignmentId = 501,
                MoodleUserId = 102,
                AttemptNumber = 1,
                Status = "Authorized",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
            });
        await dbContext.SaveChangesAsync();

        await store.FinalizePublicationClaimsAsync(
            publicationId,
            new Dictionary<Guid, string>
            {
                [succeededItemId] = "Succeeded",
                [failedItemId] = "Failed"
            },
            CancellationToken.None);
        await store.ReleasePublicationClaimsAsync(publicationId, CancellationToken.None);

        var statuses = await store.GetPublicationClaimStatusesAsync(
            publicationId,
            CancellationToken.None);
        Assert.Equal("Succeeded", statuses[succeededItemId]);
        Assert.Equal("Failed", statuses[failedItemId]);
    }

    [Fact]
    public async Task AddBatchAndItemAsync_PersisteCicloMinimoDeCorrecao()
    {
        await using var dbContext = CreateDbContext();
        IGradingReviewRepository repository = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(
            courseId: 10,
            assignmentIds: [501],
            createdBySubject: "teacher-1",
            createdByMoodleUserId: 321,
            totalItems: 1);
        var item = AssistedGradingItem.Create(
            batch.Id,
            courseId: 10,
            assignmentId: 501,
            submissionId: 9001,
            moodleUserId: 101,
            attemptNumber: 0);
        item.SetDraft(9m, 0.9m, "Rascunho.");
        item.ApplyTeacherReview(
            9.5m,
            "Feedback final.",
            "teacher-1",
            321,
            "approved",
            "Ajuste de nota validado.");

        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.SaveChangesAsync(CancellationToken.None);

        var savedBatch = await repository.GetBatchAsync(batch.Id, CancellationToken.None);
        var savedItem = await repository.GetItemAsync(item.Id, CancellationToken.None);

        Assert.NotNull(savedBatch);
        Assert.Equal([501], savedBatch!.AssignmentIds);
        Assert.NotNull(savedItem);
        Assert.Equal(GradingItemStatus.ReadyToCommit, savedItem!.Status);
        Assert.Equal(9.5m, savedItem.FinalGrade);
        Assert.Equal("approved", savedItem.TeacherDecision);
        Assert.Equal("Ajuste de nota validado.", savedItem.ReviewNotes);
        Assert.NotNull(savedItem.IdempotencyKey);
    }

    [Fact]
    public async Task AddEvidenceAsync_PersisteEListaEvidenciasDoItem()
    {
        await using var dbContext = CreateDbContext();
        IGradingReviewRepository repository = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(
            courseId: 10,
            assignmentIds: [501],
            createdBySubject: "teacher-1",
            createdByMoodleUserId: 321,
            totalItems: 1);
        var item = AssistedGradingItem.Create(
            batch.Id,
            courseId: 10,
            assignmentId: 501,
            submissionId: 9001,
            moodleUserId: 101,
            attemptNumber: 0);
        var evidence = new GradingEvidence(
            Guid.NewGuid(),
            item.Id,
            "c1",
            "Descrever eventos de TI.",
            4m,
            3m,
            "O texto menciona monitoramento e alerta.",
            "Faltou exemplo operacional.",
            TeacherReviewRequired: true,
            CreatedAt: new DateTimeOffset(2026, 6, 13, 12, 0, 0, TimeSpan.Zero));

        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.AddEvidenceAsync(evidence, CancellationToken.None);
        await repository.SaveChangesAsync(CancellationToken.None);

        var saved = Assert.Single(await repository.ListEvidenceByItemAsync(item.Id, CancellationToken.None));
        Assert.Equal("c1", saved.CriterionId);
        Assert.Equal("Descrever eventos de TI.", saved.CriterionText);
        Assert.Equal(4m, saved.MaxPoints);
        Assert.Equal(3m, saved.SuggestedPoints);
        Assert.Equal("O texto menciona monitoramento e alerta.", saved.EvidenceText);
        Assert.Equal("Faltou exemplo operacional.", saved.GapsText);
        Assert.True(saved.TeacherReviewRequired);
    }

    [Fact]
    public async Task ContextSnapshotStore_PublicaPayloadDeFormaIdempotente()
    {
        await using var dbContext = CreateDbContext();
        var repository = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.SaveChangesAsync(CancellationToken.None);

        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "10",
            "501",
            "9001",
            "101",
            assignmentStatement: "Enunciado da atividade.",
            criteria: "Descrever a solução.",
            rubricDescription: null,
            maxGrade: 10m,
            gradeScale: null,
            submissionText: "Texto que não deve ser duplicado no snapshot.",
            attachedFiles: [],
            courseMaterials: null,
            teacherInstructions: "Seja claro.");
        var snapshot = GradingContextSnapshotFactory.Create(
            item,
            context,
            new GradingContextOptions());

        IGradingContextSnapshotStore store = repository;
        await store.PublishAsync(snapshot, CancellationToken.None);
        await store.PublishAsync(snapshot, CancellationToken.None);
        await repository.SaveChangesAsync(CancellationToken.None);

        var document = Assert.Single(dbContext.GradingContextSnapshots);
        Assert.Equal(snapshot.ContextHash, document.ContextHash);
        Assert.Equal(snapshot.Version, document.Version);
        Assert.Contains("AssignmentStatement", document.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Texto que não deve ser duplicado", document.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProposalStore_PublicaVersoesAppendOnlyEDeFormaIdempotente()
    {
        await using var dbContext = CreateDbContext();
        var repository = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.Add(item);
        await dbContext.SaveChangesAsync();

        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "10",
            "501",
            "9001",
            "101",
            "Enunciado",
            "Criterio",
            null,
            10m,
            null,
            "Texto de entrega",
            [],
            null,
            null);
        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        var proposal = AiGradingProposal.Create(
            item.Id,
            batch.Id,
            await repository.GetNextVersionAsync(item.Id, CancellationToken.None),
            item.ContextHash,
            8m,
            "Feedback",
            [new AiGradingCriterionProposal("C1", "Criterio", 10m, 8m, AiGradingCriterionSource.FormalRubric, "evidencia", null, false, false, [])],
            [],
            [],
            new GradingScaleSnapshot(10m, "points", "Moodle"),
            new GradingExtractionSummary("succeeded", 1, false, 20, 20, null),
            new GradingEvidenceCoverage(1, 1, 1, 1, 20, 20, false),
            new AiGradingConfidenceResult(.9m, [], false),
            reviewRequired: false,
            createdAt: DateTimeOffset.UtcNow);

        IGradingProposalStore store = repository;
        await store.PublishAsync(proposal, CancellationToken.None);
        await store.PublishAsync(proposal, CancellationToken.None);
        await repository.SaveChangesAsync(CancellationToken.None);

        var persisted = Assert.Single(dbContext.AiGradingProposals);
        Assert.Equal(proposal.ProposalHash, persisted.ProposalHash);
        Assert.Contains("C1", persisted.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Texto de entrega", persisted.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(2, await store.GetNextVersionAsync(item.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RetentionStore_RedigeTextoExpiradoPreservaHashEEstado()
    {
        await using var dbContext = CreateDbContext();
        var repository = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var expired = new GradingArtifact(
            Guid.NewGuid(), item.Id, "submission_file", "answer.txt", "text/plain", "abc123", 10,
            ExtractionStatus.Succeeded, "texto confidencial", null, DateTimeOffset.UtcNow.AddDays(-10));
        var recent = expired with { Id = Guid.NewGuid(), ExtractedTextRef = "texto recente", CreatedAt = DateTimeOffset.UtcNow };
        var context = expired with { Id = Guid.NewGuid(), ArtifactType = "assignment_context", ExtractedTextRef = "enunciado", CreatedAt = DateTimeOffset.UtcNow.AddDays(-10) };
        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.Add(item);
        dbContext.GradingArtifacts.AddRange(expired, recent, context);
        await dbContext.SaveChangesAsync();

        IGradingRetentionStore store = repository;
        var redacted = await store.RedactExpiredArtifactTextAsync(
            DateTimeOffset.UtcNow.AddDays(-7),
            CancellationToken.None);

        Assert.Equal(1, redacted);
        var savedExpired = await dbContext.GradingArtifacts.SingleAsync(artifact => artifact.Id == expired.Id);
        Assert.Null(savedExpired.ExtractedTextRef);
        Assert.Equal("retention_redacted", savedExpired.SummaryRef);
        Assert.Equal(expired.Sha256, savedExpired.Sha256);
        Assert.Equal(expired.ExtractionStatus, savedExpired.ExtractionStatus);
        Assert.Equal("texto recente", (await dbContext.GradingArtifacts.SingleAsync(artifact => artifact.Id == recent.Id)).ExtractedTextRef);
        Assert.Equal("enunciado", (await dbContext.GradingArtifacts.SingleAsync(artifact => artifact.Id == context.Id)).ExtractedTextRef);
    }

    [Fact]
    public async Task JobStore_ImpedeClaimConcorrenteRenovaERegistraCheckpoint()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.Add(item);
        await dbContext.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var first = await store.TryClaimBatchAsync(
            batch.Id, "worker-a", now, TimeSpan.FromMinutes(10), CancellationToken.None);
        var second = await store.TryClaimBatchAsync(
            batch.Id, "worker-b", now, TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.True(await store.RenewBatchLeaseAsync(
            batch.Id, "worker-a", now.AddMinutes(1), TimeSpan.FromMinutes(10), CancellationToken.None));
        Assert.True(await store.UpdateBatchCheckpointAsync(
            batch.Id, "worker-a", item.Id, now.AddMinutes(1), CancellationToken.None));
        Assert.True(await store.ReleaseBatchLeaseAsync(
            batch.Id, "worker-a", now.AddMinutes(2), null, null, CancellationToken.None));

        var savedBatch = await store.GetBatchAsync(batch.Id, CancellationToken.None);
        Assert.NotNull(savedBatch);
        Assert.Null(savedBatch!.LeaseOwner);
        Assert.Null(savedBatch.LeaseUntil);
        Assert.Equal(item.Id, savedBatch.CheckpointItemId);
        Assert.Equal(1, savedBatch.AttemptCount);
    }

    [Fact]
    public async Task JobStore_RecuperaLeaseExpiradoSomenteQuandoHaItemPendente()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.Add(item);
        await dbContext.SaveChangesAsync();

        var claimTime = DateTimeOffset.UtcNow;
        Assert.NotNull(await store.TryClaimBatchAsync(
            batch.Id, "worker-a", claimTime, TimeSpan.FromMinutes(1), CancellationToken.None));

        var recovered = await store.RecoverExpiredBatchLeasesAsync(
            claimTime.AddMinutes(2), CancellationToken.None);

        Assert.Equal(1, recovered);
        var savedBatch = await store.GetBatchAsync(batch.Id, CancellationToken.None);
        Assert.Equal(GradingBatchStatus.Pending, savedBatch!.Status);
        Assert.Null(savedBatch.LeaseOwner);
        Assert.Equal(claimTime.AddMinutes(2), savedBatch.NextAttemptAt);
    }

    [Fact]
    public async Task JobStore_ClaimDueRespeitaPrioridadeAntesDaOrdemDeCriacao()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var low = AssistedGradingBatch.Create(10, [501], "teacher-low", 321, 1, priority: "low");
        var high = AssistedGradingBatch.Create(10, [501], "teacher-high", 321, 1, priority: "high");
        var normal = AssistedGradingBatch.Create(10, [501], "teacher-normal", 321, 1, priority: "normal");
        dbContext.GradingBatches.AddRange(low, high, normal);
        dbContext.GradingItems.AddRange(
            AssistedGradingItem.Create(low.Id, 10, 501, 9001, 101, 0),
            AssistedGradingItem.Create(high.Id, 10, 501, 9002, 102, 0),
            AssistedGradingItem.Create(normal.Id, 10, 501, 9003, 103, 0));
        await dbContext.SaveChangesAsync();

        var claims = await store.ClaimDueBatchesAsync(
            "worker-a",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(10),
            maxBatches: 3,
            CancellationToken.None);

        Assert.Equal([high.Id, normal.Id, low.Id], claims.Select(claim => claim.BatchId));
    }

    [Fact]
    public async Task JobStore_ClaimDuePromoveLoteAntigoParaEvitarStarvation()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var agedLow = AssistedGradingBatch.Create(10, [501], "teacher-aged", 321, 1, priority: "low");
        var freshHigh = AssistedGradingBatch.Create(10, [501], "teacher-fresh", 321, 1, priority: "high");
        dbContext.GradingBatches.AddRange(agedLow, freshHigh);
        dbContext.GradingItems.AddRange(
            AssistedGradingItem.Create(agedLow.Id, 10, 501, 9001, 101, 0),
            AssistedGradingItem.Create(freshHigh.Id, 10, 501, 9002, 102, 0));
        dbContext.Entry(agedLow).Property(batch => batch.CreatedAt).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-31);
        await dbContext.SaveChangesAsync();

        var claims = await store.ClaimDueBatchesAsync(
            "worker-a",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(10),
            maxBatches: 2,
            CancellationToken.None);

        Assert.Equal([agedLow.Id, freshHigh.Id], claims.Select(claim => claim.BatchId));
    }

    [Fact]
    public async Task JobStore_LeasePorItemImpedeDuplicacaoRenovaLiberaEContaTentativas()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.Add(item);
        await dbContext.SaveChangesAsync();

        var claimTime = DateTimeOffset.UtcNow;
        var first = await store.TryClaimItemAsync(
            batch.Id,
            item.Id,
            "worker-a",
            claimTime,
            TimeSpan.FromMinutes(10),
            CancellationToken.None);
        var second = await store.TryClaimItemAsync(
            batch.Id,
            item.Id,
            "worker-b",
            claimTime,
            TimeSpan.FromMinutes(10),
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(1, first!.AttemptCount);
        Assert.Null(second);
        Assert.False(await store.RenewItemLeaseAsync(
            batch.Id,
            item.Id,
            "worker-b",
            claimTime.AddMinutes(1),
            TimeSpan.FromMinutes(10),
            CancellationToken.None));
        Assert.True(await store.RenewItemLeaseAsync(
            batch.Id,
            item.Id,
            "worker-a",
            claimTime.AddMinutes(1),
            TimeSpan.FromMinutes(10),
            CancellationToken.None));
        Assert.False(await store.ReleaseItemLeaseAsync(
            batch.Id,
            item.Id,
            "worker-b",
            claimTime.AddMinutes(2),
            "ignored",
            null,
            CancellationToken.None));
        Assert.True(await store.ReleaseItemLeaseAsync(
            batch.Id,
            item.Id,
            "worker-a",
            claimTime.AddMinutes(2),
            "processing_failed",
            claimTime.AddMinutes(3),
            CancellationToken.None));

        var blockedRetry = await store.TryClaimItemAsync(
            batch.Id,
            item.Id,
            "worker-c",
            claimTime.AddMinutes(2).AddSeconds(30),
            TimeSpan.FromMinutes(10),
            CancellationToken.None);
        Assert.Null(blockedRetry);

        var retry = await store.TryClaimItemAsync(
            batch.Id,
            item.Id,
            "worker-c",
            claimTime.AddMinutes(3),
            TimeSpan.FromMinutes(10),
            CancellationToken.None);
        Assert.NotNull(retry);
        Assert.Equal(2, retry!.AttemptCount);

        var saved = await store.GetItemAsync(item.Id, CancellationToken.None);
        Assert.Equal("worker-c", saved!.LeaseOwner);
        Assert.Equal(2, saved.AttemptCount);
    }

    [Fact]
    public async Task JobStore_ClaimEReleaseEmLoteMantemExclusividadePorJanela()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 32);
        var items = Enumerable.Range(0, 32)
            .Select(index => AssistedGradingItem.Create(
                batch.Id,
                10,
                501,
                9001 + index,
                101 + index,
                0))
            .ToArray();
        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.AddRange(items);
        await dbContext.SaveChangesAsync();

        var claimTime = DateTimeOffset.UtcNow;
        var first = await store.TryClaimItemsAsync(
            batch.Id,
            items.Select(item => item.Id).ToArray(),
            "worker-a",
            claimTime,
            TimeSpan.FromMinutes(10),
            CancellationToken.None);
        var second = await store.TryClaimItemsAsync(
            batch.Id,
            items.Select(item => item.Id).ToArray(),
            "worker-b",
            claimTime,
            TimeSpan.FromMinutes(10),
            CancellationToken.None);

        Assert.Equal(32, first.Count);
        Assert.Empty(second);
        Assert.Equal(32, await store.ReleaseItemLeasesAsync(
            batch.Id,
            first,
            "worker-a",
            claimTime.AddMinutes(1),
            errorCode: null,
            nextAttemptAt: null,
            CancellationToken.None));

        var savedItems = await dbContext.GradingItems
            .AsNoTracking()
            .Where(item => item.BatchId == batch.Id)
            .ToArrayAsync();
        Assert.All(savedItems, item =>
        {
            Assert.Null(item.LeaseOwner);
            Assert.Null(item.LeaseUntil);
            Assert.Equal(1, item.AttemptCount);
        });
    }

    [Fact]
    public async Task JobStore_LeasePorItemExpiradoPodeSerRecuperado()
    {
        await using var dbContext = CreateDbContext();
        var store = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.Add(item);
        await dbContext.SaveChangesAsync();

        var claimTime = DateTimeOffset.UtcNow;
        Assert.NotNull(await store.TryClaimItemAsync(
            batch.Id,
            item.Id,
            "worker-a",
            claimTime,
            TimeSpan.FromMinutes(1),
            CancellationToken.None));

        Assert.Equal(1, await store.RecoverExpiredItemLeasesAsync(
            claimTime.AddMinutes(2),
            CancellationToken.None));

        var recovered = await store.GetItemAsync(item.Id, CancellationToken.None);
        Assert.Null(recovered!.LeaseOwner);
        Assert.Null(recovered.LeaseUntil);
        Assert.Equal(claimTime.AddMinutes(2), recovered.NextAttemptAt);
    }

    [Fact]
    public async Task PurgeCancelledGradingData_RemoveSomenteProjecaoLocal()
    {
        await using var dbContext = CreateDbContext();
        var repository = new GradingReviewRepository(dbContext);
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1, gradingRunId: run.Id);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var artifact = new GradingArtifact(
            Guid.NewGuid(), item.Id, "submission_file", "answer.txt", "text/plain", "abc123", 10,
            ExtractionStatus.Succeeded, null, null, DateTimeOffset.UtcNow);
        var evidence = new GradingEvidence(
            Guid.NewGuid(), item.Id, "c1", "Criterio", 10m, 8m, "Evidencia", null, false, DateTimeOffset.UtcNow);
        var package = GradingEvidencePackage.Create(
            "teacher-1",
            "fieg",
            null,
            "fieg",
            10,
            501,
            "501",
            1,
            new string('a', 64),
            new string('b', 64),
            batch.Id);
        var packageMember = GradingEvidencePackageMember.Create(
            package.Id,
            item.Id,
            101,
            "Aluno 101",
            9001,
            0,
            DateTimeOffset.UtcNow,
            null,
            null);
        var packageFile = GradingEvidencePackageFile.Create(
            package.Id,
            item.Id,
            artifact.Id,
            "submissions/aluno-101/answer.txt",
            "answer.txt",
            "text/plain",
            "abc123",
            10,
            false);
        dbContext.GradingRuns.Add(run);
        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.Add(item);
        dbContext.GradingArtifacts.Add(artifact);
        dbContext.GradingEvidence.Add(evidence);
        dbContext.GradingEvidencePackages.Add(package);
        dbContext.GradingEvidencePackageMembers.Add(packageMember);
        dbContext.GradingEvidencePackageFiles.Add(packageFile);
        dbContext.GradingPublicationClaims.Add(new GradingPublicationClaimEntity
        {
            PublicationId = Guid.NewGuid(),
            GradingItemId = item.Id,
            ConnectionKey = "fieg",
            AssignmentId = 501,
            MoodleUserId = 101,
            AttemptNumber = 0,
            Status = "Released",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        batch.Cancel();
        await dbContext.SaveChangesAsync();

        var result = await repository.PurgeCancelledGradingDataAsync(
            [batch.Id],
            run.Id,
            CancellationToken.None);

        Assert.True(result.Purged);
        Assert.Equal(1, result.DeletedRuns);
        Assert.Equal(1, result.DeletedBatches);
        Assert.Equal(1, result.DeletedItems);
        Assert.Equal(1, result.DeletedArtifacts);
        Assert.Equal(1, result.DeletedEvidence);
        Assert.Equal(1, result.DeletedPublicationClaims);
        Assert.Empty(dbContext.GradingRuns);
        Assert.Empty(dbContext.GradingBatches);
        Assert.Empty(dbContext.GradingItems);
        Assert.Empty(dbContext.GradingArtifacts);
        Assert.Empty(dbContext.GradingEvidence);
        Assert.Empty(dbContext.GradingPublicationClaims);
        Assert.Empty(dbContext.GradingEvidencePackages);
        Assert.Empty(dbContext.GradingEvidencePackageMembers);
        Assert.Empty(dbContext.GradingEvidencePackageFiles);
    }

    [Fact]
    public async Task PurgeCancelledGradingData_PreservaItemPublicado()
    {
        await using var dbContext = CreateDbContext();
        var repository = new GradingReviewRepository(dbContext);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkCommitSucceeded();
        batch.Cancel();
        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.Add(item);
        await dbContext.SaveChangesAsync();

        var result = await repository.PurgeCancelledGradingDataAsync(
            [batch.Id],
            null,
            CancellationToken.None);

        Assert.False(result.Purged);
        Assert.Contains("publicados", result.BlockReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await repository.GetBatchAsync(batch.Id, CancellationToken.None));
        Assert.NotNull(await repository.GetItemAsync(item.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("anchor", "member", false, false)]
    [InlineData("member", "member", false, false)]
    [InlineData("file", "file", false, false)]
    [InlineData("anchor", "file", false, false)]
    [InlineData("anchor", "none", true, false)]
    [InlineData("member", "none", true, false)]
    [InlineData("file", "none", true, false)]
    [InlineData("anchor", "none", false, false)]
    [InlineData("member", "member", false, true)]
    public async Task PurgeCancelledGradingData_ProtectsPackageScopeAndLease(
        string candidate, string outside, bool activeLease, bool fullRun)
    {
        await using var db = CreateDbContext();
        await VerifyPackagePurgeGuardAsync(db, candidate, outside, activeLease, fullRun);
    }

    internal static async Task VerifyPackagePurgeGuardAsync(
        ConnectorDbContext db, string candidate, string outside, bool activeLease, bool fullRun)
    {
        var owner = $"purge-guard-{Guid.NewGuid():N}";
        var run = GradingRun.Create(owner);
        var selected = AssistedGradingBatch.Create(10, [501], owner, 321, totalItems: 1, gradingRunId: run.Id);
        var sibling = AssistedGradingBatch.Create(10, [501], owner, 321, totalItems: 1, gradingRunId: run.Id);
        var unrelated = AssistedGradingBatch.Create(10, [501], owner, 321, totalItems: 1);
        selected.Cancel();
        if (fullRun) sibling.Cancel();
        var first = AssistedGradingItem.Create(selected.Id, 10, 501, 9001, 101, 0);
        var second = AssistedGradingItem.Create(sibling.Id, 10, 501, 9002, 102, 0);
        var other = AssistedGradingItem.Create(unrelated.Id, 10, 501, 9003, 103, 0);
        first.SetDraft(8m, 0.8m, "selected draft");
        second.SetDraft(9m, 0.9m, "sibling draft");
        other.SetDraft(7m, 0.7m, "unrelated draft");
        GradingEvidencePackage Package(Guid anchor, char hash) => GradingEvidencePackage.Create(
            owner, owner, null, "default", 10, 501, "501", 1,
            new string(hash, 64), new string(hash, 64), anchor);
        var package = Package(candidate == "anchor" ? selected.Id : sibling.Id, 'a');
        var untouched = Package(unrelated.Id, 'b');
        // An expired lease must not block cleanup; only the package has a lease.
        Assert.True(package.TryAcquireLease("package-worker",
            DateTimeOffset.UtcNow.AddMinutes(activeLease ? 0 : -10), TimeSpan.FromMinutes(5)));
        // An unrelated active package must not block cleanup.
        Assert.True(untouched.TryAcquireLease("other-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
        db.GradingRuns.Add(run);
        db.GradingBatches.AddRange(selected, sibling, unrelated);
        db.GradingItems.AddRange(first, second, other);
        db.GradingEvidencePackages.AddRange(package, untouched);
        void Member(GradingEvidencePackage target, AssistedGradingItem item, long user) =>
            db.GradingEvidencePackageMembers.Add(GradingEvidencePackageMember.Create(
                target.Id, item.Id, user, "Aluno", null, 0, DateTimeOffset.UtcNow, null, null));
        void File(Guid? itemId, string path) =>
            db.GradingEvidencePackageFiles.Add(GradingEvidencePackageFile.Create(
                package.Id, itemId, Guid.NewGuid(), path, path, "text/plain", new string('c', 64), 10, itemId is null));
        if (candidate == "member") Member(package, first, 101);
        if (candidate == "file") File(first.Id, "selected.txt");
        if (outside == "member") Member(package, second, 102);
        if (outside == "file") File(second.Id, "sibling.txt");
        File(null, "context.txt"); // Shared context has no item and must not count as outside scope.
        Member(untouched, other, 103);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var result = await new GradingReviewRepository(db).PurgeCancelledGradingDataAsync(
            fullRun ? [selected.Id, sibling.Id] : [selected.Id],
            fullRun ? run.Id : null, CancellationToken.None);
        var blocked = activeLease || outside != "none" && !fullRun;
        Assert.Equal(!blocked, result.Purged);
        if (blocked)
        {
            Assert.Contains(outside != "none" ? "pacote compartilhado possui itens fora do escopo" : "lease do pacote",
                result.BlockReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("selected draft", (await db.GradingItems.SingleAsync(x => x.Id == first.Id)).DraftFeedback);
            Assert.True(await db.GradingBatches.AnyAsync(x => x.Id == selected.Id));
        }
        else
        {
            Assert.False(await db.GradingItems.AnyAsync(x => x.Id == first.Id));
        }
        Assert.Equal(blocked, await db.GradingEvidencePackages.AnyAsync(x => x.Id == package.Id));
        Assert.Equal(blocked, await db.GradingEvidencePackageFiles.AnyAsync(x => x.PackageId == package.Id));
        if (candidate == "member" || outside == "member")
            Assert.Equal(blocked, await db.GradingEvidencePackageMembers.AnyAsync(x => x.PackageId == package.Id));
        if (!fullRun)
            Assert.Equal("sibling draft", (await db.GradingItems.SingleAsync(x => x.Id == second.Id)).DraftFeedback);
        else
            Assert.False(await db.GradingRuns.AnyAsync(x => x.Id == run.Id));
        Assert.Equal("unrelated draft", (await db.GradingItems.SingleAsync(x => x.Id == other.Id)).DraftFeedback);
        Assert.True(await db.GradingEvidencePackages.AnyAsync(x => x.Id == untouched.Id));
        Assert.True(await db.GradingEvidencePackageMembers.AnyAsync(x => x.PackageId == untouched.Id));
    }


    private static ConnectorDbContext CreateDbContext(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"grading-review-{Guid.NewGuid():N}")
            .Options;

        return new ConnectorDbContext(options);
    }

    [Fact]
    public async Task DraftReview_ConcurrentVersionConflict_DoesNotOverwriteStaleItem()
    {
        var databaseName = $"grading-review-conflict-{Guid.NewGuid():N}";
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(5m, 0.8m, "Rascunho inicial.");
        await using (var seed = CreateDbContext(databaseName))
        {
            seed.GradingBatches.Add(batch);
            seed.GradingItems.Add(item);
            await seed.SaveChangesAsync();
        }

        await using var firstContext = CreateDbContext(databaseName);
        await using var secondContext = CreateDbContext(databaseName);
        var firstRepository = new GradingReviewRepository(firstContext);
        var secondRepository = new GradingReviewRepository(secondContext);
        var firstItem = await firstRepository.GetItemAsync(item.Id, CancellationToken.None);
        var secondItem = await secondRepository.GetItemAsync(item.Id, CancellationToken.None);
        Assert.NotNull(firstItem);
        Assert.NotNull(secondItem);
        firstItem.ApplyTeacherReview(7m, "Feedback aprovado primeiro.", "teacher-1", 321, "approved", "Revisao 1.");
        await Task.Delay(10);
        secondItem.ApplyTeacherReview(6m, "Feedback obsoleto.", "teacher-2", 654, "approved", "Revisao 2.");
        await firstRepository.SaveChangesForDraftReviewAsync(CancellationToken.None);
        await Assert.ThrowsAsync<GradingDraftVersionConflictException>(() =>
            secondRepository.SaveChangesForDraftReviewAsync(CancellationToken.None));

        await using var verification = CreateDbContext(databaseName);
        var persistedItem = await verification.GradingItems.SingleAsync(candidate => candidate.Id == item.Id);
        Assert.Equal(7m, persistedItem.FinalGrade);
        Assert.Equal("Feedback aprovado primeiro.", persistedItem.FinalFeedback);
    }

    private static GradingPublicationClaimEntity CreateClaim(
        Guid publicationId,
        Guid gradingItemId,
        long moodleUserId,
        string status) =>
        new()
        {
            PublicationId = publicationId,
            GradingItemId = gradingItemId,
            ConnectionKey = "test-connection",
            AssignmentId = 501,
            MoodleUserId = moodleUserId,
            AttemptNumber = 0,
            Status = status,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

    private sealed class ReconciliationCurrentUser(string subject, params string[] scopes) : ICurrentUserContext
    {
        public string Subject { get; } = subject;
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes { get; } = scopes;
        public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);
    }

    private sealed class SingleActionPendingMoodleActionRepository(PendingMoodleAction action)
        : IPendingMoodleActionRepository
    {
        public List<MoodleAuditLog> Audits { get; } = [];

        public Task AddAsync(PendingMoodleAction pendingAction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PendingMoodleAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(id == action.Id ? action : null);

        public Task<PendingActionConfirmationClaimResult> TryConfirmWithAuditAsync(
            Guid id,
            string confirmedBySubject,
            DateTimeOffset confirmedAt,
            MoodleAuditLog confirmationAudit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PendingActionReconciliationClaimResult> TryResolveExecutionUnknownWithAuditAsync(
            Guid id,
            PendingActionStatus resolvedStatus,
            MoodleAuditLog reconciliationAudit,
            CancellationToken cancellationToken)
        {
            if (id != action.Id || action.Status != PendingActionStatus.ExecutionUnknown)
            {
                return Task.FromResult(new PendingActionReconciliationClaimResult(false, action.Status, null));
            }

            action.ResolveExecutionUnknown(resolvedStatus);
            Audits.Add(reconciliationAudit);
            return Task.FromResult(new PendingActionReconciliationClaimResult(
                true,
                resolvedStatus,
                reconciliationAudit.Id.ToString("N")));
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
