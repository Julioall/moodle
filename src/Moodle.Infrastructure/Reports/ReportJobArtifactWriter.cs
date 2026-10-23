using Moodle.Application.Abstractions;
using Moodle.Application.Reports.Templates;

namespace Moodle.Infrastructure.Reports;

public sealed class ReportJobArtifactWriter(IReportArtifactStore artifactStore)
{
    public async Task PrepareAsync(ReportJobEntity job, string contentType, string fileName,
        ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        if (job.Id == Guid.Empty) throw new ArgumentException("A report job must have an identifier.");
        // Each attempt owns a different key. A stale worker cannot overwrite a newer artifact.
        job.ArtifactBlobKey = Guid.NewGuid().ToString("N");
        job.ContentType = contentType;
        job.FileName = fileName;
        job.FileSizeBytes = content.Length;
        job.ContentText = null;
        job.ContentBase64 = null;
        using var metric = ReportMetrics.Begin(ReportMetrics.Stage.Storage);
        try
        {
            await artifactStore.WriteAsync(job.ArtifactBlobKey, content, ct);
            metric.Outcome = ReportMetrics.Result.Success;
        }
        catch (OperationCanceledException) { metric.Outcome = ReportMetrics.Result.Cancelled; throw; }
    }
    public Task WriteAsync(ReportJobEntity job, string contentType, string fileName,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        => PrepareAsync(job, contentType, fileName, content, cancellationToken);
}
