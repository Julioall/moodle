namespace Moodle.Domain.Grading;

/// <summary>
/// Projeção persistível do snapshot canônico. O documento é append-only: uma nova
/// coleta/contexto deve publicar outra versão, nunca sobrescrever uma já utilizada.
/// </summary>
public sealed class GradingContextSnapshotDocument
{
    private GradingContextSnapshotDocument()
    {
    }

    public Guid Id { get; private init; }

    public Guid GradingItemId { get; private init; }

    public Guid BatchId { get; private init; }

    public int Version { get; private init; }

    public string ContextHash { get; private init; } = string.Empty;

    public string ContextStatus { get; private init; } = string.Empty;

    public string PayloadJson { get; private init; } = string.Empty;

    public string? CoverageJson { get; private init; }

    public DateTimeOffset PublishedAt { get; private init; }

    public static GradingContextSnapshotDocument FromSnapshot(GradingContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new GradingContextSnapshotDocument
        {
            Id = Guid.NewGuid(),
            GradingItemId = snapshot.ItemId,
            BatchId = snapshot.BatchId,
            Version = snapshot.Version,
            ContextHash = snapshot.ContextHash,
            ContextStatus = snapshot.ContextStatus,
            PayloadJson = GradingContextSnapshot.SerializeCanonicalPayload(snapshot),
            CoverageJson = System.Text.Json.JsonSerializer.Serialize(snapshot.Coverage),
            PublishedAt = snapshot.PublishedAt
        };
    }
}
