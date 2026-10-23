using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Configuration;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class MoodleSnapshotSyncQueuePostgresIntegrationTests
{
    [Fact]
    public async Task RepeatedCourseOverviewRefreshReportsTheExistingDurableJobAsQueued()
    {
        var fixture = await CreateFixtureAsync();
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;
        var request = fixture.Request with
        {
            Dataset = MoodleSnapshotDatasets.Students,
            CourseId = "42",
            WorkPolicy = MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1,
            Trigger = "course_overview",
        };
        var gradebookRequest = request with
        {
            Dataset = MoodleSnapshotDatasets.Gradebook,
            WorkPolicy = MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1,
        };

        Assert.True(await fixture.Queue.EnqueueAsync(request));
        Assert.True(await fixture.Queue.EnqueueAsync(request));
        Assert.True(await fixture.Queue.EnqueueAsync(gradebookRequest));
        Assert.True(await fixture.Queue.EnqueueAsync(gradebookRequest));

        await using var verifyDb = fixture.CreateDbContext();
        var studentState = await verifyDb.MoodleSyncStates.SingleAsync(item =>
            item.OwnerId == fixture.OwnerId &&
            item.Dataset == MoodleSnapshotDatasets.Students &&
            item.CourseId == "42");
        var gradebookState = await verifyDb.MoodleSyncStates.SingleAsync(item =>
            item.OwnerId == fixture.OwnerId &&
            item.Dataset == MoodleSnapshotDatasets.Gradebook &&
            item.CourseId == "42");
        Assert.Equal("pending", studentState.Status);
        Assert.Equal(MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1, studentState.WorkPolicy);
        Assert.Equal("pending", gradebookState.Status);
        Assert.Equal(MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1, gradebookState.WorkPolicy);
        Assert.Equal(3, await verifyDb.MoodleSyncStates.CountAsync(item => item.OwnerId == fixture.OwnerId));
    }

    [Fact]
    public async Task ConcurrentCourseOverviewRefreshesReuseOneDurablePendingJob()
    {
        var fixture = await CreateFixtureAsync();
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;
        var request = fixture.Request with
        {
            Dataset = MoodleSnapshotDatasets.Students,
            CourseId = "42",
            WorkPolicy = MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1,
            Trigger = "course_overview",
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => fixture.Queue.EnqueueAsync(request)));

        Assert.All(results, accepted => Assert.True(accepted));
        await using var verifyDb = fixture.CreateDbContext();
        var states = await verifyDb.MoodleSyncStates.Where(item =>
            item.OwnerId == fixture.OwnerId &&
            item.Dataset == MoodleSnapshotDatasets.Students &&
            item.CourseId == "42").ToArrayAsync();
        var state = Assert.Single(states);
        Assert.Equal("pending", state.Status);
        Assert.Equal(MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1, state.WorkPolicy);
    }

    [Fact]
    public async Task CreatorReportsQueuedWhenAnotherCallerSignalsItsCommittedJobFirst()
    {
        var interceptor = new AfterSaveInterceptor();
        var fixture = await CreateFixtureAsync(afterSaveInterceptor: interceptor);
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;
        var request = fixture.Request with
        {
            Dataset = MoodleSnapshotDatasets.Students,
            CourseId = "42",
            WorkPolicy = MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1,
            Trigger = "course_overview",
        };

        // Pause the creator after its commit, before it signals the scheduler.
        // A second caller sees that committed row and signals it first.
        interceptor.AfterSave = async cancellationToken =>
            Assert.True(await fixture.Queue.EnqueueAsync(request, cancellationToken));

        Assert.True(await fixture.Queue.EnqueueAsync(request));
        await using var verifyDb = fixture.CreateDbContext();
        var state = Assert.Single(await verifyDb.MoodleSyncStates.Where(item =>
            item.OwnerId == fixture.OwnerId &&
            item.Dataset == MoodleSnapshotDatasets.Students &&
            item.CourseId == "42").ToArrayAsync());
        Assert.Equal("pending", state.Status);
        Assert.Equal(MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1, state.WorkPolicy);
    }

    [Fact]
    public async Task CourseOverviewWorkerStopsBeforeTheNinthHttpCallAndAppliesMinimumBackoff()
    {
        var fixture = await CreateFixtureAsync(
            dataset: MoodleSnapshotDatasets.Students,
            courseId: "course-1",
            workPolicy: MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1,
            seedCourseOverviewCourse: true,
            seedCompleteStudentsSnapshot: true);
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;
        var reservationAttempts = 0;
        var permittedHttpCalls = 0;
        fixture.Gateway.ParticipantsReader = _ =>
        {
            for (var call = 0; call < 9; call++)
            {
                reservationAttempts++;
                MoodleOverviewStudentSyncBudget.ReserveCall();
                permittedHttpCalls++;
            }

            throw new InvalidOperationException("The call budget did not reject the ninth reservation.");
        };

        await fixture.Queue.ProcessRequestAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(9, reservationAttempts);
        Assert.Equal(8, permittedHttpCalls);
        await using var verifyDb = fixture.CreateDbContext();
        var state = await verifyDb.MoodleSyncStates.SingleAsync(item => item.Id == fixture.StateId);
        Assert.Equal("failed", state.Status);
        Assert.Null(state.LeaseUntil);
        Assert.True(state.NextSyncAt.HasValue && state.NextSyncAt.Value - state.UpdatedAt >= TimeSpan.FromMinutes(5));
        var previousHead = await verifyDb.MoodleSnapshots.SingleAsync(item =>
            item.OwnerId == fixture.OwnerId && item.SnapshotType == MoodleSnapshotDatasets.Students);
        Assert.Equal("previous-students-hash", previousHead.PayloadHash);
        var run = await verifyDb.MoodleSnapshotRuns.SingleAsync(item => item.OwnerId == fixture.OwnerId);
        Assert.Equal("failed", run.Status);
    }

    [Fact]
    public async Task CourseOverviewWorkerTimeoutCancelsTheReadAndAppliesMinimumBackoff()
    {
        var fixture = await CreateFixtureAsync(
            dataset: MoodleSnapshotDatasets.Students,
            courseId: "course-1",
            workPolicy: MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1,
            seedCourseOverviewCourse: true,
            seedCompleteStudentsSnapshot: true,
            courseOverviewSyncTimeout: TimeSpan.FromMilliseconds(500));
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;
        var cancellationObserved = false;
        fixture.Gateway.ParticipantsReader = async cancellationToken =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cancellationObserved = cancellationToken.IsCancellationRequested;
            }

            return new CourseParticipantsPage(
                "course-1", 1, 500, ParticipantStatusFilter.Active, true, false, false, [], null);
        };

        await fixture.Queue.ProcessRequestAsync(fixture.Request, CancellationToken.None);

        Assert.True(cancellationObserved);
        await using var verifyDb = fixture.CreateDbContext();
        var state = await verifyDb.MoodleSyncStates.SingleAsync(item => item.Id == fixture.StateId);
        Assert.Equal("failed", state.Status);
        Assert.Null(state.LeaseUntil);
        Assert.True(state.NextSyncAt.HasValue && state.NextSyncAt.Value - state.UpdatedAt >= TimeSpan.FromMinutes(5));
        var previousHead = await verifyDb.MoodleSnapshots.SingleAsync(item =>
            item.OwnerId == fixture.OwnerId && item.SnapshotType == MoodleSnapshotDatasets.Students);
        Assert.Equal("previous-students-hash", previousHead.PayloadHash);
        var run = await verifyDb.MoodleSnapshotRuns.SingleAsync(item => item.OwnerId == fixture.OwnerId);
        Assert.Equal("failed", run.Status);
    }

    [Fact]
    public async Task InvalidTokenResponseReleasesEnforcedAdmissionAndDurableSnapshotCanRetry()
    {
        var fixture = await CreateFixtureAsync(
            dataset: MoodleSnapshotDatasets.Students,
            courseId: "course-1",
            workPolicy: MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1,
            seedCourseOverviewCourse: true,
            seedCompleteStudentsSnapshot: true);
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;
        var admissionOptions = Options.Create(new MoodleAdmissionOptions
        {
            Mode = MoodleAdmissionMode.Enforce,
            MaxConcurrentGlobal = 1,
            MaxConcurrentPerInstallation = 1,
            MaxConcurrentPerConnection = 1,
            QueueLimitGlobal = 2,
            QueueLimitPerInstallation = 2,
            QueueLimitPerConnection = 2,
            QueueWaitTimeoutSeconds = 1,
            MaximumTrackedConnections = 5,
            MaximumTrackedInstallations = 5
        });
        using var coordinator = new MoodleAdmissionCoordinator(admissionOptions, TimeProvider.System);
        var transport = new SnapshotTokenResponseHandler();
        using var httpClient = new HttpClient(new MoodleAdmissionAttemptHandler(
            admissionOptions,
            TimeProvider.System,
            coordinator)
        { InnerHandler = transport });
        var tokenProvider = new MoodleAccessTokenProvider(
            httpClient,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MoodleApiOptions()),
            Options.Create(new ConnectorSecretsOptions { TokenCacheMinutes = 20 }),
            new FixedEndpointValidator("https://integration.example"),
            NullLogger<MoodleAccessTokenProvider>.Instance,
            new MoodleAdmissionContextFactory(
                admissionOptions,
                Options.Create(new MoodleApiOptions { HttpTimeoutSeconds = 7 }),
                new ConnectorExecutionContext(),
                TimeProvider.System));
        var credentials = new MoodleCredentials(
            fixture.Request.ClientId,
            fixture.ConnectionId,
            fixture.Request.ConnectionAlias,
            "https://integration.example",
            "synthetic-user",
            "synthetic-password",
            fixture.Request.ConnectionAlias,
            false);
        fixture.Gateway.ParticipantsReader = async cancellationToken =>
        {
            await tokenProvider.GetAccessTokenAsync(credentials, cancellationToken);
            return new CourseParticipantsPage(
                "course-1", 1, 500, ParticipantStatusFilter.Active, true, false, false, [], null);
        };

        await fixture.Queue.ProcessRequestAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(1, transport.Calls);
        Assert.NotNull(transport.LastAdmissionContext);
        Assert.Equal(MoodleAdmissionOrigin.Snapshot, transport.LastAdmissionContext.Origin);
        Assert.Equal(MoodleAdmissionPriority.Background, transport.LastAdmissionContext.Priority);
        Assert.Equal(MoodleAdmissionOperation.Token, transport.LastAdmissionContext.Operation);
        Assert.Equal(fixture.OwnerId.ToString("N"), transport.LastAdmissionContext.OwnerId);
        Assert.Equal(1, transport.LastAdmissionContext.AttemptBudget.AttemptsUsed);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);

        await using (var failedDb = fixture.CreateDbContext())
        {
            var failedState = await failedDb.MoodleSyncStates.SingleAsync(item => item.Id == fixture.StateId);
            Assert.Equal("failed", failedState.Status);
            Assert.Null(failedState.LeaseUntil);
            Assert.True(failedState.NextSyncAt is { } nextSyncAt &&
                        nextSyncAt - failedState.UpdatedAt >= TimeSpan.FromMinutes(5));
            Assert.NotNull(failedState.LastError);
            var failedRun = await failedDb.MoodleSnapshotRuns.SingleAsync(item => item.OwnerId == fixture.OwnerId);
            Assert.Equal("failed", failedRun.Status);
        }

        var retryRequest = fixture.Request with { Force = true };
        Assert.True(await fixture.Queue.EnqueueAsync(retryRequest));
        await fixture.Queue.ProcessRequestAsync(retryRequest, CancellationToken.None);

        Assert.Equal(2, transport.Calls);
        Assert.NotNull(transport.LastAdmissionContext);
        Assert.Equal(MoodleAdmissionOrigin.Snapshot, transport.LastAdmissionContext.Origin);
        Assert.Equal(1, transport.LastAdmissionContext.AttemptBudget.AttemptsUsed);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
        await using var recoveredDb = fixture.CreateDbContext();
        var recoveredState = await recoveredDb.MoodleSyncStates.SingleAsync(item => item.Id == fixture.StateId);
        Assert.Equal("succeeded", recoveredState.Status);
        Assert.Equal(2, recoveredState.AttemptCount);
        Assert.Null(recoveredState.LeaseUntil);
        Assert.Null(recoveredState.LastError);
        var runStatuses = await recoveredDb.MoodleSnapshotRuns
            .Where(item => item.OwnerId == fixture.OwnerId)
            .Select(item => item.Status)
            .ToArrayAsync();
        Assert.Equal(2, runStatuses.Length);
        Assert.Contains("failed", runStatuses);
        Assert.Contains("succeeded", runStatuses);
    }

    [Fact]
    public async Task CourseOverviewGradebookLimitsRosterToOnePageAndPreservesCompleteHeadWhenTruncated()
    {
        var fixture = await CreateFixtureAsync(
            dataset: MoodleSnapshotDatasets.Gradebook,
            courseId: "course-1",
            workPolicy: MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1,
            seedCourseOverviewCourse: true,
            seedCompleteGradebookSnapshot: true);
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;
        fixture.Gateway.ParticipantsReader = _ => Task.FromResult(new CourseParticipantsPage(
            "course-1", 1, 500, ParticipantStatusFilter.Active, true, false, true, []));

        await fixture.Queue.ProcessRequestAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(ParticipantStatusFilter.Active, fixture.Gateway.LastParticipantsRequest?.StatusFilter);
        Assert.Equal(1, fixture.Gateway.LastParticipantsRequest?.Page);
        Assert.Equal(500, fixture.Gateway.LastParticipantsRequest?.PageSize);
        Assert.True(fixture.Gateway.LastParticipantsRequest?.StudentsOnly);
        Assert.False(fixture.Gateway.LastParticipantsRequest?.IncludeEmail);
        await using var verifyDb = fixture.CreateDbContext();
        var previousHead = await verifyDb.MoodleSnapshots.SingleAsync(item =>
            item.OwnerId == fixture.OwnerId && item.SnapshotType == MoodleSnapshotDatasets.Gradebook);
        Assert.Equal("previous-gradebook-hash", previousHead.PayloadHash);
        var state = await verifyDb.MoodleSyncStates.SingleAsync(item => item.Id == fixture.StateId);
        Assert.Equal("partial", state.Status);
    }

    [Fact]
    public async Task MoodleReadRunsWithoutTransactionAndSnapshotPublicationCommitsAfterIt()
    {
        var fixture = await CreateFixtureAsync();
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;

        await fixture.Queue.ProcessRequestAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(0, fixture.Gateway.ActiveTransactionsDuringCourseRead);
        Assert.True(fixture.Probe.StartedTransactions > fixture.Gateway.TransactionsStartedBeforeCourseRead);
        await using var verifyDb = fixture.CreateDbContext();
        var snapshot = await verifyDb.MoodleSnapshots.SingleAsync(item => item.OwnerId == fixture.OwnerId);
        var state = await verifyDb.MoodleSyncStates.SingleAsync(item => item.Id == fixture.StateId);
        Assert.Equal("courses", snapshot.SnapshotType);
        Assert.Equal("succeeded", state.Status);
    }

    [Fact]
    public async Task LostAttemptCannotPublishItsPreparedSnapshotOrOverwriteTheNewLease()
    {
        var fixture = await CreateFixtureAsync();
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;
        fixture.Gateway.BeforeCourseReadReturns = async cancellationToken =>
        {
            await using var db = fixture.CreateDbContext();
            var takeoverAt = DateTimeOffset.UtcNow;
            await db.MoodleSyncStates
                .Where(item => item.Id == fixture.StateId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                    .SetProperty(item => item.Status, "running")
                    .SetProperty(item => item.LeaseUntil, takeoverAt.AddHours(1))
                    .SetProperty(item => item.UpdatedAt, takeoverAt), cancellationToken);
        };

        await fixture.Queue.ProcessRequestAsync(fixture.Request, CancellationToken.None);

        await using var verifyDb = fixture.CreateDbContext();
        Assert.False(await verifyDb.MoodleSnapshots.AnyAsync(item => item.OwnerId == fixture.OwnerId));
        var state = await verifyDb.MoodleSyncStates.SingleAsync(item => item.Id == fixture.StateId);
        Assert.Equal("running", state.Status);
        Assert.Equal(2, state.AttemptCount);
        var run = await verifyDb.MoodleSnapshotRuns.SingleAsync(item => item.OwnerId == fixture.OwnerId);
        Assert.Equal("failed", run.Status);
    }

    [Fact]
    public async Task PartialCourseRefreshPreservesThePreviousCompleteHead()
    {
        var fixture = await CreateFixtureAsync(totalCourses: 2, coursePageSize: 1, maxCoursePages: 1, seedCompleteCourseSnapshot: true);
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;

        await fixture.Queue.ProcessRequestAsync(fixture.Request, CancellationToken.None);

        await using var verifyDb = fixture.CreateDbContext();
        var snapshot = await verifyDb.MoodleSnapshots.SingleAsync(item => item.OwnerId == fixture.OwnerId);
        var state = await verifyDb.MoodleSyncStates.SingleAsync(item => item.Id == fixture.StateId);
        Assert.Contains("previous-complete", snapshot.PayloadJson, StringComparison.Ordinal);
        Assert.True(snapshot.IsComplete);
        Assert.Equal("partial_refresh_preserved", snapshot.LastError);
        Assert.Equal("partial", state.Status);
    }

    [Fact]
    public async Task FailedPublicationCommitRollsBackSnapshotAndKeepsFailureStateRecoverable()
    {
        var fixture = await CreateFixtureAsync();
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;
        fixture.Gateway.BeforeCourseReadReturns = _ =>
        {
            fixture.Probe.FailNextCommit = true;
            return Task.CompletedTask;
        };

        await fixture.Queue.ProcessRequestAsync(fixture.Request, CancellationToken.None);

        await using var verifyDb = fixture.CreateDbContext();
        Assert.False(await verifyDb.MoodleSnapshots.AnyAsync(item => item.OwnerId == fixture.OwnerId));
        var state = await verifyDb.MoodleSyncStates.SingleAsync(item => item.Id == fixture.StateId);
        Assert.Equal("failed", state.Status);
        Assert.NotNull(state.NextSyncAt);
        var run = await verifyDb.MoodleSnapshotRuns.SingleAsync(item => item.OwnerId == fixture.OwnerId);
        Assert.Equal("failed", run.Status);
    }

    [Fact]
    public async Task RecoveryPollAdmitsOtherConnectionsDespiteLargeBacklogFromOneConnection()
    {
        var fixture = await CreateFixtureAsync(perConnectionConcurrency: 2);
        await using var provider = fixture.Provider;
        using var metrics = fixture.Metrics;
        var secondConnectionId = $"queue-it-other-{Guid.NewGuid():N}";

        await using (var db = fixture.CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            db.MoodleSyncStates.AddRange(Enumerable.Range(0, 40).Select(index => new MoodleSyncStateEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = fixture.OwnerId,
                ConnectionId = fixture.ConnectionId,
                ConnectionAlias = fixture.Request.ConnectionAlias,
                Dataset = MoodleSnapshotDatasets.Courses,
                CourseId = $"backlog-{index}",
                ClientId = fixture.Request.ClientId,
                UserExternalId = fixture.Request.UserExternalId,
                Status = "pending",
                NextSyncAt = now.AddMinutes(-1),
                UpdatedAt = now,
            }));
            db.MoodleSyncStates.Add(new MoodleSyncStateEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = fixture.OwnerId,
                ConnectionId = secondConnectionId,
                ConnectionAlias = "other-connection",
                Dataset = MoodleSnapshotDatasets.Courses,
                CourseId = "other-course",
                ClientId = fixture.Request.ClientId,
                UserExternalId = fixture.Request.UserExternalId,
                Status = "pending",
                NextSyncAt = now.AddMinutes(-1),
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var recovered = await fixture.Queue.LoadDueRequestsAsync(CancellationToken.None);

        Assert.Equal(2, recovered.Count(request => request.ConnectionId == fixture.ConnectionId));
        Assert.Contains(recovered, request => request.ConnectionId == secondConnectionId);
    }

    private static async Task<QueueFixture> CreateFixtureAsync(
        int totalCourses = 1,
        int coursePageSize = 100,
        int maxCoursePages = 100,
        bool seedCompleteCourseSnapshot = false,
        int perConnectionConcurrency = 1,
        string dataset = MoodleSnapshotDatasets.Courses,
        string? courseId = null,
        string workPolicy = MoodleSnapshotWorkPolicies.Default,
        bool seedCourseOverviewCourse = false,
        bool seedCompleteStudentsSnapshot = false,
        bool seedCompleteGradebookSnapshot = false,
        TimeSpan? courseOverviewSyncTimeout = null,
        AfterSaveInterceptor? afterSaveInterceptor = null)
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "MOODLE_POSTGRES_TEST_CONNECTION e obrigatoria; o gate PostgreSQL nao pode ser silenciosamente ignorado.");

        var probe = new TransactionProbe();
        var dbOptions = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(probe)
            .Options;
        var ownerId = Guid.NewGuid();
        var clientId = $"queue-it-client-{Guid.NewGuid():N}"[..40];
        var connectionId = $"queue-it-connection-{Guid.NewGuid():N}"[..48];
        var alias = $"queue-it-{Guid.NewGuid():N}"[..32];
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = new ConnectorDbContext(dbOptions))
        {
            await db.ApplyVersionedSchemaAsync();
            db.UserAccounts.Add(new UserAccountEntity
            {
                Id = ownerId,
                Name = "Queue Integration Owner",
                Email = $"{ownerId:N}@queue-integration.test",
                PasswordHash = "not-a-real-password",
                ConnectorClientId = clientId,
            });
            db.ConnectorClients.Add(new ConnectorClientCredentialEntity
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
            db.MoodleSyncStates.Add(new MoodleSyncStateEntity
            {
                Id = stateId,
                OwnerId = ownerId,
                ConnectionId = connectionId,
                ConnectionAlias = alias,
                Dataset = dataset,
                CourseId = courseId ?? string.Empty,
                ClientId = clientId,
                UserExternalId = ownerId.ToString("N"),
                Status = "pending",
                NextSyncAt = now.AddMinutes(-1),
                UpdatedAt = now,
                WorkPolicy = workPolicy,
            });
            if (seedCompleteCourseSnapshot)
            {
                db.MoodleSnapshots.Add(new MoodleSnapshotEntity
                {
                    Id = Guid.NewGuid(),
                    OwnerId = ownerId,
                    ConnectionId = connectionId,
                    ConnectionAlias = alias,
                    SnapshotType = MoodleSnapshotDatasets.Courses,
                    CourseId = string.Empty,
                    PayloadJson = "{\"state\":\"previous-complete\"}",
                    PayloadHash = "previous-hash",
                    IsComplete = true,
                    RecordCount = 1,
                    UpdatedAt = now,
                });
            }
            if (seedCourseOverviewCourse)
            {
                var course = new CourseSummary(
                    "course-1", null, "course-one", "Course One", "Course One", null, null,
                    null, null, true, null, null, null, null, null, null);
                db.MoodleSnapshots.Add(new MoodleSnapshotEntity
                {
                    Id = Guid.NewGuid(),
                    OwnerId = ownerId,
                    ConnectionId = connectionId,
                    ConnectionAlias = alias,
                    SnapshotType = MoodleSnapshotDatasets.Courses,
                    CourseId = string.Empty,
                    PayloadJson = JsonSerializer.Serialize(new[] { course }),
                    PayloadHash = "course-summary-hash",
                    IsComplete = true,
                    RecordCount = 1,
                    UpdatedAt = now,
                });
            }
            if (seedCompleteStudentsSnapshot)
            {
                db.MoodleSnapshots.Add(new MoodleSnapshotEntity
                {
                    Id = Guid.NewGuid(),
                    OwnerId = ownerId,
                    ConnectionId = connectionId,
                    ConnectionAlias = alias,
                    SnapshotType = MoodleSnapshotDatasets.Students,
                    CourseId = courseId ?? string.Empty,
                    PayloadJson = "{}",
                    PayloadHash = "previous-students-hash",
                    IsComplete = true,
                    RecordCount = 1,
                    UpdatedAt = now,
                });
            }
            if (seedCompleteGradebookSnapshot)
            {
                db.MoodleSnapshots.Add(new MoodleSnapshotEntity
                {
                    Id = Guid.NewGuid(),
                    OwnerId = ownerId,
                    ConnectionId = connectionId,
                    ConnectionAlias = alias,
                    SnapshotType = MoodleSnapshotDatasets.Gradebook,
                    CourseId = courseId ?? string.Empty,
                    PayloadJson = "{}",
                    PayloadHash = "previous-gradebook-hash",
                    IsComplete = true,
                    RecordCount = 1,
                    UpdatedAt = now,
                });
            }
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ConnectorDbContext>(options =>
        {
            options.UseNpgsql(connectionString).AddInterceptors(probe);
            if (afterSaveInterceptor is not null) options.AddInterceptors(afterSaveInterceptor);
        });
        services.AddMemoryCache();
        services.Configure<MoodleSnapshotOptions>(_ => { });
        var metrics = new MoodleSnapshotMetrics();
        services.AddSingleton(metrics);
        services.AddScoped<IMoodleSnapshotStore, MoodleSnapshotStore>();
        services.AddScoped<IConnectorExecutionContext, ConnectorExecutionContext>();
        services.AddScoped<IMoodleConnectionSelection, MoodleConnectionSelection>();
        var gateway = new QueueTestGateway(probe, totalCourses);
        services.AddSingleton(gateway);
        services.AddScoped<IMoodleCoursesGateway>(sp => sp.GetRequiredService<QueueTestGateway>());
        services.AddScoped<IMoodleCourseContentsGateway>(sp => sp.GetRequiredService<QueueTestGateway>());
        services.AddScoped<IMoodleParticipantsGateway>(sp => sp.GetRequiredService<QueueTestGateway>());
        services.AddScoped<IMoodleAssignmentSubmissionsGateway>(sp => sp.GetRequiredService<QueueTestGateway>());
        services.AddScoped<IMoodleAssignmentSettingsGateway>(sp => sp.GetRequiredService<QueueTestGateway>());
        services.AddScoped<IMoodleAssignmentGradeReadGateway>(sp => sp.GetRequiredService<QueueTestGateway>());
        services.AddScoped<IMoodleGradebookGateway>(sp => sp.GetRequiredService<QueueTestGateway>());
        var provider = services.BuildServiceProvider();
        var queue = new MoodleSnapshotSyncQueue(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MoodleSnapshotSyncQueue>.Instance,
            metrics,
            Options.Create(new MoodleSnapshotOptions
            {
                GlobalConcurrency = 1,
                PerConnectionConcurrency = perConnectionConcurrency,
                CoursePageSize = coursePageSize,
                MaxCoursePages = maxCoursePages,
            }),
            courseOverviewSyncTimeout);
        var request = new MoodleSnapshotSyncRequest(
            ownerId,
            clientId,
            alias,
            ownerId.ToString("N"),
            Dataset: dataset,
            ConnectionId: connectionId,
            CourseId: courseId,
            Trigger: workPolicy == MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1 ? "course_overview" : "scheduled",
            WorkPolicy: workPolicy);
        return new QueueFixture(provider, metrics, queue, request, gateway, probe, ownerId, stateId, connectionId, dbOptions);
    }

    private sealed record QueueFixture(
        ServiceProvider Provider,
        MoodleSnapshotMetrics Metrics,
        MoodleSnapshotSyncQueue Queue,
        MoodleSnapshotSyncRequest Request,
        QueueTestGateway Gateway,
        TransactionProbe Probe,
        Guid OwnerId,
        Guid StateId,
        string ConnectionId,
        DbContextOptions<ConnectorDbContext> DbOptions)
    {
        public ConnectorDbContext CreateDbContext() => new(DbOptions);
    }

    private sealed class SnapshotTokenResponseHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public MoodleAdmissionContext? LastAdmissionContext { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out var context);
            LastAdmissionContext = context;
            var body = Calls == 1
                ? "<html>not-json</html>"
                : """{"token":"synthetic-recovered-token"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FixedEndpointValidator(string endpoint) : IMoodleEndpointValidator
    {
        public Task<Uri> ValidateAsync(string baseUrl, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri(endpoint));
    }

    private sealed class AfterSaveInterceptor : SaveChangesInterceptor
    {
        public Func<CancellationToken, Task>? AfterSave { get; set; }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            var callback = AfterSave;
            AfterSave = null;
            if (callback is not null) await callback(cancellationToken);
            return result;
        }
    }

    private sealed class TransactionProbe : DbTransactionInterceptor
    {
        private int _activeTransactions;
        private int _startedTransactions;
        private int _failNextCommit;

        public int ActiveTransactions => Volatile.Read(ref _activeTransactions);
        public int StartedTransactions => Volatile.Read(ref _startedTransactions);
        public bool FailNextCommit
        {
            set => Interlocked.Exchange(ref _failNextCommit, value ? 1 : 0);
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _activeTransactions);
            Interlocked.Increment(ref _startedTransactions);
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Decrement(ref _activeTransactions);
            return Task.CompletedTask;
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failNextCommit, 0) == 1)
            {
                throw new InvalidOperationException("Simulated publication commit failure.");
            }

            return ValueTask.FromResult(result);
        }

        public override Task TransactionRolledBackAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Decrement(ref _activeTransactions);
            return Task.CompletedTask;
        }
    }

    private sealed class QueueTestGateway(TransactionProbe probe, int totalCourses) :
        IMoodleCoursesGateway,
        IMoodleCourseContentsGateway,
        IMoodleParticipantsGateway,
        IMoodleAssignmentSubmissionsGateway,
        IMoodleAssignmentSettingsGateway,
        IMoodleAssignmentGradeReadGateway,
        IMoodleGradebookGateway
    {
        public int ActiveTransactionsDuringCourseRead { get; private set; } = -1;
        public int TransactionsStartedBeforeCourseRead { get; private set; }
        public Func<CancellationToken, Task>? BeforeCourseReadReturns { get; set; }
        public Func<CancellationToken, Task<CourseParticipantsPage>>? ParticipantsReader { get; set; }
        public (ParticipantStatusFilter StatusFilter, int Page, int PageSize, bool StudentsOnly, bool IncludeEmail, string? GroupId)? LastParticipantsRequest { get; private set; }

        public async Task<PagedCourses> GetMyCoursesAsync(string userExternalId, int limit, int page, CancellationToken cancellationToken)
        {
            ActiveTransactionsDuringCourseRead = probe.ActiveTransactions;
            TransactionsStartedBeforeCourseRead = probe.StartedTransactions;
            if (BeforeCourseReadReturns is not null)
            {
                await BeforeCourseReadReturns(cancellationToken);
            }
            return new PagedCourses([new CourseSummary(
                "course-1", null, "course-one", "Course One", null, null, null, null, null, true, null, null, null, null, null, null)],
                totalCourses,
                page,
                limit);
        }

        public Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(string userExternalId, string query, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CourseSummary?> GetMyCourseAsync(string userExternalId, string courseId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CourseContentsSummary> GetCourseContentsAsync(string userExternalId, string courseId, IReadOnlyCollection<string> moduleTypes, bool includeHidden, bool onlyWithFiles, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
            string courseId, int maxCandidates, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This sync fixture does not support limited previews.");

        public Task<CourseParticipantsPage> GetCourseParticipantsAsync(string userExternalId, string courseId, ParticipantStatusFilter statusFilter, int page, int pageSize, bool studentsOnly, bool includeEmail, string? groupId, CancellationToken cancellationToken)
        {
            LastParticipantsRequest = (statusFilter, page, pageSize, studentsOnly, includeEmail, groupId);
            return ParticipantsReader is null
                ? throw new NotSupportedException()
                : ParticipantsReader(cancellationToken);
        }

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(string userExternalId, string courseId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(string userExternalId, string assignmentId, string? status, DateTimeOffset? since, DateTimeOffset? before, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(string userExternalId, string courseId, string assignmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssignmentExistingGrade?> GetExistingGradeAsync(string userExternalId, string assignmentId, string studentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CourseGradebook> GetStudentGradebookAsync(string courseId, string studentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
