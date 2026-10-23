using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;

namespace Moodle.Presentation;

/// <summary>Single application boundary used by the portal endpoints and MCP tools for professional Tasks and Events.</summary>
public sealed class ProfessionalPlannerService(ConnectorDbContext db)
{
    private static readonly HashSet<string> TaskStatuses = ["todo", "in_progress", "blocked", "done", "cancelled"];
    private static readonly HashSet<string> Priorities = ["low", "medium", "high", "urgent"];
    private static readonly HashSet<string> ParticipantRoles = ["owner", "collaborator", "watcher"];

    public async Task<TaskDetailDto?> GetTaskAsync(Guid ownerId, Guid id, CancellationToken ct) {
        var task = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, ct);
        return task is null ? null : await DetailAsync(task, ct);
    }

    public async Task<TaskDetailDto> UpdateSubtaskAsync(Guid ownerId, Guid actorId, Guid parentTaskId, Guid subtaskId, TaskProfessionalInput input, CancellationToken ct)
    {
        if (!await db.Tasks.AnyAsync(x => x.Id == parentTaskId && x.OwnerId == ownerId, ct)) throw new KeyNotFoundException("Task pai não encontrada.");
        if (!await db.Tasks.AnyAsync(x => x.Id == subtaskId && x.OwnerId == ownerId && x.ParentTaskId == parentTaskId, ct)) throw new KeyNotFoundException("Subtarefa não encontrada.");
        return await UpdateTaskAsync(ownerId, actorId, subtaskId, input with { ParentTaskId = parentTaskId }, ct);
    }

    public async Task<TaskDetailDto> CompleteSubtaskAsync(Guid ownerId, Guid actorId, Guid parentTaskId, Guid subtaskId, bool complete, long? expectedVersion, CancellationToken ct)
    {
        if (!await db.Tasks.AnyAsync(x => x.Id == parentTaskId && x.OwnerId == ownerId, ct)) throw new KeyNotFoundException("Task pai não encontrada.");
        if (!await db.Tasks.AnyAsync(x => x.Id == subtaskId && x.OwnerId == ownerId && x.ParentTaskId == parentTaskId, ct)) throw new KeyNotFoundException("Subtarefa não encontrada.");
        return await CompleteAsync(ownerId, actorId, subtaskId, complete, expectedVersion, ct);
    }

    public async Task<(IReadOnlyList<TaskListItemDto> Items, int Total)> ListTasksAsync(Guid ownerId, int page, int pageSize, string? search, string? status, string? priority, Guid? participantId, string? tag, string? referenceType, string? referenceId, CancellationToken ct) {
        var query = db.Tasks.AsNoTracking().Where(x => x.OwnerId == ownerId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(priority)) query = query.Where(x => x.Priority == priority.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim().ToLower(); query = query.Where(x => x.Title.ToLower().Contains(term) || (x.Description ?? "").ToLower().Contains(term) || db.TaskTags.Any(t => t.TaskId == x.Id && t.NormalizedValue.Contains(term)) || db.TaskReferences.Any(r => r.TaskId == x.Id && (r.ReferenceId.ToLower().Contains(term) || (r.ReferenceName ?? "").ToLower().Contains(term)))); }
        if (participantId is not null) query = query.Where(x => db.TaskParticipants.Any(p => p.TaskId == x.Id && p.UserId == participantId));
        if (!string.IsNullOrWhiteSpace(tag)) { var value = NormalizeTag(tag); query = query.Where(x => db.TaskTags.Any(t => t.TaskId == x.Id && t.NormalizedValue == value)); }
        if (!string.IsNullOrWhiteSpace(referenceType) && !string.IsNullOrWhiteSpace(referenceId)) query = query.Where(x => db.TaskReferences.Any(r => r.TaskId == x.Id && r.ReferenceType == referenceType.Trim().ToLowerInvariant() && r.ReferenceId == referenceId.Trim()));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.DueAt == null).ThenBy(x => x.DueAt).ThenByDescending(x => x.UpdatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (await TaskListItemsAsync(rows, ct), total);
    }

    public async Task<TaskDetailDto> CreateTaskAsync(Guid ownerId, Guid actorId, TaskProfessionalInput input, CancellationToken ct)
    {
        var title = Title(input.Title);
        var now = DateTimeOffset.UtcNow;
        ValidateTaskDates(input.StartAt, input.DueAt);
        if (input.ParentTaskId is not null && !await db.Tasks.AnyAsync(x => x.Id == input.ParentTaskId && x.OwnerId == ownerId, ct))
            throw new ArgumentException("Task pai não encontrada.");
        if (input.ParentTaskId is not null && input.Subtasks is { Count: > 0 })
            throw new ArgumentException("Somente Tasks-raiz podem receber subtarefas inline.");

        var taskStatus = Status(input.Status);
        var task = new TaskEntity
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, CreatedBy = actorId, Title = title,
            Description = Description(input.Description), Status = taskStatus, Priority = Priority(input.Priority),
            StartAt = input.StartAt, DueAt = input.DueAt, ParentTaskId = input.ParentTaskId,
            ActionType = NormalizeLegacy(input.ActionType, 120), ScheduleHint = NormalizeLegacy(input.ScheduleHint, 500),
            CompletedAt = taskStatus == "done" ? now : null, Version = 1, CreatedAt = now, UpdatedAt = now
        };
        db.Tasks.Add(task);
        var participants = input.Participants is null
            ? [new TaskParticipantInput(actorId, "owner")]
            : input.Participants.Any(x => string.Equals(x.Role?.Trim(), "owner", StringComparison.OrdinalIgnoreCase))
                ? input.Participants
                : input.Participants.Append(new TaskParticipantInput(actorId, "owner")).ToArray();
        await ReplaceTaskCollectionsAsync(task, actorId, participants, input.References, input.Tags, ct);
        AddActivity(task.Id, actorId, "task_created", null, now);
        if (task.ParentTaskId is not null)
            AddActivity(task.ParentTaskId.Value, actorId, "subtask_created", JsonSerializer.Serialize(new { taskId = task.Id }), now);

        await AddDependenciesAsync(ownerId, actorId, task.Id, input.DependsOnTaskIds, now, ct);
        if (input.Subtasks is { Count: > 0 })
        {
            if (input.Subtasks.Count > 20) throw new ArgumentException("Informe no máximo 20 subtarefas.");
            foreach (var item in input.Subtasks)
            {
                var child = new TaskEntity
                {
                    Id = Guid.NewGuid(), OwnerId = ownerId, CreatedBy = actorId, ParentTaskId = task.Id,
                    Title = Title(item.Title), Description = Description(item.Description), Status = "todo",
                    Priority = Priority(item.Priority), DueAt = item.DueAt, Version = 1, CreatedAt = now, UpdatedAt = now
                };
                db.Tasks.Add(child);
                var childOwner = item.OwnerId ?? actorId;
                if (childOwner == Guid.Empty) throw new ArgumentException("Owner de subtarefa inválido.");
                db.TaskParticipants.Add(new TaskParticipantEntity { Id = Guid.NewGuid(), TaskId = child.Id, UserId = childOwner, Role = "owner", AssignedAt = now, AssignedBy = actorId });
                AddActivity(task.Id, actorId, "subtask_created", JsonSerializer.Serialize(new { taskId = child.Id }), now);
            }
        }
        await db.SaveChangesAsync(ct);
        return await DetailAsync(task, ct);
    }

    public async Task<TaskDetailDto> UpdateTaskAsync(Guid ownerId, Guid actorId, Guid id, TaskProfessionalInput input, CancellationToken ct) {
        var task = await OwnedTaskAsync(ownerId, id, ct); AssertVersion(task.Version, input.ExpectedVersion);
        var now = DateTimeOffset.UtcNow; var oldStatus = task.Status;
        if (input.Title is not null) task.Title = Title(input.Title); if (input.Description is not null) task.Description = Description(input.Description);
        if (input.Status is not null) { task.Status = Status(input.Status); if (task.Status == "done" && oldStatus != "done") task.CompletedAt = now; if (task.Status != "done") task.CompletedAt = null; if (oldStatus != task.Status) { AddActivity(id, actorId, "status_changed", JsonSerializer.Serialize(new { from = oldStatus, to = task.Status }), now); if (task.Status == "done") AddActivity(id, actorId, "task_completed", null, now); else if (oldStatus == "done") AddActivity(id, actorId, "task_reopened", null, now); if (task.ParentTaskId is not null && task.Status == "done") AddActivity(task.ParentTaskId.Value, actorId, "subtask_completed", JsonSerializer.Serialize(new { taskId = id }), now); else if (task.ParentTaskId is not null && oldStatus == "done") AddActivity(task.ParentTaskId.Value, actorId, "subtask_reopened", JsonSerializer.Serialize(new { taskId = id }), now); } }
        if (input.Priority is not null) { var old = task.Priority; task.Priority = Priority(input.Priority); if (old != task.Priority) AddActivity(id, actorId, "priority_changed", JsonSerializer.Serialize(new { from = old, to = task.Priority }), now); }
        if (input.StartAt is not null && input.StartAt != task.StartAt) { task.StartAt = input.StartAt; AddActivity(id, actorId, "start_date_changed", null, now); }
        if (input.DueAt is not null && input.DueAt != task.DueAt) { task.DueAt = input.DueAt; AddActivity(id, actorId, "due_date_changed", null, now); }
        if (input.ActionType is not null) task.ActionType = NormalizeLegacy(input.ActionType, 120);
        if (input.ScheduleHint is not null) task.ScheduleHint = NormalizeLegacy(input.ScheduleHint, 500);
        if (input.ClearStartAt && task.StartAt is not null) { task.StartAt = null; AddActivity(id, actorId, "start_date_changed", null, now); }
        if (input.ClearDueAt && task.DueAt is not null) { task.DueAt = null; AddActivity(id, actorId, "due_date_changed", null, now); }
        ValidateTaskDates(task.StartAt, task.DueAt);
        await ReplaceTaskCollectionsAsync(task, actorId, input.Participants, input.References, input.Tags, ct);
        if (input.DependsOnTaskIds is not null) await ReplaceDependenciesAsync(ownerId, actorId, id, input.DependsOnTaskIds, now, ct);
        task.UpdatedAt = now; task.Version++; await db.SaveChangesAsync(ct); return await DetailAsync(task, ct);
    }

    public async Task<TaskDetailDto> CompleteAsync(Guid ownerId, Guid actorId, Guid id, bool complete, long? expectedVersion, CancellationToken ct) { var task = await OwnedTaskAsync(ownerId, id, ct); AssertVersion(task.Version, expectedVersion); if ((complete && task.Status == "done") || (!complete && task.Status == "todo" && task.CompletedAt is null)) return await DetailAsync(task, ct); var now = DateTimeOffset.UtcNow; task.Status = complete ? "done" : "todo"; task.CompletedAt = complete ? now : null; task.Version++; task.UpdatedAt = now; AddActivity(id, actorId, complete ? "task_completed" : "task_reopened", null, now); if (task.ParentTaskId is not null) AddActivity(task.ParentTaskId.Value, actorId, complete ? "subtask_completed" : "subtask_reopened", JsonSerializer.Serialize(new { taskId = id }), now); await db.SaveChangesAsync(ct); return await DetailAsync(task, ct); }
    public async Task DeleteTaskAsync(Guid ownerId, Guid id, CancellationToken ct) { var task = await OwnedTaskAsync(ownerId, id, ct); db.Tasks.Remove(task); await db.SaveChangesAsync(ct); }
    public async Task DeleteEventAsync(Guid ownerId, Guid id, CancellationToken ct) { var calendarEvent = await OwnedEventAsync(ownerId, id, ct); var now = DateTimeOffset.UtcNow; var links = await db.TaskEventLinks.Where(x => x.EventId == id).ToArrayAsync(ct); foreach (var link in links) AddActivity(link.TaskId, ownerId, "event_cancelled", JsonSerializer.Serialize(new { eventId = id, occurrenceStartAt = link.OccurrenceStartAt }), now); db.CalendarEvents.Remove(calendarEvent); await db.SaveChangesAsync(ct); }
    public async Task AddCommentAsync(Guid ownerId, Guid actorId, Guid taskId, string content, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var text = Description(content) ?? throw new ArgumentException("Comentário é obrigatório."); var now = DateTimeOffset.UtcNow; db.TaskComments.Add(new() { Id = Guid.NewGuid(), TaskId = taskId, AuthorId = actorId, Content = text, CreatedAt = now }); AddActivity(taskId, actorId, "comment_added", null, now); await db.SaveChangesAsync(ct); }
    public async Task EditCommentAsync(Guid ownerId, Guid actorId, Guid taskId, Guid commentId, string content, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var comment = await db.TaskComments.SingleOrDefaultAsync(x => x.Id == commentId && x.TaskId == taskId, ct) ?? throw new KeyNotFoundException("Comentário não encontrado."); comment.Content = Description(content) ?? throw new ArgumentException("Comentário é obrigatório."); comment.EditedAt = DateTimeOffset.UtcNow; AddActivity(taskId, actorId, "comment_edited", JsonSerializer.Serialize(new { commentId }), comment.EditedAt.Value); await db.SaveChangesAsync(ct); }
    public async Task AddParticipantAsync(Guid ownerId, Guid actorId, Guid taskId, TaskParticipantInput input, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var role = input.Role.Trim().ToLowerInvariant(); if (input.UserId == Guid.Empty || !ParticipantRoles.Contains(role)) throw new ArgumentException("Participante inválido."); var existing = await db.TaskParticipants.SingleOrDefaultAsync(x => x.TaskId == taskId && x.UserId == input.UserId, ct); if (role == "owner") { var oldOwner = await db.TaskParticipants.SingleOrDefaultAsync(x => x.TaskId == taskId && x.Role == "owner", ct); if (oldOwner is not null && oldOwner.UserId != input.UserId) db.TaskParticipants.Remove(oldOwner); } if (existing is null) db.TaskParticipants.Add(new() { Id = Guid.NewGuid(), TaskId = taskId, UserId = input.UserId, Role = role, AssignedAt = DateTimeOffset.UtcNow, AssignedBy = actorId }); else existing.Role = role; AddActivity(taskId, actorId, role == "owner" ? "owner_changed" : "collaborator_added", JsonSerializer.Serialize(new { input.UserId, role }), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); }
    public async Task RemoveParticipantAsync(Guid ownerId, Guid actorId, Guid taskId, Guid userId, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var item = await db.TaskParticipants.SingleOrDefaultAsync(x => x.TaskId == taskId && x.UserId == userId, ct); if (item is null) return; db.TaskParticipants.Remove(item); AddActivity(taskId, actorId, item.Role == "owner" ? "owner_changed" : "collaborator_removed", JsonSerializer.Serialize(new { userId }), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); }
    public async Task AddReferenceAsync(Guid ownerId, Guid actorId, Guid taskId, TaskReferenceV2Input input, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var r = NormalizeReferences([input]).Single(); if (await db.TaskReferences.AnyAsync(x => x.TaskId == taskId && x.ReferenceType == r.ReferenceType && x.ReferenceId == r.ReferenceId && x.ConnectionRef == r.ConnectionRef, ct)) return; db.TaskReferences.Add(new() { Id = Guid.NewGuid(), TaskId = taskId, ReferenceType = r.ReferenceType, ReferenceId = r.ReferenceId, ReferenceName = r.ReferenceName, ConnectionRef = r.ConnectionRef, Relation = r.Relation }); AddActivity(taskId, actorId, "reference_added", JsonSerializer.Serialize(new { r.ReferenceType, r.ReferenceId }), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); }
    public async Task RemoveReferenceAsync(Guid ownerId, Guid actorId, Guid taskId, Guid referenceId, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var item = await db.TaskReferences.SingleOrDefaultAsync(x => x.TaskId == taskId && x.Id == referenceId, ct); if (item is null) return; db.TaskReferences.Remove(item); AddActivity(taskId, actorId, "reference_removed", JsonSerializer.Serialize(new { referenceId }), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); }
    public async Task AddTagAsync(Guid ownerId, Guid actorId, Guid taskId, string value, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var tag = NormalizeTags([value]).Single(); if (await db.TaskTags.AnyAsync(x => x.TaskId == taskId && x.NormalizedValue == NormalizeTag(tag), ct)) return; db.TaskTags.Add(new() { TaskId = taskId, Value = tag, NormalizedValue = NormalizeTag(tag) }); AddActivity(taskId, actorId, "tag_added", JsonSerializer.Serialize(new { tag }), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); }
    public async Task RemoveTagAsync(Guid ownerId, Guid actorId, Guid taskId, string value, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var normalized = NormalizeTag(value); var items = await db.TaskTags.Where(x => x.TaskId == taskId && x.NormalizedValue == normalized).ToListAsync(ct); if (items.Count == 0) return; db.TaskTags.RemoveRange(items); AddActivity(taskId, actorId, "tag_removed", JsonSerializer.Serialize(new { tag = normalized }), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); }
    public async Task<TaskTimelinePageDto> TimelineAsync(Guid ownerId, Guid taskId, int page, int pageSize, CancellationToken ct)
    {
        await OwnedTaskAsync(ownerId, taskId, ct);
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var take = page * pageSize + 1;
        var comments = await db.TaskComments.AsNoTracking().Where(x => x.TaskId == taskId).OrderByDescending(x => x.CreatedAt).Take(take).Select(x => new TaskCommentDto(x.Id, x.AuthorId, x.Content, x.CreatedAt, x.EditedAt)).ToArrayAsync(ct);
        var activities = await db.TaskActivities.AsNoTracking().Where(x => x.TaskId == taskId).OrderByDescending(x => x.CreatedAt).Take(take).Select(x => new TaskActivityDto(x.Id, x.ActorId, x.EventType, x.Data, x.CreatedAt)).ToArrayAsync(ct);
        var combined = comments.Select(x => new TimelineItem(x.CreatedAt, x, null))
            .Concat(activities.Select(x => new TimelineItem(x.CreatedAt, null, x)))
            .OrderByDescending(x => x.At).ThenByDescending(x => x.Comment?.Id ?? x.Activity?.Id)
            .ToArray();
        var pageItems = combined.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return new(pageItems.Where(x => x.Comment is not null).Select(x => x.Comment!).ToArray(), pageItems.Where(x => x.Activity is not null).Select(x => x.Activity!).ToArray(), page, pageSize, combined.Length > page * pageSize);
    }
    private sealed record TimelineItem(DateTimeOffset At, TaskCommentDto? Comment, TaskActivityDto? Activity);
    private async Task ValidateDependencyIdsAsync(Guid ownerId, Guid taskId, IReadOnlyList<Guid> dependsOnIds, CancellationToken ct)
    {
        if (dependsOnIds.Count > 50) throw new ArgumentException("Informe no máximo 50 dependências.");
        var seen = new HashSet<Guid>();
        foreach (var dependsOnId in dependsOnIds)
        {
            if (dependsOnId == Guid.Empty || dependsOnId == taskId || !seen.Add(dependsOnId)) throw new ArgumentException("Dependências inválidas ou duplicadas.");
            await OwnedTaskAsync(ownerId, dependsOnId, ct);
            if (await HasPathAsync(dependsOnId, taskId, ct)) throw new ArgumentException("A dependência criaria um ciclo.");
        }
    }
    private async Task AddDependenciesAsync(Guid ownerId, Guid actorId, Guid taskId, IReadOnlyList<Guid>? dependsOnIds, DateTimeOffset now, CancellationToken ct)
    {
        if (dependsOnIds is null) return;
        await ValidateDependencyIdsAsync(ownerId, taskId, dependsOnIds, ct);
        foreach (var dependsOnId in dependsOnIds)
        {
            db.TaskDependencies.Add(new TaskDependencyEntity { TaskId = taskId, DependsOnTaskId = dependsOnId, CreatedBy = actorId, CreatedAt = now });
            AddActivity(taskId, actorId, "dependency_added", JsonSerializer.Serialize(new { dependsOnTaskId = dependsOnId }), now);
        }
    }
    private async Task ReplaceDependenciesAsync(Guid ownerId, Guid actorId, Guid taskId, IReadOnlyList<Guid> dependsOnIds, DateTimeOffset now, CancellationToken ct)
    {
        await ValidateDependencyIdsAsync(ownerId, taskId, dependsOnIds, ct);
        db.TaskDependencies.RemoveRange(await db.TaskDependencies.Where(x => x.TaskId == taskId).ToArrayAsync(ct));
        foreach (var dependsOnId in dependsOnIds) { db.TaskDependencies.Add(new TaskDependencyEntity { TaskId = taskId, DependsOnTaskId = dependsOnId, CreatedBy = actorId, CreatedAt = now }); AddActivity(taskId, actorId, "dependency_added", JsonSerializer.Serialize(new { dependsOnTaskId = dependsOnId }), now); }
    }
    public async Task AddDependencyAsync(Guid ownerId, Guid actorId, Guid taskId, Guid dependsOnId, CancellationToken ct) { if (taskId == dependsOnId) throw new ArgumentException("Uma Task não pode depender dela mesma."); await OwnedTaskAsync(ownerId, taskId, ct); await OwnedTaskAsync(ownerId, dependsOnId, ct); if (await HasPathAsync(dependsOnId, taskId, ct)) throw new ArgumentException("A dependência criaria um ciclo."); if (await db.TaskDependencies.AnyAsync(x => x.TaskId == taskId && x.DependsOnTaskId == dependsOnId, ct)) throw new ArgumentException("Dependência já existe."); var now = DateTimeOffset.UtcNow; db.TaskDependencies.Add(new() { TaskId = taskId, DependsOnTaskId = dependsOnId, CreatedBy = actorId, CreatedAt = now }); AddActivity(taskId, actorId, "dependency_added", JsonSerializer.Serialize(new { dependsOnTaskId = dependsOnId }), now); await db.SaveChangesAsync(ct); }
    public async Task RemoveDependencyAsync(Guid ownerId, Guid actorId, Guid taskId, Guid dependsOnId, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var d = await db.TaskDependencies.SingleOrDefaultAsync(x => x.TaskId == taskId && x.DependsOnTaskId == dependsOnId, ct); if (d is null) return; db.TaskDependencies.Remove(d); AddActivity(taskId, actorId, "dependency_removed", JsonSerializer.Serialize(new { dependsOnTaskId = dependsOnId }), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); }

    public async Task<EventDto> CreateEventAsync(Guid ownerId, Guid actorId, EventProfessionalInput input, CancellationToken ct) { var start = (input.StartAt ?? throw new ArgumentException("Início é obrigatório.")).ToUniversalTime(); var end = input.EndAt?.ToUniversalTime(); ValidateEvent(input.Title, input.Description, start, end, input.TimeZoneId, input.Location); var now = DateTimeOffset.UtcNow; var entity = new CalendarEventEntity { Id = Guid.NewGuid(), OwnerId = ownerId, Title = Title(input.Title), Description = Description(input.Description), StartAt = start, EndAt = end, Type = EventType(input.Type), TimeZoneId = Zone(input.TimeZoneId), Location = Trim(input.Location, 500), AvailabilityStatus = Availability(input.AvailabilityStatus), IsAllDay = input.IsAllDay ?? false, Source = "manual", Version = 1, CreatedAt = now, UpdatedAt = now }; db.CalendarEvents.Add(entity); await ReplaceEventCollectionsAsync(entity.Id, input.References, input.Tags, ct); await SetRecurrenceAsync(entity.Id, input.Recurrence, now, ct); await db.SaveChangesAsync(ct); return await EventAsync(entity, ct); }
    public async Task<EventDto?> GetEventAsync(Guid ownerId, Guid id, CancellationToken ct) { var entity = await db.CalendarEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, ct); return entity is null ? null : await EventAsync(entity, ct); }
    public async Task<(IReadOnlyList<EventDto> Events, IReadOnlyDictionary<Guid, (IReadOnlyList<DateTimeOffset> ExDates, IReadOnlyList<DateTimeOffset> RDates)> Dates)> ExportEventsAsync(Guid ownerId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) { var rows = await db.CalendarEvents.AsNoTracking().Where(x => x.OwnerId == ownerId && x.StartAt < to && (x.EndAt == null || x.EndAt > from)).OrderBy(x => x.StartAt).ToArrayAsync(ct); var events = new List<EventDto>(rows.Length); var dates = new Dictionary<Guid, (IReadOnlyList<DateTimeOffset>, IReadOnlyList<DateTimeOffset>)>(); foreach (var row in rows) { events.Add(await EventAsync(row, ct)); var recurrenceDates = await db.EventRecurrenceDates.AsNoTracking().Where(x => x.EventId == row.Id).ToArrayAsync(ct); dates[row.Id] = (recurrenceDates.Where(x => x.Kind == "exclude").Select(x => x.OccurrenceStartAt).ToArray(), recurrenceDates.Where(x => x.Kind == "include").Select(x => x.OccurrenceStartAt).ToArray()); } return (events, dates); }
    internal async Task<(EventDto Event, bool Created)> ImportEventAsync(Guid ownerId, Guid actorId, ImportedPlannerItem item, CancellationToken ct) { if (item.StartAt is null) throw new ArgumentException("Event importado precisa de DTSTART."); var existing = await db.CalendarEvents.SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.ExternalSource == "ical" && x.ExternalUid == item.Uid, ct); var created = existing is null; if (existing is null) { existing = new CalendarEventEntity { Id = Guid.NewGuid(), OwnerId = ownerId, ExternalSource = "ical", ExternalUid = item.Uid, CreatedAt = DateTimeOffset.UtcNow, Version = 1 }; db.CalendarEvents.Add(existing); } existing.Title = Title(item.Title); existing.Description = Description(item.Description); existing.StartAt = item.StartAt.Value; existing.EndAt = item.EndAt; existing.TimeZoneId = Zone(item.TimeZoneId); existing.Location = Trim(item.Location, 500); existing.Source = "ical"; existing.Type = "other"; existing.AvailabilityStatus = "busy"; existing.IsAllDay = item.IsAllDay; existing.UpdatedAt = DateTimeOffset.UtcNow; existing.Version = created ? 1 : existing.Version + 1; await ReplaceEventCollectionsAsync(existing.Id, item.References.Select(r => new TaskReferenceV2Input(r.ReferenceType, r.ReferenceId, r.ReferenceName, r.ConnectionRef)).ToArray(), item.Tags, ct); await SetRecurrenceAsync(existing.Id, string.IsNullOrWhiteSpace(item.RRule) ? null : new EventRecurrenceInput(item.RRule, item.ExDates, item.RDates), existing.UpdatedAt, ct); await db.SaveChangesAsync(ct); return (await EventAsync(existing, ct), created); }
    public async Task<EventDto> UpdateEventAsync(Guid ownerId, Guid actorId, Guid id, EventProfessionalInput input, CancellationToken ct) { var entity = await OwnedEventAsync(ownerId, id, ct); AssertVersion(entity.Version, input.ExpectedVersion); var start = (input.StartAt ?? entity.StartAt).ToUniversalTime(); var end = input.ClearEndAt ? null : input.EndAt?.ToUniversalTime() ?? entity.EndAt; if (input.StartAt is not null && input.EndAt is null && !input.ClearEndAt && entity.EndAt is not null) end = start + (entity.EndAt.Value - entity.StartAt); ValidateEvent(input.Title ?? entity.Title, input.Description ?? entity.Description, start, end, input.TimeZoneId ?? entity.TimeZoneId, input.Location ?? entity.Location); var rescheduled = start != entity.StartAt || end != entity.EndAt; entity.Title = input.Title is null ? entity.Title : Title(input.Title); if (input.Description is not null) entity.Description = Description(input.Description); entity.StartAt = start; entity.EndAt = end; entity.TimeZoneId = Zone(input.TimeZoneId ?? entity.TimeZoneId); entity.Location = input.Location is null ? entity.Location : Trim(input.Location, 500); entity.AvailabilityStatus = Availability(input.AvailabilityStatus ?? entity.AvailabilityStatus); entity.IsAllDay = input.IsAllDay ?? entity.IsAllDay; entity.Type = input.Type is null ? entity.Type : EventType(input.Type); entity.Version++; entity.UpdatedAt = DateTimeOffset.UtcNow; await ReplaceEventCollectionsAsync(id, input.References, input.Tags, ct); if (input.Recurrence is not null) await SetRecurrenceAsync(id, input.Recurrence, entity.UpdatedAt, ct); if (rescheduled) foreach (var link in await db.TaskEventLinks.Where(x => x.EventId == id).ToArrayAsync(ct)) AddActivity(link.TaskId, actorId, "event_rescheduled", JsonSerializer.Serialize(new { eventId = id, startAt = start, endAt = end }), entity.UpdatedAt); await db.SaveChangesAsync(ct); return await EventAsync(entity, ct); }
    public async Task<IReadOnlyList<EventOccurrenceDto>> OccurrencesAsync(Guid ownerId, DateTimeOffset from, DateTimeOffset to, string? tag, Guid? taskId, CancellationToken ct, string? referenceType = null, string? referenceId = null, int page = 1, int pageSize = 100) { if (to <= from) throw new ArgumentException("O fim deve ser posterior ao início."); from = from.ToUniversalTime(); to = to.ToUniversalTime(); page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200); var events = db.CalendarEvents.AsNoTracking().Where(x => x.OwnerId == ownerId && (db.EventRecurrences.Any(r => r.EventId == x.Id && (r.UntilAt == null || r.UntilAt >= from)) || db.EventRecurrenceDates.Any(d => d.EventId == x.Id && d.Kind == "include" && d.OccurrenceStartAt < to && d.OccurrenceStartAt >= from) || (x.StartAt < to && (x.EndAt == null || x.EndAt > from)))); if (!string.IsNullOrWhiteSpace(tag)) { var normalized = NormalizeTag(tag); events = events.Where(x => db.EventTags.Any(t => t.EventId == x.Id && t.NormalizedValue == normalized)); } if (taskId is not null) events = events.Where(x => db.TaskEventLinks.Any(l => l.EventId == x.Id && l.TaskId == taskId)); if (!string.IsNullOrWhiteSpace(referenceType) && !string.IsNullOrWhiteSpace(referenceId)) { var rt = referenceType.Trim().ToLowerInvariant(); var ri = referenceId.Trim(); events = events.Where(x => db.EventReferences.Any(r => r.EventId == x.Id && r.ReferenceType == rt && r.ReferenceId == ri)); } var rows = await events.ToListAsync(ct); var output = new List<EventOccurrenceDto>(); foreach (var e in rows) output.AddRange(await ExpandAsync(e, from, to, ct)); return output.OrderBy(x => x.OccurrenceStartAt).ThenBy(x => x.Id).Take(1000).Skip((page - 1) * pageSize).Take(pageSize).ToArray(); }
    public async Task OverrideOccurrenceAsync(Guid ownerId, Guid id, DateTimeOffset originalStart, OccurrenceOverrideInput input, CancellationToken ct)
    {
        var calendarEvent = await OwnedEventAsync(ownerId, id, ct);
        originalStart = originalStart.ToUniversalTime();
        var hasRecurrence = await db.EventRecurrences.AnyAsync(x => x.EventId == id, ct);
        if (!hasRecurrence && calendarEvent.StartAt.ToUniversalTime() != originalStart)
            throw new ArgumentException("A ocorrência indicada não pertence ao Event.");
        if (hasRecurrence)
            await ValidateOccurrenceLinkAsync(calendarEvent, originalStart, ct, allowCancelledOverride: true);
        var existing = await db.EventOccurrenceOverrides.SingleOrDefaultAsync(x => x.EventId == id && x.OriginalStartAt == originalStart, ct);
        var effectiveStart = input.StartAt?.ToUniversalTime() ?? existing?.StartAt ?? originalStart;
        var effectiveEnd = input.EndAt?.ToUniversalTime() ?? existing?.EndAt;
        if (effectiveEnd is not null && effectiveEnd <= effectiveStart)
            throw new ArgumentException("O fim da ocorrência deve ser posterior ao início.");
        var now = DateTimeOffset.UtcNow;
        if (existing is null) { existing = new() { EventId = id, OriginalStartAt = originalStart }; db.EventOccurrenceOverrides.Add(existing); }
        existing.IsCancelled = input.IsCancelled;
        existing.Title = input.Title is null ? existing.Title : Title(input.Title);
        if (input.Description is not null) existing.Description = Description(input.Description);
        existing.StartAt = input.StartAt?.ToUniversalTime(); existing.EndAt = effectiveEnd; existing.UpdatedAt = now;
        var eventType = input.IsCancelled ? "event_cancelled" : input.StartAt is not null || input.EndAt is not null ? "event_rescheduled" : null;
        if (eventType is not null) foreach (var link in await db.TaskEventLinks.Where(x => x.EventId == id && (x.OccurrenceStartAt == null || x.OccurrenceStartAt == originalStart)).ToArrayAsync(ct)) AddActivity(link.TaskId, ownerId, eventType, JsonSerializer.Serialize(new { eventId = id, occurrenceStartAt = originalStart }), now);
        await db.SaveChangesAsync(ct);
    }
    public async Task<TaskEventLinkDto> LinkAsync(Guid ownerId, Guid actorId, Guid taskId, TaskEventLinkInput input, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var calendarEvent = await OwnedEventAsync(ownerId, input.EventId, ct); var occurrence = input.OccurrenceStartAt?.ToUniversalTime(); if (occurrence is not null) await ValidateOccurrenceLinkAsync(calendarEvent, occurrence.Value, ct); var existing = await db.TaskEventLinks.SingleOrDefaultAsync(x => x.TaskId == taskId && x.EventId == input.EventId && x.OccurrenceStartAt == occurrence, ct); if (existing is not null) throw new ArgumentException("Vínculo já existe."); var link = new TaskEventLinkEntity { Id = Guid.NewGuid(), TaskId = taskId, EventId = input.EventId, OccurrenceStartAt = occurrence, Relation = Relation(input.Relation), CreatedBy = actorId, CreatedAt = DateTimeOffset.UtcNow }; db.TaskEventLinks.Add(link); AddActivity(taskId, actorId, "event_linked", JsonSerializer.Serialize(new { eventId = input.EventId, occurrenceStartAt = occurrence }), link.CreatedAt); await db.SaveChangesAsync(ct); return LinkDto(link); }
    public async Task<EventDto> CreateEventFromTaskAsync(Guid ownerId, Guid actorId, Guid taskId, CreateEventFromTaskInput input, CancellationToken ct) { var task = await OwnedTaskAsync(ownerId, taskId, ct); var refs = await db.TaskReferences.AsNoTracking().Where(x => x.TaskId == taskId).Select(x => new TaskReferenceV2Input(x.ReferenceType, x.ReferenceId, x.ReferenceName, x.ConnectionRef, x.Relation)).ToArrayAsync(ct); var tags = await db.TaskTags.AsNoTracking().Where(x => x.TaskId == taskId).Select(x => x.Value).ToArrayAsync(ct); var result = await CreateEventAsync(ownerId, actorId, new EventProfessionalInput(task.Title, task.Description, input.StartAt, input.EndAt, Tags: tags, References: refs, Recurrence: input.Recurrence), ct); await LinkAsync(ownerId, actorId, taskId, new(result.Id, null, input.Relation), ct); AddActivity(taskId, actorId, "event_created_from_task", JsonSerializer.Serialize(new { eventId = result.Id }), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return result; }
    public async Task<TaskDetailDto> CreateTaskFromEventAsync(Guid ownerId, Guid actorId, Guid eventId, CreateTaskFromEventInput input, CancellationToken ct) { var e = await OwnedEventAsync(ownerId, eventId, ct); var refs = await db.EventReferences.AsNoTracking().Where(x => x.EventId == eventId).Select(x => new TaskReferenceV2Input(x.ReferenceType, x.ReferenceId, x.ReferenceName, x.ConnectionRef, x.Relation)).ToArrayAsync(ct); var tags = await db.EventTags.AsNoTracking().Where(x => x.EventId == eventId).Select(x => x.Value).ToArrayAsync(ct); var result = await CreateTaskAsync(ownerId, actorId, new TaskProfessionalInput(e.Title, e.Description, DueAt: input.DueAt, References: refs, Tags: tags), ct); await LinkAsync(ownerId, actorId, result.Id, new(eventId, input.OccurrenceStartAt, input.Relation), ct); AddActivity(result.Id, actorId, "task_created_from_event", JsonSerializer.Serialize(new { eventId, input.OccurrenceStartAt }), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return await GetTaskAsync(ownerId, result.Id, ct) ?? result; }
    public async Task UnlinkAsync(Guid ownerId, Guid actorId, Guid taskId, Guid linkId, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); var link = await db.TaskEventLinks.SingleOrDefaultAsync(x => x.Id == linkId && x.TaskId == taskId, ct); if (link is null) return; db.TaskEventLinks.Remove(link); AddActivity(taskId, actorId, "event_unlinked", JsonSerializer.Serialize(new { eventId = link.EventId }), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); }
    public async Task<IReadOnlyList<TaskEventLinkDto>> TaskLinksAsync(Guid ownerId, Guid taskId, CancellationToken ct) { await OwnedTaskAsync(ownerId, taskId, ct); return await db.TaskEventLinks.AsNoTracking().Where(x => x.TaskId == taskId).OrderBy(x => x.CreatedAt).Select(x => LinkDto(x)).ToArrayAsync(ct); }
    public async Task<IReadOnlyList<TaskEventLinkDto>> EventLinksAsync(Guid ownerId, Guid eventId, CancellationToken ct) { await OwnedEventAsync(ownerId, eventId, ct); return await db.TaskEventLinks.AsNoTracking().Where(x => x.EventId == eventId).OrderBy(x => x.CreatedAt).Select(x => LinkDto(x)).ToArrayAsync(ct); }

    private async Task<TaskDetailDto> DetailAsync(TaskEntity task, CancellationToken ct) { var rows = await TaskListItemsAsync([task], ct); var participants = await db.TaskParticipants.AsNoTracking().Where(x => x.TaskId == task.Id).Select(x => new TaskParticipantDto(x.UserId, x.Role, x.AssignedAt)).ToArrayAsync(ct); var refs = await db.TaskReferences.AsNoTracking().Where(x => x.TaskId == task.Id).Select(x => new TaskReferenceV2Dto(x.Id, x.ReferenceType, x.ReferenceId, x.ReferenceName, x.ConnectionRef, x.Relation)).ToArrayAsync(ct); var tags = await db.TaskTags.AsNoTracking().Where(x => x.TaskId == task.Id).OrderBy(x => x.Value).Select(x => x.Value).ToArrayAsync(ct); var children = await db.Tasks.AsNoTracking().Where(x => x.ParentTaskId == task.Id).OrderBy(x => x.DueAt).ToListAsync(ct); var deps = await db.TaskDependencies.AsNoTracking().Where(x => x.TaskId == task.Id).Select(x => x.DependsOnTaskId).ToArrayAsync(ct); var blocks = await db.TaskDependencies.AsNoTracking().Where(x => x.DependsOnTaskId == task.Id).Select(x => x.TaskId).ToArrayAsync(ct); var links = await db.TaskEventLinks.AsNoTracking().Where(x => x.TaskId == task.Id).Select(x => LinkDto(x)).ToArrayAsync(ct); return new TaskDetailDto(task.Id, task.Title, task.Description, task.Status, task.Priority, task.StartAt, task.DueAt, task.CompletedAt, task.ParentTaskId, participants, refs, tags, await TaskListItemsAsync(children, ct), rows[0].SubtaskProgress, deps, blocks, links, task.Version, task.CreatedAt, task.UpdatedAt) { ActionType = task.ActionType, ScheduleHint = task.ScheduleHint }; }
    private async Task<IReadOnlyList<TaskListItemDto>> TaskListItemsAsync(IReadOnlyList<TaskEntity> tasks, CancellationToken ct) { if (tasks.Count == 0) return []; var ids = tasks.Select(x => x.Id).ToArray(); var participants = await db.TaskParticipants.AsNoTracking().Where(x => ids.Contains(x.TaskId)).ToListAsync(ct); var refs = await db.TaskReferences.AsNoTracking().Where(x => ids.Contains(x.TaskId)).ToListAsync(ct); var tags = await db.TaskTags.AsNoTracking().Where(x => ids.Contains(x.TaskId)).ToListAsync(ct); var children = await db.Tasks.AsNoTracking().Where(x => x.ParentTaskId != null && ids.Contains(x.ParentTaskId.Value)).Select(x => new { x.ParentTaskId, x.Status }).ToListAsync(ct); return tasks.Select(t => { var subs = children.Where(x => x.ParentTaskId == t.Id).ToArray(); TaskProgressDto? p = subs.Length == 0 ? null : new(subs.Count(x => x.Status == "done"), subs.Length, Math.Round(100m * subs.Count(x => x.Status == "done") / subs.Length, 2)); var owner = participants.Where(x => x.TaskId == t.Id && x.Role == "owner").Select(x => new TaskParticipantDto(x.UserId, x.Role, x.AssignedAt)).SingleOrDefault(); var taskRefs = refs.Where(x => x.TaskId == t.Id).Select(x => new TaskReferenceV2Dto(x.Id, x.ReferenceType, x.ReferenceId, x.ReferenceName, x.ConnectionRef, x.Relation)).ToArray(); var taskTags = tags.Where(x => x.TaskId == t.Id).OrderBy(x => x.Value).Select(x => x.Value).ToArray(); return new TaskListItemDto(t.Id, t.Title, Summary(t.Description), t.Status, t.Priority, t.DueAt, owner, p, taskRefs, t.Version, t.StartAt, t.CreatedAt, t.UpdatedAt, t.ActionType, t.ScheduleHint) { Tags = taskTags }; }).ToArray(); }
    private async Task<EventDto> EventAsync(CalendarEventEntity e, CancellationToken ct) { var rec = await db.EventRecurrences.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == e.Id, ct); var tags = await db.EventTags.AsNoTracking().Where(x => x.EventId == e.Id).OrderBy(x => x.Value).Select(x => x.Value).ToArrayAsync(ct); var refs = await db.EventReferences.AsNoTracking().Where(x => x.EventId == e.Id).Select(x => new EventReferenceV2Dto(x.Id, x.ReferenceType, x.ReferenceId, x.ReferenceName, x.ConnectionRef, x.Relation)).ToArrayAsync(ct); var dates = await db.EventRecurrenceDates.AsNoTracking().Where(x => x.EventId == e.Id).ToArrayAsync(ct); return new(e.Id, e.Title, e.Description, e.StartAt, e.EndAt, e.Type, e.TimeZoneId, e.Location, e.AvailabilityStatus, e.IsAllDay, e.Source, e.ExternalUid, rec?.RRule, tags, refs, e.Version, e.CreatedAt, e.UpdatedAt) { ExDates = dates.Where(x => x.Kind == "exclude").Select(x => x.OccurrenceStartAt).ToArray(), RDates = dates.Where(x => x.Kind == "include").Select(x => x.OccurrenceStartAt).ToArray() }; }
    private async Task<IReadOnlyList<EventOccurrenceDto>> ExpandAsync(CalendarEventEntity e, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) { var dto = await EventAsync(e, ct); var recurrence = await db.EventRecurrences.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == e.Id, ct); var excluded = (await db.EventRecurrenceDates.AsNoTracking().Where(x => x.EventId == e.Id && x.Kind == "exclude").Select(x => x.OccurrenceStartAt).ToArrayAsync(ct)).ToHashSet(); var added = await db.EventRecurrenceDates.AsNoTracking().Where(x => x.EventId == e.Id && x.Kind == "include").Select(x => x.OccurrenceStartAt).ToArrayAsync(ct); var overrides = (await db.EventOccurrenceOverrides.AsNoTracking().Where(x => x.EventId == e.Id).ToArrayAsync(ct)).ToDictionary(x => x.OriginalStartAt); var duration = e.EndAt is null ? (TimeSpan?)null : e.EndAt.Value - e.StartAt; var expansionFrom = duration is null ? from : from - duration.Value; var starts = recurrence is null ? new List<DateTimeOffset> { e.StartAt } : Repeat(e.StartAt, recurrence.RRule, recurrence.UntilAt, recurrence.Count, expansionFrom, to, e.TimeZoneId); starts.AddRange(added); return starts.Distinct().Where(x => !excluded.Contains(x)).Select(x => { overrides.TryGetValue(x, out var o); var effectiveStart = o?.StartAt ?? x; var end = o?.EndAt ?? (duration is null ? null : effectiveStart + duration); return new EventOccurrenceDto(e.Id, effectiveStart, end, o?.Title ?? e.Title, o?.Description ?? e.Description, e.TimeZoneId, e.Location, e.AvailabilityStatus, e.IsAllDay, o?.IsCancelled ?? false, recurrence?.RRule, dto.Tags, dto.References, e.Version) { Type = e.Type, CreatedAt = e.CreatedAt, UpdatedAt = e.UpdatedAt }; }).Where(x => !x.IsCancelled && x.OccurrenceStartAt < to && (x.OccurrenceEndAt is null || x.OccurrenceEndAt > from)).ToArray(); }
    private static List<DateTimeOffset> Repeat(DateTimeOffset start, string rule, DateTimeOffset? until, int? count, DateTimeOffset from, DateTimeOffset to, string timeZoneId)
    {
        var parts = rule.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0].Trim().ToUpperInvariant(), x => x[1].Trim().ToUpperInvariant());
        var unsupported = parts.Keys.Except(["FREQ", "INTERVAL", "COUNT", "UNTIL", "BYDAY"], StringComparer.OrdinalIgnoreCase).ToArray();
        if (unsupported.Length > 0) throw new ArgumentException($"RRULE contém componentes não suportados: {string.Join(", ", unsupported)}.");
        if (!parts.TryGetValue("FREQ", out var frequency) || frequency is not ("DAILY" or "WEEKLY" or "MONTHLY")) throw new ArgumentException("RRULE suporta FREQ=DAILY, WEEKLY ou MONTHLY.");
        var interval = parts.TryGetValue("INTERVAL", out var v) && int.TryParse(v, out var i) ? Math.Clamp(i, 1, 365) : 1;
        if (parts.TryGetValue("INTERVAL", out var rawInterval) && (!int.TryParse(rawInterval, out var parsedInterval) || parsedInterval < 1)) throw new ArgumentException("INTERVAL deve ser um inteiro positivo.");
        var max = count ?? (parts.TryGetValue("COUNT", out var c) && int.TryParse(c, out var n) ? n : 1000);
        if (parts.TryGetValue("COUNT", out var rawCount) && (!int.TryParse(rawCount, out var parsedCount) || parsedCount < 1)) throw new ArgumentException("COUNT deve ser um inteiro positivo.");
        var rruleUntil = until ?? (parts.TryGetValue("UNTIL", out var u) && DateTimeOffset.TryParseExact(u, ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd"], null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null);
        if (parts.ContainsKey("UNTIL") && rruleUntil is null) throw new ArgumentException("UNTIL deve estar em formato iCalendar válido.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); var localStart = TimeZoneInfo.ConvertTime(start, zone).DateTime; var list = new List<DateTimeOffset>();
        var byDays = parts.TryGetValue("BYDAY", out var byDayValue) ? byDayValue.Split(',').Select(ParseWeekday).Where(x => x is not null).Select(x => x!.Value).Distinct().ToArray() : [];
        if (parts.TryGetValue("BYDAY", out _) && byDays.Length == 0) throw new ArgumentException("BYDAY contém dias inválidos.");
        var generated = 0;
        for (var period = 0; period < 1000 && generated < max; period++)
        {
            IEnumerable<DateTime> candidates = frequency switch
            {
                "WEEKLY" when byDays.Length > 0 => byDays.Select(day => StartOfWeek(localStart, DayOfWeek.Monday).AddDays(7 * interval * period + ((int)day - 1 + 7) % 7).Add(localStart.TimeOfDay)).Where(x => period > 0 || x >= localStart),
                _ => [frequency switch { "DAILY" => localStart.AddDays(interval * period), "WEEKLY" => localStart.AddDays(7 * interval * period), _ => localStart.AddMonths(interval * period) }]
            };
            foreach (var candidateLocal in candidates.OrderBy(x => x))
            {
                if (generated++ >= max) break;
                var current = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidateLocal, DateTimeKind.Unspecified), zone));
                if (rruleUntil is not null && current > rruleUntil) return list;
                if (current >= from && current < to) list.Add(current);
                if (current >= to && frequency != "WEEKLY") return list;
            }
        }
        return list.OrderBy(x => x).Take(Math.Min(max, 1000)).ToList();
    }
    private static DateTime StartOfWeek(DateTime value, DayOfWeek firstDay) { var diff = (7 + (value.DayOfWeek - firstDay)) % 7; return value.Date.AddDays(-diff); }
    private static DayOfWeek? ParseWeekday(string value) => value.Trim() switch { "SU" => DayOfWeek.Sunday, "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, _ => null };
    private static (DateTimeOffset? UntilAt, int? Count) ParseRuleMetadata(string rule) { var parts = rule.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0].Trim().ToUpperInvariant(), x => x[1].Trim().ToUpperInvariant()); DateTimeOffset? until = null; if (parts.TryGetValue("UNTIL", out var u) && DateTimeOffset.TryParseExact(u, ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd"], null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)) until = parsed; int? count = parts.TryGetValue("COUNT", out var c) && int.TryParse(c, out var n) && n > 0 ? n : null; return (until, count); }
    private async Task ReplaceTaskCollectionsAsync(TaskEntity task, Guid actorId, IReadOnlyList<TaskParticipantInput>? participants, IReadOnlyList<TaskReferenceV2Input>? references, IReadOnlyList<string>? tags, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (participants is not null)
        {
            var normalized = participants.Select(p => new TaskParticipantInput(p.UserId, p.Role.Trim().ToLowerInvariant())).ToArray();
            if (normalized.Any(p => p.UserId == Guid.Empty || !ParticipantRoles.Contains(p.Role)) || normalized.Count(p => p.Role == "owner") > 1 || normalized.Select(p => p.UserId).Distinct().Count() != normalized.Length) throw new ArgumentException("Participantes inválidos.");
            var previous = await db.TaskParticipants.Where(x => x.TaskId == task.Id).ToArrayAsync(ct);
            db.TaskParticipants.RemoveRange(previous);
            foreach (var p in normalized)
            {
                db.TaskParticipants.Add(new TaskParticipantEntity { Id = Guid.NewGuid(), TaskId = task.Id, UserId = p.UserId, Role = p.Role, AssignedAt = now, AssignedBy = actorId });
                var old = previous.SingleOrDefault(x => x.UserId == p.UserId);
                if (old is null || old.Role != p.Role) AddActivity(task.Id, actorId, p.Role == "owner" ? "owner_changed" : "collaborator_added", JsonSerializer.Serialize(new { userId = p.UserId, role = p.Role }), now);
            }
            foreach (var old in previous.Where(x => !normalized.Any(p => p.UserId == x.UserId))) AddActivity(task.Id, actorId, old.Role == "owner" ? "owner_changed" : "collaborator_removed", JsonSerializer.Serialize(new { userId = old.UserId }), now);
        }
        if (references is not null)
        {
            var normalized = NormalizeReferences(references);
            var previous = await db.TaskReferences.Where(x => x.TaskId == task.Id).ToArrayAsync(ct);
            db.TaskReferences.RemoveRange(previous);
            foreach (var r in normalized)
            {
                db.TaskReferences.Add(new TaskReferenceEntity { Id = Guid.NewGuid(), TaskId = task.Id, ReferenceType = r.ReferenceType, ReferenceId = r.ReferenceId, ReferenceName = r.ReferenceName, ConnectionRef = r.ConnectionRef, Relation = r.Relation });
                if (!previous.Any(x => x.ReferenceType == r.ReferenceType && x.ReferenceId == r.ReferenceId && x.ConnectionRef == r.ConnectionRef)) AddActivity(task.Id, actorId, "reference_added", JsonSerializer.Serialize(new { r.ReferenceType, r.ReferenceId }), now);
            }
            foreach (var old in previous.Where(x => !normalized.Any(r => r.ReferenceType == x.ReferenceType && r.ReferenceId == x.ReferenceId && r.ConnectionRef == x.ConnectionRef))) AddActivity(task.Id, actorId, "reference_removed", JsonSerializer.Serialize(new { referenceId = old.Id }), now);
        }
        if (tags is not null)
        {
            var normalized = NormalizeTags(tags);
            var previous = await db.TaskTags.Where(x => x.TaskId == task.Id).ToArrayAsync(ct);
            db.TaskTags.RemoveRange(previous);
            foreach (var value in normalized)
            {
                db.TaskTags.Add(new TaskTagEntity { TaskId = task.Id, Value = value, NormalizedValue = NormalizeTag(value) });
                if (!previous.Any(x => x.NormalizedValue == NormalizeTag(value))) AddActivity(task.Id, actorId, "tag_added", JsonSerializer.Serialize(new { tag = value }), now);
            }
            foreach (var old in previous.Where(x => !normalized.Any(value => NormalizeTag(value) == x.NormalizedValue))) AddActivity(task.Id, actorId, "tag_removed", JsonSerializer.Serialize(new { tag = old.Value }), now);
        }
    }
    private async Task ReplaceEventCollectionsAsync(Guid eventId, IReadOnlyList<TaskReferenceV2Input>? refs, IReadOnlyList<string>? tags, CancellationToken ct) { if (refs is not null) { db.EventReferences.RemoveRange(await db.EventReferences.Where(x => x.EventId == eventId).ToListAsync(ct)); foreach (var r in NormalizeReferences(refs)) db.EventReferences.Add(new() { Id = Guid.NewGuid(), EventId = eventId, ReferenceType = r.ReferenceType, ReferenceId = r.ReferenceId, ReferenceName = r.ReferenceName, ConnectionRef = r.ConnectionRef, Relation = r.Relation }); } if (tags is not null) { db.EventTags.RemoveRange(await db.EventTags.Where(x => x.EventId == eventId).ToListAsync(ct)); foreach (var value in NormalizeTags(tags)) db.EventTags.Add(new() { EventId = eventId, Value = value, NormalizedValue = NormalizeTag(value) }); } }
    private async Task SetRecurrenceAsync(Guid id, EventRecurrenceInput? input, DateTimeOffset now, CancellationToken ct) { var current = await db.EventRecurrences.SingleOrDefaultAsync(x => x.EventId == id, ct); if (string.IsNullOrWhiteSpace(input?.RRule)) { if (current is not null) db.EventRecurrences.Remove(current); db.EventRecurrenceDates.RemoveRange(await db.EventRecurrenceDates.Where(x => x.EventId == id).ToListAsync(ct)); return; } var rule = input.RRule.Trim().ToUpperInvariant(); if (rule.Length > 1000) throw new ArgumentException("RRULE deve ter no máximo 1.000 caracteres."); _ = Repeat(DateTimeOffset.UtcNow, rule, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "America/Sao_Paulo"); var meta = ParseRuleMetadata(rule); if (current is null) { current = new() { EventId = id, CreatedAt = now }; db.EventRecurrences.Add(current); } current.RRule = rule; current.UntilAt = meta.UntilAt; current.Count = meta.Count; current.UpdatedAt = now; db.EventRecurrenceDates.RemoveRange(await db.EventRecurrenceDates.Where(x => x.EventId == id).ToListAsync(ct)); foreach (var x in (input.ExDates ?? []).Distinct()) db.EventRecurrenceDates.Add(new() { EventId = id, OccurrenceStartAt = x.ToUniversalTime(), Kind = "exclude" }); foreach (var x in (input.RDates ?? []).Distinct()) db.EventRecurrenceDates.Add(new() { EventId = id, OccurrenceStartAt = x.ToUniversalTime(), Kind = "include" }); }
    private async Task<TaskEntity> OwnedTaskAsync(Guid owner, Guid id, CancellationToken ct) => await db.Tasks.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct) ?? throw new KeyNotFoundException("Task não encontrada.");
    private async Task<CalendarEventEntity> OwnedEventAsync(Guid owner, Guid id, CancellationToken ct) => await db.CalendarEvents.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct) ?? throw new KeyNotFoundException("Event não encontrado.");
    private async Task<bool> HasPathAsync(Guid from, Guid target, CancellationToken ct) { var pending = new Queue<Guid>([from]); var seen = new HashSet<Guid>(); while (pending.TryDequeue(out var node)) { if (!seen.Add(node)) continue; if (node == target) return true; foreach (var next in await db.TaskDependencies.Where(x => x.TaskId == node).Select(x => x.DependsOnTaskId).ToArrayAsync(ct)) pending.Enqueue(next); } return false; }
    private async Task ValidateOccurrenceLinkAsync(CalendarEventEntity calendarEvent, DateTimeOffset occurrence, CancellationToken ct, bool allowCancelledOverride = false)
    {
        var recurrence = await db.EventRecurrences.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == calendarEvent.Id, ct);
        if (recurrence is null)
        {
            if (calendarEvent.StartAt.ToUniversalTime() != occurrence) throw new ArgumentException("A ocorrência indicada não pertence ao Event.");
            return;
        }
        var candidates = Repeat(calendarEvent.StartAt, recurrence.RRule, recurrence.UntilAt, recurrence.Count, occurrence.AddTicks(-1), occurrence.AddTicks(1), calendarEvent.TimeZoneId);
        var rdate = await db.EventRecurrenceDates.AsNoTracking().AnyAsync(x => x.EventId == calendarEvent.Id && x.Kind == "include" && x.OccurrenceStartAt == occurrence, ct);
        var excluded = await db.EventRecurrenceDates.AsNoTracking().AnyAsync(x => x.EventId == calendarEvent.Id && x.Kind == "exclude" && x.OccurrenceStartAt == occurrence, ct);
        var overrideRow = await db.EventOccurrenceOverrides.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == calendarEvent.Id && x.OriginalStartAt == occurrence, ct);
        if ((!candidates.Contains(occurrence) && !rdate) || excluded || (!allowCancelledOverride && overrideRow?.IsCancelled == true)) throw new ArgumentException("A ocorrência indicada não pertence ao Event ou foi cancelada.");
    }
    private void AddActivity(Guid task, Guid actor, string type, string? data, DateTimeOffset at) => db.TaskActivities.Add(new() { Id = Guid.NewGuid(), TaskId = task, ActorId = actor, EventType = type, Data = data, CreatedAt = at });
    private static TaskEventLinkDto LinkDto(TaskEventLinkEntity x) => new(x.Id, x.TaskId, x.EventId, x.OccurrenceStartAt, x.Relation, x.CreatedAt);
    private static void AssertVersion(long actual, long? expected) { if (expected is not null && expected != actual) throw new InvalidOperationException("A Task ou Event foi alterado por outra pessoa. Atualize antes de salvar."); }
    private static string Title(string? value) { var v = value?.Trim(); if (string.IsNullOrWhiteSpace(v) || v.Length > 240) throw new ArgumentException("Título deve ter entre 1 e 240 caracteres."); return v; }
    private static string? Description(string? value) { var v = value?.Trim(); if (v?.Length > 4000) throw new ArgumentException("Descrição deve ter no máximo 4.000 caracteres."); return string.IsNullOrWhiteSpace(v) ? null : v; }
    private static string Status(string? value) { var v = string.IsNullOrWhiteSpace(value) ? "todo" : value.Trim().ToLowerInvariant(); return TaskStatuses.Contains(v) ? v : throw new ArgumentException("Status inválido."); }
    private static string Priority(string? value) { var v = string.IsNullOrWhiteSpace(value) ? "medium" : value.Trim().ToLowerInvariant(); return Priorities.Contains(v) ? v : throw new ArgumentException("Prioridade inválida."); }
    private static string Availability(string? value) { var v = string.IsNullOrWhiteSpace(value) ? "busy" : value.Trim().ToLowerInvariant(); return v is "free" or "busy" or "tentative" ? v : throw new ArgumentException("Disponibilidade inválida."); }
    private static string EventType(string? value) { var v = string.IsNullOrWhiteSpace(value) ? "other" : value.Trim().ToLowerInvariant(); return v is "manual" or "meeting" or "alignment" or "delivery" or "training" or "webclass" or "class" or "deadline" or "other" ? v : "other"; }
    private static string Relation(string? value) { var v = string.IsNullOrWhiteSpace(value) ? "related" : value.Trim().ToLowerInvariant(); return v is "related" or "scheduled_for" or "generated_from" ? v : throw new ArgumentException("Relação inválida."); }
    private static string Zone(string? value) { var v = string.IsNullOrWhiteSpace(value) ? "America/Sao_Paulo" : value.Trim(); try { _ = TimeZoneInfo.FindSystemTimeZoneById(v); return v; } catch { throw new ArgumentException("Timezone IANA inválido."); } }
    private static string? Trim(string? value, int limit) { var v = value?.Trim(); if (v?.Length > limit) throw new ArgumentException($"Campo deve ter no máximo {limit} caracteres."); return string.IsNullOrWhiteSpace(v) ? null : v; }
    private static string? NormalizeLegacy(string? value, int limit) => Trim(value, limit);
    private static void ValidateTaskDates(DateTimeOffset? startAt, DateTimeOffset? dueAt) { if (startAt is not null && dueAt is not null && dueAt < startAt) throw new ArgumentException("O prazo deve ser posterior ou igual ao início."); }
    private static void ValidateEvent(string? title, string? description, DateTimeOffset start, DateTimeOffset? end, string? tz, string? location) { _ = Title(title); _ = Description(description); _ = Zone(tz); _ = Trim(location, 500); if (end is not null && end <= start) throw new ArgumentException("O fim deve ser posterior ao início."); }
    private static IReadOnlyList<TaskReferenceV2Input> NormalizeReferences(IReadOnlyList<TaskReferenceV2Input> refs) { if (refs.Count > 50) throw new ArgumentException("Informe no máximo 50 referências."); var output = new List<TaskReferenceV2Input>(); foreach (var r in refs) { var type = r.ReferenceType?.Trim().ToLowerInvariant(); var id = r.ReferenceId?.Trim(); if (string.IsNullOrWhiteSpace(type) || type.Length > 32 || string.IsNullOrWhiteSpace(id) || id.Length > 200) throw new ArgumentException("Referência inválida."); if (!output.Any(x => x.ReferenceType == type && x.ReferenceId == id && x.ConnectionRef == r.ConnectionRef?.Trim())) output.Add(new(type, id, Trim(r.ReferenceName, 240), Trim(r.ConnectionRef, 64), Trim(r.Relation, 64))); } return output; }
    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string> tags) { if (tags.Count > 20) throw new ArgumentException("Informe no máximo 20 tags."); var output = tags.Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (output.Any(x => x.Length > 64)) throw new ArgumentException("Tags devem ter entre 1 e 64 caracteres."); return output; }
    private static string NormalizeTag(string value) => value.Trim().ToLowerInvariant();
    private static string? Summary(string? description) => string.IsNullOrWhiteSpace(description) ? null : description.Length <= 160 ? description : description[..160];
}
