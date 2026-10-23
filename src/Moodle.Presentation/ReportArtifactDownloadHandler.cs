using System.Text;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Presentation;

internal static class ReportArtifactDownloadHandler
{
    public static async Task<IResult> DownloadAsync(
        ConnectorDbContext dbContext,
        Guid ownerId,
        Guid jobId,
        IReportArtifactStore artifactStore,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.ReportJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobId && item.OwnerId == ownerId, cancellationToken);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (job.Status != "completed")
        {
            return ReportNotReady();
        }

        if (!string.IsNullOrWhiteSpace(job.ArtifactBlobKey))
        {
            Stream stream;
            try
            {
                stream = await artifactStore.OpenReadAsync(job.ArtifactBlobKey, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.Json(
                    new
                    {
                        error = new
                        {
                            code = "report_artifact_unavailable",
                            message = "O arquivo do relatório está temporariamente indisponível.",
                        },
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.File(
                stream,
                job.ContentType ?? "application/octet-stream",
                job.FileName ?? "relatorio.xlsx",
                enableRangeProcessing: true);
        }

        if (!string.IsNullOrEmpty(job.ContentBase64))
        {
            return Results.File(
                Convert.FromBase64String(job.ContentBase64),
                job.ContentType ?? "application/octet-stream",
                job.FileName ?? "relatorio.xlsx");
        }

        if (!string.IsNullOrEmpty(job.ContentText))
        {
            return Results.File(
                Encoding.UTF8.GetBytes(job.ContentText),
                job.ContentType ?? "text/csv; charset=utf-8",
                job.FileName ?? "relatorio.csv");
        }

        return ReportNotReady();
    }

    private static IResult ReportNotReady() =>
        Results.Conflict(new { error = new { code = "report_not_ready", message = "O relatório ainda não está disponível para download." } });
}
