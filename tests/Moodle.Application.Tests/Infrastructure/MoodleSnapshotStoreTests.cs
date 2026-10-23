using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.Metrics;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleSnapshotStoreTests
{
    [Fact]
    public async Task SaveAsync_GravaConnectionIdEstavelELePorEsseId()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-store-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = ownerId,
            Name = "Owner",
            Email = $"{ownerId:N}@example.test",
            PasswordHash = "hash",
            ConnectorClientId = "client-1",
        });
        db.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = "connection-1",
            ClientId = "client-1",
            MoodleAlias = "goias",
            MoodleBaseUrl = "https://moodle.example",
            MoodleUsernameEncrypted = "user",
            MoodlePasswordEncrypted = "password",
            MoodleTarget = "goias",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(
            ownerId,
            "goias",
            "courses",
            string.Empty,
            new Dictionary<string, string> { ["state"] = "published" },
            "warm",
            frozen: false,
            complete: true,
            recordCount: 1,
            now);

        var entity = await db.MoodleSnapshots.SingleAsync();
        Assert.Equal("connection-1", entity.ConnectionId);

        var read = await store.GetAsync<Dictionary<string, string>>(ownerId, "goias", "courses");
        Assert.NotNull(read);
        Assert.Equal("published", read!.Data["state"]);
    }

    [Fact]
    public async Task GetAsync_FazFallbackControladoParaRegistroLegadoSemConnectionId()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-legacy-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.MoodleSnapshots.Add(new MoodleSnapshotEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ConnectionId = string.Empty,
            ConnectionAlias = "legado",
            SnapshotType = "courses",
            CourseId = string.Empty,
            PayloadJson = "{\"state\":\"legacy\"}",
            Tier = "warm",
            UpdatedAt = DateTimeOffset.UtcNow,
            FreshUntil = DateTimeOffset.UtcNow.AddHours(1),
            StaleUntil = DateTimeOffset.UtcNow.AddHours(2),
        });
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);

        var read = await store.GetAsync<Dictionary<string, string>>(ownerId, "legado", "courses");

        Assert.NotNull(read);
        Assert.Equal("legacy", read!.Data["state"]);
    }

    [Fact]
    public async Task GetManyAsyncReturnsOneEnvelopePerCourseAndPreservesFreshness()
    {
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-bulk-quality-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = ownerId,
            Name = "Owner",
            Email = $"{ownerId:N}@example.test",
            PasswordHash = "hash",
            ConnectorClientId = "client-1",
        });
        db.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = "connection-1",
            ClientId = "client-1",
            MoodleAlias = "goias",
            MoodleBaseUrl = "https://moodle.example",
            MoodleUsernameEncrypted = "user",
            MoodlePasswordEncrypted = "password",
            MoodleTarget = "goias",
            IsActive = true,
        });

        MoodleSnapshotEntity CreateSnapshot(
            string courseId,
            bool complete,
            bool frozen,
            DateTimeOffset freshUntil,
            DateTimeOffset staleUntil) => new()
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ConnectionId = "connection-1",
            ConnectionAlias = "goias",
            SnapshotType = MoodleSnapshotDatasets.Activities,
            CourseId = courseId,
            PayloadJson = "[\"snapshot\"]",
            Tier = "warm",
            IsFrozen = frozen,
            FrozenAt = frozen ? now.AddDays(-5) : null,
            UpdatedAt = now.AddDays(-5),
            FreshUntil = freshUntil,
            StaleUntil = staleUntil,
            IsComplete = complete,
            RecordCount = 1,
        };

        db.MoodleSnapshots.AddRange(
            CreateSnapshot("course-stale", complete: true, frozen: false, now.AddMinutes(-1), now.AddHours(1)),
            CreateSnapshot("course-frozen", complete: true, frozen: true, now.AddDays(-4), now.AddDays(-3)),
            CreateSnapshot("course-incomplete", complete: false, frozen: false, now.AddHours(1), now.AddDays(1)),
            CreateSnapshot("course-expired", complete: true, frozen: false, now.AddDays(-2), now.AddSeconds(-1)));
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);

        var result = await store.GetManyAsync<string[]>(
            ownerId,
            "goias",
            MoodleSnapshotDatasets.Activities,
            [" course-stale ", "course-stale", "course-frozen", "course-incomplete", "course-expired", "missing"]);

        Assert.Equal(3, result.Count);
        Assert.True(result["course-stale"].IsStale);
        Assert.True(result["course-frozen"].IsFrozen);
        Assert.False(result["course-frozen"].IsStale);
        Assert.False(result["course-incomplete"].IsComplete);
        Assert.DoesNotContain("course-expired", result.Keys);
        Assert.DoesNotContain("missing", result.Keys);
    }

    [Fact]
    public async Task GetManyAsyncFallsBackToAliasOnlyForMissingConnectionHeads()
    {
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-bulk-legacy-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = ownerId,
            Name = "Owner",
            Email = $"{ownerId:N}@example.test",
            PasswordHash = "hash",
            ConnectorClientId = "client-1",
        });
        db.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = "connection-1",
            ClientId = "client-1",
            MoodleAlias = "goias",
            MoodleBaseUrl = "https://moodle.example",
            MoodleUsernameEncrypted = "user",
            MoodlePasswordEncrypted = "password",
            MoodleTarget = "goias",
            IsActive = true,
        });
        MoodleSnapshotEntity CreateSnapshot(Guid snapshotOwnerId, string connectionId, string alias, string courseId, string state) => new()
        {
            Id = Guid.NewGuid(),
            OwnerId = snapshotOwnerId,
            ConnectionId = connectionId,
            ConnectionAlias = alias,
            SnapshotType = MoodleSnapshotDatasets.Activities,
            CourseId = courseId,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new[] { state }),
            Tier = "warm",
            UpdatedAt = now,
            FreshUntil = now.AddHours(1),
            StaleUntil = now.AddDays(1),
        };
        db.MoodleSnapshots.AddRange(
            CreateSnapshot(ownerId, "connection-1", "goias", "course-current", "current"),
            CreateSnapshot(ownerId, string.Empty, "goias", "course-current", "older-alias"),
            CreateSnapshot(ownerId, string.Empty, "goias", "course-legacy", "legacy"),
            CreateSnapshot(ownerId, string.Empty, "other", "course-other-alias", "other-alias"),
            CreateSnapshot(Guid.NewGuid(), "connection-1", "goias", "course-other-owner", "other-owner"));
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);

        var result = await store.GetManyAsync<string[]>(
            ownerId,
            "goias",
            MoodleSnapshotDatasets.Activities,
            ["course-current", "course-legacy", "course-other-alias", "course-other-owner", "missing"]);

        Assert.Equal("current", Assert.Single(result["course-current"].Data));
        Assert.Equal("legacy", Assert.Single(result["course-legacy"].Data));
        Assert.DoesNotContain("course-other-alias", result.Keys);
        Assert.DoesNotContain("course-other-owner", result.Keys);
        Assert.DoesNotContain("missing", result.Keys);
    }

    [Fact]
    public async Task GetAsync_NaoTrataGradebookCongeladoLegadoComoEterno()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-legacy-gradebook-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        var now = DateTimeOffset.UtcNow;
        var payload = new CourseGradebookSnapshot(
            "course-1",
            new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase),
            new GradebookSnapshotCoverage("bulk", 0, 0, true, false, [], []));
        db.MoodleSnapshots.Add(new MoodleSnapshotEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ConnectionId = string.Empty,
            ConnectionAlias = "legado",
            SnapshotType = MoodleSnapshotDatasets.Gradebook,
            CourseId = "course-1",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            Tier = "cold",
            IsFrozen = true,
            UpdatedAt = now.AddDays(-1),
            FreshUntil = now.AddYears(10),
            StaleUntil = now.AddYears(10),
        });
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);

        var read = await store.GetAsync<CourseGradebookSnapshot>(ownerId, "legado", MoodleSnapshotDatasets.Gradebook, "course-1");

        Assert.Null(read);
    }

    [Fact]
    public async Task InvalidatePorDatasetNaoExpulsaOutroDatasetDoCacheL1()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-granular-cache-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = ownerId,
            Name = "Owner",
            Email = $"{ownerId:N}@example.test",
            PasswordHash = "hash",
            ConnectorClientId = "client-1",
        });
        db.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = "connection-1",
            ClientId = "client-1",
            MoodleAlias = "goias",
            MoodleBaseUrl = "https://moodle.example",
            MoodleUsernameEncrypted = "user",
            MoodlePasswordEncrypted = "password",
            MoodleTarget = "goias",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        using var listener = new MeterListener();
        var l1Hits = 0L;
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.InstrumentationMeter) && instrument.Name == "moodle_snapshot_l1_hits")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref l1Hits, value));
        listener.Start();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(ownerId, "goias", "courses", "", new[] { "course" }, "warm", false, true, 1, now);
        await store.SaveAsync(ownerId, "goias", "activities", "42", new[] { "activity" }, "hot", false, true, 1, now);
        await store.GetAsync<string[]>(ownerId, "goias", "courses");
        await store.GetAsync<string[]>(ownerId, "goias", "activities", "42");

        store.Invalidate(ownerId, "goias", "courses");
        await store.GetAsync<string[]>(ownerId, "goias", "courses");
        await store.GetAsync<string[]>(ownerId, "goias", "activities", "42");

        Assert.Equal(1, l1Hits);
    }

    [Fact]
    public async Task InvalidateSemPayloadNaoRetemMetadadosDeVersao()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-version-retention-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);
        var owners = Enumerable.Range(0, 5_000)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        foreach (var owner in owners)
        {
            store.Invalidate(owner, "review", "courses", "course");
        }

        Assert.All(owners, owner => Assert.Equal(0, GetMetadataCount(owner)));
    }

    [Fact]
    public async Task GetAsync_ReutilizaIdentidadeResolvidaNoEscopo()
    {
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-identity-scope-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = ownerId,
            Name = "Owner",
            Email = $"{ownerId:N}@example.test",
            PasswordHash = "hash",
            ConnectorClientId = "client-1",
        });
        var connection = new ConnectorClientCredentialEntity
        {
            Id = "connection-1",
            ClientId = "client-1",
            MoodleAlias = "goias",
            MoodleBaseUrl = "https://moodle.example",
            MoodleUsernameEncrypted = "user",
            MoodlePasswordEncrypted = "password",
            MoodleTarget = "goias",
            IsActive = true,
        };
        db.ConnectorClients.Add(connection);
        db.MoodleSnapshots.Add(new MoodleSnapshotEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ConnectionId = connection.Id,
            ConnectionAlias = connection.MoodleAlias,
            SnapshotType = "courses",
            CourseId = string.Empty,
            PayloadJson = "{\"state\":\"cached\"}",
            Tier = "warm",
            UpdatedAt = now,
            FreshUntil = now.AddHours(1),
            StaleUntil = now.AddHours(2),
        });
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        using var listener = new MeterListener();
        var identityResolutionSamples = 0;
        long identityResolutionCommands = 0;
        long databaseReadCommands = 0;
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.InstrumentationMeter) &&
                instrument.Name is "moodle_snapshot_identity_resolution_duration_ms" or "moodle_snapshot_identity_resolution_commands" or "moodle_snapshot_database_read_commands")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
        {
            if (instrument.Name == "moodle_snapshot_identity_resolution_duration_ms")
                Interlocked.Increment(ref identityResolutionSamples);
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "moodle_snapshot_identity_resolution_commands")
                Interlocked.Add(ref identityResolutionCommands, value);
            if (instrument.Name == "moodle_snapshot_database_read_commands")
                Interlocked.Add(ref databaseReadCommands, value);
        });
        listener.Start();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);

        var first = await store.GetAsync<Dictionary<string, string>>(ownerId, "goias", "courses");
        Assert.NotNull(first);

        connection.IsActive = false;
        await db.SaveChangesAsync();

        var second = await store.GetAsync<Dictionary<string, string>>(ownerId, "goias", "courses");

        Assert.NotNull(second);
        Assert.Equal("cached", second!.Data["state"]);
        Assert.Equal(2, identityResolutionSamples);
        Assert.Equal(2, identityResolutionCommands);
        Assert.Equal(1, databaseReadCommands);
    }

    [Fact]
    public async Task GetAsync_UsaCredencialAtivaJaResolvidaEAindaFiltraPeloOwner()
    {
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-active-credentials-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.MoodleSnapshots.AddRange(
            new MoodleSnapshotEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                ConnectionId = "persisted-connection-id",
                ConnectionAlias = "fieg",
                SnapshotType = "courses",
                CourseId = string.Empty,
                PayloadJson = "{\"owner\":\"requested\"}",
                Tier = "warm",
                UpdatedAt = DateTimeOffset.UtcNow,
                FreshUntil = DateTimeOffset.UtcNow.AddHours(1),
                StaleUntil = DateTimeOffset.UtcNow.AddHours(2),
            },
            new MoodleSnapshotEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = otherOwnerId,
                ConnectionId = "persisted-connection-id",
                ConnectionAlias = "fieg",
                SnapshotType = "courses",
                CourseId = string.Empty,
                PayloadJson = "{\"owner\":\"other\"}",
                Tier = "warm",
                UpdatedAt = DateTimeOffset.UtcNow,
                FreshUntil = DateTimeOffset.UtcNow.AddHours(1),
                StaleUntil = DateTimeOffset.UtcNow.AddHours(2),
            });
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        using var listener = new MeterListener();
        long identityCommands = 0;
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.InstrumentationMeter) &&
                instrument.Name == "moodle_snapshot_identity_resolution_commands")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref identityCommands, value));
        listener.Start();
        var credentials = new MoodleCredentials(
            "client-1", "persisted-connection-id", "FIEG", "https://fieg.example", "user", "password", "fieg", false);
        var store = new MoodleSnapshotStore(
            db,
            cache,
            metrics,
            NullLogger<MoodleSnapshotStore>.Instance,
            credentialsProvider: new CachedCredentialsProvider(credentials));

        var result = await store.GetAsync<Dictionary<string, string>>(ownerId, "fieg", "courses");

        Assert.NotNull(result);
        Assert.Equal("requested", result!.Data["owner"]);
        Assert.Equal(0, identityCommands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_QuandoCredencialEstaAusenteOuAliasNaoConfere_UsaResolverLegado(bool returnMismatchedCredentials)
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-credentials-fallback-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = ownerId,
            Name = "Owner",
            Email = $"{ownerId:N}@example.test",
            PasswordHash = "hash",
            ConnectorClientId = "client-1",
        });
        db.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = "fieg-connection-id",
            ClientId = "client-1",
            MoodleAlias = "fieg",
            MoodleBaseUrl = "https://fieg.example",
            MoodleUsernameEncrypted = "user",
            MoodlePasswordEncrypted = "password",
            MoodleTarget = "fieg",
            IsActive = true,
        });
        db.MoodleSnapshots.Add(new MoodleSnapshotEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ConnectionId = "fieg-connection-id",
            ConnectionAlias = "fieg",
            SnapshotType = "courses",
            CourseId = string.Empty,
            PayloadJson = "[\"fieg-course\"]",
            Tier = "warm",
            UpdatedAt = DateTimeOffset.UtcNow,
            FreshUntil = DateTimeOffset.UtcNow.AddHours(1),
            StaleUntil = DateTimeOffset.UtcNow.AddHours(2),
        });
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        var cachedCredentials = returnMismatchedCredentials
            ? new MoodleCredentials("client-1", "senai-connection-id", "senai", "https://senai.example", "user", "password", "senai", false)
            : null;
        var store = new MoodleSnapshotStore(
            db,
            cache,
            metrics,
            NullLogger<MoodleSnapshotStore>.Instance,
            credentialsProvider: new CachedCredentialsProvider(cachedCredentials));

        var result = await store.GetAsync<string[]>(ownerId, "fieg", "courses");

        Assert.NotNull(result);
        Assert.Equal("fieg-course", Assert.Single(result!.Data));
    }

    [Fact]
    public async Task GetAsync_ConexaoDesativadaNaoCaiNoSnapshotLegadoDoAlias()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-disabled-connection-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = ownerId,
            Name = "Owner",
            Email = $"{ownerId:N}@example.test",
            PasswordHash = "hash",
            ConnectorClientId = "client-1",
        });
        db.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = "disabled-connection-id",
            ClientId = "client-1",
            MoodleAlias = "fieg",
            MoodleBaseUrl = "https://fieg.example",
            MoodleUsernameEncrypted = "user",
            MoodlePasswordEncrypted = "password",
            MoodleTarget = "fieg",
            IsActive = false,
        });
        db.MoodleSnapshots.Add(new MoodleSnapshotEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ConnectionId = string.Empty,
            ConnectionAlias = "fieg",
            SnapshotType = "courses",
            CourseId = string.Empty,
            PayloadJson = "[\"legacy-course\"]",
            Tier = "warm",
            UpdatedAt = DateTimeOffset.UtcNow,
            FreshUntil = DateTimeOffset.UtcNow.AddHours(1),
            StaleUntil = DateTimeOffset.UtcNow.AddHours(2),
        });
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetAsync<string[]>(ownerId, "fieg", "courses"));
    }

    [Fact]
    public async Task GetAsync_NaoRetemMetadadosQuandoConsultaFalha()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-query-failure-{Guid.NewGuid():N}")
            .AddInterceptors(new FailSnapshotQueries())
            .Options;
        await using var db = new ConnectorDbContext(options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var metrics = new MoodleSnapshotMetrics();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);

        for (var index = 0; index < 100; index++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                store.GetAsync<string[]>(ownerId, "review", "activities", index.ToString()));
        }

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, GetMetadataCount(ownerId));
    }

    [Fact]
    public async Task LeiturasConcorrentesMantemEntradaMaisRecenteNoCache()
    {
        var ownerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"snapshot-concurrent-fill-{Guid.NewGuid():N}")
            .Options;
        await using (var seed = new ConnectorDbContext(options))
        {
            seed.MoodleSnapshots.Add(new MoodleSnapshotEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                ConnectionId = string.Empty,
                ConnectionAlias = "review",
                SnapshotType = "courses",
                CourseId = string.Empty,
                PayloadJson = "[\"value\"]",
                Tier = "warm",
                UpdatedAt = DateTimeOffset.UtcNow,
                FreshUntil = DateTimeOffset.UtcNow.AddHours(1),
                StaleUntil = DateTimeOffset.UtcNow.AddHours(2),
            });
            await seed.SaveChangesAsync();
        }

        using var cache = new ConcurrentFillCache();
        using var metrics = new MoodleSnapshotMetrics();
        await using var db1 = new ConnectorDbContext(options);
        await using var db2 = new ConnectorDbContext(options);
        var store1 = new MoodleSnapshotStore(db1, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);
        var store2 = new MoodleSnapshotStore(db2, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);

        var results = await Task.WhenAll(
            Task.Run(() => store1.GetAsync<string[]>(ownerId, "review", "courses")),
            Task.Run(() => store2.GetAsync<string[]>(ownerId, "review", "courses")));

        Assert.All(results, result => Assert.Equal("value", Assert.Single(result!.Data)));
        await WaitUntilAsync(() => cache.LastKey is not null && cache.Inner.TryGetValue(cache.LastKey, out _));
    }

    private static int GetCacheMetadataCount()
    {
        var field = typeof(MoodleSnapshotStore).GetField(
            "CacheVersions",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return ((ICollection)field.GetValue(null)!).Count;
    }

    private static int GetMetadataCount(Guid ownerId) =>
        ((IDictionary)typeof(MoodleSnapshotStore)
            .GetField("CacheVersions", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!)
        .Keys.Cast<string>()
        .Count(key => key.StartsWith(ownerId.ToString("N") + ":", StringComparison.Ordinal));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "A condição não foi satisfeita dentro do prazo de teste.");
    }

    private sealed class CachedCredentialsProvider(MoodleCredentials? credentials)
        : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            credentials is null
                ? throw new NotSupportedException("Credentials were not pre-resolved for this test.")
                : Task.FromResult(credentials);

        public MoodleCredentials? TryGetResolvedCredentials(string connectionAlias) => credentials;
    }

    private sealed class FailSnapshotQueries : IQueryExpressionInterceptor
    {
        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            var visitor = new SnapshotQueryVisitor();
            visitor.Visit(queryExpression);
            if (visitor.Found)
            {
                throw new TimeoutException("Falha injetada na consulta de snapshots.");
            }

            return queryExpression;
        }
    }

    private sealed class SnapshotQueryVisitor : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root && root.EntityType.ClrType == typeof(MoodleSnapshotEntity))
            {
                Found = true;
            }

            return base.VisitExtension(node);
        }
    }

    private sealed class ConcurrentFillCache : IMemoryCache
    {
        public MemoryCache Inner { get; } = new(new MemoryCacheOptions());
        private readonly Barrier fills = new(2);
        public object? LastKey { get; private set; }

        public ICacheEntry CreateEntry(object key)
        {
            LastKey = key;
            if (!fills.SignalAndWait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("O preenchimento concorrente não encontrou a segunda leitura.");
            }

            return Inner.CreateEntry(key);
        }

        public bool TryGetValue(object key, out object? value) => Inner.TryGetValue(key, out value);

        public void Remove(object key) => Inner.Remove(key);

        public void Dispose()
        {
            Inner.Dispose();
            fills.Dispose();
        }
    }
}
