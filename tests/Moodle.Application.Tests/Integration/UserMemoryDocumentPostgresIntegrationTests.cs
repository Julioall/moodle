using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moodle.Application.Abstractions;
using Moodle.Application.Memory;
using Moodle.Domain;
using Moodle.Infrastructure;
using Npgsql;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class UserMemoryDocumentPostgresIntegrationTests
{
    private readonly ITestOutputHelper output;

    public UserMemoryDocumentPostgresIntegrationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [PostgresFact]
    public async Task Benchmark_legacy_and_batched_removals_with_sql_counts()
    {
        await using var database = await IsolatedMemoryDatabase.CreateAsync();
        const int warmups = 3;
        const int samples = 30;
        var legacyTimes = new List<double>(samples);
        var batchedTimes = new List<double>(samples);
        var legacyCounts = default(RemovalCounts);
        var batchedCounts = default(RemovalCounts);

        for (var index = 0; index < warmups + samples; index++)
        {
            var legacyFirst = index % 2 == 0;
            var legacy = await RunRemovalAsync(database, legacy: legacyFirst);
            var batched = await RunRemovalAsync(database, legacy: !legacyFirst);
            if (index >= warmups)
            {
                (legacyFirst ? legacyTimes : batchedTimes).Add(legacyFirst ? legacy.ElapsedMilliseconds : batched.ElapsedMilliseconds);
                (legacyFirst ? batchedTimes : legacyTimes).Add(legacyFirst ? batched.ElapsedMilliseconds : legacy.ElapsedMilliseconds);
            }

            if (index == warmups)
            {
                legacyCounts = legacyFirst ? legacy.Counts : batched.Counts;
                batchedCounts = legacyFirst ? batched.Counts : legacy.Counts;
            }
        }

        Assert.Equal(new RemovalCounts(53, 50, 1, 51, 0), legacyCounts);
        Assert.Equal(new RemovalCounts(1, 1, 1, 1, 1), batchedCounts);
        output.WriteLine($"Legacy removal ms P50/P95/P99: {Percentiles(legacyTimes)}");
        output.WriteLine($"Batched removal ms P50/P95/P99: {Percentiles(batchedTimes)}");
        output.WriteLine($"Legacy SELECT/memory DELETE/document DELETE/SaveChanges/explicit COMMIT: {legacyCounts}");
        output.WriteLine($"Batched SELECT/memory DELETE/document DELETE/SaveChanges/explicit COMMIT: {batchedCounts}");
    }

    [PostgresFact]
    public async Task Save_commits_document_and_link_once_and_preserves_the_public_dto()
    {
        await using var database = await IsolatedMemoryDatabase.CreateAsync();
        await using var db = new ConnectorDbContext(database.Options);
        var service = CreateService(db, "owner-save");

        database.ResetCounters();
        var saved = await service.SaveAsync(new SaveUserMemoryDocumentRequest(
            "course plan", "Course Plan", "document contents", "markdown", "explicit"));

        Assert.Equal("course-plan", saved.NormalizedKey);
        Assert.Equal("Course Plan", saved.Title);
        Assert.Equal("document contents", saved.Content);
        Assert.Equal("markdown", saved.Format);
        Assert.Equal(2, database.Commands.SelectCount);
        Assert.Equal(1, database.Commands.DocumentUpsertCount);
        Assert.Equal(1, database.Commands.MemoryUpsertCount);
        Assert.Equal(1, database.Saves.Count);
        Assert.Equal(1, database.Transactions.CommitCount);
        await using var verify = new ConnectorDbContext(database.Options);
        var persistedDocument = await verify.UserMemoryDocuments.SingleAsync(item => item.Id == saved.Id);
        var linkedMemory = await verify.UserMemories.SingleAsync(item => item.LinkedDocumentId == saved.Id);
        Assert.Equal(saved.Content, persistedDocument.Content);
        Assert.Contains(saved.Id.ToString(), linkedMemory.Content, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Save_rolls_back_document_when_link_upsert_fails()
    {
        await using var database = await IsolatedMemoryDatabase.CreateAsync();
        var failingOptions = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(new FailMemoryUpsertInterceptor())
            .Options;
        var owner = "owner-save-failure";
        Guid? attemptedId = null;
        await using (var db = new ConnectorDbContext(failingOptions))
        {
            var service = CreateService(db, owner);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
                new SaveUserMemoryDocumentRequest("course plan", "Course Plan", "document contents", "markdown", "explicit")));
            Assert.Equal("Forced linked memory upsert failure.", failure.Message);
            attemptedId = await db.UserMemoryDocuments
                .Where(item => item.OwnerSubject == owner)
                .Select(item => (Guid?)item.Id)
                .SingleOrDefaultAsync();
        }

        await using var verify = new ConnectorDbContext(database.Options);
        Assert.Null(attemptedId);
        Assert.Equal(0, await verify.UserMemoryDocuments.CountAsync(item => item.OwnerSubject == owner));
        Assert.Equal(0, await verify.UserMemories.CountAsync(item => item.OwnerSubject == owner));
    }

    [PostgresFact]
    public async Task Concurrent_saves_same_key_keep_one_document_and_one_link()
    {
        await using var database = await IsolatedMemoryDatabase.CreateAsync();
        async Task<UserMemoryDocumentDto> SaveAsync(string content)
        {
            await using var db = new ConnectorDbContext(database.Options);
            return await CreateService(db, "owner-concurrent-save").SaveAsync(
                new SaveUserMemoryDocumentRequest("course plan", "Course Plan", content, "markdown", "explicit"));
        }

        var results = await Task.WhenAll(SaveAsync("same content"), SaveAsync("same content"));

        Assert.Equal(results[0].Id, results[1].Id);
        await using var verify = new ConnectorDbContext(database.Options);
        Assert.Equal(1, await verify.UserMemoryDocuments.CountAsync(item => item.OwnerSubject == "owner-concurrent-save"));
        var linkedMemory = await verify.UserMemories.SingleAsync(item => item.OwnerSubject == "owner-concurrent-save");
        Assert.Equal(results[0].Id, linkedMemory.LinkedDocumentId);
    }

    [PostgresFact]
    public async Task Remove_deletes_all_owned_links_once_and_preserves_other_owners()
    {
        await using var database = await IsolatedMemoryDatabase.CreateAsync();
        var owner = "owner-a";
        var document = NewDocument(owner);
        await SeedAsync(database.Options, document,
            Enumerable.Range(0, 51).Select(index => NewMemory(owner, document.Id, $"owned-{index}"))
                .Append(NewMemory("owner-b", document.Id, "foreign"))
                .Append(NewMemory(owner, null, "unlinked")));

        database.ResetCounters();
        await using (var db = new ConnectorDbContext(database.Options))
        {
            var removed = await new UserMemoryDocumentRepository(db)
                .RemoveWithLinkedMemoriesAsync(document.Id, owner);
            Assert.True(removed);
        }

        Assert.Equal(1, database.Commands.SelectCount);
        Assert.Equal(1, database.Commands.MemoryDeleteCount);
        Assert.Equal(1, database.Commands.DocumentDeleteCount);
        Assert.Equal(1, database.Saves.Count);
        Assert.Equal(1, database.Transactions.CommitCount);

        await using var verify = new ConnectorDbContext(database.Options);
        Assert.Null(await verify.UserMemoryDocuments.SingleOrDefaultAsync(item => item.Id == document.Id));
        Assert.Equal(0, await verify.UserMemories.CountAsync(item => item.OwnerSubject == owner && item.LinkedDocumentId == document.Id));
        Assert.Equal(1, await verify.UserMemories.CountAsync(item => item.OwnerSubject == "owner-b" && item.LinkedDocumentId == document.Id));
        Assert.Equal(1, await verify.UserMemories.CountAsync(item => item.OwnerSubject == owner && item.LinkedDocumentId == null));
    }

    [PostgresFact]
    public async Task Remove_rolls_back_link_deletion_when_document_delete_fails()
    {
        await using var database = await IsolatedMemoryDatabase.CreateAsync();
        var owner = "owner-a";
        var document = NewDocument(owner);
        await SeedAsync(database.Options, document,
        [
            NewMemory(owner, document.Id, "linked-1"),
            NewMemory(owner, document.Id, "linked-2")
        ]);

        var failingOptions = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(new FailDocumentDeleteInterceptor())
            .Options;
        await using (var db = new ConnectorDbContext(failingOptions))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => new UserMemoryDocumentRepository(db)
                .RemoveWithLinkedMemoriesAsync(document.Id, owner));
        }

        await using var verify = new ConnectorDbContext(database.Options);
        Assert.NotNull(await verify.UserMemoryDocuments.SingleOrDefaultAsync(item => item.Id == document.Id));
        Assert.Equal(2, await verify.UserMemories.CountAsync(item => item.OwnerSubject == owner && item.LinkedDocumentId == document.Id));
    }

    [PostgresFact]
    public async Task Concurrent_removals_return_one_success_and_leave_no_partial_rows()
    {
        await using var database = await IsolatedMemoryDatabase.CreateAsync();
        var owner = "owner-a";
        var document = NewDocument(owner);
        await SeedAsync(database.Options, document,
        [
            NewMemory(owner, document.Id, "linked-1"),
            NewMemory(owner, document.Id, "linked-2")
        ]);

        async Task<bool> RemoveAsync()
        {
            await using var db = new ConnectorDbContext(database.Options);
            return await new UserMemoryDocumentRepository(db).RemoveWithLinkedMemoriesAsync(document.Id, owner);
        }

        var results = await Task.WhenAll(RemoveAsync(), RemoveAsync());

        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        await using var verify = new ConnectorDbContext(database.Options);
        Assert.Null(await verify.UserMemoryDocuments.SingleOrDefaultAsync(item => item.Id == document.Id));
        Assert.Equal(0, await verify.UserMemories.CountAsync(item => item.LinkedDocumentId == document.Id));
    }

    private static UserMemoryDocument NewDocument(string owner) =>
        new(owner, "test-document", "Test document", "contents", "text", "explicit", null, null, DateTimeOffset.UtcNow);

    private static UserMemory NewMemory(string owner, Guid? documentId, string key) =>
        new(owner, "modelo", key, "short link", "explicit", null, null, DateTimeOffset.UtcNow, documentId);

    private static UserMemoryDocumentService CreateService(ConnectorDbContext db, string owner)
    {
        var currentUser = new TestCurrentUser(owner);
        var clock = TimeProvider.System;
        return new UserMemoryDocumentService(
            new UserMemoryDocumentRepository(db),
            new UserMemoryService(new UserMemoryRepository(db), currentUser, clock),
            currentUser,
            clock);
    }

    private static async Task SeedAsync(
        DbContextOptions<ConnectorDbContext> options,
        UserMemoryDocument document,
        IEnumerable<UserMemory> memories)
    {
        await using var db = new ConnectorDbContext(options);
        db.UserMemoryDocuments.Add(document);
        db.UserMemories.AddRange(memories);
        await db.SaveChangesAsync();
    }

    private static async Task<RemovalSample> RunRemovalAsync(IsolatedMemoryDatabase database, bool legacy)
    {
        var owner = $"bench-{Guid.NewGuid():N}";
        var document = new UserMemoryDocument(owner, "bench-document", "Benchmark document", "contents", "text", "explicit", null, null, DateTimeOffset.UtcNow);
        await SeedAsync(database.Options, document,
            Enumerable.Range(0, 51).Select(index => NewMemory(owner, document.Id, $"bench-document-link-{index:D2}")));
        database.ResetCounters();

        var timer = Stopwatch.StartNew();
        await using (var db = new ConnectorDbContext(database.Options))
        {
            var documentRepository = new UserMemoryDocumentRepository(db);
            if (legacy)
            {
                var memoryService = new UserMemoryService(new UserMemoryRepository(db), new TestCurrentUser(owner), TimeProvider.System);
                var found = await documentRepository.FindOwnedAsync(document.Id, owner);
                if (found is not null)
                {
                    var removed = await documentRepository.RemoveAsync(document.Id, owner);
                    if (removed) await documentRepository.SaveChangesAsync();
                    var linkedMemories = await memoryService.ListAsync(new ListUserMemoriesRequest(null, null, 50, "modelo", found.NormalizedKey));
                    foreach (var memory in linkedMemories.Where(memory => memory.LinkedDocumentId == document.Id))
                    {
                        await memoryService.RemoveAsync(memory.Id);
                    }
                }
            }
            else
            {
                Assert.True(await documentRepository.RemoveWithLinkedMemoriesAsync(document.Id, owner));
            }
        }
        timer.Stop();

        return new RemovalSample(timer.Elapsed.TotalMilliseconds, new RemovalCounts(
            database.Commands.SelectCount,
            database.Commands.MemoryDeleteCount,
            database.Commands.DocumentDeleteCount,
            database.Saves.Count,
            database.Transactions.CommitCount));
    }

    private static string Percentiles(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        double At(double percentile) => sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1)];
        return $"{At(0.50):F3}/{At(0.95):F3}/{At(0.99):F3}";
    }

    private sealed record RemovalSample(double ElapsedMilliseconds, RemovalCounts Counts);
    private sealed record RemovalCounts(int Selects, int MemoryDeletes, int DocumentDeletes, int SaveChanges, int ExplicitCommits);

    private sealed class IsolatedMemoryDatabase : IAsyncDisposable
    {
        private readonly string schema;

        private IsolatedMemoryDatabase(string schema, string connectionString)
        {
            this.schema = schema;
            ConnectionString = connectionString;
            Commands = new MemoryDocumentCommandCounter();
            Saves = new MemoryDocumentSaveCounter();
            Transactions = new MemoryDocumentTransactionCounter();
            Options = new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(Commands, Saves, Transactions)
                .Options;
        }

        public string ConnectionString { get; }
        public DbContextOptions<ConnectorDbContext> Options { get; }
        public MemoryDocumentCommandCounter Commands { get; }
        public MemoryDocumentSaveCounter Saves { get; }
        public MemoryDocumentTransactionCounter Transactions { get; }

        public static async Task<IsolatedMemoryDatabase> CreateAsync()
        {
            var rawConnectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(rawConnectionString))
            {
                throw new InvalidOperationException("PostgresFact must skip the test when MOODLE_POSTGRES_TEST_CONNECTION is not configured.");
            }

            var adminBuilder = new NpgsqlConnectionStringBuilder(rawConnectionString);
            if (string.IsNullOrWhiteSpace(adminBuilder.Database) ||
                !adminBuilder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The PostgreSQL integration connection must target a database whose name contains 'test'.");
            }

            var schema = $"memory_it_{Guid.NewGuid():N}";
            await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE SCHEMA {schema}";
                await command.ExecuteNonQueryAsync();
            }

            var isolatedBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                SearchPath = schema
            };
            var database = new IsolatedMemoryDatabase(schema, isolatedBuilder.ConnectionString);
            try
            {
                await using var connection = new NpgsqlConnection(isolatedBuilder.ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE user_memory_documents (
                        "Id" uuid PRIMARY KEY,
                        "OwnerSubject" varchar(200) NOT NULL,
                        "NormalizedKey" varchar(120) NOT NULL,
                        "Title" varchar(200) NOT NULL,
                        "Content" varchar(200000) NOT NULL,
                        "Format" varchar(32) NOT NULL,
                        "Origin" varchar(32) NOT NULL,
                        "MoodleAlias" varchar(64) NULL,
                        "CourseId" varchar(64) NULL,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL
                    );
                    CREATE TABLE user_memories (
                        "Id" uuid PRIMARY KEY,
                        "OwnerSubject" varchar(200) NOT NULL,
                        "Category" varchar(32) NOT NULL,
                        "NormalizedKey" varchar(120) NOT NULL,
                        "Content" varchar(1000) NOT NULL,
                        "Origin" varchar(32) NOT NULL,
                        "MoodleAlias" varchar(64) NULL,
                        "CourseId" varchar(64) NULL,
                        "LinkedDocumentId" uuid NULL,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL
                    );
                    CREATE UNIQUE INDEX user_memory_documents_owner_scope_key
                        ON user_memory_documents ("OwnerSubject", "MoodleAlias", "CourseId", "NormalizedKey") NULLS NOT DISTINCT;
                    CREATE UNIQUE INDEX user_memories_owner_category_scope_key
                        ON user_memories ("OwnerSubject", "Category", "MoodleAlias", "CourseId", "NormalizedKey") NULLS NOT DISTINCT;
                    """;
                await command.ExecuteNonQueryAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public void ResetCounters()
        {
            Commands.Reset();
            Saves.Reset();
            Transactions.Reset();
        }

        public async ValueTask DisposeAsync()
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                SearchPath = null
            };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE";
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class MemoryDocumentCommandCounter : DbCommandInterceptor
    {
        private int selectCount;
        private int memoryDeleteCount;
        private int documentDeleteCount;
        private int documentUpsertCount;
        private int memoryUpsertCount;

        public int SelectCount => Volatile.Read(ref selectCount);
        public int MemoryDeleteCount => Volatile.Read(ref memoryDeleteCount);
        public int DocumentDeleteCount => Volatile.Read(ref documentDeleteCount);
        public int DocumentUpsertCount => Volatile.Read(ref documentUpsertCount);
        public int MemoryUpsertCount => Volatile.Read(ref memoryUpsertCount);

        public void Reset() => (selectCount, memoryDeleteCount, documentDeleteCount, documentUpsertCount, memoryUpsertCount) = (0, 0, 0, 0, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Count(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count(command.CommandText);
            return ValueTask.FromResult(result);
        }

        private void Count(string sql)
        {
            if (sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref selectCount);
            if (sql.Contains("DELETE FROM user_memories", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref memoryDeleteCount);
            if (sql.Contains("DELETE FROM user_memory_documents", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref documentDeleteCount);
            if (sql.Contains("INSERT INTO user_memory_documents", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref documentUpsertCount);
            if (sql.Contains("INSERT INTO user_memories", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref memoryUpsertCount);
        }
    }

    private sealed class MemoryDocumentSaveCounter : SaveChangesInterceptor
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public void Reset() => Interlocked.Exchange(ref count, 0);
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MemoryDocumentTransactionCounter : DbTransactionInterceptor
    {
        private int commitCount;
        public int CommitCount => Volatile.Read(ref commitCount);
        public void Reset() => Interlocked.Exchange(ref commitCount, 0);
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            Interlocked.Increment(ref commitCount);
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref commitCount);
            return Task.CompletedTask;
        }
    }

    private sealed class FailDocumentDeleteInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ThrowIfDocumentDelete(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            ThrowIfDocumentDelete(command.CommandText);
            return ValueTask.FromResult(result);
        }

        private static void ThrowIfDocumentDelete(string sql)
        {
            if (sql.Contains("DELETE FROM user_memory_documents", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Forced document delete failure.");
            }
        }
    }

    private sealed class FailMemoryUpsertInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfMemoryUpsert(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ThrowIfMemoryUpsert(command.CommandText);
            return ValueTask.FromResult(result);
        }

        private static void ThrowIfMemoryUpsert(string sql)
        {
            if (sql.Contains("INSERT INTO user_memories", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Forced linked memory upsert failure.");
            }
        }
    }

    private sealed class TestCurrentUser(string subject) : ICurrentUserContext
    {
        public string Subject => subject;
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes => [];
        public bool HasScope(string scope) => false;
    }

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION")))
            {
                Skip = "Configure MOODLE_POSTGRES_TEST_CONNECTION to run the isolated PostgreSQL integration test.";
            }
        }
    }
}
