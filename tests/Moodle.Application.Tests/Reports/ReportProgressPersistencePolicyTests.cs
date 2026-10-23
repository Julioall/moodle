using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MediatR;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Application.Abstractions;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Configuration;
using Moodle.Application.Reports.Queries;
using Moodle.Application.Reports.Templates;
using Moodle.Application.Tests.Reports.Templates;
using Moodle.Domain;
using Moodle.Domain.Reports;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Reports;
using Microsoft.Extensions.Options;

namespace Moodle.Application.Tests.Reports;

public sealed class ReportProgressPersistencePolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(10, 2)]
    [InlineData(100, 20)]
    public async Task PersistsEveryFiveCoursesAndAlwaysOnFinalCourse(int courseCount, int expectedProgressSaves)
    {
        var saveCounter = new SaveChangesCounter();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"report-progress-{Guid.NewGuid():N}")
            .AddInterceptors(saveCounter)
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var job = new ReportJobEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            ClientId = "report-progress-test",
            ConnectionAlias = "default",
            ScopeType = "courses",
            CourseIdsJson = JsonSerializer.Serialize(Enumerable.Range(1, courseCount).Select(index => index.ToString()).ToArray()),
            Status = "queued",
            RequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.ReportJobs.Add(job);
        await dbContext.SaveChangesAsync();
        saveCounter.Count = 0;

        var artifactStore = new NoOpArtifactStore();
        var processor = new ReportJobProcessor(
            dbContext,
            new CoursesGateway(courseCount),
            new EmptyReportMediator(),
            new ExecutionContext(),
            new ConnectionSelection(),
            new ReportJobArtifactWriter(artifactStore),
            artifactStore,
            new ReportJobClaimer(dbContext),
            new ReportJobLeaseRenewer(new TestDbContextFactory(options)),
            new ReportArtifactDeletionQueue(new TestDbContextFactory(options)),
            NullLogger<ReportJobProcessor>.Instance);

        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));

        Assert.Equal("completed", job.Status);
        Assert.Equal(3 + expectedProgressSaves, saveCounter.Count);
    }

    [Fact]
    public async Task CancellationBeforeArtifactWriteMarksJobFailed()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"report-cancel-{Guid.NewGuid():N}")
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var job = new ReportJobEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            ClientId = "report-cancel-test",
            ConnectionAlias = "default",
            ScopeType = "courses",
            CourseIdsJson = "[\"1\"]",
            Status = "queued",
            RequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.ReportJobs.Add(job);
        await dbContext.SaveChangesAsync();

        var artifactStore = new NoOpArtifactStore();
        using var cancellation = new CancellationTokenSource();
        var processor = new ReportJobProcessor(
            dbContext,
            new CancelledCoursesGateway(cancellation),
            new EmptyReportMediator(),
            new ExecutionContext(),
            new ConnectionSelection(),
            new ReportJobArtifactWriter(artifactStore),
            artifactStore,
            new ReportJobClaimer(dbContext),
            new ReportJobLeaseRenewer(new TestDbContextFactory(options)),
            new ReportArtifactDeletionQueue(new TestDbContextFactory(options)),
            NullLogger<ReportJobProcessor>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessNextAsync(cancellation.Token));

        Assert.Equal("failed", job.Status);
        Assert.Contains("cancelada", job.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateDeadlineDuringBlobWriteFailsJobAndCleansUnpublishedArtifact()
    {
        var dbOptions = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"report-template-deadline-{Guid.NewGuid():N}")
            .Options;
        await using var dbContext = new ConnectorDbContext(dbOptions);
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1];
        const string clientId = "report-template-deadline-test";
        var ownerId = fixture.Owner;
        dbContext.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = "synthetic-connection", ClientId = clientId, MoodleAlias = "synthetic", IsActive = true,
        });
        var definition = new ReportDefinition(1, "student_course", [new ReportColumnDefinition("student.name")]);
        var job = new ReportJobEntity
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, ClientId = clientId, ConnectionAlias = "synthetic",
            ScopeType = "courses", CourseIdsJson = "[\"1\"]", Status = "queued",
            RequestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            DefinitionSnapshotJson = JsonSerializer.Serialize(definition, ReportDefinitionValidator.JsonOptions),
            SelectionSnapshotJson = JsonSerializer.Serialize(
                new ReportSelectionSnapshot(1, "synthetic-connection", "synthetic", ["1"]),
                ReportDefinitionValidator.JsonOptions),
        };
        dbContext.ReportJobs.Add(job);
        await dbContext.SaveChangesAsync();

        var artifacts = new BlockingArtifactStore();
        var reportOptions = Options.Create(new ReportTemplateOptions { GenerationTimeoutSeconds = 2 });
        var generator = new ReportTemplateJobGenerator(fixture.Resolver(), new ExcelReportRenderer(),
            new ReportTemplateJobGeneratorTests.Permissions(), reportOptions);
        var processor = new ReportJobProcessor(
            dbContext,
            fixture.Courses,
            new EmptyReportMediator(),
            new ExecutionContext(),
            new ConnectionSelection(),
            new ReportJobArtifactWriter(artifacts),
            artifacts,
            new ReportJobClaimer(dbContext),
            new ReportJobLeaseRenewer(new TestDbContextFactory(dbOptions)),
            new ReportArtifactDeletionQueue(new TestDbContextFactory(dbOptions)),
            NullLogger<ReportJobProcessor>.Instance,
            templateGenerator: generator,
            reportTemplateOptions: reportOptions);

        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));

        Assert.True(artifacts.WriteStarted.Task.IsCompleted);
        Assert.Equal("failed", job.Status);
        Assert.Equal("report_deadline_exceeded", job.ErrorMessage);
        Assert.Null(job.ArtifactBlobKey);
        Assert.Equal(1, artifacts.DeleteCount);
    }

    [Fact]
    public async Task ArtifactDeletionFailureIsQueuedThroughIndependentContext()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"report-deletion-queue-{Guid.NewGuid():N}")
            .Options;
        var blobKey = Guid.NewGuid().ToString("N");
        var queue = new ReportArtifactDeletionQueue(new TestDbContextFactory(options));

        await queue.EnqueueAsync(blobKey, new IOException("Storage is temporarily unavailable."));

        await using var verificationContext = new ConnectorDbContext(options);
        var deletion = await verificationContext.ReportArtifactDeletions.SingleAsync();
        Assert.Equal(blobKey, deletion.ArtifactBlobKey);
        Assert.Equal(1, deletion.AttemptCount);
    }

    [Fact]
    public async Task PreservesArtifactReferenceWhenARecoveredJobCannotSaveItsTrackedState()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"report-artifact-reference-{Guid.NewGuid():N}")
            .Options;
        var job = new ReportJobEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            ClientId = "artifact-reference-test",
            ConnectionAlias = "default",
            ReportType = "grades",
            ScopeType = "course",
            Status = "failed",
            RequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await using (var setup = new ConnectorDbContext(options))
        {
            setup.ReportJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        var artifact = new ReportJobEntity
        {
            Id = job.Id,
            ArtifactBlobKey = Guid.NewGuid().ToString("N"),
            FileSizeBytes = 42,
            FileName = "report.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        };
        var queue = new ReportArtifactDeletionQueue(new TestDbContextFactory(options));

        await queue.PersistFailedJobArtifactAsync(artifact);

        await using var verificationContext = new ConnectorDbContext(options);
        var persisted = await verificationContext.ReportJobs.SingleAsync();
        Assert.Equal(artifact.ArtifactBlobKey, persisted.ArtifactBlobKey);
        Assert.Equal(42, persisted.FileSizeBytes);
    }

    private sealed class SaveChangesCounter : SaveChangesInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CoursesGateway(int courseCount) : IMoodleCoursesGateway
    {
        public Task<PagedCourses> GetMyCoursesAsync(string userExternalId, int limit, int page, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedCourses([], 0, page, limit));

        public Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(string userExternalId, string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CourseSummary>>([]);

        public Task<CourseSummary?> GetMyCourseAsync(string userExternalId, string courseId, CancellationToken cancellationToken)
        {
            if (!int.TryParse(courseId, out var numericId) || numericId < 1 || numericId > courseCount)
            {
                return Task.FromResult<CourseSummary?>(null);
            }

            return Task.FromResult<CourseSummary?>(new CourseSummary(
                courseId,
                null,
                null,
                $"Course {courseId}",
                $"Course {courseId}",
                null,
                "Shared category",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null));
        }
    }

    private sealed class EmptyReportMediator : IMediator
    {
        private static readonly GenerateCourseGradesReportResult EmptyReport = new(
            "course",
            DateTimeOffset.UtcNow,
            0,
            0,
            0,
            null,
            [],
            null);

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)EmptyReport);

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(EmptyReport);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<object?>();
    }

    private sealed class ExecutionContext : IConnectorExecutionContext
    {
        public string? ClientId { get; private set; }
        public string? Subject { get; private set; }
        public string? Email { get; private set; }
        public IReadOnlyCollection<string> Scopes { get; private set; } = [];

        public void Enter(string clientId, string subject, string? email, IReadOnlyCollection<string>? scopes = null)
        {
            ClientId = clientId;
            Subject = subject;
            Email = email;
            Scopes = scopes ?? [];
        }

        public void Clear()
        {
            ClientId = null;
            Subject = null;
            Email = null;
            Scopes = [];
        }
    }

    private sealed class ConnectionSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class NoOpArtifactStore : IReportArtifactStore
    {
        public Task WriteAsync(string blobKey, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) => Task.FromResult<Stream>(Stream.Null);
        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BlockingArtifactStore : IReportArtifactStore
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DeleteCount { get; private set; }
        public async Task WriteAsync(string blobKey, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            WriteStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) => Task.FromResult<Stream>(Stream.Null);
        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken)
        {
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancelledCoursesGateway(CancellationTokenSource cancellation) : IMoodleCoursesGateway
    {
        public Task<PagedCourses> GetMyCoursesAsync(string userExternalId, int limit, int page, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedCourses([], 0, page, limit));

        public Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(string userExternalId, string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CourseSummary>>([]);

        public Task<CourseSummary?> GetMyCourseAsync(string userExternalId, string courseId, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<CourseSummary?>(cancellation.Token);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<ConnectorDbContext> options) : IDbContextFactory<ConnectorDbContext>
    {
        public ConnectorDbContext CreateDbContext() => new(options);

        public Task<ConnectorDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
