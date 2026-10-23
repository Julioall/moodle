using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Moodle.Infrastructure.Reports;

public static class ReportStorageCalculator
{
    public const long LimitBytes = 300L * 1024L * 1024L;

    public static async Task<long> GetUsedBytesAsync(
        ConnectorDbContext dbContext,
        Guid ownerId,
        CancellationToken cancellationToken = default,
        Guid? excludeJobId = null)
    {
        var storedBytesQuery = dbContext.ReportJobs
            .AsNoTracking()
            .Where(job => job.OwnerId == ownerId && job.Status == "completed" && job.FileSizeBytes > 0);

        if (excludeJobId is { } id)
        {
            storedBytesQuery = storedBytesQuery.Where(job => job.Id != id);
        }

        var storedBytes = await storedBytesQuery
            .Select(job => (long?)job.FileSizeBytes)
            .SumAsync(cancellationToken) ?? 0L;

        var legacyPayloadsQuery = dbContext.ReportJobs
            .AsNoTracking()
            .Where(job => job.OwnerId == ownerId && job.Status == "completed" && job.FileSizeBytes == 0);

        if (excludeJobId is { } legacyId)
        {
            legacyPayloadsQuery = legacyPayloadsQuery.Where(job => job.Id != legacyId);
        }

        var legacyPayloads = await legacyPayloadsQuery
            .Select(job => new { job.ContentBase64, job.ContentText })
            .ToListAsync(cancellationToken);

        return storedBytes + legacyPayloads.Sum(payload =>
            !string.IsNullOrWhiteSpace(payload.ContentBase64)
                ? GetBase64DecodedLength(payload.ContentBase64)
                : string.IsNullOrEmpty(payload.ContentText)
                    ? 0L
                    : Encoding.UTF8.GetByteCount(payload.ContentText));
    }

    public static long GetBase64DecodedLength(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0L;
        var padding = value.EndsWith("==", StringComparison.Ordinal) ? 2 : value.EndsWith('=') ? 1 : 0;
        return Math.Max(0L, ((long)value.Length * 3L / 4L) - padding);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kilobytes = bytes / 1024d;
        if (kilobytes < 1024) return $"{kilobytes:0.#} KB";
        var megabytes = kilobytes / 1024d;
        if (megabytes < 1024) return $"{megabytes:0.#} MB";
        return $"{megabytes / 1024d:0.##} GB";
    }
}
