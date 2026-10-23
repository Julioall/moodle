using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moodle.Domain;
using Moodle.Infrastructure;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Tarefas pessoais do planejador do portal, incluindo vínculos Moodle e
/// mutações protegidas por antiforgery.
/// </summary>
internal static class PortalTaskEndpoints
{
    public static void MapTasks(WebApplication app, string rateLimitPolicy)
    {
        app.MapGet("/api/tasks", async (
            HttpContext context,
            ConnectorDbContext dbContext,
            int page = 1,
            int pageSize = 20,
            string? status = null,
            string? priority = null,
            CancellationToken cancellationToken = default) =>
        {
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var query = dbContext.Tasks.AsNoTracking().Where(task => task.OwnerId == identity.Id);
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(task => task.Status == status);
            if (!string.IsNullOrWhiteSpace(priority)) query = query.Where(task => task.Priority == priority);
            var total = await query.CountAsync(cancellationToken);
            var taskEntities = await query
                .OrderBy(task => task.DueAt)
                .ThenByDescending(task => task.UpdatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            var taskReferences = await PlannerReferenceStore.ForTasksAsync(
                dbContext,
                identity.Id,
                taskEntities.Select(item => item.Id).ToArray(),
                cancellationToken);
            var items = taskEntities.Select(task => new TaskDto(
                task.Id,
                task.Title,
                task.Description,
                task.Status,
                task.Priority,
                task.StartAt,
                task.DueAt,
                task.CreatedAt,
                task.UpdatedAt,
                taskReferences.GetValueOrDefault(task.Id, []),
                task.ActionType,
                task.ScheduleHint)).ToList();
            return Results.Ok(new AppListEnvelope<TaskDto>(
                items,
                new AppListMeta(page, pageSize, items.Count, page * pageSize < total, DateTimeOffset.UtcNow, null, null, total)));
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPost("/api/tasks", async (
            HttpContext context,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            TaskInput input,
            CancellationToken cancellationToken) =>
        {
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.TasksManage)) return Results.Forbid();
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);
            if (string.IsNullOrWhiteSpace(input.Title))
                return Results.BadRequest(new { error = new { code = "invalid_title", message = "Título é obrigatório." } });

            var now = DateTimeOffset.UtcNow;
            var task = new TaskEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = identity.Id,
                Title = input.Title.Trim(),
                Description = input.Description?.Trim(),
                Status = NormalizeTaskStatus(input.Status),
                Priority = NormalizeTaskPriority(input.Priority),
                StartAt = input.StartAt,
                DueAt = input.DueAt,
                ActionType = NormalizePlannerAction(input.ActionType),
                ScheduleHint = NormalizePlannerSchedule(input.ScheduleHint),
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.Tasks.Add(task);
            if (input.References is not null)
                await PlannerReferenceStore.ReplaceForTaskAsync(dbContext, identity.Id, task.Id, input.References, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            var taskReferences = input.References is null
                ? Array.Empty<PlannerReferenceDto>()
                : PlannerReferenceStore.Normalize(input.References)
                    .Select(reference => new PlannerReferenceDto(
                        reference.ReferenceType,
                        reference.ReferenceId,
                        reference.ReferenceName,
                        reference.ConnectionRef,
                        reference.ParentReferenceType,
                        reference.ParentReferenceId,
                        reference.ParentReferenceName))
                    .ToArray();
            return Results.Created($"/api/tasks/{task.Id}", new AppEnvelope<TaskDto>(
                new TaskDto(task.Id, task.Title, task.Description, task.Status, task.Priority, task.StartAt, task.DueAt,
                    task.CreatedAt, task.UpdatedAt, taskReferences, task.ActionType, task.ScheduleHint),
                new AppMeta(now, null)));
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapPatch("/api/tasks/{id:guid}", async (
            Guid id,
            HttpContext context,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            TaskInput input,
            CancellationToken cancellationToken) =>
        {
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.TasksManage)) return Results.Forbid();
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);

            var task = await dbContext.Tasks.SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == identity.Id, cancellationToken);
            if (task is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(input.Title)) task.Title = input.Title.Trim();
            if (input.Description is not null) task.Description = input.Description.Trim();
            if (input.Status is not null) task.Status = NormalizeTaskStatus(input.Status);
            if (input.Priority is not null) task.Priority = NormalizeTaskPriority(input.Priority);
            if (input.StartAt is not null) task.StartAt = input.StartAt;
            if (input.DueAt is not null) task.DueAt = input.DueAt;
            if (input.ActionType is not null) task.ActionType = NormalizePlannerAction(input.ActionType);
            if (input.ScheduleHint is not null) task.ScheduleHint = NormalizePlannerSchedule(input.ScheduleHint);
            if (input.References is not null)
                await PlannerReferenceStore.ReplaceForTaskAsync(dbContext, identity.Id, task.Id, input.References, cancellationToken);
            task.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            var taskReferences = await PlannerReferenceStore.ForTasksAsync(dbContext, identity.Id, [task.Id], cancellationToken);
            return Results.Ok(new AppEnvelope<TaskDto>(
                new TaskDto(task.Id, task.Title, task.Description, task.Status, task.Priority, task.StartAt, task.DueAt,
                    task.CreatedAt, task.UpdatedAt, taskReferences.GetValueOrDefault(task.Id, []), task.ActionType, task.ScheduleHint),
                new AppMeta(task.UpdatedAt, null)));
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapDelete("/api/tasks", async (
            [FromBody] TaskBulkDeleteInput? input,
            HttpContext context,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.TasksManage)) return Results.Forbid();
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);

            var ids = input?.Ids?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray() ?? Array.Empty<Guid>();
            if (ids.Length == 0 || ids.Length > 100)
            {
                return Results.BadRequest(new { error = new { code = "invalid_task_ids", message = "Informe entre 1 e 100 tarefas para remover." } });
            }

            var tasks = await dbContext.Tasks
                .Where(task => task.OwnerId == identity.Id && ids.Contains(task.Id))
                .ToListAsync(cancellationToken);
            dbContext.PlannerLinks.RemoveRange(await dbContext.PlannerLinks
                .Where(link => link.OwnerId == identity.Id && link.TaskId != null && ids.Contains(link.TaskId.Value))
                .ToListAsync(cancellationToken));
            dbContext.Tasks.RemoveRange(tasks);
            await dbContext.SaveChangesAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new AppEnvelope<TaskBulkDeleteResult>(new TaskBulkDeleteResult(ids.Length, tasks.Count), new AppMeta(now, null)));
        }).RequireRateLimiting(rateLimitPolicy);

        app.MapDelete("/api/tasks/{id:guid}", async (
            Guid id,
            HttpContext context,
            ConnectorDbContext dbContext,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.TasksManage)) return Results.Forbid();
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
            if (identity is null) return Results.Unauthorized();
            await antiforgery.ValidateRequestAsync(context);

            var task = await dbContext.Tasks.SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == identity.Id, cancellationToken);
            if (task is null) return Results.NotFound();
            dbContext.PlannerLinks.RemoveRange(await dbContext.PlannerLinks
                .Where(link => link.OwnerId == identity.Id && link.TaskId == id)
                .ToListAsync(cancellationToken));
            dbContext.Tasks.Remove(task);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }).RequireRateLimiting(rateLimitPolicy);
    }

    private static string NormalizeTaskStatus(string? value) => value switch
    {
        "in_progress" => "in_progress",
        "blocked" => "blocked",
        "done" => "done",
        "cancelled" => "cancelled",
        _ => "todo"
    };

    private static string NormalizeTaskPriority(string? value) => value switch
    {
        "low" => "low",
        "high" => "high",
        "urgent" => "urgent",
        _ => "medium"
    };

    private static string? NormalizePlannerAction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length > 80 ? normalized[..80] : normalized;
    }

    private static string? NormalizePlannerSchedule(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length > 240 ? normalized[..240] : normalized;
    }
}
