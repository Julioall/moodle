using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;

namespace Moodle.Presentation;

internal static class PlannerReferenceStore
{
    private static readonly HashSet<string> AllowedTypes = ["course", "student", "class", "school"];

    public static IReadOnlyList<PlannerReferenceInput> Normalize(IReadOnlyList<PlannerReferenceInput>? references)
    {
        if (references is null || references.Count == 0) return [];
        if (references.Count > 50) throw new ArgumentException("Uma tarefa ou evento pode ter no máximo 50 vínculos.", nameof(references));

        var normalized = new List<PlannerReferenceInput>(references.Count);
        foreach (var reference in references)
        {
            var type = reference.ReferenceType?.Trim().ToLowerInvariant();
            var id = reference.ReferenceId?.Trim();
            if (type is null || !AllowedTypes.Contains(type))
                throw new ArgumentException("O tipo do vínculo deve ser course, student, class ou school.", nameof(references));
            if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
                throw new ArgumentException("Todo vínculo precisa de um referenceId válido.", nameof(references));

            normalized.Add(new PlannerReferenceInput(
                type,
                id,
                Trim(reference.ReferenceName, 240),
                Trim(reference.ConnectionRef, 64),
                NormalizeOptionalType(reference.ParentReferenceType),
                Trim(reference.ParentReferenceId, 200),
                Trim(reference.ParentReferenceName, 240)));
        }

        return normalized
            .GroupBy(reference => string.Join("|", reference.ReferenceType, reference.ReferenceId, reference.ConnectionRef, reference.ParentReferenceId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public static async Task ReplaceForTaskAsync(ConnectorDbContext db, Guid ownerId, Guid taskId, IReadOnlyList<PlannerReferenceInput>? references, CancellationToken cancellationToken)
    {
        var existing = await db.PlannerLinks.Where(item => item.OwnerId == ownerId && item.TaskId == taskId).ToListAsync(cancellationToken);
        db.PlannerLinks.RemoveRange(existing);
        db.PlannerLinks.AddRange(Normalize(references).Select(reference => ToEntity(ownerId, taskId, null, reference)));
    }

    public static async Task ReplaceForEventAsync(ConnectorDbContext db, Guid ownerId, Guid eventId, IReadOnlyList<PlannerReferenceInput>? references, CancellationToken cancellationToken)
    {
        var existing = await db.PlannerLinks.Where(item => item.OwnerId == ownerId && item.CalendarEventId == eventId).ToListAsync(cancellationToken);
        db.PlannerLinks.RemoveRange(existing);
        db.PlannerLinks.AddRange(Normalize(references).Select(reference => ToEntity(ownerId, null, eventId, reference)));
    }

    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlannerReferenceDto>>> ForTasksAsync(ConnectorDbContext db, Guid ownerId, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken)
    {
        var rows = await db.PlannerLinks.AsNoTracking().Where(item => item.OwnerId == ownerId && item.TaskId != null && taskIds.Contains(item.TaskId.Value)).ToListAsync(cancellationToken);
        return rows.GroupBy(item => item.TaskId!.Value).ToDictionary(group => group.Key, group => (IReadOnlyList<PlannerReferenceDto>)group.Select(ToDto).ToArray());
    }

    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlannerReferenceDto>>> ForEventsAsync(ConnectorDbContext db, Guid ownerId, IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken)
    {
        var rows = await db.PlannerLinks.AsNoTracking().Where(item => item.OwnerId == ownerId && item.CalendarEventId != null && eventIds.Contains(item.CalendarEventId.Value)).ToListAsync(cancellationToken);
        return rows.GroupBy(item => item.CalendarEventId!.Value).ToDictionary(group => group.Key, group => (IReadOnlyList<PlannerReferenceDto>)group.Select(ToDto).ToArray());
    }

    public static PlannerReferenceDto ToDto(PlannerLinkEntity item) => new(item.ReferenceType, item.ReferenceId, item.ReferenceName, item.ConnectionRef, item.ParentReferenceType, item.ParentReferenceId, item.ParentReferenceName);

    private static PlannerLinkEntity ToEntity(Guid ownerId, Guid? taskId, Guid? eventId, PlannerReferenceInput reference) => new()
    {
        Id = Guid.NewGuid(), OwnerId = ownerId, TaskId = taskId, CalendarEventId = eventId,
        ReferenceType = reference.ReferenceType, ReferenceId = reference.ReferenceId, ReferenceName = reference.ReferenceName,
        ConnectionRef = reference.ConnectionRef, ParentReferenceType = reference.ParentReferenceType,
        ParentReferenceId = reference.ParentReferenceId, ParentReferenceName = reference.ParentReferenceName,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static string? NormalizeOptionalType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return AllowedTypes.Contains(normalized) ? normalized : throw new ArgumentException("O tipo do vínculo pai deve ser course, student, class ou school.", nameof(value));
    }

    private static string? Trim(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];
}
