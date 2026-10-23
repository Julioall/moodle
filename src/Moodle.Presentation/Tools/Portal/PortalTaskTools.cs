using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Tools;
using Moodle.Infrastructure;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Tools;
using Moodle.Presentation;
using Microsoft.EntityFrameworkCore;

namespace Moodle.Presentation.Tools.Portal;

[McpServerToolType]
public sealed class PortalTaskTools(ConnectorDbContext dbContext, PortalMcpIdentityResolver identityResolver)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(
        Name = "list_tasks",
        Title = "Listar tarefas do portal",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PortalTaskListResponse>))]
    [MoodleToolMetadata(
        Family = "portal-tasks",
        Classification = "R1",
        Kind = "read",
        CanonicalOperation = "portal.tasks.list",
        RequiredPlatformPermission = "tasks.manage",
        Evidence = "Consulta tarefas operacionais persistidas no portal para o usuário autenticado.")]
    [Description("Lista as tarefas do usuário no portal, com filtros opcionais de status e prioridade. Não consulta tarefas do Moodle.")]
    public async Task<CallToolResult> ListAsync(
        [Description("Número da página, começando em 1.")] int page = 1,
        [Description("Quantidade por página, entre 1 e 100.")] int pageSize = 50,
        [Description("Status opcional: todo, in_progress ou done.")] string? status = null,
        [Description("Prioridade opcional: low, medium, high ou urgent.")] string? priority = null,
        [Description("Busca por título ou descrição.")] string? search = null,
        [Description("Filtra pelo participante responsável ou colaborador.")] Guid? participantId = null,
        [Description("Filtra por tag.")] string? tag = null,
        [Description("Tipo de referência estruturada.")] string? referenceType = null,
        [Description("Identificador da referência estruturada.")] string? referenceId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var identity = await identityResolver.ResolveAsync(cancellationToken);
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var result = await new ProfessionalPlannerService(dbContext).ListTasksAsync(identity.Id, page, pageSize, search, status, priority, participantId, tag, referenceType, referenceId, cancellationToken);
            var tasks = result.Items.Select(item => new TaskDto(item.Id, item.Title, item.Summary, item.Status, item.Priority, item.StartAt, item.DueAt, item.CreatedAt ?? DateTimeOffset.UtcNow, item.UpdatedAt ?? DateTimeOffset.UtcNow,
                item.References.Select(reference => new PlannerReferenceDto(reference.ReferenceType, reference.ReferenceId, reference.ReferenceName, reference.ConnectionRef, null, null, null)).ToArray(), item.ActionType, item.ScheduleHint)
            {
                Tags = item.Tags, Version = item.Version, Participants = item.Owner is null ? [] : [item.Owner]
            }).ToArray();
            var total = result.Total;

            return Success(new PortalTaskListResponse(tasks, total, page, pageSize, page * pageSize < total),
                $"{tasks.Length} tarefa(s) retornada(s).");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ToolResultHelper.Error<PortalTaskListResponse>(exception.Message, errorCode: "portal_tasks_invalid_request");
        }
    }

    [McpServerTool(
        Name = "create_task",
        Title = "Criar tarefa do portal",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PortalTaskWriteResponse>))]
    [MoodleToolMetadata(
        Family = "portal-tasks",
        Classification = "R2",
        Kind = "write",
        CanonicalOperation = "portal.tasks.create",
        RequiredPlatformPermission = "tasks.manage",
        Evidence = "Cria uma tarefa operacional privada no portal.")]
    [Description("Cria uma tarefa operacional no portal para o usuário autenticado. A tarefa não é enviada ao Moodle.")]
    public async Task<CallToolResult> CreateAsync(
        [Description("Título da tarefa.")] string title,
        [Description("Descrição opcional.")] string? description = null,
        [Description("Status inicial: todo, in_progress ou done.")] string? status = null,
        [Description("Prioridade: low, medium, high ou urgent.")] string? priority = null,
        [Description("Data/hora opcional de início em ISO 8601.")] DateTimeOffset? startAt = null,
        [Description("Data/hora opcional de vencimento em ISO 8601.")] DateTimeOffset? dueAt = null,
        [Description("Vínculos com objetos Moodle: course, student, class ou school. Para turmas, informe parentReferenceId com o curso.")] IReadOnlyList<PlannerReferenceInput>? references = null,
        [Description("Tags livres da tarefa, até 20.")] IReadOnlyList<string>? tags = null,
        [Description("Participantes, com role owner, collaborator ou watcher.")] IReadOnlyList<TaskParticipantInput>? participants = null,
        [Description("UUID da Task pai para criar uma subtarefa.")] Guid? parentTaskId = null,
        [Description("Subtarefas inline; use somente na criação de uma Task-raiz.")] IReadOnlyList<TaskSubtaskInput>? subtasks = null,
        [Description("UUIDs das Tasks que bloqueiam esta Task.")] IReadOnlyList<Guid>? dependsOnTaskIds = null,
        [Description("Tipo de ação planejada, por exemplo grade_report ou send_to_coordination.")] string? actionType = null,
        [Description("Descrição da programação, por exemplo mensal ou toda sexta-feira.")] string? scheduleHint = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var identity = await identityResolver.ResolveAsync(cancellationToken);
            var refs = references?.Select(r => new TaskReferenceV2Input(r.ReferenceType, r.ReferenceId, r.ReferenceName, r.ConnectionRef)).ToArray();
            var result = await new ProfessionalPlannerService(dbContext).CreateTaskAsync(identity.Id, identity.Id, new TaskProfessionalInput(title, description, status, priority, startAt, dueAt, parentTaskId, participants, refs, tags, ActionType: actionType, ScheduleHint: scheduleHint, Subtasks: subtasks, DependsOnTaskIds: dependsOnTaskIds), cancellationToken);
            return Success(new PortalTaskWriteResponse("created", ToDto(result)), "Tarefa criada.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ToolResultHelper.Error<PortalTaskWriteResponse>(exception.Message, errorCode: "portal_tasks_invalid_request");
        }
    }

    [McpServerTool(
        Name = "create_tasks_for_references",
        Title = "Criar tarefas por curso ou turma",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PortalTaskBatchWriteResponse>))]
    [MoodleToolMetadata(
        Family = "portal-tasks",
        Classification = "R2",
        Kind = "write",
        CanonicalOperation = "portal.tasks.create_batch",
        RequiredPlatformPermission = "tasks.manage",
        Evidence = "Cria uma tarefa operacional para cada referência de curso, turma, estudante ou escola informada pelo usuário.")]
    [Description("Cria uma tarefa operacional por referência. Use {name} no título para incluir o nome do curso ou turma; a operação permanece local no portal e não executa a ação Moodle.")]
    public async Task<CallToolResult> CreateForReferencesAsync(
        [Description("Título base; {name} será substituído pelo nome do vínculo.")] string titleTemplate,
        [Description("Referências tipadas a serem vinculadas: course, class, student ou school.")] IReadOnlyList<PlannerReferenceInput> references,
        [Description("Descrição opcional da tarefa.")] string? description = null,
        [Description("Status inicial.")] string? status = null,
        [Description("Prioridade.")] string? priority = null,
        [Description("Data/hora opcional de início.")] DateTimeOffset? startAt = null,
        [Description("Data/hora opcional de vencimento.")] DateTimeOffset? dueAt = null,
        [Description("Tipo de ação planejada, por exemplo grade_report ou send_to_coordination.")] string? actionType = null,
        [Description("Descrição da programação, por exemplo mensal ou toda sexta-feira.")] string? scheduleHint = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var identity = await identityResolver.ResolveAsync(cancellationToken);
            var normalizedReferences = PlannerReferenceStore.Normalize(references);
            if (normalizedReferences.Count == 0) throw new ArgumentException("Informe ao menos um vínculo.", nameof(references));
            var service = new ProfessionalPlannerService(dbContext);
            var tasks = new List<TaskDetailDto>(normalizedReferences.Count);
            foreach (var reference in normalizedReferences)
            {
                var task = await service.CreateTaskAsync(identity.Id, identity.Id, new TaskProfessionalInput(
                    PortalMcpValueNormalizer.RequireTitle(titleTemplate.Replace("{name}", reference.ReferenceName ?? reference.ReferenceId, StringComparison.OrdinalIgnoreCase)),
                    PortalMcpValueNormalizer.NormalizeDescription(description),
                    PortalMcpValueNormalizer.NormalizeTaskStatus(status), PortalMcpValueNormalizer.NormalizeTaskPriority(priority),
                    startAt, dueAt, References: [new(reference.ReferenceType, reference.ReferenceId, reference.ReferenceName, reference.ConnectionRef)],
                    ActionType: NormalizePlannerAction(actionType), ScheduleHint: NormalizePlannerSchedule(scheduleHint)), cancellationToken);
                tasks.Add(task);
            }
            return Success(new PortalTaskBatchWriteResponse("created", tasks.Select(ToDto).ToArray()), $"{tasks.Count} tarefa(s) criada(s).");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ToolResultHelper.Error<PortalTaskBatchWriteResponse>(exception.Message, errorCode: "portal_tasks_invalid_request");
        }
    }

    [McpServerTool(
        Name = "update_task",
        Title = "Editar tarefa do portal",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PortalTaskWriteResponse>))]
    [MoodleToolMetadata(
        Family = "portal-tasks",
        Classification = "R2",
        Kind = "write",
        CanonicalOperation = "portal.tasks.update",
        RequiredPlatformPermission = "tasks.manage",
        Evidence = "Atualiza uma tarefa operacional privada no portal.")]
    [Description("Edita uma tarefa do portal pertencente ao usuário autenticado. Campos omitidos são preservados.")]
    public async Task<CallToolResult> UpdateAsync(
        [Description("UUID da tarefa.")] Guid taskId,
        [Description("Novo título, quando necessário.")] string? title = null,
        [Description("Nova descrição; envie texto vazio para limpar.")] string? description = null,
        [Description("Novo status: todo, in_progress ou done.")] string? status = null,
        [Description("Nova prioridade: low, medium, high ou urgent.")] string? priority = null,
        [Description("Nova data/hora de início em ISO 8601.")] DateTimeOffset? startAt = null,
        [Description("Nova data/hora de vencimento em ISO 8601.")] DateTimeOffset? dueAt = null,
        [Description("Limpa a data de início quando true.")] bool clearStartAt = false,
        [Description("Limpa a data de vencimento quando true.")] bool clearDueAt = false,
        [Description("Substitui os vínculos quando informado: course, student, class ou school.")] IReadOnlyList<PlannerReferenceInput>? references = null,
        [Description("Substitui as tags quando informado.")] IReadOnlyList<string>? tags = null,
        [Description("Substitui os participantes quando informado.")] IReadOnlyList<TaskParticipantInput>? participants = null,
        [Description("Versão esperada para concorrência otimista.")] long? expectedVersion = null,
        [Description("Novo tipo de ação planejada.")] string? actionType = null,
        [Description("Nova descrição da programação.")] string? scheduleHint = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (taskId == Guid.Empty)
                throw new ArgumentException("Informe um taskId UUID válido.", nameof(taskId));

            var identity = await identityResolver.ResolveAsync(cancellationToken);
            var refs = references?.Select(r => new TaskReferenceV2Input(r.ReferenceType, r.ReferenceId, r.ReferenceName, r.ConnectionRef)).ToArray();
            var result = await new ProfessionalPlannerService(dbContext).UpdateTaskAsync(identity.Id, identity.Id, taskId,
                new TaskProfessionalInput(title, description, status, priority, startAt, dueAt, Participants: participants, References: refs, Tags: tags, ExpectedVersion: expectedVersion, ActionType: actionType, ScheduleHint: scheduleHint, ClearStartAt: clearStartAt, ClearDueAt: clearDueAt), cancellationToken);
            return Success(new PortalTaskWriteResponse("updated", ToDto(result)), "Tarefa atualizada.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ToolResultHelper.Error<PortalTaskWriteResponse>(exception.Message, errorCode: "portal_tasks_invalid_request");
        }
    }

    [McpServerTool(
        Name = "remove_task",
        Title = "Remover tarefa do portal",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PortalTaskRemovalResponse>))]
    [MoodleToolMetadata(
        Family = "portal-tasks",
        Classification = "R3",
        Kind = "destructive-write",
        CanonicalOperation = "portal.tasks.remove",
        RequiredPlatformPermission = "tasks.manage",
        Evidence = "Remove uma tarefa operacional privada do portal.")]
    [Description("Remove uma tarefa do portal pertencente ao usuário autenticado. É uma operação destrutiva e exige confirmação do cliente MCP.")]
    public async Task<CallToolResult> RemoveAsync(
        [Description("UUID da tarefa.")] Guid taskId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (taskId == Guid.Empty)
                throw new ArgumentException("Informe um taskId UUID válido.", nameof(taskId));

            var identity = await identityResolver.ResolveAsync(cancellationToken);
            try
            {
                await new ProfessionalPlannerService(dbContext).DeleteTaskAsync(identity.Id, taskId, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                return ToolResultHelper.Error<PortalTaskRemovalResponse>("Tarefa não encontrada.", errorCode: "portal_task_not_found");
            }
            return Success(new PortalTaskRemovalResponse(taskId, true), "Tarefa removida.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ToolResultHelper.Error<PortalTaskRemovalResponse>(exception.Message, errorCode: "portal_tasks_invalid_request");
        }
    }

    private static TaskDto ToDto(TaskEntity task, IReadOnlyList<PlannerReferenceDto>? references = null) =>
        new(task.Id, task.Title, task.Description, task.Status, task.Priority, task.StartAt, task.DueAt, task.CreatedAt, task.UpdatedAt, references ?? [], task.ActionType, task.ScheduleHint);

    private static TaskDto ToDto(TaskDetailDto task) => new(task.Id, task.Title, task.Description, task.Status, task.Priority, task.StartAt, task.DueAt, task.CreatedAt, task.UpdatedAt,
        task.References.Select(reference => new PlannerReferenceDto(reference.ReferenceType, reference.ReferenceId, reference.ReferenceName, reference.ConnectionRef, null, null, null)).ToArray(), task.ActionType, task.ScheduleHint)
    {
        Tags = task.Tags, Participants = task.Participants, CompletedAt = task.CompletedAt, Version = task.Version
    };

    private static string? NormalizePlannerAction(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 80)];
    private static string? NormalizePlannerSchedule(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 240)];

    private static CallToolResult Success<T>(T data, string message)
    {
        var response = new ToolResponse<T>("ok", data, [], Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, Message: message);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = message }],
            StructuredContent = JsonSerializer.SerializeToElement(response, JsonOptions),
            IsError = false
        };
    }
}

public sealed record PortalTaskListResponse(
    [property: JsonPropertyName("tasks")] IReadOnlyList<TaskDto> Tasks,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("hasMore")] bool HasMore);

public sealed record PortalTaskWriteResponse(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("task")] TaskDto Task);

public sealed record PortalTaskRemovalResponse(
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("removed")] bool Removed);

public sealed record PortalTaskBatchWriteResponse(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("tasks")] IReadOnlyList<TaskDto> Tasks);
