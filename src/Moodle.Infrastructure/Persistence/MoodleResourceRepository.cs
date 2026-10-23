using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Infrastructure;

internal sealed class MoodleResourceRepository(ConnectorDbContext dbContext) : IMoodleResourceRepository
{
    public Task RegisterAsync(MoodleResource resource, CancellationToken cancellationToken) =>
        dbContext.MoodleResources.AddAsync(resource, cancellationToken).AsTask();

    public Task<MoodleResource?> FindReusableAsync(
        string clientId,
        string connectionId,
        string ownerSubject,
        MoodleResourceRegistration request,
        string normalizedRemoteFileReference,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requestedHash = string.IsNullOrWhiteSpace(request.Sha256)
            ? null
            : request.Sha256.Trim().ToLowerInvariant();
        return dbContext.MoodleResources
            .AsNoTracking()
            .Where(resource =>
                resource.ClientId == clientId &&
                resource.ConnectionId == connectionId &&
                resource.OwnerSubject == ownerSubject &&
                resource.ResourceType == (string.IsNullOrWhiteSpace(request.ResourceType) ? "submission_attachment" : request.ResourceType.Trim()) &&
                resource.CourseId == request.CourseId &&
                resource.AssignmentId == request.AssignmentId &&
                resource.SubmissionId == request.SubmissionId &&
                resource.StudentId == request.StudentId &&
                resource.Filename == Path.GetFileName(request.Filename.Trim()) &&
                resource.RemoteFileReference == normalizedRemoteFileReference &&
                (requestedHash == null || resource.Sha256 == requestedHash) &&
                resource.RevokedAt == null &&
                resource.ExpiresAt > now &&
                resource.ParentResourceId == null)
            .OrderByDescending(resource => resource.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MoodleResource>> ListReusableAsync(
        string clientId,
        string connectionId,
        string ownerSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return await dbContext.MoodleResources
            .AsNoTracking()
            .Where(resource =>
                resource.ClientId == clientId &&
                resource.ConnectionId == connectionId &&
                resource.OwnerSubject == ownerSubject &&
                resource.RevokedAt == null &&
                resource.ExpiresAt > now &&
                resource.ParentResourceId == null)
            .OrderByDescending(resource => resource.CreatedAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MoodleResource>> FindReusableManyAsync(
        string clientId,
        string connectionId,
        string ownerSubject,
        IReadOnlyCollection<MoodleResourceRegistration> requests,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalizedRemoteReferences = requests
            .Select(request => request.RemoteFileReference)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedRemoteReferences.Length == 0)
        {
            return [];
        }

        // Keep the IN list bounded for providers with conservative parameter
        // limits. The owner/connection/expiry predicates are covered by the
        // reuse index; remote references narrow the result to this page.
        var result = new List<MoodleResource>();
        foreach (var chunk in normalizedRemoteReferences.Chunk(500))
        {
            var resources = await dbContext.MoodleResources
                .AsNoTracking()
                .Where(resource =>
                    resource.ClientId == clientId &&
                    resource.ConnectionId == connectionId &&
                    resource.OwnerSubject == ownerSubject &&
                    resource.RemoteFileReference != null &&
                    chunk.Contains(resource.RemoteFileReference) &&
                    resource.RevokedAt == null &&
                    resource.ExpiresAt > now &&
                    resource.ParentResourceId == null)
                .OrderByDescending(resource => resource.CreatedAt)
                .ToArrayAsync(cancellationToken);
            result.AddRange(resources);
        }

        return result;
    }

    public Task RegisterManyAsync(
        IReadOnlyCollection<MoodleResource> resources,
        CancellationToken cancellationToken)
    {
        if (resources.Count == 0)
        {
            return Task.CompletedTask;
        }

        return dbContext.MoodleResources.AddRangeAsync(resources, cancellationToken);
    }

    public Task<MoodleResource?> FindAsync(string resourceId, CancellationToken cancellationToken) =>
        dbContext.MoodleResources.SingleOrDefaultAsync(resource => resource.ResourceId == resourceId, cancellationToken);

    public async Task<IReadOnlyDictionary<string, MoodleResource>> FindManyAsync(
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken)
    {
        var normalizedIds = resourceIds
            .Where(resourceId => !string.IsNullOrWhiteSpace(resourceId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedIds.Length == 0)
        {
            return new Dictionary<string, MoodleResource>(StringComparer.Ordinal);
        }

        // Keep the IN list bounded for providers with conservative parameter
        // limits while still reducing the common 10k-item path to a handful
        // of indexed round trips.
        var result = new Dictionary<string, MoodleResource>(StringComparer.Ordinal);
        foreach (var chunk in normalizedIds.Chunk(500))
        {
            var resources = await dbContext.MoodleResources
                .AsNoTracking()
                .Where(resource => chunk.Contains(resource.ResourceId))
                .ToArrayAsync(cancellationToken);
            foreach (var resource in resources)
            {
                result[resource.ResourceId] = resource;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<MoodleResource>> ListBySubmissionAsync(
        string clientId,
        string connectionId,
        long submissionId,
        CancellationToken cancellationToken) =>
        await dbContext.MoodleResources
            .Where(resource =>
                resource.ClientId == clientId &&
                resource.ConnectionId == connectionId &&
                resource.SubmissionId == submissionId &&
                resource.ParentResourceId == null)
            .OrderBy(resource => resource.ResourceId)
            .ToArrayAsync(cancellationToken);

    public Task<int> RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.MoodleResources.Where(resource => resource.ExpiresAt <= now || resource.RevokedAt != null)
            .ExecuteDeleteAsync(cancellationToken);

    public Task<bool> ExistsAndNotExpiredAsync(string resourceId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.MoodleResources.AnyAsync(
            resource => resource.ResourceId == resourceId &&
                        resource.RevokedAt == null &&
                        resource.ExpiresAt > now,
            cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
