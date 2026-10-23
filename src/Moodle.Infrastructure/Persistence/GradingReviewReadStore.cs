using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Domain.Grading;

namespace Moodle.Infrastructure;

/// <summary>
/// Read model da tela de revisão. Toda a informação vem do banco local; em
/// particular, não injete nem use gateways Moodle neste tipo.
/// </summary>
public sealed class GradingReviewReadStore(ConnectorDbContext dbContext) : IGradingReviewReadStore
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<GradingReviewPageReadModel> GetPageAsync(
        Guid batchJobId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var rows = await (
            from item in dbContext.GradingItems.AsNoTracking()
            where item.BatchId == batchJobId
            orderby item.CreatedAt, item.Id
            select new ReviewRow(
                item,
                dbContext.GradingContextSnapshots.AsNoTracking()
                    .Where(snapshot => snapshot.GradingItemId == item.Id)
                    .OrderByDescending(snapshot => snapshot.Version)
                    .Select(snapshot => new SnapshotRow(
                        snapshot.ContextHash,
                        snapshot.PayloadJson,
                        snapshot.CoverageJson))
                    .FirstOrDefault()))
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize + 1)
            .ToArrayAsync(cancellationToken);

        var pageRows = rows.Take(safePageSize).ToArray();
        var itemIds = pageRows.Select(row => row.Item.Id).ToArray();
        var evidenceByItem = itemIds.Length == 0
            ? new Dictionary<Guid, IReadOnlyList<GradingEvidence>>()
            : (await dbContext.GradingEvidence.AsNoTracking()
                .Where(evidence => itemIds.Contains(evidence.GradingItemId))
                .OrderBy(evidence => evidence.CreatedAt)
                .ThenBy(evidence => evidence.Id)
                .ToArrayAsync(cancellationToken))
                .GroupBy(evidence => evidence.GradingItemId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<GradingEvidence>)group.ToArray());

        var items = pageRows.Select(row => ToReadModel(
            row.Item,
            row.Snapshot,
            evidenceByItem.GetValueOrDefault(row.Item.Id, []))).ToArray();

        var batch = await dbContext.GradingBatches.AsNoTracking()
            .Where(candidate => candidate.Id == batchJobId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.CourseDisplayName,
                candidate.Status,
                candidate.TotalItems,
                candidate.ReadyItems,
                candidate.BlockedItems,
                candidate.FailedItems,
                candidate.ProcessedItems
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Lote de correcao nao encontrado.");

        var progressPercent = batch.TotalItems == 0
            ? 0
            : (int)Math.Clamp(Math.Round(batch.ProcessedItems * 100m / batch.TotalItems, MidpointRounding.AwayFromZero), 0, 100);
        return new GradingReviewPageReadModel(
            batch.Id,
            batch.Status.ToString(),
            batch.TotalItems,
            batch.ReadyItems,
            batch.BlockedItems,
            batch.FailedItems,
            progressPercent,
            safePage,
            safePageSize,
            rows.Length > safePageSize,
            items,
            batch.CourseDisplayName,
            QueryCount: 3);
    }

    private static GradingReviewItemReadModel ToReadModel(
        AssistedGradingItem item,
        SnapshotRow? snapshot,
        IReadOnlyList<GradingEvidence> evidence)
    {
        var payload = DeserializePayload(snapshot?.PayloadJson);
        var warnings = (payload?.Warnings ?? []).Concat(payload?.Blockers ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var maxGrade = payload?.GradingScale?.MaximumGrade is > 0
            ? payload.GradingScale.MaximumGrade
            : null;
        var gradingMode = payload?.GradingScale?.GradingMode?.Trim().ToLowerInvariant() switch
        {
            "numeric" when maxGrade is not null => "numeric",
            "numeric" => "unknown",
            "scale" => "scale",
            "feedback_only" => "feedback_only",
            "unknown" => "unknown",
            _ when payload?.GradingScale is null => "unknown",
            _ when !string.IsNullOrWhiteSpace(payload.GradingScale.Name) ||
                   !string.IsNullOrWhiteSpace(payload.GradingScale.Description) => "scale",
            // Compatibilidade com snapshots v1, que representavam uma
            // atividade sem nota como um objeto de escala vazio.
            _ => "feedback_only"
        };

        var reason = item.PrivateNotesToTeacher
            ?? (warnings.Length > 0 ? warnings[0] : null)
            ?? (gradingMode == "feedback_only"
                ? "Atividade sem avaliacao numerica no Moodle. Salve e envie somente o feedback; nenhuma nota sera publicada."
                : null);
        return new GradingReviewItemReadModel(
            item.Id,
            item.AssignmentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.SubmissionId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.StudentDisplayName,
            item.Status.ToString(),
            item.ReviewStatus.ToString(),
            item.CommitStatus.ToString(),
            reason,
            GradingDraftVersionHash.Compute(item),
            item.FinalGrade,
            item.FinalFeedback,
            item.SuggestedGrade,
            item.DraftFeedback,
            maxGrade,
            gradingMode,
            payload?.ActivityName,
            item.Confidence,
            snapshot?.ContextHash ?? item.ContextHash,
            warnings,
            evidence,
            payload?.Coverage);
    }

    private static ReviewSnapshotPayload? DeserializePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReviewSnapshotPayload>(json, SnapshotJsonOptions);
        }
        catch (JsonException)
        {
            // Lotes legados ou payloads malformados continuam legíveis por IDs.
            return null;
        }
    }

    private sealed record ReviewRow(AssistedGradingItem Item, SnapshotRow? Snapshot);
    private sealed record SnapshotRow(string ContextHash, string PayloadJson, string? CoverageJson);
    private sealed record ReviewSnapshotPayload(
        string? ActivityName,
        ReviewScalePayload? GradingScale,
        IReadOnlyList<string>? Warnings,
        IReadOnlyList<string>? Blockers,
        GradingEvidenceCoverage? Coverage = null);
    private sealed record ReviewScalePayload(
        decimal? MaximumGrade,
        string? Name,
        string? Description,
        string? GradingMode = null);
}
