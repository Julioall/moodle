using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading.Packages;
using Moodle.Domain.Grading;

namespace Moodle.Infrastructure;

/// <summary>
/// Store durável dos pacotes. O manifesto e os membros ficam no banco; os
/// bytes do ZIP serão tratados por um blob store separado na etapa seguinte.
/// </summary>
public sealed class GradingPackageStore(
    ConnectorDbContext dbContext,
    IGradingPackageBlobStore? blobStore = null) : IGradingPackageStore
{
    public Task<GradingEvidencePackage?> GetAsync(
        Guid packageId,
        CancellationToken cancellationToken) =>
        dbContext.GradingEvidencePackages
            .SingleOrDefaultAsync(package => package.Id == packageId, cancellationToken);

    public async Task<GradingEvidencePackage?> FindReusableAsync(
        GradingPackageIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var now = DateTimeOffset.UtcNow;
        var candidate = await dbContext.GradingEvidencePackages
            .Where(package =>
                package.OwnerSubject == identity.OwnerSubject &&
                package.PreparationKey == identity.PreparationKey &&
                package.Status != GradingEvidencePackageStatus.Cancelled &&
                (package.Status != GradingEvidencePackageStatus.Ready &&
                 package.Status != GradingEvidencePackageStatus.PartiallyReady &&
                 package.Status != GradingEvidencePackageStatus.ResultsReceived ||
                 (package.BlobKey != null &&
                 package.BlobKey != string.Empty &&
                  (package.ExpiresAt == null || package.ExpiresAt > now))))
            .OrderByDescending(package => package.PackageVersion)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is not null)
        {
            return await EnsureTransportIsAvailableAsync(candidate, cancellationToken);
        }

        // The worker may enrich an item with its content hash only after the
        // package key was reserved. Reconcile that one-way enrichment using
        // the persisted member identities instead of creating a duplicate
        // package on the next prepare call. A real content/context change does
        // not match and still receives a new package version.
        var enrichedCandidates = await dbContext.GradingEvidencePackages
            .Where(package =>
                package.OwnerSubject == identity.OwnerSubject &&
                package.ActivityKey == identity.ActivityKey &&
                package.PreparationKey != identity.PreparationKey &&
                package.Status != GradingEvidencePackageStatus.Cancelled &&
                (package.Status != GradingEvidencePackageStatus.Ready &&
                 package.Status != GradingEvidencePackageStatus.PartiallyReady &&
                 package.Status != GradingEvidencePackageStatus.ResultsReceived ||
                 (package.BlobKey != null &&
                  package.BlobKey != string.Empty &&
                  (package.ExpiresAt == null || package.ExpiresAt > now))))
            .OrderByDescending(package => package.PackageVersion)
            .ToArrayAsync(cancellationToken);

        var candidateIds = enrichedCandidates.Select(package => package.Id).ToArray();
        var membersByPackage = (await dbContext.GradingEvidencePackageMembers
                .Where(member => candidateIds.Contains(member.PackageId))
                .ToArrayAsync(cancellationToken))
            .GroupBy(member => member.PackageId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<GradingEvidencePackageMember>)group.ToArray());
        foreach (var enrichedCandidate in enrichedCandidates)
        {
            if (!membersByPackage.TryGetValue(enrichedCandidate.Id, out var members) ||
                !MatchesItemIdentities(members, identity.Items))
            {
                continue;
            }

            var reusable = await EnsureTransportIsAvailableAsync(enrichedCandidate, cancellationToken);
            if (reusable is not null)
            {
                return reusable;
            }
        }

        // A transitional identity may now include the alias because older
        // batches did not persist a stable connection/client id. Reuse a
        // matching legacy package instead of forcing a second analysis; the
        // member hashes and alias still have to agree before it is returned.
        if (identity.UsesAliasFallback)
        {
            var legacyCandidates = await dbContext.GradingEvidencePackages
                .Where(package =>
                    package.OwnerSubject == identity.OwnerSubject &&
                    package.ActivityKey == identity.LegacyActivityKey &&
                    package.ConnectionAlias == identity.ConnectionAlias &&
                    package.Status != GradingEvidencePackageStatus.Cancelled &&
                    (package.Status != GradingEvidencePackageStatus.Ready &&
                     package.Status != GradingEvidencePackageStatus.PartiallyReady &&
                     package.Status != GradingEvidencePackageStatus.ResultsReceived ||
                     (package.BlobKey != null &&
                      package.BlobKey != string.Empty &&
                      (package.ExpiresAt == null || package.ExpiresAt > now))))
                .OrderByDescending(package => package.PackageVersion)
                .ToArrayAsync(cancellationToken);

            var legacyCandidateIds = legacyCandidates.Select(package => package.Id).ToArray();
            var legacyMembersByPackage = (await dbContext.GradingEvidencePackageMembers
                    .Where(member => legacyCandidateIds.Contains(member.PackageId))
                    .ToArrayAsync(cancellationToken))
                .GroupBy(member => member.PackageId)
                .ToDictionary(group => group.Key, group => (IReadOnlyCollection<GradingEvidencePackageMember>)group.ToArray());
            foreach (var legacyCandidate in legacyCandidates)
            {
                if (!legacyMembersByPackage.TryGetValue(legacyCandidate.Id, out var members) ||
                    !MatchesItemIdentities(members, identity.Items))
                {
                    continue;
                }

                var reusable = await EnsureTransportIsAvailableAsync(legacyCandidate, cancellationToken);
                if (reusable is not null)
                {
                    return reusable;
                }
            }
        }

        return null;
    }

    private async Task<GradingEvidencePackage?> EnsureTransportIsAvailableAsync(
        GradingEvidencePackage candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.Status is not (GradingEvidencePackageStatus.Ready or
            GradingEvidencePackageStatus.PartiallyReady or
            GradingEvidencePackageStatus.ResultsReceived))
        {
            return candidate;
        }

        if (blobStore is null)
        {
            return candidate;
        }

        try
        {
            await using var stream = await blobStore.OpenReadAsync(candidate.BlobKey!, cancellationToken);
            return candidate;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            // A falha de storage pode ser transitória. Não bloqueie uma nova
            // preparação nem reescreva o pacote anterior; a chave de versão
            // permite gerar um transporte substituto preservando o histórico.
            return null;
        }
    }

    private static bool MatchesItemIdentities(
        IReadOnlyCollection<GradingEvidencePackageMember> members,
        IReadOnlyCollection<GradingPackageItemIdentity> items)
    {
        if (members.Count != items.Count)
        {
            return false;
        }

        var membersByItemId = members.ToDictionary(member => member.GradingItemId);
        return items.All(item =>
            membersByItemId.TryGetValue(item.GradingItemId, out var member) &&
            HashesEqual(member.ContextHash, item.ContextHash) &&
            HashesEqual(member.SubmissionContentHash, item.SubmissionContentHash));
    }

    private static bool HashesEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public async Task<GradingEvidencePackage> GetOrCreateAsync(
        GradingPackageIdentity identity,
        Guid? batchJobId,
        CancellationToken cancellationToken)
    {
        var result = await AcquireAsync(identity, batchJobId, cancellationToken);
        return result.Package;
    }

    public async Task<GradingPackageAcquireResult> AcquireAsync(
        GradingPackageIdentity identity,
        Guid? batchJobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return await WithActivityVersionLockAsync(
            identity,
            cancellationToken,
            () => AcquireWithoutLockAsync(identity, batchJobId, cancellationToken));
    }

    private async Task<TResult> WithActivityVersionLockAsync<TResult>(
        GradingPackageIdentity identity,
        CancellationToken cancellationToken,
        Func<Task<TResult>> action)
    {
        if (!IsPostgres)
        {
            return await action();
        }

        if (dbContext.Database.CurrentTransaction is not null)
        {
            await AcquirePostgresLockAsync(identity, cancellationToken);
            return await action();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquirePostgresLockAsync(identity, cancellationToken);
        var result = await action();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<GradingPackageAcquireResult> AcquireWithoutLockAsync(
        GradingPackageIdentity identity,
        Guid? batchJobId,
        CancellationToken cancellationToken)
    {
        var reusable = await FindReusableAsync(identity, cancellationToken);
        if (reusable is not null)
        {
            return new GradingPackageAcquireResult(reusable, WasExisting: true);
        }

        var (package, wasExisting) = await CreateVersionAsync(
            identity, batchJobId, cancellationToken, reuseOnConflict: true);
        return new GradingPackageAcquireResult(package, wasExisting);
    }

    private async Task AcquirePostgresLockAsync(
        GradingPackageIdentity identity,
        CancellationToken cancellationToken)
    {
        var activityKeys = identity.UsesAliasFallback
            ? new[] { identity.ActivityKey, identity.LegacyActivityKey }
            : [identity.ActivityKey];
        var lockKeys = activityKeys
            .Distinct(StringComparer.Ordinal)
            .Select(activityKey =>
            {
                var lockIdentity = Encoding.UTF8.GetBytes($"{identity.OwnerSubject}\0{activityKey}");
                return BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(lockIdentity));
            })
            .Distinct()
            .Order()
            .ToArray();

        foreach (var lockKey in lockKeys)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})",
                cancellationToken);
        }
    }

    public async Task<GradingEvidencePackage> CreateNextVersionAsync(
        GradingPackageIdentity identity,
        Guid? batchJobId,
        CancellationToken cancellationToken) =>
        await WithActivityVersionLockAsync(
            identity,
            cancellationToken,
            async () => (await CreateVersionAsync(
                identity,
                batchJobId,
                cancellationToken,
                reuseOnConflict: true)).Package);

    private async Task<(GradingEvidencePackage Package, bool WasExisting)> CreateVersionAsync(
        GradingPackageIdentity identity,
        Guid? batchJobId,
        CancellationToken cancellationToken,
        bool reuseOnConflict)
    {
        // The preparation key reuses the same evidence while its transport is
        // valid. A new version is created when a previous ZIP expired or was
        // removed; the version key preserves that history. A concurrent
        // version conflict is retried after reload.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var activityKeys = identity.UsesAliasFallback
                ? new[] { identity.ActivityKey, identity.LegacyActivityKey }
                : [identity.ActivityKey];
            var currentVersion = await dbContext.GradingEvidencePackages
                .Where(package =>
                    package.OwnerSubject == identity.OwnerSubject &&
                    activityKeys.Contains(package.ActivityKey))
                .Select(package => (int?)package.PackageVersion)
                .MaxAsync(cancellationToken) ?? 0;
            var package = GradingEvidencePackage.Create(
                identity.OwnerSubject,
                identity.MoodleConnectionId,
                identity.ConnectorClientId,
                identity.ConnectionAlias,
                identity.CourseId,
                identity.AssignmentId,
                identity.Cmid,
                currentVersion + 1,
                identity.PreparationKey,
                identity.ActivityKey,
                batchJobId,
                DateTimeOffset.UtcNow);
            await dbContext.GradingEvidencePackages.AddAsync(package, cancellationToken);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return (package, WasExisting: false);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // Only the just-attempted package row lost the uniqueness
                // race. Clearing the whole tracker would also discard
                // members/files from earlier package groups that are still
                // pending in this unit of work.
                dbContext.Entry(package).State = EntityState.Detached;
                if (reuseOnConflict)
                {
                    var reusable = await FindReusableAsync(identity, cancellationToken);
                    if (reusable is not null)
                    {
                        return (reusable, WasExisting: true);
                    }
                }
            }
        }

        throw new InvalidOperationException("Nao foi possivel reservar uma versao unica do pacote de correcao.");
    }

    public async Task AddMemberAsync(
        GradingEvidencePackageMember member,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        await dbContext.GradingEvidencePackageMembers.AddAsync(member, cancellationToken);
    }

    public async Task<IReadOnlyList<GradingEvidencePackageMember>> ListMembersAsync(
        Guid packageId,
        CancellationToken cancellationToken) =>
        await dbContext.GradingEvidencePackageMembers
            .Where(member => member.PackageId == packageId)
            .OrderBy(member => member.CreatedAt)
            .ThenBy(member => member.Id)
            .ToArrayAsync(cancellationToken);

    public async Task AddFileAsync(
        GradingEvidencePackageFile file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        await dbContext.GradingEvidencePackageFiles.AddAsync(file, cancellationToken);
    }

    public async Task<IReadOnlyList<GradingEvidencePackageFile>> ListFilesAsync(
        Guid packageId,
        CancellationToken cancellationToken) =>
        await dbContext.GradingEvidencePackageFiles
            .Where(file => file.PackageId == packageId)
            .OrderBy(file => file.RelativePath)
            .ThenBy(file => file.Id)
            .ToArrayAsync(cancellationToken);

    public async Task<bool> TryClaimAsync(
        Guid packageId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (packageId == Guid.Empty || string.IsNullOrWhiteSpace(workerId) || leaseDuration <= TimeSpan.Zero)
        {
            return false;
        }

        var normalizedWorkerId = workerId.Trim();
        if (IsInMemory)
        {
            var package = await GetAsync(packageId, cancellationToken);
            if (package is null || !package.TryAcquireLease(normalizedWorkerId, now, leaseDuration))
            {
                return false;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        var updated = await dbContext.GradingEvidencePackages
            .Where(package =>
                package.Id == packageId &&
                package.Status != GradingEvidencePackageStatus.Ready &&
                package.Status != GradingEvidencePackageStatus.PartiallyReady &&
                package.Status != GradingEvidencePackageStatus.ResultsReceived &&
                package.Status != GradingEvidencePackageStatus.Cancelled &&
                (package.NextAttemptAt == null || package.NextAttemptAt <= now) &&
                (package.LeaseUntil == null || package.LeaseUntil <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(package => package.Status, GradingEvidencePackageStatus.Preparing)
                .SetProperty(package => package.LeaseOwner, normalizedWorkerId)
                .SetProperty(package => package.LeaseUntil, now.Add(leaseDuration))
                .SetProperty(package => package.AttemptCount, package => package.AttemptCount + 1)
                .SetProperty(package => package.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(package => package.UpdatedAt, now), cancellationToken);
        if (updated == 1)
        {
            var tracked = dbContext.ChangeTracker.Entries<GradingEvidencePackage>()
                .SingleOrDefault(entry => entry.Entity.Id == packageId);
            if (tracked is not null)
            {
                await tracked.ReloadAsync(cancellationToken);
            }
        }

        return updated == 1;
    }

    public async Task<bool> RenewClaimAsync(
        Guid packageId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (packageId == Guid.Empty || string.IsNullOrWhiteSpace(workerId) || leaseDuration <= TimeSpan.Zero)
        {
            return false;
        }

        var normalizedWorkerId = workerId.Trim();
        if (IsInMemory)
        {
            var package = await GetAsync(packageId, cancellationToken);
            if (package is null || !package.RenewLease(normalizedWorkerId, now, leaseDuration))
            {
                return false;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        var updated = await dbContext.GradingEvidencePackages
            .Where(package =>
                package.Id == packageId &&
                package.Status == GradingEvidencePackageStatus.Preparing &&
                package.LeaseOwner == normalizedWorkerId &&
                package.LeaseUntil != null && package.LeaseUntil > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(package => package.LeaseUntil, now.Add(leaseDuration))
                .SetProperty(package => package.UpdatedAt, now), cancellationToken);
        if (updated == 1)
        {
            var tracked = dbContext.ChangeTracker.Entries<GradingEvidencePackage>()
                .SingleOrDefault(entry => entry.Entity.Id == packageId);
            if (tracked is not null)
            {
                // Preserve staged manifest/member changes; refresh only the
                // original values needed by optimistic concurrency.
                var current = await dbContext.GradingEvidencePackages
                    .AsNoTracking()
                    .Where(package => package.Id == packageId)
                    .Select(package => new { package.UpdatedAt, package.LeaseUntil })
                    .SingleAsync(cancellationToken);
                tracked.Property(package => package.UpdatedAt).OriginalValue = current.UpdatedAt;
                tracked.Property(package => package.LeaseUntil).OriginalValue = current.LeaseUntil;
            }
        }

        return updated == 1;
    }

    public async Task<bool> ReleaseClaimAsync(
        Guid packageId,
        string workerId,
        DateTimeOffset now,
        string? errorCode,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken)
    {
        if (packageId == Guid.Empty || string.IsNullOrWhiteSpace(workerId))
        {
            return false;
        }

        var normalizedWorkerId = workerId.Trim();
        if (IsInMemory)
        {
            var package = await GetAsync(packageId, cancellationToken);
            if (package is null || !package.ReleaseLease(normalizedWorkerId, now, errorCode, nextAttemptAt))
            {
                return false;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        var updated = await dbContext.GradingEvidencePackages
            .Where(package => package.Id == packageId && package.LeaseOwner == normalizedWorkerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(package => package.LeaseOwner, (string?)null)
                .SetProperty(package => package.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(package => package.LastErrorCode, Normalize(errorCode))
                .SetProperty(package => package.NextAttemptAt, nextAttemptAt)
                .SetProperty(package => package.Status,
                    nextAttemptAt is null
                        ? GradingEvidencePackageStatus.Preparing
                        : GradingEvidencePackageStatus.Queued)
                .SetProperty(package => package.UpdatedAt, now), cancellationToken);
        return updated == 1;
    }

    public async Task<int> RecoverExpiredClaimsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (IsInMemory)
        {
            var packages = await dbContext.GradingEvidencePackages
                .Where(package => package.Status == GradingEvidencePackageStatus.Preparing &&
                                  package.LeaseUntil != null && package.LeaseUntil <= now)
                .ToArrayAsync(cancellationToken);
            var recovered = packages.Count(package => package.RecoverExpiredLease(now));
            if (recovered > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return recovered;
        }

        return await dbContext.GradingEvidencePackages
            .Where(package => package.Status == GradingEvidencePackageStatus.Preparing &&
                              package.LeaseUntil != null && package.LeaseUntil <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(package => package.Status, GradingEvidencePackageStatus.Queued)
                .SetProperty(package => package.LeaseOwner, (string?)null)
                .SetProperty(package => package.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(package => package.NextAttemptAt, now)
                .SetProperty(package => package.UpdatedAt, now), cancellationToken);
    }

    public async Task<IReadOnlyList<GradingEvidencePackage>> ListExpiredBlobsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await dbContext.GradingEvidencePackages
            .Where(package => package.ExpiresAt != null &&
                              package.ExpiresAt <= now &&
                              package.BlobKey != null)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<GradingEvidencePackage>> ListDueAsync(
        DateTimeOffset now,
        int maxPackages,
        CancellationToken cancellationToken) =>
        await dbContext.GradingEvidencePackages
            .Where(package => package.BatchJobId != null &&
                              package.Status == GradingEvidencePackageStatus.Queued &&
                              (package.NextAttemptAt == null || package.NextAttemptAt <= now) &&
                              (package.LeaseUntil == null || package.LeaseUntil <= now))
            .OrderBy(package => package.NextAttemptAt)
            .ThenBy(package => package.CreatedAt)
            .Take(Math.Clamp(maxPackages, 1, 100))
            .ToArrayAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);

    private bool IsInMemory => string.Equals(
        dbContext.Database.ProviderName,
        "Microsoft.EntityFrameworkCore.InMemory",
        StringComparison.Ordinal);

    private bool IsPostgres => string.Equals(
        dbContext.Database.ProviderName,
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        StringComparison.Ordinal);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(120, value.Trim().Length)];

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
