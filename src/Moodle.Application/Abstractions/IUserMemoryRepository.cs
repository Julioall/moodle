using Moodle.Domain;

namespace Moodle.Application.Abstractions;

public interface IUserMemoryRepository
{
    Task<UserMemory?> FindEquivalentAsync(string ownerSubject, string category, string? moodleAlias, string? courseId, string normalizedKey, CancellationToken cancellationToken = default);
    Task AddAsync(UserMemory memory, CancellationToken cancellationToken = default);
    Task<UserMemory> UpsertAsync(UserMemory candidate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserMemory>> ListAsync(string ownerSubject, string? moodleAlias, string? courseId, string? category, string? contentQuery, string? normalizedKeyQuery, int limit, CancellationToken cancellationToken = default);
    Task<UserMemory?> FindOwnedAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IUserMemoryDocumentRepository
{
    Task<UserMemoryDocument> UpsertAsync(UserMemoryDocument candidate, CancellationToken cancellationToken = default);
    Task<UserMemoryDocument?> FindOwnedAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserMemoryDocument>> ListAsync(string ownerSubject, string? moodleAlias, string? courseId, string? query, string? normalizedKeyQuery, int limit, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default);
    Task<bool> RemoveWithLinkedMemoriesAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default);
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
