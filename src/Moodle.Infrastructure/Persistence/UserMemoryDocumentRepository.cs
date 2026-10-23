using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Moodle.Infrastructure;

public sealed class UserMemoryDocumentRepository(ConnectorDbContext dbContext) : IUserMemoryDocumentRepository
{
    internal const string UpsertSql = """
        INSERT INTO user_memory_documents
            ("Id", "OwnerSubject", "NormalizedKey", "Title", "Content", "Format", "Origin", "MoodleAlias", "CourseId", "CreatedAtUtc", "UpdatedAtUtc")
        VALUES
            ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10})
        ON CONFLICT ("OwnerSubject", "MoodleAlias", "CourseId", "NormalizedKey")
        DO UPDATE SET
            "Title" = EXCLUDED."Title",
            "Content" = EXCLUDED."Content",
            "Format" = EXCLUDED."Format",
            "Origin" = EXCLUDED."Origin",
            "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc";
        """;

    public async Task<UserMemoryDocument> UpsertAsync(UserMemoryDocument candidate, CancellationToken cancellationToken = default)
    {
        if (!dbContext.Database.IsNpgsql())
        {
            var existing = await FindEquivalentAsync(candidate, cancellationToken);
            if (existing is null)
            {
                await dbContext.UserMemoryDocuments.AddAsync(candidate, cancellationToken);
                return candidate;
            }

            existing.Update(candidate.Title, candidate.Content, candidate.Format, candidate.Origin, candidate.UpdatedAtUtc);
            return existing;
        }

        await dbContext.Database.ExecuteSqlRawAsync(
            UpsertSql,
            [
                candidate.Id,
                candidate.OwnerSubject,
                candidate.NormalizedKey,
                candidate.Title,
                candidate.Content,
                candidate.Format,
                candidate.Origin,
                NullableTextParameter("moodleAlias", candidate.MoodleAlias),
                NullableTextParameter("courseId", candidate.CourseId),
                candidate.CreatedAtUtc,
                candidate.UpdatedAtUtc
            ],
            cancellationToken);

        return await dbContext.UserMemoryDocuments
            .AsNoTracking()
            .SingleAsync(document =>
                document.OwnerSubject == candidate.OwnerSubject &&
                document.MoodleAlias == candidate.MoodleAlias &&
                document.CourseId == candidate.CourseId &&
                document.NormalizedKey == candidate.NormalizedKey,
                cancellationToken);
    }

    public Task<UserMemoryDocument?> FindOwnedAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default)
    {
        return dbContext.UserMemoryDocuments.SingleOrDefaultAsync(
            document => document.Id == id && document.OwnerSubject == ownerSubject,
            cancellationToken);
    }

    public async Task<IReadOnlyList<UserMemoryDocument>> ListAsync(
        string ownerSubject,
        string? moodleAlias,
        string? courseId,
        string? query,
        string? normalizedKeyQuery,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var documents = dbContext.UserMemoryDocuments
            .Where(document => document.OwnerSubject == ownerSubject &&
                ((document.MoodleAlias == null && document.CourseId == null) ||
                 (moodleAlias != null && document.MoodleAlias == moodleAlias && document.CourseId == null) ||
                 (moodleAlias != null && courseId != null &&
                  document.MoodleAlias == moodleAlias && document.CourseId == courseId)));

        if (query is not null || normalizedKeyQuery is not null)
        {
            if (dbContext.Database.IsNpgsql())
            {
                var queryPattern = query is null ? null : $"%{EscapeLikePattern(query)}%";
                var normalizedKeyPattern = normalizedKeyQuery is null ? null : $"%{EscapeLikePattern(normalizedKeyQuery)}%";
                documents = documents.Where(document =>
                    (normalizedKeyPattern != null && EF.Functions.ILike(document.NormalizedKey, normalizedKeyPattern, "\\")) ||
                    (queryPattern != null && EF.Functions.ILike(document.Title, queryPattern, "\\")) ||
                    (queryPattern != null && EF.Functions.ILike(document.Content, queryPattern, "\\")));
            }
            else
            {
                var loweredQuery = query?.ToLowerInvariant();
                documents = documents.Where(document =>
                    (normalizedKeyQuery != null && document.NormalizedKey.ToLower().Contains(normalizedKeyQuery)) ||
                    (loweredQuery != null && document.Title.ToLower().Contains(loweredQuery)) ||
                    (loweredQuery != null && document.Content.ToLower().Contains(loweredQuery)));
            }
        }

        return await documents
            .OrderByDescending(document => document.CourseId != null ? 2 : document.MoodleAlias != null ? 1 : 0)
            .ThenByDescending(document => document.UpdatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default)
    {
        var document = await FindOwnedAsync(id, ownerSubject, cancellationToken);
        if (document is null)
        {
            return false;
        }

        dbContext.UserMemoryDocuments.Remove(document);
        return true;
    }

    public Task<bool> RemoveWithLinkedMemoriesAsync(
        Guid id,
        string ownerSubject,
        CancellationToken cancellationToken = default) =>
        ExecuteInTransactionAsync(async transactionToken =>
        {
            var document = await FindOwnedAsync(id, ownerSubject, transactionToken);
            if (document is null)
            {
                return false;
            }

            var linkedMemories = dbContext.UserMemories
                .Where(memory => memory.OwnerSubject == ownerSubject && memory.LinkedDocumentId == id);
            if (dbContext.Database.IsRelational())
            {
                await linkedMemories.ExecuteDeleteAsync(transactionToken);
            }
            else
            {
                dbContext.UserMemories.RemoveRange(await linkedMemories.ToListAsync(transactionToken));
            }

            dbContext.UserMemoryDocuments.Remove(document);
            try
            {
                await dbContext.SaveChangesAsync(transactionToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }, cancellationToken);

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!dbContext.Database.IsRelational())
        {
            return await operation(cancellationToken);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await operation(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }

    private Task<UserMemoryDocument?> FindEquivalentAsync(UserMemoryDocument candidate, CancellationToken cancellationToken)
    {
        return dbContext.UserMemoryDocuments.SingleOrDefaultAsync(
            document => document.OwnerSubject == candidate.OwnerSubject &&
                        document.MoodleAlias == candidate.MoodleAlias &&
                        document.CourseId == candidate.CourseId &&
                        document.NormalizedKey == candidate.NormalizedKey,
            cancellationToken);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static NpgsqlParameter NullableTextParameter(string name, string? value) =>
        new(name, NpgsqlDbType.Varchar)
        {
            Value = (object?)value ?? DBNull.Value
        };
}
