using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Reports;

namespace Moodle.Application.Tests.Reports;

public sealed class ReportJobArtifactWriterTests
{
    [Fact]
    public async Task NewReportStoresBlobKeySizeAndNoBase64()
    {
        var store = new RecordingReportArtifactStore();
        var writer = new ReportJobArtifactWriter(store);
        var job = new ReportJobEntity
        {
            Id = Guid.NewGuid(),
            Status = "running",
        };
        var content = new byte[] { 1, 3, 5, 7, 9 };

        await writer.WriteAsync(
            job,
            "application/test",
            "report.xlsx",
            content,
            CancellationToken.None);

        Assert.NotEqual(job.Id.ToString("N"), store.LastBlobKey);
        Assert.Equal(store.LastBlobKey, job.ArtifactBlobKey);
        Assert.Equal(content.LongLength, job.FileSizeBytes);
        Assert.Null(job.ContentBase64);
        Assert.Null(job.ContentText);
        Assert.Equal("application/test", job.ContentType);
        Assert.Equal("report.xlsx", job.FileName);
        Assert.Equal("running", job.Status);
        Assert.Equal(0, job.ProgressPercent);
        Assert.Null(job.CompletedAt);
        Assert.Equal(content, store.LastContent);
    }

    private sealed class RecordingReportArtifactStore : IReportArtifactStore
    {
        public string? LastBlobKey { get; private set; }
        public byte[]? LastContent { get; private set; }

        public Task WriteAsync(string blobKey, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            LastBlobKey = blobKey;
            LastContent = content.ToArray();
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
