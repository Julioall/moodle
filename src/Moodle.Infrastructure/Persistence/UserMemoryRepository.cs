using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Moodle.Infrastructure;

public sealed class UserMemoryRepository(ConnectorDbContext dbContext) : IUserMemoryRepository
{
    internal const string UpsertSql = """
        INSERT INTO user_memories
            ("Id", "OwnerSubject", "Category", "NormalizedKey", "Content", "Origin", "MoodleAlias", "CourseId", "LinkedDocumentId", "CreatedAtUtc", "UpdatedAtUtc")
        VALUES
            ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10})
        ON CONFLICT ("OwnerSubject", "Category", "MoodleAlias", "CourseId", "NormalizedKey")
        DO UPDATE SET
            "Content" = EXCLUDED."Content",
            "Origin" = EXCLUDED."Origin",
            "LinkedDocumentId" = EXCLUDED."LinkedDocumentId",
            "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc";
        """;

    public Task<UserMemory?> FindEquivalentAsync(
        string ownerSubject,
        string category,
        string? moodleAlias,
        string? courseId,
        string normalizedKey,
        CancellationToken cancellationToken = default)
    {
        return dbContext.UserMemories.SingleOrDefaultAsync(
            memory => memory.OwnerSubject == ownerSubject &&
                      memory.Category == category &&
                      memory.MoodleAlias == moodleAlias &&
                      memory.CourseId == courseId &&
                      memory.NormalizedKey == normalizedKey,
            cancellationToken);
    }

    public async Task AddAsync(UserMemory memory, CancellationToken cancellationToken = default)
    {
        await dbContext.UserMemories.AddAsync(memory, cancellationToken);
    }

    public async Task<UserMemory> UpsertAsync(
        UserMemory candidate,
        CancellationToken cancellationToken = default)
    {
        if (!dbContext.Database.IsNpgsql())
        {
            var existing = await FindEquivalentAsync(
                candidate.OwnerSubject,
                candidate.Category,
                candidate.MoodleAlias,
                candidate.CourseId,
                candidate.NormalizedKey,
                cancellationToken);
            if (existing is null)
            {
                await AddAsync(candidate, cancellationToken);
                return candidate;
            }

            existing.Update(candidate.Content, candidate.Origin, candidate.UpdatedAtUtc, candidate.LinkedDocumentId);
            return existing;
        }

        await dbContext.Database.ExecuteSqlRawAsync(
            UpsertSql,
            [
                candidate.Id,
                candidate.OwnerSubject,
                candidate.Category,
                candidate.NormalizedKey,
                candidate.Content,
                candidate.Origin,
                NullableTextParameter("moodleAlias", candidate.MoodleAlias),
                NullableTextParameter("courseId", candidate.CourseId),
                NullableGuidParameter("linkedDocumentId", candidate.LinkedDocumentId),
                candidate.CreatedAtUtc,
                candidate.UpdatedAtUtc
            ],
            cancellationToken);

        return await dbContext.UserMemories
            .AsNoTracking()
            .SingleAsync(memory =>
                memory.OwnerSubject == candidate.OwnerSubject &&
                memory.Category == candidate.Category &&
                memory.MoodleAlias == candidate.MoodleAlias &&
                memory.CourseId == candidate.CourseId &&
                memory.NormalizedKey == candidate.NormalizedKey,
                cancellationToken);
    }

    public async Task<IReadOnlyList<UserMemory>> ListAsync(
        string ownerSubject,
        string? moodleAlias,
        string? courseId,
        string? category,
        string? contentQuery,
        string? normalizedKeyQuery,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var memories = dbContext.UserMemories
            .Where(memory => memory.OwnerSubject == ownerSubject &&
                ((memory.MoodleAlias == null && memory.CourseId == null) ||
                 (moodleAlias != null && memory.MoodleAlias == moodleAlias && memory.CourseId == null) ||
                 (moodleAlias != null && courseId != null &&
                  memory.MoodleAlias == moodleAlias && memory.CourseId == courseId)));

        if (category is not null)
        {
            memories = memories.Where(memory => memory.Category == category);
        }

        if (contentQuery is not null || normalizedKeyQuery is not null)
        {
            if (dbContext.Database.IsNpgsql())
            {
                var contentPattern = contentQuery is null ? null : $"%{EscapeLikePattern(contentQuery)}%";
                var normalizedKeyPattern = normalizedKeyQuery is null ? null : $"%{EscapeLikePattern(normalizedKeyQuery)}%";
                memories = memories.Where(memory =>
                    (normalizedKeyPattern != null && EF.Functions.ILike(memory.NormalizedKey, normalizedKeyPattern, "\\")) ||
                    (contentPattern != null && EF.Functions.ILike(memory.Content, contentPattern, "\\")));
            }
            else
            {
                var loweredContentQuery = contentQuery?.ToLowerInvariant();
                memories = memories.Where(memory =>
                    (normalizedKeyQuery != null && memory.NormalizedKey.ToLower().Contains(normalizedKeyQuery)) ||
                    (loweredContentQuery != null && memory.Content.ToLower().Contains(loweredContentQuery)));
            }
        }

        return await memories
            .OrderByDescending(memory => memory.CourseId != null ? 2 : memory.MoodleAlias != null ? 1 : 0)
            .ThenByDescending(memory => memory.UpdatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    public async Task<bool> RemoveAsync(
        Guid id,
        string ownerSubject,
        CancellationToken cancellationToken = default)
    {
        var memory = await FindOwnedAsync(id, ownerSubject, cancellationToken);
        if (memory is null)
        {
            return false;
        }

        dbContext.UserMemories.Remove(memory);
        return true;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<UserMemory?> FindOwnedAsync(
        Guid id,
        string ownerSubject,
        CancellationToken cancellationToken = default)
    {
        return dbContext.UserMemories.SingleOrDefaultAsync(
            memory => memory.OwnerSubject == ownerSubject && memory.Id == id,
            cancellationToken);
    }

    private static NpgsqlParameter NullableTextParameter(string name, string? value) =>
        new(name, NpgsqlDbType.Varchar)
        {
            Value = (object?)value ?? DBNull.Value
        };

    private static NpgsqlParameter NullableGuidParameter(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid)
        {
            Value = (object?)value ?? DBNull.Value
        };
}
