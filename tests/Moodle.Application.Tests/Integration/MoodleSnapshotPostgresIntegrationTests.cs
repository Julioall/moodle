using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Infrastructure;
using Npgsql;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class MoodleSnapshotPostgresIntegrationTests
{
    [Fact]
    public async Task SchemaAndConcurrentHeadUpsertConvergemNoPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "MOODLE_POSTGRES_TEST_CONNECTION e obrigatoria; o gate PostgreSQL nao pode ser silenciosamente ignorado.");

        var ownerId = Guid.NewGuid();
        var alias = $"it-{Guid.NewGuid():N}"[..32];
        var clientId = $"it-client-{Guid.NewGuid():N}"[..40];
        var connectionId = $"it-connection-{Guid.NewGuid():N}"[..40];
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
            schemaDb.UserAccounts.Add(new UserAccountEntity
            {
                Id = ownerId,
                Name = "Integration Owner",
                Email = $"{ownerId:N}@integration.test",
                PasswordHash = "not-a-real-password",
                ConnectorClientId = clientId,
            });
            schemaDb.ConnectorClients.Add(new ConnectorClientCredentialEntity
            {
                Id = connectionId,
                ClientId = clientId,
                MoodleAlias = alias,
                MoodleBaseUrl = "https://integration.example",
                MoodleUsernameEncrypted = "encrypted-user",
                MoodlePasswordEncrypted = "encrypted-password",
                MoodleTarget = alias,
                IsActive = true,
            });
            schemaDb.MoodleSyncStates.Add(new MoodleSyncStateEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                ConnectionId = connectionId,
                ConnectionAlias = alias,
                Dataset = "courses",
                CourseId = string.Empty,
                ClientId = clientId,
                UserExternalId = ownerId.ToString("N"),
                Status = "pending",
                NextSyncAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await schemaDb.SaveChangesAsync();
        }

        var claimTime = DateTimeOffset.UtcNow;
        async Task<int> ClaimAsync()
        {
            await using var db = new ConnectorDbContext(options);
            return await db.MoodleSyncStates
                .Where(item => item.OwnerId == ownerId &&
                               item.ConnectionId == connectionId &&
                               item.Dataset == "courses" &&
                               item.Status != "running" &&
                               item.NextSyncAt <= claimTime)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, "running")
                    .SetProperty(item => item.LeaseUntil, claimTime.AddMinutes(30))
                    .SetProperty(item => item.LastStartedAt, claimTime));
        }

        var claims = await Task.WhenAll(ClaimAsync(), ClaimAsync());
        Assert.Equal(1, claims.Count(item => item == 1));
        Assert.Equal(1, claims.Count(item => item == 0));

        async Task SaveAsync(string state)
        {
            await using var db = new ConnectorDbContext(options);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            using var metrics = new MoodleSnapshotMetrics();
            var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);
            await store.SaveAsync(
                ownerId,
                alias,
                "courses",
                string.Empty,
                new Dictionary<string, string> { ["state"] = state },
                "warm",
                frozen: false,
                complete: true,
                recordCount: 1,
                DateTimeOffset.UtcNow);
        }

        await Task.WhenAll(SaveAsync("first"), SaveAsync("second"));

        await using var verifyDb = new ConnectorDbContext(options);
        var heads = await verifyDb.MoodleSnapshots
            .Where(item => item.OwnerId == ownerId && item.ConnectionId == connectionId)
            .ToListAsync();
        var head = Assert.Single(heads);
        Assert.True(
            head.PayloadJson.Contains("\"first\"", StringComparison.Ordinal) ||
            head.PayloadJson.Contains("\"second\"", StringComparison.Ordinal));
        Assert.Equal(alias, head.ConnectionAlias);
    }

    [Fact]
    public async Task GetManyAsyncReadsManyCourseHeadsWithOneCommandPerDataset()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "MOODLE_POSTGRES_TEST_CONNECTION e obrigatoria; o gate PostgreSQL nao pode ser silenciosamente ignorado.");

        var ownerId = Guid.NewGuid();
        var clientId = $"it-client-{Guid.NewGuid():N}"[..40];
        var connectionId = $"it-connection-{Guid.NewGuid():N}"[..40];
        var alias = $"it-{Guid.NewGuid():N}"[..32];
        var courseIds = Enumerable.Range(1, 12).Select(index => $"course-{index}").ToArray();
        var now = DateTimeOffset.UtcNow;
        var counter = new SnapshotSelectCounter();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(counter)
            .Options;

        await using (var setupDb = new ConnectorDbContext(options))
        {
            await setupDb.ApplyVersionedSchemaAsync();
            setupDb.UserAccounts.Add(new UserAccountEntity
            {
                Id = ownerId,
                Name = "Integration Owner",
                Email = $"{ownerId:N}@integration.test",
                PasswordHash = "not-a-real-password",
                ConnectorClientId = clientId,
            });
            setupDb.ConnectorClients.Add(new ConnectorClientCredentialEntity
            {
                Id = connectionId,
                ClientId = clientId,
                MoodleAlias = alias,
                MoodleBaseUrl = "https://integration.example",
                MoodleUsernameEncrypted = "encrypted-user",
                MoodlePasswordEncrypted = "encrypted-password",
                MoodleTarget = alias,
                IsActive = true,
            });
            setupDb.MoodleSnapshots.AddRange(courseIds.Select(courseId => new MoodleSnapshotEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                ConnectionId = connectionId,
                ConnectionAlias = alias,
                SnapshotType = MoodleSnapshotDatasets.Activities,
                CourseId = courseId,
                PayloadJson = "[]",
                Tier = "warm",
                UpdatedAt = now,
                FreshUntil = now.AddHours(1),
                StaleUntil = now.AddDays(1),
            }));
            await setupDb.SaveChangesAsync();
        }

        try
        {
            async Task<int> ReadAndCountAsync(IReadOnlyCollection<string> requestedCourseIds)
            {
                counter.Reset();
                await using var db = new ConnectorDbContext(options);
                using var cache = new MemoryCache(new MemoryCacheOptions());
                using var metrics = new MoodleSnapshotMetrics();
                var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);

                var result = await store.GetManyAsync<string[]>(
                    ownerId,
                    alias,
                    MoodleSnapshotDatasets.Activities,
                    requestedCourseIds);

                Assert.Equal(requestedCourseIds.Count, result.Count);
                return counter.Count;
            }

            var smallBatchCommands = await ReadAndCountAsync(courseIds.Take(2).ToArray());
            var largeBatchCommands = await ReadAndCountAsync(courseIds);

            Assert.Equal(1, smallBatchCommands);
            Assert.Equal(smallBatchCommands, largeBatchCommands);
        }
        finally
        {
            await using var cleanupDb = new ConnectorDbContext(options);
            cleanupDb.MoodleSnapshots.RemoveRange(cleanupDb.MoodleSnapshots.Where(item => item.OwnerId == ownerId));
            cleanupDb.ConnectorClients.RemoveRange(cleanupDb.ConnectorClients.Where(item => item.Id == connectionId));
            cleanupDb.UserAccounts.RemoveRange(cleanupDb.UserAccounts.Where(item => item.Id == ownerId));
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task CourseOverviewSnapshotHeadsUseThreeColdReadsAndZeroWarmReads()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "MOODLE_POSTGRES_TEST_CONNECTION e obrigatoria; o gate PostgreSQL nao pode ser silenciosamente ignorado.");

        var ownerId = Guid.NewGuid();
        var clientId = $"it-client-{Guid.NewGuid():N}"[..40];
        var connectionId = $"it-connection-{Guid.NewGuid():N}"[..40];
        var alias = $"it-{Guid.NewGuid():N}"[..32];
        var now = DateTimeOffset.UtcNow;
        var queryCounter = new StoreReadCounter();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(queryCounter)
            .Options;

        try
        {
            await using (var seedDb = new ConnectorDbContext(options))
            {
                await seedDb.ApplyVersionedSchemaAsync();
                seedDb.UserAccounts.Add(new UserAccountEntity
                {
                    Id = ownerId,
                    Name = "Synthetic Integration Owner",
                    Email = $"{ownerId:N}@integration.test",
                    PasswordHash = "not-a-real-password",
                    ConnectorClientId = clientId,
                });
                seedDb.ConnectorClients.Add(new ConnectorClientCredentialEntity
                {
                    Id = connectionId,
                    ClientId = clientId,
                    MoodleAlias = alias,
                    MoodleBaseUrl = "https://integration.example",
                    MoodleUsernameEncrypted = "synthetic-user-placeholder",
                    MoodlePasswordEncrypted = "synthetic-secret-placeholder",
                    MoodleTarget = alias,
                    IsActive = true,
                });
                await seedDb.SaveChangesAsync();

                using var writerCache = new MemoryCache(new MemoryCacheOptions());
                using var writerMetrics = new MoodleSnapshotMetrics();
                var writer = new MoodleSnapshotStore(seedDb, writerCache, writerMetrics, NullLogger<MoodleSnapshotStore>.Instance);
                var student = new CourseParticipantSummary("student-1", "Synthetic Student", null, false, null, null, null, [], [], "active");
                var gradebook = new CourseGradebookSnapshot(
                    "42",
                    new Dictionary<string, CourseGradebook>(),
                    new GradebookSnapshotCoverage("bulk", 1, 1, true, false, [], []));

                await writer.SaveAsync(
                    ownerId, alias, MoodleSnapshotDatasets.Courses, string.Empty,
                    (IReadOnlyList<CourseSummary>)[new CourseSummary("42", null, "SYN", "Synthetic Course", "Synthetic Course", null, null, null, null, true, null, null, null, null, null, null)],
                    "warm", frozen: false, complete: true, recordCount: 1, now);
                await writer.SaveAsync(
                    ownerId, alias, MoodleSnapshotDatasets.Students, "42",
                    new CourseParticipantsPage("42", 1, 500, ParticipantStatusFilter.Active, true, false, false, [student]),
                    "hot", frozen: false, complete: true, recordCount: 1, now);
                await writer.SaveAsync(
                    ownerId, alias, MoodleSnapshotDatasets.Gradebook, "42",
                    gradebook, "hot", frozen: false, complete: true, recordCount: 1, now);
            }

            queryCounter.Reset();
            await using var readDb = new ConnectorDbContext(options);
            using var readCache = new MemoryCache(new MemoryCacheOptions());
            using var readMetrics = new MoodleSnapshotMetrics();
            var store = new MoodleSnapshotStore(readDb, readCache, readMetrics, NullLogger<MoodleSnapshotStore>.Instance);

            Assert.NotNull(await store.GetCoursesAsync(ownerId, alias));
            Assert.NotNull(await store.GetStudentsAsync(ownerId, alias, "42"));
            Assert.NotNull(await store.GetAsync<CourseGradebookSnapshot>(ownerId, alias, MoodleSnapshotDatasets.Gradebook, "42"));
            var coldReads = queryCounter.TotalCount;
            var coldSnapshotReads = queryCounter.SnapshotCount;

            Assert.NotNull(await store.GetCoursesAsync(ownerId, alias));
            Assert.NotNull(await store.GetStudentsAsync(ownerId, alias, "42"));
            Assert.NotNull(await store.GetAsync<CourseGradebookSnapshot>(ownerId, alias, MoodleSnapshotDatasets.Gradebook, "42"));

            Assert.Equal(3, coldSnapshotReads);
            Assert.Equal(5, coldReads); // two local connection-identity queries plus three snapshot-head reads
            Assert.Equal(coldReads, queryCounter.TotalCount);
            Assert.Equal(0, queryCounter.TotalCount - coldReads);
        }
        finally
        {
            await using var cleanupDb = new ConnectorDbContext(options);
            cleanupDb.MoodleSnapshots.RemoveRange(cleanupDb.MoodleSnapshots.Where(item => item.OwnerId == ownerId));
            cleanupDb.ConnectorClients.RemoveRange(cleanupDb.ConnectorClients.Where(item => item.Id == connectionId));
            cleanupDb.UserAccounts.RemoveRange(cleanupDb.UserAccounts.Where(item => item.Id == ownerId));
            await cleanupDb.SaveChangesAsync();
        }
    }

    private sealed class SnapshotSelectCounter : DbCommandInterceptor
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Reset() => Interlocked.Exchange(ref count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("moodle_snapshots", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref count);
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class StoreReadCounter : DbCommandInterceptor
    {
        private int totalCount;
        private int snapshotCount;

        public int TotalCount => Volatile.Read(ref totalCount);
        public int SnapshotCount => Volatile.Read(ref snapshotCount);

        public void Reset()
        {
            Interlocked.Exchange(ref totalCount, 0);
            Interlocked.Exchange(ref snapshotCount, 0);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref totalCount);
            if (command.CommandText.Contains("moodle_snapshots", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref snapshotCount);
            }

            return ValueTask.FromResult(result);
        }
    }
}

[Trait("Category", "PostgresIntegration")]
public sealed class ConnectorDbContextSchemaInitializerPostgresTests
{
    [Fact]
    public async Task CurrentVersionMarkerDoesNotHideMissingLatestSchemaObjects()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(rawConnectionString),
            "MOODLE_POSTGRES_TEST_CONNECTION e obrigatoria; o gate PostgreSQL nao pode ser silenciosamente ignorado.");

        var adminBuilder = new NpgsqlConnectionStringBuilder(rawConnectionString);
        Assert.Contains("test", adminBuilder.Database, StringComparison.OrdinalIgnoreCase);

        var schema = $"schema_repair_{Guid.NewGuid():N}";
        await using (var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var createSchema = adminConnection.CreateCommand();
            createSchema.CommandText = $"CREATE SCHEMA {schema}";
            await createSchema.ExecuteNonQueryAsync();
        }

        var scopedBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            SearchPath = schema,
        };

        try
        {
            var options = new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseNpgsql(scopedBuilder.ConnectionString)
                .Options;
            await using (var setup = new ConnectorDbContext(options))
                await setup.ApplyVersionedSchemaAsync();

            await using (var connection = new NpgsqlConnection(scopedBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT MAX(\"Version\") FROM \"MOODLE_schema_versions\"";
                Assert.Equal(84, Convert.ToInt32(await command.ExecuteScalarAsync()));

                command.CommandText = """
                    ALTER TABLE report_jobs
                        DROP COLUMN "ArtifactBlobKey",
                        DROP COLUMN "WorkerLeaseId",
                        DROP COLUMN "LeaseUntil";
                    ALTER TABLE moodle_sync_states DROP COLUMN "WorkPolicy";
                    DROP TABLE report_artifact_deletions;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var repair = new ConnectorDbContext(options))
                await repair.ApplyVersionedSchemaAsync();

            await using var verifyConnection = new NpgsqlConnection(scopedBuilder.ConnectionString);
            await verifyConnection.OpenAsync();
            await using var verify = verifyConnection.CreateCommand();
            verify.CommandText = """
                SELECT
                    to_regclass('report_artifact_deletions') IS NOT NULL
                    AND EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = 'report_jobs'
                          AND column_name = 'ArtifactBlobKey')
                    AND EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = 'report_jobs'
                          AND column_name = 'WorkerLeaseId')
                    AND EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = 'report_jobs'
                          AND column_name = 'LeaseUntil')
                    AND EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = 'moodle_sync_states'
                          AND column_name = 'WorkPolicy')
                    AND to_regclass('moodle_call_diagnostic_payloads') IS NOT NULL
                    AND (
                        SELECT COUNT(*) = 4 FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = 'moodle_call_diagnostics'
                          AND column_name IN ('RawPayloadState','RawPayloadSizeBytes','RawPayloadSha256','RawPayloadContentType'))
                    AND (SELECT MAX("Version") = 84 FROM "MOODLE_schema_versions");
                """;
            Assert.True(Convert.ToBoolean(await verify.ExecuteScalarAsync()));

            verify.CommandText = "ALTER TABLE moodle_call_diagnostics DROP COLUMN \"RawPayloadContentType\"";
            await verify.ExecuteNonQueryAsync();
            await using var driftedSchema = new ConnectorDbContext(options);
            var drift = await Assert.ThrowsAsync<InvalidOperationException>(
                () => driftedSchema.ApplyVersionedSchemaAsync());
            Assert.Contains("version 81", drift.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
            await adminConnection.OpenAsync();
            await using var dropSchema = adminConnection.CreateCommand();
            dropSchema.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE";
            await dropSchema.ExecuteNonQueryAsync();
        }
    }
}
