using System.Text;
using Moodle.Application.Configuration;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading.Packages;

internal static class GradingPackagePartitioner
{
    public static IReadOnlyList<IReadOnlyList<AssistedGradingItem>> SplitPackageItems(
        IReadOnlyList<AssistedGradingItem> items,
        IReadOnlyDictionary<Guid, IReadOnlyList<GradingArtifact>> artifactsByItem,
        GradingLimitsOptions options)
    {
        var maxItems = Math.Clamp(options.MaxGradingPackageItems, 1, 400);
        var maxTotalEntries = Math.Clamp(options.MaxGradingPackageEntries, 4, 10_000);
        var maxTotalBytes = Math.Max(1L, options.MaxGradingPackageUncompressedMb) * 1024L * 1024L;
        var maxArchiveBytes = Math.Max(1L, options.MaxGradingPackageZipMb) * 1024L * 1024L;
        var maxFileBytes = Math.Max(1L, options.MaxFileSizeMb) * 1024L * 1024L;
        var maxEntryBytes = Math.Max(
            1L,
            Math.Min(maxFileBytes, maxArchiveBytes - 64L * 1024L));
        var maxOnlineTextBytes = Math.Min(
            maxEntryBytes,
            Math.Max(1L, (long)Math.Max(1, options.MaxTextCharsPerSubmission) * 4L));
        var contextArtifacts = items
            .SelectMany(item => artifactsByItem.GetValueOrDefault(item.Id) ?? [])
            .Where(artifact => GradingPackageArtifactTypes.IsContext(artifact.ArtifactType))
            .GroupBy(GradingPackageContextIdentity.GetKey)
            .Select(group => group.First())
            .ToArray();
        var maxEntries = Math.Max(1, maxTotalEntries - 3 - contextArtifacts.Length);
        var contextBytes = contextArtifacts.Sum(artifact => EstimateArtifactBytes(artifact, maxEntryBytes));
        // A ZIP with incompressible attachments can approach its raw size;
        // reserve room for the central directory and keep the estimate below
        // both configured budgets before the worker downloads anything.
        var transportBudget = Math.Max(1L, maxArchiveBytes - 64L * 1024L);
        var maxUncompressedBytes = Math.Max(1L, Math.Min(maxTotalBytes, transportBudget) - contextBytes);
        var result = new List<IReadOnlyList<AssistedGradingItem>>();
        var current = new List<AssistedGradingItem>();
        var currentEntries = 0;
        var currentBytes = 0L;

        foreach (var item in items)
        {
            var artifacts = artifactsByItem.GetValueOrDefault(item.Id) ?? [];
            var submissionArtifacts = artifacts
                .Where(artifact => GradingPackageArtifactTypes.IsSubmissionFile(artifact.ArtifactType))
                .ToArray();
            var itemEntries = submissionArtifacts.Length + 1; // online text may be current even when not yet persisted
            var itemBytes = submissionArtifacts.Sum(artifact => EstimateArtifactBytes(artifact, maxEntryBytes));
            // The submission gateway is queried by the background worker, so
            // the planner does not know the online-text size yet. Reserve the
            // configured text ceiling rather than the binary-file ceiling;
            // otherwise the default 25 MB file limit would reduce a normal
            // 20 MB package to one student per ZIP.
            itemBytes = checked(itemBytes + maxOnlineTextBytes);

            var wouldExceed = current.Count > 0 &&
                (current.Count >= maxItems ||
                 currentEntries + itemEntries > maxEntries ||
                 currentBytes + itemBytes > maxUncompressedBytes);
            if (wouldExceed)
            {
                result.Add(current.ToArray());
                current = [];
                currentEntries = 0;
                currentBytes = 0L;
            }

            current.Add(item);
            currentEntries = checked(currentEntries + itemEntries);
            currentBytes = checked(currentBytes + itemBytes);
        }

        if (current.Count > 0)
        {
            result.Add(current.ToArray());
        }

        return result;
    }

    private static long EstimateArtifactBytes(GradingArtifact artifact, long maxEntryBytes)
    {
        // When there is no source URL, the worker materializes the persisted
        // text itself. Its UTF-8 length is then the actual archive payload.
        // A source URL means the worker downloads the original file, so an
        // extracted-text length must not be used as a binary-size estimate.
        if (string.IsNullOrWhiteSpace(artifact.SourceUrl) &&
            !string.IsNullOrEmpty(artifact.ExtractedTextRef))
        {
            return Math.Min(Encoding.UTF8.GetByteCount(artifact.ExtractedTextRef), maxEntryBytes);
        }

        if (artifact.SizeBytes is > 0)
        {
            return Math.Min(artifact.SizeBytes.Value, maxEntryBytes);
        }

        return maxEntryBytes;
    }
}
