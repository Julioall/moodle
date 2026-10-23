using Moodle.Domain;

namespace Moodle.Application.Abstractions;

public interface IMoodleResourceRepository
{
    Task RegisterAsync(MoodleResource resource, CancellationToken cancellationToken);

    Task<MoodleResource?> FindReusableAsync(
        string clientId,
        string connectionId,
        string ownerSubject,
        MoodleResourceRegistration request,
        string normalizedRemoteFileReference,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.FromResult<MoodleResource?>(null);

    /// <summary>
    /// Loads reusable resources for one owner/connection in a single indexed
    /// query. The default preserves compatibility with lightweight stores;
    /// production PostgreSQL overrides it to avoid one SELECT per attachment.
    /// </summary>
    Task<IReadOnlyList<MoodleResource>> ListReusableAsync(
        string clientId,
        string connectionId,
        string ownerSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MoodleResource>>([]);

    /// <summary>
    /// Loads only the reusable resources that can match one registration page.
    /// The default keeps lightweight stores source-compatible; PostgreSQL
    /// implementations should override it with a bounded indexed query.
    /// </summary>
    async Task<IReadOnlyList<MoodleResource>> FindReusableManyAsync(
        string clientId,
        string connectionId,
        string ownerSubject,
        IReadOnlyCollection<MoodleResourceRegistration> requests,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = new List<MoodleResource>();
        foreach (var request in requests)
        {
            if (string.IsNullOrWhiteSpace(request.RemoteFileReference))
            {
                continue;
            }

            var resource = await FindReusableAsync(
                clientId,
                connectionId,
                ownerSubject,
                request,
                request.RemoteFileReference,
                now,
                cancellationToken);
            if (resource is not null)
            {
                result.Add(resource);
            }
        }

        return result;
    }

    async Task RegisterManyAsync(
        IReadOnlyCollection<MoodleResource> resources,
        CancellationToken cancellationToken)
    {
        foreach (var resource in resources)
        {
            await RegisterAsync(resource, cancellationToken);
        }
    }

    Task<MoodleResource?> FindAsync(string resourceId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a set of resources with one indexed query. This is used by the
    /// publication preflight so a 10k-item run does not issue one SELECT per
    /// attachment while revalidating submission integrity.
    /// </summary>
    async Task<IReadOnlyDictionary<string, MoodleResource>> FindManyAsync(
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MoodleResource>(StringComparer.Ordinal);
        foreach (var resourceId in resourceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var resource = await FindAsync(resourceId, cancellationToken);
            if (resource is not null)
            {
                result[resource.ResourceId] = resource;
            }
        }

        return result;
    }

    Task<IReadOnlyList<MoodleResource>> ListBySubmissionAsync(
        string clientId,
        string connectionId,
        long submissionId,
        CancellationToken cancellationToken);
    /// <summary>
    /// Verifica se um resource existe e não está expirado nem revogado.
    /// Usado na validação de evidências do grading proposal sem fazer download.
    /// </summary>
    Task<bool> ExistsAndNotExpiredAsync(string resourceId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
