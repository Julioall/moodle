using Moodle.Domain.Grading;
using Moodle.Application.Grading.Packages;

namespace Moodle.Application.Abstractions;

public interface IGradingPackageStore
{
    Task<GradingEvidencePackage?> GetAsync(Guid packageId, CancellationToken cancellationToken);

    Task<GradingEvidencePackage?> FindReusableAsync(
        GradingPackageIdentity identity,
        CancellationToken cancellationToken);

    Task<GradingEvidencePackage> GetOrCreateAsync(
        GradingPackageIdentity identity,
        Guid? batchJobId,
        CancellationToken cancellationToken);

    async Task<GradingPackageAcquireResult> AcquireAsync(
        GradingPackageIdentity identity,
        Guid? batchJobId,
        CancellationToken cancellationToken)
    {
        var reusable = await FindReusableAsync(identity, cancellationToken);
        if (reusable is not null)
        {
            return new GradingPackageAcquireResult(reusable, WasExisting: true);
        }

        // Compatibility fallback for alternate stores. The durable production
        // store overrides this method so its common path performs one lookup.
        var package = await GetOrCreateAsync(identity, batchJobId, cancellationToken);
        return new GradingPackageAcquireResult(package, WasExisting: false);
    }

    Task<GradingEvidencePackage> CreateNextVersionAsync(
        GradingPackageIdentity identity,
        Guid? batchJobId,
        CancellationToken cancellationToken);

    Task AddMemberAsync(
        GradingEvidencePackageMember member,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GradingEvidencePackageMember>> ListMembersAsync(
        Guid packageId,
        CancellationToken cancellationToken);

    Task AddFileAsync(
        GradingEvidencePackageFile file,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GradingEvidencePackageFile>> ListFilesAsync(
        Guid packageId,
        CancellationToken cancellationToken);

    Task<bool> TryClaimAsync(
        Guid packageId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> RenewClaimAsync(
        Guid packageId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> ReleaseClaimAsync(
        Guid packageId,
        string workerId,
        DateTimeOffset now,
        string? errorCode,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken);

    Task<int> RecoverExpiredClaimsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GradingEvidencePackage>> ListExpiredBlobsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GradingEvidencePackage>> ListDueAsync(
        DateTimeOffset now,
        int maxPackages,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
