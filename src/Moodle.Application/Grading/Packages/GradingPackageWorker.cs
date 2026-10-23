using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading.Packages;

internal static class GradingPackageMemberScope
{
    public static bool IsValid(GradingEvidencePackage package, AssistedGradingBatch anchor,
        AssistedGradingBatch batch, AssistedGradingItem item) =>
        batch.Status != GradingBatchStatus.Cancelled &&
        (batch.Id == anchor.Id || anchor.GradingRunId is not null && batch.GradingRunId == anchor.GradingRunId) &&
        batch.CreatedBySubject == anchor.CreatedBySubject && anchor.CreatedBySubject == package.OwnerSubject &&
        string.Equals(batch.TeacherInstructions, anchor.TeacherInstructions, StringComparison.Ordinal) &&
        batch.CourseId == anchor.CourseId && anchor.CourseId == package.CourseId && item.CourseId == package.CourseId &&
        item.BatchId == batch.Id && item.AssignmentId == package.AssignmentId &&
        batch.AssignmentIds.Contains(package.AssignmentId) && anchor.AssignmentIds.Contains(package.AssignmentId) &&
        SameConnection(anchor.MoodleConnectionId, anchor.ConnectorClientId, anchor.ConnectionAlias,
            batch.MoodleConnectionId, batch.ConnectorClientId, batch.ConnectionAlias) &&
        SameConnection(anchor.MoodleConnectionId, anchor.ConnectorClientId, anchor.ConnectionAlias,
            package.MoodleConnectionId, package.ConnectorClientId, package.ConnectionAlias);

    private static bool SameConnection(string? leftId, string? leftClient, string? leftAlias,
        string? rightId, string? rightClient, string? rightAlias) =>
        string.Equals(leftId, rightId, StringComparison.Ordinal) &&
        string.Equals(leftClient, rightClient, StringComparison.Ordinal) &&
        string.Equals(leftAlias, rightAlias, StringComparison.Ordinal);

    public static bool HashesEqual(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Prepara pacotes fora do request MCP. A etapa de leitura é limitada por
/// conexão e cada falha é associada ao item/arquivo afetado; o arquivo ZIP só
/// é promovido depois que o manifesto e todas as entradas disponíveis foram
/// validados.
/// </summary>
public sealed class GradingPackageWorker(
    IGradingReviewRepository repository,
    IGradingPackageStore packageStore,
    IGradingPackageBlobStore blobStore,
    IGradingPackageArchiveWriter archiveWriter,
    IMoodleSubmissionFileGateway submissionFileGateway,
    IMoodleAssignmentSubmissionsGateway submissionsGateway,
    IOptions<GradingLimitsOptions> limits,
    ILogger<GradingPackageWorker> logger,
    IServiceScopeFactory scopeFactory,
    GradingPackageDownloadLimiter downloadLimiter,
    IGradingOperationTelemetry? telemetry = null,
    IGradingPackageArtifactCache? artifactCache = null,
    GradingPackageSubmissionReadCache? submissionReadCache = null)
{
    public Task<GradingPackagePreparationResult> PrepareAsync(
        Guid batchJobId,
        CancellationToken cancellationToken) =>
        PrepareAsync(batchJobId, null, cancellationToken);

    public async Task<GradingPackagePreparationResult> PrepareAsync(
        Guid batchJobId,
        Guid? requestedPackageId,
        CancellationToken cancellationToken) =>
        await PrepareCoreAsync(batchJobId, requestedPackageId, includePendingItems: true, cancellationToken);

    internal Task<GradingPackagePreparationResult> PrepareForBackgroundAsync(
        Guid batchJobId,
        Guid packageId,
        CancellationToken cancellationToken) =>
        PrepareCoreAsync(batchJobId, packageId, includePendingItems: false, cancellationToken);

    private async Task<GradingPackagePreparationResult> PrepareCoreAsync(
        Guid batchJobId,
        Guid? requestedPackageId,
        bool includePendingItems,
        CancellationToken cancellationToken)
    {
        if (batchJobId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(batchJobId));
        }

        var batch = await repository.GetBatchAsync(batchJobId, cancellationToken)
            ?? throw new InvalidOperationException("O lote de correcao nao foi encontrado.");
        if (batch.Status == GradingBatchStatus.Cancelled && requestedPackageId is null)
        {
            return new GradingPackagePreparationResult(batchJobId, [], [], [],
                ["O lote foi cancelado; nenhuma preparacao foi iniciada."]);
        }
        var workerId = $"grading-package-worker:{Guid.NewGuid():N}";
        IReadOnlyList<AssistedGradingItem> items = [];
        var warnings = new List<string>();
        var connectionKey = GradingBatchScopeResolver.ResolveConnectionKey(batch);
        GradingEvidencePackage? persistedPackage = null;
        IReadOnlyList<AssistedGradingItem> selectedItems;
        if (requestedPackageId is Guid packageId)
        {
            persistedPackage = await packageStore.GetAsync(packageId, cancellationToken)
                ?? throw new InvalidOperationException("O pacote de correcao solicitado nao foi encontrado.");
            if (persistedPackage.BatchJobId != batchJobId)
            {
                throw new InvalidOperationException("O pacote de correcao nao pertence ao lote solicitado.");
            }

            if (persistedPackage.Status is GradingEvidencePackageStatus.Ready or
                GradingEvidencePackageStatus.ResultsReceived)
            {
                // Another worker may have completed the package while this
                // execution was waiting in the queue. Do not reload the
                // whole batch or touch Moodle just to discover that the
                // immutable transport is already available.
                return new GradingPackagePreparationResult(
                    batchJobId,
                    [ToDescriptor(persistedPackage)],
                    [persistedPackage.Id],
                    [],
                    []);
            }

            var persistedMembers = await packageStore.ListMembersAsync(packageId, cancellationToken);
            if (persistedMembers.Count == 0)
            {
                // Packages created before persisted selection was introduced
                // can still be resumed once from the current batch. New
                // requests always persist members before enqueueing.
                items = await GradingItemProcessor.LoadAllBatchItemsAsync(
                    repository,
                    batchJobId,
                    cancellationToken,
                    Math.Clamp(limits.Value.MaxBatchItems, 1, 400));
                warnings.Add($"Pacote {packageId}: selecao antiga nao persistida; uma retomada compativel sera reconstruida uma vez.");
                persistedPackage = null;
                selectedItems = items
                    .Where(item => item.Status == GradingItemStatus.AwaitingAiAnalysis)
                    .ToArray();
            }
            else
            {
                var persistedItemIds = persistedMembers.Select(member => member.GradingItemId).ToArray();
                var itemsById = await repository.GetItemsAsync(persistedItemIds, cancellationToken);
                selectedItems = persistedMembers
                    .OrderBy(member => member.GradingItemId)
                    .Select(member => itemsById.TryGetValue(member.GradingItemId, out var item)
                        ? item
                        : null)
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .ToArray();
                var memberBatchIds = selectedItems
                    .Select(item => item.BatchId)
                    .Where(memberBatchId => memberBatchId != batch.Id)
                    .Distinct()
                    .ToArray();
                var memberBatches = (await repository.GetBatchesAsync(memberBatchIds, cancellationToken))
                    .ToDictionary(entry => entry.Key, entry => entry.Value);
                memberBatches[batch.Id] = batch;
                foreach (var item in selectedItems)
                {
                    if (!memberBatches.TryGetValue(item.BatchId, out var memberBatch))
                        throw new InvalidOperationException("O lote de um membro do pacote nao existe mais.");
                    var member = persistedMembers.Single(value => value.GradingItemId == item.Id);
                    if (batch.Status == GradingBatchStatus.Cancelled || memberBatch.Status == GradingBatchStatus.Cancelled)
                    {
                        persistedPackage.Cancel();
                        await packageStore.SaveChangesAsync(cancellationToken);
                        return new GradingPackagePreparationResult(batchJobId, [ToDescriptor(persistedPackage)], [],
                            persistedItemIds, ["Um sublote do pacote foi cancelado. Prepare novamente a execucao para reunir apenas as entregas ativas; rascunhos anteriores foram preservados."]);
                    }
                    if (!GradingPackageMemberScope.IsValid(persistedPackage, batch, memberBatch, item) ||
                        !GradingPackageMemberScope.HashesEqual(item.ContextHash, member.ContextHash))
                    {
                        throw new InvalidOperationException("Um membro do pacote esta fora do escopo da execucao, atividade ou contexto.");
                    }
                }
                if (selectedItems.Count != persistedMembers.Count)
                {
                    warnings.Add($"Pacote {packageId}: alguns itens selecionados nao existem mais no lote e permanecem pendentes.");
                }

                // Interactive callers need the full pending-item list. The
                // background consumer persists only package state, so avoid
                // reloading unrelated items when the members are already stored.
                if (includePendingItems)
                {
                    items = await GradingItemProcessor.LoadAllBatchItemsAsync(
                        repository,
                        batchJobId,
                        cancellationToken,
                        Math.Clamp(limits.Value.MaxBatchItems, 1, 400));
                }
            }
        }
        else
        {
            items = await GradingItemProcessor.LoadAllBatchItemsAsync(
                repository,
                batchJobId,
                cancellationToken,
                Math.Clamp(limits.Value.MaxBatchItems, 1, 400));
            selectedItems = items
                .Where(item => item.Status == GradingItemStatus.AwaitingAiAnalysis)
                .ToArray();
        }

        IReadOnlyList<GradingPackageItemGroup> packageGroups;
        if (persistedPackage is not null)
        {
            // A current request persists the exact members before enqueueing;
            // never re-plan that selection during a retry.
            packageGroups = [new GradingPackageItemGroup(persistedPackage.AssignmentId, selectedItems)];
        }
        else
        {
            // Legacy packages have no persisted members. Reconstruct their
            // groups with the same entry/byte-aware planner used by the
            // request handler, otherwise a retry could create an oversized ZIP
            // and block otherwise valid students.
            var artifactsByItem = selectedItems.Count == 0
                ? new Dictionary<Guid, IReadOnlyList<GradingArtifact>>()
                : await repository.ListArtifactsByItemsAsync(
                    selectedItems.Select(item => item.Id).ToArray(),
                    cancellationToken);
            packageGroups = selectedItems
                .GroupBy(item => new { item.AssignmentId, item.ContextHash })
                .SelectMany(group => GradingPackagePartitioner
                    .SplitPackageItems(group.ToArray(), artifactsByItem, limits.Value)
                    .Select(chunk => new GradingPackageItemGroup(group.Key.AssignmentId, chunk)))
                .ToArray();
        }

        var selectedItemIds = selectedItems
            .Select(item => item.Id)
            .ToHashSet();
        var pendingItems = items
            .Where(item => !selectedItemIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToList();
        var descriptors = new List<GradingPackageDescriptor>();
        var reusedPackageIds = new List<Guid>();
        var submissionReadsByAssignment = new Dictionary<long,
            (GradingPackageSubmissionReadResult Read, double DurationMs)>();

        foreach (var assignmentGroup in packageGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupItems = assignmentGroup.Items.ToArray();
            var package = persistedPackage;
            if (package is null)
            {
                var identity = GradingPackageIdentity.Create(
                    batch.CreatedBySubject,
                    batch.MoodleConnectionId,
                    batch.ConnectorClientId,
                    batch.ConnectionAlias,
                    batch.CourseId,
                    assignmentGroup.AssignmentId,
                    cmid: null,
                    groupItems.Select(item => new GradingPackageItemIdentity(
                        item.Id,
                        item.ContextHash,
                        item.SubmissionContentHash)).ToArray());
                package = await packageStore.GetOrCreateAsync(identity, batchJobId, cancellationToken);
            }

            if (package.Status is GradingEvidencePackageStatus.Ready or
                GradingEvidencePackageStatus.PartiallyReady or
                GradingEvidencePackageStatus.ResultsReceived)
            {
                reusedPackageIds.Add(package.Id);
                descriptors.Add(ToDescriptor(package));
                pendingItems.AddRange(groupItems.Where(item =>
                    package.Status == GradingEvidencePackageStatus.PartiallyReady).Select(item => item.Id));
                continue;
            }

            if (!await packageStore.TryClaimAsync(
                    package.Id,
                    workerId,
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromMinutes(Math.Clamp(limits.Value.BatchLeaseMinutes, 1, 120)),
                    cancellationToken))
            {
                descriptors.Add(ToDescriptor(package));
                pendingItems.AddRange(groupItems.Select(item => item.Id));
                continue;
            }

            var stagedFiles = new ConcurrentBag<string>();
            GradingPackageBlobDescriptor? writtenArchive = null;
            var archiveCommitted = false;
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using var leaseHeartbeat = new GradingPackageLeaseHeartbeat(
                package.Id,
                workerId,
                TimeSpan.FromMinutes(Math.Clamp(limits.Value.BatchLeaseMinutes, 1, 120)),
                scopeFactory,
                operationCancellation,
                logger);
            var packageStopwatch = Stopwatch.StartNew();
            var submissionRead = new GradingPackageSubmissionReadResult(
                new GradingPackageSubmissionReadSnapshot(
                    new Dictionary<(long, string), AssignmentSubmissionRecord>(), IsComplete: false),
                GradingPackageSubmissionReadCacheStatus.Bypass,
                EstimatedBytes: 0);
            var submissionReadDurationMs = 0d;
            var submissionGatewayCallCount = 0;
            GradingPackageBuildResult? packageBuild = null;
            string? retryReason = null;

            async Task<GradingPackageSubmissionReadSnapshot> LoadSubmissionRecordsForPackageAsync(
                CancellationToken token)
            {
                Interlocked.Increment(ref submissionGatewayCallCount);
                return await LoadSubmissionRecordsAsync(batch, groupItems, warnings, token);
            }

            try
            {
                var requiredSubmissionIds = groupItems
                    .Where(item => item.SubmissionId is not null)
                    .Select(item => (item.AssignmentId,
                        item.SubmissionId!.Value.ToString(CultureInfo.InvariantCulture)))
                    .ToArray();
                var hasLocalRead = submissionReadsByAssignment.TryGetValue(
                    assignmentGroup.AssignmentId, out var localRead) &&
                    localRead.Read.Snapshot.IsComplete &&
                    requiredSubmissionIds.All(localRead.Read.Snapshot.Records.ContainsKey);
                if (hasLocalRead)
                {
                    submissionRead = localRead.Read with
                    {
                        CacheStatus = localRead.Read.CacheStatus == GradingPackageSubmissionReadCacheStatus.Bypass
                            ? GradingPackageSubmissionReadCacheStatus.Bypass
                            : GradingPackageSubmissionReadCacheStatus.Hit
                    };
                }
                else
                {
                    var readScope = new GradingPackageSubmissionReadScope(
                        batch.GradingRunId ?? batch.Id,
                        batch.GradingRunId is not null,
                        batch.CreatedBySubject,
                        batch.CreatedByMoodleUserId,
                        batch.MoodleConnectionId,
                        batch.ConnectorClientId,
                        batch.ConnectionAlias,
                        batch.CourseId,
                        assignmentGroup.AssignmentId);
                    var readStopwatch = Stopwatch.StartNew();
                    try
                    {
                        if (submissionReadCache is null)
                        {
                            var snapshot = await LoadSubmissionRecordsForPackageAsync(operationCancellation.Token);
                            submissionRead = new GradingPackageSubmissionReadResult(
                                snapshot, GradingPackageSubmissionReadCacheStatus.Bypass, EstimatedBytes: 0);
                        }
                        else
                        {
                            submissionRead = await submissionReadCache.GetOrLoadAsync(
                                readScope,
                                package.PackageVersion,
                                package.AttemptCount,
                                requiredSubmissionIds,
                                LoadSubmissionRecordsForPackageAsync,
                                operationCancellation.Token);
                        }
                    }
                    finally
                    {
                        readStopwatch.Stop();
                        submissionReadDurationMs = readStopwatch.Elapsed.TotalMilliseconds;
                    }

                    submissionReadsByAssignment[assignmentGroup.AssignmentId] =
                        (submissionRead, submissionReadDurationMs);
                }

                var submissionRecords = submissionRead.Snapshot.Records;
                var build = await BuildPackageAsync(
                    batch,
                    package,
                    groupItems,
                    submissionRecords,
                    warnings,
                    stagedFiles,
                    maxPackageEntryBytes: GetMaxPackageEntryBytes(),
                    connectionKey: connectionKey is null ? null : $"connection:{connectionKey}",
                    cancellationToken: operationCancellation.Token);
                packageBuild = build;
                leaseHeartbeat.ThrowIfLost();
                await EnsureLeaseAsync(package, workerId, operationCancellation.Token);
                var manifestJson = GradingPackageManifestHash.Serialize(build.Manifest);
                var manifestHash = GradingPackageManifestHash.Compute(build.Manifest);
                package.MarkManifest(manifestJson, manifestHash, DateTimeOffset.UtcNow);

                var existingMembers = await packageStore.ListMembersAsync(package.Id, operationCancellation.Token);
                var existingMembersByItemId = existingMembers
                    .ToDictionary(member => member.GradingItemId);
                foreach (var member in build.Members)
                {
                    if (existingMembersByItemId.TryGetValue(member.GradingItemId, out var existingMember))
                    {
                        existingMember.RefreshIdentity(
                            member.MoodleUserId,
                            member.StudentDisplayName,
                            member.SubmissionId,
                            member.AttemptNumber,
                            member.ModifiedAt,
                            member.ContextHash,
                            member.SubmissionContentHash);
                        if (member.Status == GradingEvidencePackageMemberStatus.Included)
                        {
                            existingMember.MarkIncluded();
                        }
                        else
                        {
                            existingMember.MarkFailed(
                                member.IssueCode ?? "package_member_failed",
                                member.IssueMessage ?? "A evidencia nao foi incluida.");
                        }
                    }
                    else
                    {
                        await packageStore.AddMemberAsync(member, operationCancellation.Token);
                    }
                }

                var existingFiles = await packageStore.ListFilesAsync(package.Id, operationCancellation.Token);
                var existingFilesByPath = existingFiles
                    .ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
                foreach (var file in build.Files)
                {
                    if (existingFilesByPath.TryGetValue(file.RelativePath, out var existingFile))
                    {
                        existingFile.RefreshIdentity(
                            file.GradingItemId,
                            file.ArtifactId,
                            file.RelativePath,
                            file.IsContext);
                        existingFile.RefreshMetadata(
                            file.OriginalName,
                            file.MimeType,
                            file.Sha256,
                            file.SizeBytes);
                    }
                    else
                    {
                        await packageStore.AddFileAsync(file, operationCancellation.Token);
                    }
                }

                var maxZipBytes = Math.Max(1L, limits.Value.MaxGradingPackageZipMb) * 1024L * 1024L;
                var maxUncompressedBytes = Math.Max(1L, limits.Value.MaxGradingPackageUncompressedMb) * 1024L * 1024L;
                var archive = await blobStore.WriteAsync(
                    $"packages/{package.Id:N}/attempt-{package.AttemptCount:D6}.zip",
                    async (destination, archiveCancellationToken) =>
                    {
                        await archiveWriter.WriteAsync(
                            destination,
                            build.Entries,
                            new GradingPackageArchiveLimits(
                                Math.Clamp(limits.Value.MaxGradingPackageEntries, 4, 10_000),
                                maxUncompressedBytes,
                                maxZipBytes,
                                GetMaxPackageEntryBytes()),
                            archiveCancellationToken);
                    },
                    operationCancellation.Token);
                writtenArchive = archive;
                leaseHeartbeat.ThrowIfLost();
                await EnsureLeaseAsync(package, workerId, operationCancellation.Token);
                await leaseHeartbeat.StopAsync();
                leaseHeartbeat.ThrowIfLost();
                // O heartbeat usa outro DbContext. Pare-o antes da última
                // renovação para que essa renovação recarregue o token de
                // concorrência no contexto que vai publicar o manifesto e o
                // estado final; sem isso uma renovação concorrente pode fazer
                // o SaveChanges final falhar em preparações longas.
                await EnsureLeaseAsync(package, workerId, operationCancellation.Token);

                // Os arquivos só são evidências importáveis depois que o ZIP
                // foi escrito com êxito. Em uma nova tentativa, o DbContext
                // pode já conter o registro pendente criado pela tentativa
                // anterior; marque o registro existente, em vez de duplicá-lo.
                using (var finalizationScope = scopeFactory.CreateScope())
                {
                    var freshRepository = finalizationScope.ServiceProvider.GetService<IGradingReviewRepository>() ?? repository;
                    foreach (var memberBatchId in groupItems.Select(item => item.BatchId).Append(batch.Id).Distinct())
                    {
                        var freshBatch = await freshRepository.GetBatchAsync(memberBatchId, operationCancellation.Token);
                        if (freshBatch is null || freshBatch.Status == GradingBatchStatus.Cancelled)
                        {
                            package.Cancel();
                            await packageStore.SaveChangesAsync(operationCancellation.Token);
                            await CleanupArchiveAfterFailureAsync(writtenArchive, package);
                            descriptors.Add(ToDescriptor(package));
                            pendingItems.AddRange(groupItems.Select(item => item.Id));
                            warnings.Add("Um sublote foi cancelado durante a preparacao; gere um novo pacote para as entregas ativas.");
                            return new GradingPackagePreparationResult(batchJobId, descriptors, reusedPackageIds,
                                pendingItems.Distinct().ToArray(), warnings);
                        }
                    }
                }

                var archivePaths = build.Files
                    .Select(file => file.RelativePath)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var file in existingFiles.Where(file => archivePaths.Contains(file.RelativePath)))
                {
                    file.MarkIncluded();
                }
                foreach (var file in build.Files)
                {
                    file.MarkIncluded();
                }

                var included = build.Members.Count(member => member.Status == GradingEvidencePackageMemberStatus.Included);
                var pending = build.Members.Count - included;
                if (pending > 0 || build.Issues.Count > 0)
                {
                    package.MarkPartiallyReady(
                        archive.BlobKey,
                        archive.Sha256,
                        archive.SizeBytes,
                        build.Entries.Sum(entry => entry.DeclaredSizeBytes ?? 0),
                        build.Entries.Count,
                        build.Members.Count,
                        included,
                        pending,
                        build.Issues.Count,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddDays(Math.Clamp(limits.Value.RawFileRetentionDays, 1, 365)));
                    pendingItems.AddRange(build.Members
                        .Where(member => member.Status != GradingEvidencePackageMemberStatus.Included)
                        .Select(member => member.GradingItemId));
                }
                else
                {
                    package.MarkReady(
                        archive.BlobKey,
                        archive.Sha256,
                        archive.SizeBytes,
                        build.Entries.Sum(entry => entry.DeclaredSizeBytes ?? 0),
                        build.Entries.Count,
                        build.Members.Count,
                        included,
                        0,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddDays(Math.Clamp(limits.Value.RawFileRetentionDays, 1, 365)));
                }

                leaseHeartbeat.ThrowIfLost();
                await packageStore.SaveChangesAsync(operationCancellation.Token);
                archiveCommitted = true;
                descriptors.Add(ToDescriptor(package));
                telemetry?.RecordPhase(
                    "grading_package",
                    "prepare",
                    package.Status.ToString(),
                    packageStopwatch.Elapsed.TotalMilliseconds,
                    queryCount: 3,
                    itemCount: groupItems.Length,
                    bytes: archive.SizeBytes);
            }
            catch (OperationCanceledException) when (leaseHeartbeat.IsLost)
            {
                retryReason = "lease_lost";
                if (!archiveCommitted)
                {
                    await CleanupArchiveAfterFailureAsync(writtenArchive, package);
                }
                logger.LogWarning("A posse de uma preparacao de pacote foi perdida durante a preparacao.");
                throw new GradingPackageLeaseLostException();
            }
            catch (OperationCanceledException exception)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    retryReason = GetSafeFailureReason(exception);
                }
                throw;
            }
            catch (Exception) when (leaseHeartbeat.IsLost)
            {
                retryReason = "lease_lost";
                if (!archiveCommitted)
                {
                    await CleanupArchiveAfterFailureAsync(writtenArchive, package);
                }
                logger.LogWarning("A posse de uma preparacao de pacote foi perdida durante a finalizacao.");
                throw new GradingPackageLeaseLostException();
            }
            catch (GradingPackageLeaseLostException)
            {
                retryReason = "lease_lost";
                if (!archiveCommitted)
                {
                    await CleanupArchiveAfterFailureAsync(writtenArchive, package);
                }
                logger.LogWarning("A posse de uma preparacao de pacote foi perdida antes da finalizacao.");
                // The DbContext may still track manifest/member changes made
                // before the lease check. Stop this scope so those changes
                // cannot be flushed after another worker takes ownership.
                throw;
            }
            catch (Exception) when (package.Status is GradingEvidencePackageStatus.Ready or
                                               GradingEvidencePackageStatus.PartiallyReady or
                                               GradingEvidencePackageStatus.ResultsReceived)
            {
                retryReason = "finalization_conflict";
                if (!archiveCommitted)
                {
                    await CleanupArchiveAfterFailureAsync(writtenArchive, package);
                }
                logger.LogWarning("A finalizacao de uma preparacao de pacote perdeu a concorrencia; a retomada sera feita pelo polling.");
                throw;
            }
            catch (Exception exception)
            {
                retryReason = GetSafeFailureReason(exception);
                if (!archiveCommitted)
                {
                    await CleanupArchiveAfterFailureAsync(writtenArchive, package);
                }
                var now = DateTimeOffset.UtcNow;
                var maxRetries = Math.Clamp(limits.Value.GradingPackageMaxRetries, 0, 20);
                if (GradingPackageRetryPolicy.IsTransient(exception) && package.AttemptCount <= maxRetries)
                {
                    var delay = GradingPackageRetryPolicy.GetDelay(
                        exception,
                        package.AttemptCount,
                        limits.Value.GradingPackageRetryBaseSeconds);
                    var errorCode = GradingPackageRetryPolicy.IsRateLimited(exception)
                        ? "http_429"
                        : exception is GradingTransientDependencyException transient
                            ? transient.ErrorCode
                            : "package_transient_dependency";
                    package.ScheduleRetry(
                        errorCode,
                        exception.Message,
                        now.Add(delay),
                        now);
                    warnings.Add($"Atividade {assignmentGroup.AssignmentId}: dependência temporariamente indisponível; nova tentativa agendada para {now.Add(delay):O}.");
                }
                else
                {
                    package.MarkNeedsAttention("package_preparation_failed", exception.Message, now);
                    warnings.Add($"Atividade {assignmentGroup.AssignmentId}: pacote nao ficou pronto ({exception.Message}).");
                }
                await packageStore.SaveChangesAsync(cancellationToken);
                pendingItems.AddRange(groupItems.Select(item => item.Id));
                descriptors.Add(ToDescriptor(package));
                telemetry?.RecordPhase(
                    "grading_package",
                    "prepare",
                    package.Status.ToString(),
                    0,
                    queryCount: 1,
                    itemCount: groupItems.Length);
                logger.LogWarning("Uma tentativa de preparacao de pacote terminou com falha recuperavel ou permanente.");
            }
            finally
            {
                packageStopwatch.Stop();
                var artifactReadFailedCount = packageBuild?.Issues.Count(issue =>
                    issue.Code == "artifact_read_failed") ?? 0;
                var contextUnavailableCount = packageBuild?.Issues.Count(issue =>
                    issue.Code is "context_unavailable" or "package_context_unavailable") ?? 0;
                if (retryReason == "context_unavailable")
                {
                    contextUnavailableCount = Math.Max(contextUnavailableCount, groupItems.Length);
                }

                telemetry?.RecordPackageSubmissionRead(
                    submissionRead.CacheStatus.ToString().ToLowerInvariant(),
                    submissionReadDurationMs,
                    submissionRead.Snapshot.Records.Count,
                    submissionRead.EstimatedBytes,
                    submissionGatewayCallCount);
                telemetry?.RecordPackageOutcome(
                    package.Status.ToString(),
                    package.AttemptCount,
                    retryReason,
                    artifactReadFailedCount,
                    contextUnavailableCount);
                var summary = "grading_package_attempt {Status} {PackageVersion} {AttemptCount} {DurationMs} {ItemCount} {SubmissionCount} {SubmissionResponseBytes} {SubmissionReadCache} {SubmissionGatewayCalls} {RetryReason} {ArtifactReadFailedCount} {ContextUnavailableCount} {Http429Count} {TimeoutCount}";
                var http429Count = retryReason == "http_429" ? 1 : 0;
                var timeoutCount = retryReason == "timeout" ? 1 : 0;
                if (package.Status is GradingEvidencePackageStatus.Ready or GradingEvidencePackageStatus.ResultsReceived)
                {
                    logger.LogInformation(summary,
                        package.Status.ToString(), package.PackageVersion, package.AttemptCount,
                        packageStopwatch.Elapsed.TotalMilliseconds, groupItems.Length,
                        submissionRead.Snapshot.Records.Count, submissionRead.EstimatedBytes,
                        submissionRead.CacheStatus.ToString().ToLowerInvariant(), submissionGatewayCallCount,
                        retryReason, artifactReadFailedCount, contextUnavailableCount, http429Count, timeoutCount);
                }
                else
                {
                    logger.LogWarning(summary,
                        package.Status.ToString(), package.PackageVersion, package.AttemptCount,
                        packageStopwatch.Elapsed.TotalMilliseconds, groupItems.Length,
                        submissionRead.Snapshot.Records.Count, submissionRead.EstimatedBytes,
                        submissionRead.CacheStatus.ToString().ToLowerInvariant(), submissionGatewayCallCount,
                        retryReason, artifactReadFailedCount, contextUnavailableCount, http429Count, timeoutCount);
                }
                foreach (var stagedFile in stagedFiles)
                {
                    TryDeleteStagedFile(stagedFile);
                }
            }
        }

        return new GradingPackagePreparationResult(
            batchJobId,
            descriptors,
            reusedPackageIds,
            pendingItems.Distinct().ToArray(),
            warnings);
    }

    private async Task CleanupArchiveAfterFailureAsync(
        GradingPackageBlobDescriptor? archive,
        GradingEvidencePackage package)
    {
        if (archive is null)
        {
            return;
        }

        if (package.Status is GradingEvidencePackageStatus.Ready or
            GradingEvidencePackageStatus.PartiallyReady or
            GradingEvidencePackageStatus.ResultsReceived)
        {
            var persisted = await TryConfirmPublishedArchiveAsync(package.Id, archive);
            if (persisted is true)
            {
                logger.LogInformation(
                    "O ZIP do pacote de correcao permaneceu publicado apos uma resposta ambigua da persistencia.");
                return;
            }

            if (persisted is null)
            {
                // A falha de leitura deixa o resultado da transacao
                // desconhecido. Reter o ZIP e seguro para a correcao; uma
                // limpeza cega poderia remover o transporte ja publicado.
                logger.LogWarning(
                    "Nao foi possivel confirmar o estado persistido de um pacote de correcao; o ZIP sera retido para evitar perda de evidencia.");
                return;
            }
        }

        await DeleteUnpublishedArchiveAsync(archive, package.Id);
    }

    private async Task<bool?> TryConfirmPublishedArchiveAsync(
        Guid packageId,
        GradingPackageBlobDescriptor archive)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IGradingPackageStore>();
            var persisted = await store.GetAsync(packageId, CancellationToken.None);
            if (persisted is null)
            {
                return false;
            }

            var isFinalized = persisted.Status is GradingEvidencePackageStatus.Ready or
                GradingEvidencePackageStatus.PartiallyReady or
                GradingEvidencePackageStatus.ResultsReceived;
            return isFinalized &&
                   string.Equals(persisted.BlobKey, archive.BlobKey, StringComparison.Ordinal) &&
                   string.Equals(persisted.ZipSha256, archive.Sha256, StringComparison.OrdinalIgnoreCase) &&
                   persisted.ZipSizeBytes == archive.SizeBytes;
        }
        catch (Exception)
        {
            logger.LogWarning("Falha ao confirmar a publicacao de um ZIP de pacote de correcao.");
            return null;
        }
    }

    private async Task DeleteUnpublishedArchiveAsync(
        GradingPackageBlobDescriptor? archive,
        Guid packageId)
    {
        if (archive is null)
        {
            return;
        }

        try
        {
            await blobStore.DeleteAsync(archive.BlobKey, CancellationToken.None);
        }
        catch (Exception)
        {
            logger.LogWarning("Nao foi possivel remover um ZIP de pacote de correcao nao publicado.");
        }
    }

    private async Task EnsureLeaseAsync(
        GradingEvidencePackage package,
        string workerId,
        CancellationToken cancellationToken)
    {
        var renewed = await packageStore.RenewClaimAsync(
            package.Id,
            workerId,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(Math.Clamp(limits.Value.BatchLeaseMinutes, 1, 120)),
            cancellationToken);
        if (!renewed)
        {
            throw new GradingPackageLeaseLostException();
        }
    }

    private async Task<GradingPackageBuildResult> BuildPackageAsync(
        AssistedGradingBatch batch,
        GradingEvidencePackage package,
        IReadOnlyCollection<AssistedGradingItem> items,
        IReadOnlyDictionary<(long AssignmentId, string SubmissionId), AssignmentSubmissionRecord> submissionRecords,
        List<string> warnings,
        ConcurrentBag<string> stagedFiles,
        long maxPackageEntryBytes,
        string? connectionKey,
        CancellationToken cancellationToken)
    {
        var packageIssues = new ConcurrentBag<GradingPackageIssue>();
        var sourceItems = new List<GradingPackageSourceItem>();
        var statementSources = new List<GradingPackageSourceFile>();
        var contextSnapshots = await repository.ListLatestContextSnapshotsByItemsAsync(
            items.Select(item => item.Id).ToArray(),
            cancellationToken);
        var contextFailureItemIds = items
            .Where(item => !contextSnapshots.TryGetValue(item.Id, out var snapshot) ||
                           !string.Equals(snapshot.ContextHash, item.ContextHash, StringComparison.OrdinalIgnoreCase) ||
                           !GradingPackageActivityContext.IsSnapshotPayloadValid(snapshot))
            .Select(item => item.Id)
            .ToHashSet();
        var retryableContextFailureCount = items.Count(item =>
            !string.IsNullOrWhiteSpace(item.ContextHash) &&
            contextFailureItemIds.Contains(item.Id));
        if (retryableContextFailureCount > 0 && contextFailureItemIds.Count == items.Count)
        {
            throw new GradingTransientDependencyException(
                "package_context_unavailable",
                $"O snapshot canonico de contexto ainda nao esta disponivel ou esta desatualizado para {retryableContextFailureCount} item(ns); a preparacao sera retomada automaticamente.");
        }
        var artifactsByItem = await repository.ListArtifactsByItemsAsync(
            items.Select(item => item.Id).ToArray(),
            cancellationToken);
        var contextArtifactHashesByItem = items
            .Where(item => !contextFailureItemIds.Contains(item.Id))
            .ToDictionary(
                item => item.Id,
                item => GradingPackageActivityContext.GetContextArtifactHashes(contextSnapshots[item.Id]));
        var contextArtifactConflictsByItem = items.ToDictionary(
            item => item.Id,
            item => contextFailureItemIds.Contains(item.Id)
                ? (IReadOnlySet<Guid>)new HashSet<Guid>()
                : GradingPackageActivityContext.GetConflictingArtifactIds(
                    contextSnapshots[item.Id],
                    artifactsByItem.GetValueOrDefault(item.Id)));
        foreach (var item in items)
        {
            foreach (var artifactId in contextArtifactConflictsByItem.GetValueOrDefault(item.Id) ?? new HashSet<Guid>())
            {
                packageIssues.Add(new GradingPackageIssue(
                    "context_artifact_hash_conflict",
                    "O hash ou o tipo MIME do artefato diverge do snapshot da atividade; o arquivo foi omitido até a integridade ser reconciliada.",
                    item.Id,
                    artifactId));
            }
        }

        var contextHashes = items.Where(item => !contextFailureItemIds.Contains(item.Id) &&
                                                 contextArtifactConflictsByItem.GetValueOrDefault(item.Id)?.Count == 0)
            .Select(item => GradingPackageActivityContext.GetHash(
                               contextSnapshots[item.Id],
                               artifactsByItem.GetValueOrDefault(item.Id)) ?? contextSnapshots[item.Id].ContextHash)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var contextDiverged = contextHashes.Length > 1;
        if (contextDiverged)
        {
            packageIssues.Add(new GradingPackageIssue(
                "context_divergence",
                "Os itens selecionados possuem snapshots canonicos divergentes; o pacote foi mantido parcial para evitar aplicar um enunciado incorreto."));
        }
        using var downloadGate = new SemaphoreSlim(
            Math.Clamp(limits.Value.GradingPackageDownloadConcurrency, 1, 16));
        var contextMaterializations = new ConcurrentDictionary<
            string,
            Lazy<Task<GradingPackageSourceFile?>>>();
        var materializedItems = await Task.WhenAll(items.Select(async item =>
        {
            var submissionRecord = submissionRecords.TryGetValue(
                    (item.AssignmentId, item.SubmissionId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                    out var currentSubmissionRecord)
                ? currentSubmissionRecord
                : null;
            var artifacts = artifactsByItem.GetValueOrDefault(item.Id) ?? [];
            var submissionArtifacts = artifacts
                .Where(artifact => GradingPackageArtifactTypes.IsSubmissionFile(artifact.ArtifactType))
                .ToArray();
            var materializedFiles = await Task.WhenAll(submissionArtifacts.Select(artifact =>
                MaterializeArtifactAsync(
                    batch,
                    item,
                    artifact,
                    isContext: false,
                    expectedContextSha256: null,
                    downloadGate,
                    connectionKey,
                    packageIssues,
                    stagedFiles,
                    maxPackageEntryBytes,
                    cancellationToken)));
            var hasSubmissionArtifactFailure = materializedFiles.Any(file => file is null);
            var expectedSubmissionFileCount = Math.Max(
                submissionArtifacts.Length,
                submissionRecord?.FileCount ?? 0);
            if (submissionRecord is not null && submissionRecord.FileCount > submissionArtifacts.Length)
            {
                hasSubmissionArtifactFailure = true;
                packageIssues.Add(new GradingPackageIssue(
                    "submission_artifact_missing",
                    $"A submissao informa {submissionRecord.FileCount} arquivo(s), mas somente {submissionArtifacts.Length} artifact(s) foram persistidos.",
                    item.Id));
            }

            var contextArtifacts = artifacts
                .Where(artifact => GradingPackageArtifactTypes.IsContext(artifact.ArtifactType) &&
                    !contextArtifactConflictsByItem.GetValueOrDefault(item.Id)!.Contains(artifact.Id))
                .ToArray();
            var materializedContext = await Task.WhenAll(contextArtifacts.Select(artifact =>
            {
                var lazy = contextMaterializations.GetOrAdd(
                    GradingPackageContextIdentity.GetKey(artifact),
                    _ => new Lazy<Task<GradingPackageSourceFile?>>(
                        () => MaterializeArtifactAsync(
                            batch,
                            item,
                            artifact,
                            isContext: true,
                            expectedContextSha256: contextArtifactHashesByItem.GetValueOrDefault(item.Id)
                                ?.GetValueOrDefault(artifact.Id),
                            downloadGate,
                            connectionKey,
                            packageIssues,
                            stagedFiles,
                            maxPackageEntryBytes,
                            cancellationToken)));
                return lazy.Value;
            }));
            var hasContextArtifactFailure = contextArtifactConflictsByItem.GetValueOrDefault(item.Id)?.Count > 0 ||
                                            materializedContext.Any(file => file is null);

            var onlineText = submissionRecord?.OnlineText;
            if (!string.IsNullOrWhiteSpace(onlineText))
            {
                var maxTextChars = Math.Max(1, limits.Value.MaxTextCharsPerSubmission);
                var onlineTextSizeBytes = Encoding.UTF8.GetByteCount(onlineText);
                if (onlineText.Length > maxTextChars || onlineTextSizeBytes > maxPackageEntryBytes)
                {
                    hasSubmissionArtifactFailure = true;
                    packageIssues.Add(new GradingPackageIssue(
                        "online_text_too_large",
                        "O texto on-line da entrega excede o limite de tamanho do pacote.",
                        item.Id));
                    onlineText = null;
                }
            }

            return new MaterializedPackageItem(
                item,
                submissionRecord,
                artifacts,
                materializedFiles.Where(file => file is not null).Select(file => file!).ToArray(),
                materializedContext.Where(file => file is not null).Select(file => file!).ToArray(),
                onlineText,
                expectedSubmissionFileCount,
                hasSubmissionArtifactFailure,
                hasContextArtifactFailure);
        }));

        foreach (var materialized in materializedItems)
        {
            var item = materialized.Item;
            var onlineText = materialized.OnlineText;
            Guid? onlineTextArtifactId = null;
            if (!string.IsNullOrWhiteSpace(onlineText))
            {
                var onlineTextBytes = Encoding.UTF8.GetBytes(onlineText);
                var onlineTextHash = Convert.ToHexString(SHA256.HashData(onlineTextBytes)).ToLowerInvariant();
                var existingOnlineArtifact = FindOnlineTextArtifact(
                    materialized.Artifacts,
                    onlineTextHash);
                if (existingOnlineArtifact is null)
                {
                    existingOnlineArtifact = new GradingArtifact(
                        Guid.NewGuid(),
                        item.Id,
                        "submission_online_text",
                        "texto-online.html",
                        "text/html",
                        onlineTextHash,
                        onlineTextBytes.LongLength,
                        ExtractionStatus.Succeeded,
                        onlineText,
                        "online_text",
                        DateTimeOffset.UtcNow);
                    await repository.AddArtifactAsync(existingOnlineArtifact, cancellationToken);
                }

                onlineTextArtifactId = existingOnlineArtifact.Id;
            }

            var effectiveSubmissionContentHash = item.SubmissionContentHash;
            if (!materialized.HasSubmissionArtifactFailure &&
                materialized.SubmissionRecord is not null &&
                (materialized.Files.Count > 0 || !string.IsNullOrWhiteSpace(onlineText)) &&
                materialized.Files.All(file => !string.IsNullOrWhiteSpace(file.Sha256)))
            {
                effectiveSubmissionContentHash = SubmissionContentHash.Compute(
                    materialized.Files.Select(file => file.Sha256!),
                    onlineText,
                    materialized.SubmissionRecord.AttemptNumber ?? item.AttemptNumber,
                    materialized.SubmissionRecord.ModifiedAt);
                var effectiveSubmissionRevisionHash = SubmissionRevisionHash.TryCompute(
                    materialized.SubmissionRecord,
                    Math.Clamp(limits.Value.MaxFilesPerSubmission, 0, 100),
                    out var computedSubmissionRevisionHash)
                    ? computedSubmissionRevisionHash
                    : null;
                if (!string.Equals(
                        item.SubmissionContentHash,
                        effectiveSubmissionContentHash,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        item.SubmissionRevisionHash,
                        effectiveSubmissionRevisionHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    item.RecordSubmissionIntegrity(
                        effectiveSubmissionContentHash,
                        item.GetSubmissionResourceIds(),
                        effectiveSubmissionRevisionHash);
                }
            }

            statementSources.AddRange(materialized.ContextFiles);
            sourceItems.Add(new GradingPackageSourceItem(
                item.Id,
                item.BatchId,
                batch.GradingRunId,
                item.MoodleUserId,
                item.StudentDisplayName,
                item.SubmissionId,
                item.AttemptNumber,
                materialized.SubmissionRecord?.ModifiedAt,
                item.ContextHash,
                effectiveSubmissionContentHash,
                materialized.Files,
                onlineText,
                onlineTextArtifactId,
                materialized.ExpectedSubmissionFileCount,
                materialized.HasSubmissionArtifactFailure,
                contextFailureItemIds.Contains(item.Id) ||
                contextDiverged ||
                materialized.HasContextArtifactFailure));
        }

        var activity = BuildActivity(batch, items, contextSnapshots, artifactsByItem, useCanonicalContext: !contextDiverged);
        var orderedIssues = packageIssues
            .OrderBy(issue => issue.GradingItemId)
            .ThenBy(issue => issue.ArtifactId)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal)
            .ToArray();
        var result = new GradingPackageContentBuilder().Build(new GradingPackageBuildRequest(
            package,
            activity,
            sourceItems,
            statementSources,
            orderedIssues));
        warnings.AddRange(result.Issues.Select(issue =>
            $"Item {issue.GradingItemId?.ToString() ?? "n/d"}: {issue.Code} — {issue.Message}"));
        return result;
    }

    internal static GradingArtifact? FindOnlineTextArtifact(
        IEnumerable<GradingArtifact> artifacts,
        string currentHash) =>
        artifacts.FirstOrDefault(artifact =>
            GradingPackageArtifactTypes.IsOnlineText(artifact.ArtifactType) &&
            GradingPackageArtifactTypes.IsOnlineTextSummary(artifact.SummaryRef) &&
            string.Equals(artifact.Sha256, currentHash, StringComparison.OrdinalIgnoreCase));

    private async Task<GradingPackageSourceFile?> MaterializeArtifactAsync(
        AssistedGradingBatch batch,
        AssistedGradingItem item,
        GradingArtifact artifact,
        bool isContext,
        string? expectedContextSha256,
        SemaphoreSlim downloadGate,
        string? connectionKey,
        ConcurrentBag<GradingPackageIssue> issues,
        ConcurrentBag<string> stagedFiles,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await downloadGate.WaitAsync(cancellationToken);
        try
        {
            var expectedSha256 = NormalizeSha256(artifact.Sha256);
            var snapshotSha256 = NormalizeSha256(expectedContextSha256);
            if (expectedSha256 is not null && snapshotSha256 is not null &&
                !string.Equals(expectedSha256, snapshotSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("O hash persistido do arquivo diverge do snapshot da atividade.");
            }
            expectedSha256 ??= snapshotSha256;
            var cacheArtifact = expectedSha256 is null
                ? artifact
                : artifact with { Sha256 = expectedSha256 };
            var cacheId = isContext && expectedSha256 is not null
                ? GradingPackageContextIdentity.GetCacheId(batch, item.AssignmentId, cacheArtifact)
                : artifact.Id;
            using var contextCacheLease = isContext && artifactCache is not null && expectedSha256 is not null
                ? await downloadLimiter.AcquireAsync($"context-cache:{cacheId:N}", 1, cancellationToken)
                : null;
            if (artifactCache is not null && expectedSha256 is not null)
            {
                try
                {
                    var cached = await artifactCache.TryOpenReadAsync(
                        cacheId,
                        expectedSha256,
                        artifact.SizeBytes,
                        cancellationToken);
                    if (cached is not null)
                    {
                        await using (cached.Content)
                        {
                            return await StageSourceFileAsync(
                                artifact,
                                isContext,
                                cached.Content,
                                artifact.MimeType,
                                cached.SizeBytes,
                                cached.Sha256,
                                stagedFiles,
                                cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    logger.LogWarning("Nao foi possivel ler o cache do artifact; o download sera repetido.");
                }
            }

            if (!string.IsNullOrWhiteSpace(artifact.SourceUrl))
            {
                var userExternalId = batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture)
                    ?? batch.CreatedBySubject;
                SubmissionFileDownloadResult downloaded;
                using (var connectionLease = await downloadLimiter.AcquireAsync(
                           connectionKey,
                           limits.Value.MaxConcurrentDownloadsPerConnection,
                           cancellationToken))
                using (var batchLease = await downloadLimiter.AcquireAsync(
                           $"batch:{batch.Id:N}",
                           limits.Value.MaxConcurrentDownloadsPerBatch,
                           cancellationToken))
                {
                    downloaded = await submissionFileGateway.DownloadFileAsync(
                        userExternalId,
                        artifact.SourceUrl,
                        artifact.Filename ?? "arquivo",
                        maxBytes,
                        cancellationToken);
                }
                if (downloaded.Truncated || downloaded.SizeBytes > maxBytes ||
                    downloaded.SizeBytes != downloaded.Content.LongLength)
                {
                    throw new InvalidDataException("O arquivo excedeu o limite de tamanho do pacote.");
                }

                var actualSha256 = Convert.ToHexString(SHA256.HashData(downloaded.Content)).ToLowerInvariant();
                if (!string.Equals(actualSha256, NormalizeSha256(downloaded.Sha256Hex), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("O hash informado pelo gateway nao corresponde aos bytes recebidos.");
                }

                if (expectedSha256 is not null &&
                    !string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("O hash do arquivo mudou desde a ingestao.");
                }

                if (artifactCache is not null && expectedSha256 is not null)
                {
                    try
                    {
                        await artifactCache.StoreAsync(
                            cacheId,
                            expectedSha256,
                            downloaded.SizeBytes,
                            (destination, token) => destination.WriteAsync(downloaded.Content, token).AsTask(),
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        logger.LogWarning("Nao foi possivel gravar o cache do artifact; a entrega continuara.");
                    }
                }

                return await StageSourceFileAsync(
                    artifact,
                    isContext,
                    downloaded.Content,
                    downloaded.MimeType,
                    downloaded.SizeBytes,
                    actualSha256,
                    stagedFiles,
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(artifact.ExtractedTextRef))
            {
                var content = Encoding.UTF8.GetBytes(artifact.ExtractedTextRef);
                if (content.LongLength > maxBytes)
                {
                    throw new InvalidDataException("O texto persistido excedeu o limite de tamanho do pacote.");
                }
                var actualSha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (isContext && expectedSha256 is not null &&
                    !string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("O texto persistido diverge do hash do snapshot da atividade.");
                }
                return await StageSourceFileAsync(
                    artifact,
                    isContext,
                    content,
                    artifact.MimeType ?? "text/plain",
                    content.LongLength,
                    actualSha256,
                    stagedFiles,
                    cancellationToken);
            }

            throw new InvalidDataException("O artifact nao possui URL nem texto persistido.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && GradingPackageRetryPolicy.IsTransient(exception))
        {
            issues.Add(new GradingPackageIssue(
                "artifact_read_transient",
                "A leitura de um artifact do pacote falhou temporariamente; o item ficou pendente para nova tentativa.",
                item.Id,
                artifact.Id));
            return null;
        }
        catch (Exception exception)
        {
            issues.Add(new GradingPackageIssue(
                "artifact_read_failed",
                exception.Message,
                item.Id,
                artifact.Id));
            return null;
        }
        finally
        {
            downloadGate.Release();
        }
    }

    private static async Task<GradingPackageSourceFile> StageSourceFileAsync(
        GradingArtifact artifact,
        bool isContext,
        byte[] content,
        string? mimeType,
        long sizeBytes,
        string sha256,
        ConcurrentBag<string> stagedFiles,
        CancellationToken cancellationToken) =>
        await StageSourceFileAsync(
            artifact,
            isContext,
            mimeType,
            sizeBytes,
            sha256,
            stagedFiles,
            (destination, token) => destination.WriteAsync(content, token).AsTask(),
            cancellationToken);

    private static async Task<GradingPackageSourceFile> StageSourceFileAsync(
        GradingArtifact artifact,
        bool isContext,
        Stream content,
        string? mimeType,
        long sizeBytes,
        string sha256,
        ConcurrentBag<string> stagedFiles,
        CancellationToken cancellationToken) =>
        await StageSourceFileAsync(
            artifact,
            isContext,
            mimeType,
            sizeBytes,
            sha256,
            stagedFiles,
            (destination, token) => content.CopyToAsync(destination, 64 * 1024, token),
            cancellationToken);

    private static async Task<GradingPackageSourceFile> StageSourceFileAsync(
        GradingArtifact artifact,
        bool isContext,
        string? mimeType,
        long sizeBytes,
        string sha256,
        ConcurrentBag<string> stagedFiles,
        Func<Stream, CancellationToken, Task> copyContent,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"moodle-grading-package-{Guid.NewGuid():N}.bin");
        try
        {
            await using (var file = new FileStream(
                             path,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await copyContent(file, cancellationToken);
            }

            stagedFiles.Add(path);
            return new GradingPackageSourceFile(
                artifact.Id,
                isContext ? null : artifact.GradingItemId,
                artifact.Filename,
                mimeType,
                sha256,
                sizeBytes,
                isContext,
                (destination, token) => CopyStagedFileAsync(path, destination, token));
        }
        catch
        {
            TryDeleteStagedFile(path);
            throw;
        }
    }

    private static async Task CopyStagedFileAsync(
        string path,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 64 * 1024, cancellationToken);
    }

    private static void TryDeleteStagedFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A failed cleanup must not hide the package result.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup must not hide the package result.
        }
    }

    private async Task<GradingPackageSubmissionReadSnapshot> LoadSubmissionRecordsAsync(
        AssistedGradingBatch batch,
        IReadOnlyCollection<AssistedGradingItem> items,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var assignmentIds = items
            .Select(item => item.AssignmentId.ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (assignmentIds.Length == 0)
        {
            return new GradingPackageSubmissionReadSnapshot(
                new Dictionary<(long, string), AssignmentSubmissionRecord>(), IsComplete: true);
        }

        var userExternalId = batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture)
            ?? batch.CreatedBySubject;
        IReadOnlyList<AssignmentSubmissionsBatch> batches;
        try
        {
            batches = await submissionsGateway.GetAssignmentSubmissionsBatchAsync(
                userExternalId,
                assignmentIds,
                status: null,
                since: null,
                before: null,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && GradingPackageRetryPolicy.IsTransient(exception))
        {
            throw new GradingTransientDependencyException(
                "package_submission_read_transient",
                "A leitura das submissões para texto on-line falhou temporariamente.",
                exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"Texto on-line nao foi consultado nesta tentativa: {exception.Message}");
            return new GradingPackageSubmissionReadSnapshot(
                new Dictionary<(long, string), AssignmentSubmissionRecord>(), IsComplete: false);
        }

        var result = new Dictionary<(long, string), AssignmentSubmissionRecord>();
        var expectedAssignmentIds = assignmentIds
            .Select(value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (long?)null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToHashSet();
        var seenAssignmentIds = new HashSet<long>();
        var isComplete = true;
        foreach (var submissionBatch in batches)
        {
            if (!long.TryParse(submissionBatch.AssignmentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var assignmentId))
            {
                warnings.Add("Texto on-line ignorado para uma atividade com identificador invalido.");
                isComplete = false;
                continue;
            }

            if (!seenAssignmentIds.Add(assignmentId))
            {
                isComplete = false;
            }

            if (!string.IsNullOrWhiteSpace(submissionBatch.ErrorCode))
            {
                warnings.Add($"Atividade {assignmentId}: o texto on-line nao foi lido ({submissionBatch.ErrorCode}).");
                isComplete = false;
                continue;
            }

            foreach (var record in submissionBatch.Submissions)
            {
                result[(assignmentId, record.SubmissionId)] = record;
            }
        }

        if (!expectedAssignmentIds.SetEquals(seenAssignmentIds))
        {
            isComplete = false;
        }

        return new GradingPackageSubmissionReadSnapshot(result, isComplete);
    }

    private static string GetSafeFailureReason(Exception exception)
    {
        if (GradingPackageRetryPolicy.IsRateLimited(exception))
        {
            return "http_429";
        }

        if (exception is GradingTransientDependencyException transient)
        {
            if (transient.ErrorCode.Contains("context_unavailable", StringComparison.OrdinalIgnoreCase))
            {
                return "context_unavailable";
            }
            if (transient.ErrorCode.Contains("artifact", StringComparison.OrdinalIgnoreCase))
            {
                return "artifact_read_failed";
            }
            if (transient.InnerException is not null)
            {
                return GetSafeFailureReason(transient.InnerException);
            }
        }

        if (exception is HttpRequestException httpException && (int?)httpException.StatusCode == 429)
        {
            return "http_429";
        }
        if (exception is TimeoutException or TaskCanceledException)
        {
            return "timeout";
        }
        if (exception is GradingPackageLeaseLostException)
        {
            return "lease_lost";
        }

        return GradingPackageRetryPolicy.IsTransient(exception)
            ? "transient_dependency"
            : "package_failure";
    }

    private static GradingPackageActivity BuildActivity(
        AssistedGradingBatch batch,
        IReadOnlyCollection<AssistedGradingItem> items,
        IReadOnlyDictionary<Guid, GradingContextSnapshotDocument> snapshots,
        IReadOnlyDictionary<Guid, IReadOnlyList<GradingArtifact>> artifactsByItem,
        bool useCanonicalContext)
    {
        var snapshotItem = useCanonicalContext
            ? items
                .Where(item => snapshots.TryGetValue(item.Id, out var value) &&
                    GradingPackageMemberScope.HashesEqual(item.ContextHash, value.ContextHash))
                .FirstOrDefault()
            : null;
        var snapshot = snapshotItem is null ? null : snapshots.GetValueOrDefault(snapshotItem.Id);
        var payload = snapshot is null
            ? null
            : TryParseSnapshotPayload(snapshot.PayloadJson);
        var maxGrade = TryGetDecimal(payload, "GradingScale", "MaximumGrade");
        var gradingMode = TryGetString(payload, "GradingScale", "GradingMode") ?? "unknown";
        var criteria = TryGetArray(payload, "Criteria", "Description");
        var assignmentId = items.FirstOrDefault()?.AssignmentId ?? batch.AssignmentIds.First();
        return new GradingPackageActivity(
            batch.CourseId,
            assignmentId,
            TryGetDecimal(payload, "Assignment", "CourseModuleId")?.ToString(CultureInfo.InvariantCulture),
            gradingMode,
            maxGrade,
            [],
            criteria,
            batch.TeacherInstructions,
            TryGetRootString(payload, "AssignmentStatement"),
            snapshot is null
                ? null
                : GradingPackageActivityContext.GetHash(snapshot, artifactsByItem.GetValueOrDefault(snapshotItem!.Id)),
            snapshot?.Version)
        {
            InstanceId = assignmentId,
            ActivityName = TryGetRootString(payload, "ActivityName"),
            Rubric = TryGetPayload<GradingRubricSnapshot>(payload, "Rubric"),
            CriterionDetails = TryGetPayload<GradingCriterionSnapshot[]>(payload, "Criteria")
        };
    }

    private static T? TryGetPayload<T>(JsonElement? root, string property) where T : class
    {
        if (root is not JsonElement value || !value.TryGetProperty(property, out var payload)) return null;
        try { return payload.Deserialize<T>(); }
        catch (JsonException) { return null; }
    }

    private static JsonElement? TryParseSnapshotPayload(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal? TryGetDecimal(JsonElement? root, string parent, string property)
    {
        if (root is not JsonElement value ||
            !value.TryGetProperty(parent, out var parentValue) ||
            !parentValue.TryGetProperty(property, out var propertyValue) ||
            propertyValue.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return propertyValue.TryGetDecimal(out var decimalValue) ? decimalValue : null;
    }

    private static string? TryGetString(JsonElement? root, string parent, string property)
    {
        if (root is not JsonElement value ||
            !value.TryGetProperty(parent, out var parentValue) ||
            !parentValue.TryGetProperty(property, out var propertyValue))
        {
            return null;
        }

        return propertyValue.ValueKind == JsonValueKind.String ? propertyValue.GetString() : null;
    }

    private static string? TryGetRootString(JsonElement? root, string property)
    {
        if (root is not JsonElement value ||
            !value.TryGetProperty(property, out var propertyValue))
        {
            return null;
        }

        return propertyValue.ValueKind == JsonValueKind.String ? propertyValue.GetString() : null;
    }

    private static IReadOnlyList<string> TryGetArray(JsonElement? root, string arrayProperty, string valueProperty)
    {
        if (root is not JsonElement value ||
            !value.TryGetProperty(arrayProperty, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(element => element.TryGetProperty(valueProperty, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToArray();
    }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return null;
        }

        try
        {
            _ = Convert.FromHexString(value);
            return value.ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private long GetMaxPackageEntryBytes()
    {
        var maxFileBytes = Math.Max(1L, limits.Value.MaxFileSizeMb) * 1024L * 1024L;
        var maxZipBytes = Math.Max(1L, limits.Value.MaxGradingPackageZipMb) * 1024L * 1024L;
        return Math.Max(1L, Math.Min(maxFileBytes, maxZipBytes - 64L * 1024L));
    }

    private static GradingPackageDescriptor ToDescriptor(GradingEvidencePackage package) =>
        GradingPackageDescriptorMapper.ToDescriptor(package);

    private sealed record GradingPackageItemGroup(
        long AssignmentId,
        IReadOnlyList<AssistedGradingItem> Items);

    private sealed record MaterializedPackageItem(
        AssistedGradingItem Item,
        AssignmentSubmissionRecord? SubmissionRecord,
        IReadOnlyList<GradingArtifact> Artifacts,
        IReadOnlyList<GradingPackageSourceFile> Files,
        IReadOnlyList<GradingPackageSourceFile> ContextFiles,
        string? OnlineText,
        int ExpectedSubmissionFileCount,
        bool HasSubmissionArtifactFailure,
        bool HasContextArtifactFailure);

    private sealed class GradingPackageLeaseHeartbeat : IAsyncDisposable
    {
        private readonly Guid packageId;
        private readonly string workerId;
        private readonly TimeSpan leaseDuration;
        private readonly IServiceScopeFactory scopeFactory;
        private readonly CancellationTokenSource operationCancellation;
        private readonly ILogger<GradingPackageWorker> logger;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private int lost;

        public GradingPackageLeaseHeartbeat(
            Guid packageId,
            string workerId,
            TimeSpan leaseDuration,
            IServiceScopeFactory scopeFactory,
            CancellationTokenSource operationCancellation,
            ILogger<GradingPackageWorker> logger)
        {
            this.packageId = packageId;
            this.workerId = workerId;
            this.leaseDuration = leaseDuration;
            this.scopeFactory = scopeFactory;
            this.operationCancellation = operationCancellation;
            this.logger = logger;
            loop = RunAsync();
        }

        public bool IsLost => Volatile.Read(ref lost) == 1;

        public void ThrowIfLost()
        {
            if (IsLost)
            {
                throw new GradingPackageLeaseLostException();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            stop.Dispose();
        }

        public async Task StopAsync()
        {
            stop.Cancel();
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation only stops the heartbeat; the owner already
                // controls the outcome of the package operation.
            }
        }

        private async Task RunAsync()
        {
            var interval = TimeSpan.FromMilliseconds(
                Math.Clamp(leaseDuration.TotalMilliseconds / 3d, 1_000d, 30_000d));
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var store = scope.ServiceProvider.GetRequiredService<IGradingPackageStore>();
                        var renewed = await store.RenewClaimAsync(
                            packageId,
                            workerId,
                            DateTimeOffset.UtcNow,
                            leaseDuration,
                            stop.Token).ConfigureAwait(false);
                        if (!renewed)
                        {
                            MarkLost("A reserva do pacote nao pertence mais a esta execucao.");
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        MarkLost("A renovacao da reserva falhou.");
                        logger.LogWarning("Falha ao renovar a reserva de uma preparacao de pacote.");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }

        private void MarkLost(string reason)
        {
            if (Interlocked.Exchange(ref lost, 1) == 0)
            {
                logger.LogWarning("A posse de uma preparacao de pacote foi perdida: {Reason}", reason);
                operationCancellation.Cancel();
            }
        }
    }

    private sealed class GradingPackageLeaseLostException : Exception
    {
    }
}
