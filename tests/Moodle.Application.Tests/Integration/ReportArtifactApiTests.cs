using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Presentation;

namespace Moodle.Application.Tests.Integration;

public sealed class ReportArtifactApiTests
{
    [Fact]
    public async Task DownloadUsesFileStreamForBlobAndLegacyFallbackForBase64()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"report-artifact-api-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        var artifactJobId = Guid.NewGuid();
        var blobKey = artifactJobId.ToString("N");
        var legacyJobId = Guid.NewGuid();
        var artifactBytes = new byte[] { 10, 20, 30 };
        var legacyBytes = new byte[] { 40, 50, 60 };
        db.ReportJobs.AddRange(
            new ReportJobEntity
            {
                Id = artifactJobId,
                OwnerId = ownerId,
                Status = "completed",
                ArtifactBlobKey = blobKey,
                FileName = "private-report.xlsx",
                ContentType = "application/test",
            },
            new ReportJobEntity
            {
                Id = legacyJobId,
                OwnerId = ownerId,
                Status = "completed",
                ContentBase64 = Convert.ToBase64String(legacyBytes),
                FileName = "legacy-report.xlsx",
                ContentType = "application/test",
            });
        await db.SaveChangesAsync();
        var store = new RecordingReportArtifactStore(new Dictionary<string, byte[]>
        {
            [blobKey] = artifactBytes,
        });

        var artifactResult = await ReportArtifactDownloadHandler.DownloadAsync(
            db,
            ownerId,
            artifactJobId,
            store,
            CancellationToken.None);
        Assert.IsType<FileStreamHttpResult>(artifactResult);
        Assert.Equal(artifactBytes, await ExecuteAsync(artifactResult));
        Assert.Equal(1, store.OpenReadCalls);

        var legacyResult = await ReportArtifactDownloadHandler.DownloadAsync(
            db,
            ownerId,
            legacyJobId,
            store,
            CancellationToken.None);
        Assert.IsType<FileContentHttpResult>(legacyResult);
        Assert.Equal(legacyBytes, await ExecuteAsync(legacyResult));
        Assert.Equal(1, store.OpenReadCalls);

        var deniedResult = await ReportArtifactDownloadHandler.DownloadAsync(
            db,
            otherOwnerId,
            artifactJobId,
            store,
            CancellationToken.None);
        var deniedContext = new DefaultHttpContext();
        deniedContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        await deniedResult.ExecuteAsync(deniedContext);
        Assert.Equal(StatusCodes.Status404NotFound, deniedContext.Response.StatusCode);
        Assert.Equal(1, store.OpenReadCalls);
    }

    [Fact]
    public async Task MissingArtifactReturnsGenericUnavailableError()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"report-artifact-missing-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        var ownerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        db.ReportJobs.Add(new ReportJobEntity
        {
            Id = jobId,
            OwnerId = ownerId,
            Status = "completed",
            ArtifactBlobKey = jobId.ToString("N"),
            FileName = "report.xlsx",
            ContentType = "application/test",
        });
        await db.SaveChangesAsync();
        const string privatePath = "C:\\private\\reports\\artifact.bin";
        var store = new RecordingReportArtifactStore(
            new Dictionary<string, byte[]>(),
            new UnauthorizedAccessException($"Access denied to '{privatePath}'."));

        var result = await ReportArtifactDownloadHandler.DownloadAsync(
            db,
            ownerId,
            jobId,
            store,
            CancellationToken.None);
        var response = await ExecuteResponseAsync(result);
        var responseText = System.Text.Encoding.UTF8.GetString(response.Body);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Contains("report_artifact_unavailable", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(privatePath, responseText, StringComparison.Ordinal);
    }

    private static async Task<byte[]> ExecuteAsync(IResult result)
    {
        var response = await ExecuteResponseAsync(result);
        return response.Body;
    }

    private static async Task<(int StatusCode, byte[] Body)> ExecuteResponseAsync(IResult result)
    {
        var responseBody = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = responseBody;
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, responseBody.ToArray());
    }

    private sealed class RecordingReportArtifactStore(
        IReadOnlyDictionary<string, byte[]> artifacts,
        Exception? openReadException = null) : IReportArtifactStore
    {
        public int OpenReadCalls { get; private set; }

        public Task WriteAsync(string blobKey, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken)
        {
            OpenReadCalls++;
            if (openReadException is not null)
            {
                throw openReadException;
            }

            return Task.FromResult<Stream>(new MemoryStream(artifacts[blobKey], writable: false));
        }

        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
