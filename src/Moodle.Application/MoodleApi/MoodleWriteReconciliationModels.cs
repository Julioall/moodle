using System.Text.Json.Serialization;

namespace Moodle.Application.MoodleApi;

public sealed record MoodleWriteReconciliationResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("pendingActionId")] Guid PendingActionId,
    [property: JsonPropertyName("function")] string Function,
    [property: JsonPropertyName("resolution")] string Resolution,
    [property: JsonPropertyName("auditId")] string? AuditId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("gradingItemId")] Guid? GradingItemId = null,
    [property: JsonPropertyName("itemOutcome")] string? ItemOutcome = null,
    [property: JsonPropertyName("evidence")] string? Evidence = null);

public interface IMoodleWriteReconciliationService
{
    Task<MoodleWriteReconciliationResult> ReconcileAsync(
        Guid pendingActionId,
        string resolution,
        CancellationToken cancellationToken);

    Task<MoodleWriteReconciliationResult> ReconcileAsync(
        Guid pendingActionId,
        string resolution,
        Guid? gradingItemId,
        CancellationToken cancellationToken) =>
        ReconcileAsync(pendingActionId, resolution, cancellationToken);
}
