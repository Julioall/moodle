using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Application.Courses;
using Moodle.Application.Grading;
using Moodle.Application.Submissions;
using Moodle.Domain;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class GradingRunIdempotencyPostgresTests
{
    [Fact]
    public async Task SelectedRun_ReplaysChildBatchesAfterCrashWithoutDuplicatingSelectedItems()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail())
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var owner = $"selection-crash-{Guid.NewGuid():N}";
        var key = Guid.NewGuid().ToString("N");
        const string connection = "selection-crash-connection";
        const string client = "selection-crash-client";
        var command = new StartPendingGradingRunCommand(
            UserExternalId: "321",
            MaxCourses: 1,
            MaxItemsPerBatch: 1,
            CourseId: "10",
            AssignmentIds: ["501"],
            IncludeAlreadyGraded: true,
            SubmissionIds: ["9003", "9001", "9002"],
            IdempotencyKey: key,
            MoodleConnectionId: connection,
            SnapshotClientId: client,
            SnapshotConnectionAlias: "synthetic");

        await using (var interruptedDb = new ConnectorDbContext(options))
        {
            var repository = new GradingReviewRepository(interruptedDb);
            var mediator = new SelectionRunMediator(options, owner, connection, client, crashOnSecondChild: true);
            var handler = CreateSelectionHandler(mediator, repository, owner);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                handler.Handle(command, CancellationToken.None));
            Assert.Equal(2, mediator.ChildCreationCalls);
        }

        Guid interruptedRunId;
        await using (var inspectDb = new ConnectorDbContext(options))
        {
            var interruptedRun = await inspectDb.GradingRuns.AsNoTracking()
                .SingleAsync(run => run.CreatedBySubject == owner && run.IdempotencyKey == key);
            interruptedRunId = interruptedRun.Id;
            Assert.Equal(GradingRunStatus.Preparing, interruptedRun.Status);
            var interruptedChildren = await inspectDb.GradingBatches.AsNoTracking()
                .Where(batch => batch.GradingRunId == interruptedRunId)
                .ToArrayAsync();
            Assert.Single(interruptedChildren);
            Assert.Single(await inspectDb.GradingItems.AsNoTracking()
                .Where(item => item.BatchId == interruptedChildren[0].Id)
                .ToArrayAsync());
        }

        await using (var changedAttemptDb = new ConnectorDbContext(options))
        {
            var repository = new GradingReviewRepository(changedAttemptDb);
            var changedAttemptMediator = new SelectionRunMediator(
                options,
                owner,
                connection,
                client,
                crashOnSecondChild: false,
                attemptNumber: 2);
            var handler = CreateSelectionHandler(changedAttemptMediator, repository, owner);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.Handle(command, CancellationToken.None));
            Assert.Equal(0, changedAttemptMediator.ChildCreationCalls);
        }

        StartPendingGradingRunResult resumed;
        await using (var resumedDb = new ConnectorDbContext(options))
        {
            var repository = new GradingReviewRepository(resumedDb);
            var mediator = new SelectionRunMediator(options, owner, connection, client, crashOnSecondChild: false);
            var handler = CreateSelectionHandler(mediator, repository, owner);
            resumed = await handler.Handle(command, CancellationToken.None);
            Assert.Equal(3, mediator.ChildCreationCalls);
        }

        Assert.Equal(interruptedRunId, resumed.GradingRunId);
        Assert.Equal(3, resumed.TotalItems);
        Assert.Equal(3, resumed.Batches.Count);

        await using var verifyDb = new ConnectorDbContext(options);
        var persistedRun = await verifyDb.GradingRuns.AsNoTracking()
            .SingleAsync(run => run.Id == interruptedRunId);
        Assert.Equal(GradingRunStatus.Ready, persistedRun.Status);
        Assert.Equal(3, persistedRun.ExpectedItemCount);
        Assert.Equal(3, persistedRun.ExpectedBatchCount);

        var batches = await verifyDb.GradingBatches.AsNoTracking()
            .Where(batch => batch.GradingRunId == interruptedRunId)
            .ToArrayAsync();
        Assert.Equal(3, batches.Length);
        Assert.Equal(3, batches.Select(batch => batch.IdempotencyKey).Distinct().Count());

        var items = await verifyDb.GradingItems.AsNoTracking()
            .Where(item => batches.Select(batch => batch.Id).Contains(item.BatchId))
            .ToArrayAsync();
        Assert.Equal([9001L, 9002L, 9003L], items.Select(item => item.SubmissionId!.Value).Order().ToArray());
        Assert.Equal(3, items.Select(item => item.SubmissionId).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentReservation_ReplaysSameRunAndScopesKeysByClientAndConnection()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail())
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var owner = $"run-reservation-{Guid.NewGuid():N}";
        var key = Guid.NewGuid().ToString("N");
        var digest = new string('a', 64);
        const string client = "selection-client-a";
        const string connection = "selection-connection-a";

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => ReserveAsync(options, owner, client, connection, key, digest)));

        Assert.Single(concurrent.Select(result => result.Run.Id).Distinct());
        Assert.Single(concurrent, result => !result.Replayed);
        Assert.Equal(7, concurrent.Count(result => result.Replayed));
        Assert.All(concurrent, result => Assert.False(result.PayloadConflict));

        var restartedReplay = await ReserveAsync(options, owner, client, connection, key, digest);
        Assert.True(restartedReplay.Replayed);
        Assert.Equal(concurrent[0].Run.Id, restartedReplay.Run.Id);
        Assert.Equal(GradingRunStatus.Preparing, restartedReplay.Run.Status);

        var changedRequest = await ReserveAsync(
            options,
            owner,
            client,
            connection,
            key,
            new string('b', 64));
        Assert.True(changedRequest.Replayed);
        Assert.True(changedRequest.PayloadConflict);
        Assert.Equal(concurrent[0].Run.Id, changedRequest.Run.Id);

        var distinctClient = await ReserveAsync(
            options,
            owner,
            "selection-client-b",
            connection,
            key,
            digest);
        Assert.False(distinctClient.Replayed);
        Assert.NotEqual(concurrent[0].Run.Id, distinctClient.Run.Id);

        var distinctConnection = await ReserveAsync(
            options,
            owner,
            client,
            "selection-connection-b",
            key,
            digest);
        Assert.False(distinctConnection.Replayed);
        Assert.NotEqual(concurrent[0].Run.Id, distinctConnection.Run.Id);

        await using var verifyDb = new ConnectorDbContext(options);
        var persisted = await verifyDb.GradingRuns.AsNoTracking()
            .Where(run => run.CreatedBySubject == owner && run.IdempotencyKey == key)
            .ToArrayAsync();
        Assert.Equal(3, persisted.Length);
    }

    private static async Task<GradingRunReservationResult> ReserveAsync(
        DbContextOptions<ConnectorDbContext> options,
        string owner,
        string client,
        string connection,
        string key,
        string digest)
    {
        await using var db = new ConnectorDbContext(options);
        var repository = new GradingReviewRepository(db);
        return await repository.ReserveGradingRunAsync(
            GradingRun.Create(
                owner,
                moodleConnectionId: connection,
                connectorClientId: client,
                connectionAlias: "default",
                idempotencyKey: key,
                requestDigest: digest),
            CancellationToken.None);
    }

    private static string GetConnectionStringOrFail()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "PostgreSQL integration tests must run through scripts/test-postgres.ps1; the database gate cannot silently skip.");
        return connectionString!;
    }

    private static StartPendingGradingRunCommandHandler CreateSelectionHandler(
        IMediator mediator,
        IGradingReviewRepository repository,
        string owner) =>
        new(
            mediator,
            new SelectionCourseContentsGateway(),
            gradingRepository: repository,
            currentUser: new SelectionCurrentUser(owner));

    private sealed class SelectionCourseContentsGateway : IMoodleCourseContentsGateway
    {
        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId,
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CourseContentsSummary(
                courseId,
                moduleTypes.ToArray(),
                includeHidden,
                onlyWithFiles,
                [new CourseSectionSummary(
                    "section-1",
                    1,
                    "Synthetic section",
                    null,
                    true,
                    1,
                    false,
                    [new CourseModuleSummary(
                        "900",
                        "501",
                        "assign",
                        "Synthetic assignment",
                        null,
                        true,
                        true,
                        null,
                        null,
                        [],
                        [])])]));
    }

    private sealed class SelectionRunMediator(
        DbContextOptions<ConnectorDbContext> options,
        string owner,
        string connection,
        string client,
        bool crashOnSecondChild,
        int attemptNumber = 1) : IMediator
    {
        private readonly AssignmentSubmissionSummary[] _submissions =
        [
            SyntheticSubmission("601", "9001", attemptNumber),
            SyntheticSubmission("602", "9002", attemptNumber),
            SyntheticSubmission("603", "9003", attemptNumber)
        ];

        public int ChildCreationCalls { get; private set; }

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public async Task<TResponse> Send<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default)
        {
            if (request is GetCourseQuery getCourse)
            {
                CourseSummary? course = getCourse.CourseId == "10"
                    ? SyntheticCourse()
                    : null;
                return (TResponse)(object?)course!;
            }

            if (request is ListAssignmentSubmissionsQuery submissionsQuery)
            {
                var page = new AssignmentSubmissionsPage(
                    submissionsQuery.CourseId,
                    submissionsQuery.AssignmentId,
                    "900",
                    "Synthetic assignment",
                    submissionsQuery.Page,
                    submissionsQuery.PageSize,
                    submissionsQuery.Filter,
                    submissionsQuery.IncludeLate,
                    submissionsQuery.IncludeUngraded,
                    submissionsQuery.Since,
                    submissionsQuery.Before,
                    _submissions.Length,
                    HasMore: false,
                    _submissions);
                return (TResponse)(object)page;
            }

            if (request is CreateAssistedGradingBatchCommand create)
            {
                return (TResponse)(object)await CreateChildBatchAsync(create, cancellationToken);
            }

            throw new NotSupportedException($"Request nao suportado: {request.GetType().Name}");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"Request nao suportado: {request.GetType().Name}");

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(
            object request,
            CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<object?>();

        private async Task<CreateAssistedGradingBatchResult> CreateChildBatchAsync(
            CreateAssistedGradingBatchCommand request,
            CancellationToken cancellationToken)
        {
            ChildCreationCalls++;
            if (crashOnSecondChild && ChildCreationCalls == 2)
            {
                throw new OperationCanceledException("Synthetic process interruption between child batches.");
            }

            await using var db = new ConnectorDbContext(options);
            var repository = new GradingReviewRepository(db);
            var existing = await repository.GetBatchByIdempotencyKeyAsync(
                owner,
                request.IdempotencyKey!,
                cancellationToken);
            if (existing is not null)
            {
                return new CreateAssistedGradingBatchResult(
                    existing.Id,
                    existing.CourseId.ToString(CultureInfo.InvariantCulture),
                    existing.AssignmentIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
                    existing.TotalItems,
                    existing.TotalItems,
                    existing.BlockedItems,
                    "IdempotentReplay",
                    []);
            }

            var submission = Assert.Single(request.PrefetchedSubmissions!);
            var batch = AssistedGradingBatch.Create(
                long.Parse(request.CourseId, CultureInfo.InvariantCulture),
                [long.Parse(request.AssignmentIds[0], CultureInfo.InvariantCulture)],
                owner,
                321,
                totalItems: 1,
                connectorClientId: client,
                connectionAlias: "synthetic",
                idempotencyKey: request.IdempotencyKey,
                courseDisplayName: "Synthetic course",
                moodleConnectionId: connection,
                gradingRunId: request.GradingRunId);
            var item = AssistedGradingItem.Create(
                batch.Id,
                long.Parse(request.CourseId, CultureInfo.InvariantCulture),
                long.Parse(request.AssignmentIds[0], CultureInfo.InvariantCulture),
                long.Parse(submission.SubmissionId!, CultureInfo.InvariantCulture),
                long.Parse(submission.UserId, CultureInfo.InvariantCulture),
                submission.AttemptNumber);
            var itemKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{batch.Id:N}:{item.SubmissionId}:{item.AttemptNumber}"))).ToLowerInvariant();
            item.SetIdempotencyKey(itemKey);

            await repository.AddBatchAsync(batch, cancellationToken);
            await repository.AddItemAsync(item, cancellationToken);
            await repository.SaveChangesAsync(cancellationToken);

            return new CreateAssistedGradingBatchResult(
                batch.Id,
                request.CourseId,
                request.AssignmentIds,
                TotalItems: 1,
                AcceptedItems: 1,
                BlockedItems: 0,
                Status: "ReadyForReview",
                Warnings: []);
        }

        private static AssignmentSubmissionSummary SyntheticSubmission(
            string userId,
            string submissionId,
            int attemptNumber) => new(
            userId,
            FullName: null,
            submissionId,
            Status: "graded",
            GradingStatus: "graded",
            Submitted: true,
            Late: false,
            NeedsGrading: false,
            SubmittedAt: DateTimeOffset.UtcNow,
            ModifiedAt: DateTimeOffset.UtcNow,
            AttemptNumber: attemptNumber,
            FileCount: 0,
            HasOnlineText: true,
            Files: []);

        private static CourseSummary SyntheticCourse() => new(
            "10",
            IdNumber: null,
            ShortName: "Synthetic course",
            FullName: "Synthetic course",
            DisplayName: "Synthetic course",
            CategoryId: null,
            CategoryName: null,
            StartDate: null,
            EndDate: null,
            Visible: true,
            ViewUrl: null,
            CourseImage: null,
            Progress: null,
            HasProgress: null,
            IsFavourite: null,
            LastAccessAt: null);
    }

    private sealed class SelectionCurrentUser(string subject) : ICurrentUserContext
    {
        public string Subject => subject;
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes => [];
        public bool HasScope(string scope) => false;
    }
}
