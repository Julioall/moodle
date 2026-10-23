using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Tools;
using Moodle.Infrastructure;
using Moodle.Presentation;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Tools;

namespace Moodle.Presentation.Tools.Portal;

[McpServerToolType]
public sealed class PortalAgendaTools(ConnectorDbContext dbContext, PortalMcpIdentityResolver identityResolver)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(
        Name = "list_agenda_events",
        Title = "Consultar agenda do portal",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PortalAgendaListResponse>))]
    [MoodleToolMetadata(
        Family = "portal-agenda",
        Classification = "R1",
        Kind = "read",
        CanonicalOperation = "portal.agenda.list",
        RequiredPlatformPermission = "agenda.manage",
        Evidence = "Consulta eventos da agenda persistidos no portal para o usuário autenticado.")]
    [Description("Lista os eventos da agenda do portal em um intervalo de datas. Não consulta eventos do Moodle.")]
    public async Task<CallToolResult> ListAsync(
        [Description("Início do intervalo em ISO 8601. Padrão: agora.")] DateTimeOffset? from = null,
        [Description("Fim do intervalo em ISO 8601. Padrão: 30 dias após o início.")] DateTimeOffset? to = null,
        [Description("Limite de eventos, entre 1 e 200.")] int limit = 100,
        [Description("Filtra por tag livre.")] string? tag = null,
        [Description("Filtra por Task relacionada.")] Guid? taskId = null,
        [Description("Tipo da referência estruturada.")] string? referenceType = null,
        [Description("Identificador da referência estruturada.")] string? referenceId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var identity = await identityResolver.ResolveAsync(cancellationToken);
            var start = from ?? DateTimeOffset.UtcNow;
            var end = to ?? start.AddDays(30);
            if (end <= start)
                throw new ArgumentException("O fim do intervalo deve ser posterior ao início.", nameof(to));

            limit = Math.Clamp(limit, 1, 200);
            var occurrences = await new ProfessionalPlannerService(dbContext).OccurrencesAsync(identity.Id, start, end, tag, taskId, cancellationToken, referenceType, referenceId);
            var events = occurrences.Take(limit).Select(item => new CalendarEventDto(item.Id, item.Title, item.Description, item.OccurrenceStartAt, item.OccurrenceEndAt, item.Type, item.CreatedAt, item.UpdatedAt,
                item.References.Select(reference => new PlannerReferenceDto(reference.ReferenceType, reference.ReferenceId, reference.ReferenceName, reference.ConnectionRef, null, null, null)).ToArray())
            {
                OccurrenceStartAt = item.OccurrenceStartAt,
                TimeZoneId = item.TimeZoneId, Location = item.Location, AvailabilityStatus = item.AvailabilityStatus,
                IsAllDay = item.IsAllDay, RRule = item.RRule, Tags = item.Tags, Version = item.Version
            }).ToArray();

            return Success(new PortalAgendaListResponse(events, events.Length, start, end),
                $"{events.Length} evento(s) retornado(s).");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ToolResultHelper.Error<PortalAgendaListResponse>(exception.Message, errorCode: "portal_agenda_invalid_request");
        }
    }

    [McpServerTool(
        Name = "create_agenda_event",
        Title = "Criar evento na agenda",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PortalAgendaWriteResponse>))]
    [MoodleToolMetadata(
        Family = "portal-agenda",
        Classification = "R2",
        Kind = "write",
        CanonicalOperation = "portal.agenda.create",
        RequiredPlatformPermission = "agenda.manage",
        Evidence = "Cria um evento privado na agenda do portal.")]
    [Description("Cria um evento na agenda do portal para o usuário autenticado. O evento não é criado no Moodle.")]
    public async Task<CallToolResult> CreateAsync(
        [Description("Título do evento.")] string title,
        [Description("Início do evento em ISO 8601.")] DateTimeOffset startAt,
        [Description("Fim opcional do evento em ISO 8601.")] DateTimeOffset? endAt = null,
        [Description("Descrição opcional.")] string? description = null,
        [Description("Tipo: meeting, alignment, delivery, training, webclass ou other.")] string? type = null,
        [Description("Vínculos com objetos Moodle: course, student, class ou school. Para turmas, informe parentReferenceId com o curso.")] IReadOnlyList<PlannerReferenceInput>? references = null,
        [Description("Fuso IANA, por padrão America/Sao_Paulo.")] string? timeZoneId = null,
        [Description("Local físico ou link, opcional.")] string? location = null,
        [Description("Disponibilidade: free, busy ou tentative.")] string? availabilityStatus = null,
        [Description("Indica evento de dia inteiro.")] bool isAllDay = false,
        [Description("Tags livres do evento.")] IReadOnlyList<string>? tags = null,
        [Description("RRULE e exceções da série, opcional.")] EventRecurrenceInput? recurrence = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var identity = await identityResolver.ResolveAsync(cancellationToken);
            ValidateRange(startAt, endAt);
            var refs = references?.Select(r => new TaskReferenceV2Input(r.ReferenceType, r.ReferenceId, r.ReferenceName, r.ConnectionRef)).ToArray();
            var result = await new ProfessionalPlannerService(dbContext).CreateEventAsync(identity.Id, identity.Id, new EventProfessionalInput(title, description, startAt, endAt, timeZoneId, location, availabilityStatus, isAllDay, tags, refs, recurrence, Type: type), cancellationToken);
            var links = result.References.Select(reference => new PlannerReferenceDto(reference.ReferenceType, reference.ReferenceId, reference.ReferenceName, reference.ConnectionRef, null, null, null)).ToArray();
            return Success(new PortalAgendaWriteResponse("created", new CalendarEventDto(result.Id, result.Title, result.Description, result.StartAt, result.EndAt, result.Type, result.CreatedAt, result.UpdatedAt, links)
            {
                TimeZoneId = result.TimeZoneId, Location = result.Location, AvailabilityStatus = result.AvailabilityStatus,
                IsAllDay = result.IsAllDay, Source = result.Source, ExternalUid = result.ExternalUid, RRule = result.RRule,
                Tags = result.Tags, Version = result.Version
            }), "Evento criado na agenda.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ToolResultHelper.Error<PortalAgendaWriteResponse>(exception.Message, errorCode: "portal_agenda_invalid_request");
        }
    }

    [McpServerTool(
        Name = "update_agenda_event",
        Title = "Editar evento da agenda",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PortalAgendaWriteResponse>))]
    [MoodleToolMetadata(
        Family = "portal-agenda",
        Classification = "R2",
        Kind = "write",
        CanonicalOperation = "portal.agenda.update",
        RequiredPlatformPermission = "agenda.manage",
        Evidence = "Atualiza um evento privado da agenda do portal.")]
    [Description("Edita um evento da agenda pertencente ao usuário autenticado. Campos omitidos são preservados.")]
    public async Task<CallToolResult> UpdateAsync(
        [Description("UUID do evento.")] Guid eventId,
        [Description("Novo título, quando necessário.")] string? title = null,
        [Description("Novo início em ISO 8601.")] DateTimeOffset? startAt = null,
        [Description("Novo fim em ISO 8601.")] DateTimeOffset? endAt = null,
        [Description("Nova descrição; envie texto vazio para limpar.")] string? description = null,
        [Description("Novo tipo do evento.")] string? type = null,
        [Description("Limpa o fim do evento quando true.")] bool clearEndAt = false,
        [Description("Substitui os vínculos quando informado: course, student, class ou school.")] IReadOnlyList<PlannerReferenceInput>? references = null,
        [Description("Fuso IANA, quando alterado.")] string? timeZoneId = null,
        [Description("Novo local, quando alterado.")] string? location = null,
        [Description("Nova disponibilidade.")] string? availabilityStatus = null,
        [Description("Altera o modo dia inteiro.")] bool? isAllDay = null,
        [Description("Substitui tags quando informado.")] IReadOnlyList<string>? tags = null,
        [Description("Substitui a recorrência quando informado.")] EventRecurrenceInput? recurrence = null,
        [Description("Versão esperada para concorrência otimista.")] long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (eventId == Guid.Empty)
                throw new ArgumentException("Informe um eventId UUID válido.", nameof(eventId));

            var identity = await identityResolver.ResolveAsync(cancellationToken);
            var refs = references?.Select(r => new TaskReferenceV2Input(r.ReferenceType, r.ReferenceId, r.ReferenceName, r.ConnectionRef)).ToArray();
            var result = await new ProfessionalPlannerService(dbContext).UpdateEventAsync(identity.Id, identity.Id, eventId,
                new EventProfessionalInput(title, description, startAt, endAt, timeZoneId, location, availabilityStatus, isAllDay, tags, refs, recurrence, expectedVersion, type, clearEndAt), cancellationToken);
            var links = result.References.Select(reference => new PlannerReferenceDto(reference.ReferenceType, reference.ReferenceId, reference.ReferenceName, reference.ConnectionRef, null, null, null)).ToArray();
            return Success(new PortalAgendaWriteResponse("updated", new CalendarEventDto(result.Id, result.Title, result.Description, result.StartAt, result.EndAt, result.Type, result.CreatedAt, result.UpdatedAt, links)
            {
                TimeZoneId = result.TimeZoneId, Location = result.Location, AvailabilityStatus = result.AvailabilityStatus,
                IsAllDay = result.IsAllDay, Source = result.Source, ExternalUid = result.ExternalUid, RRule = result.RRule,
                Tags = result.Tags, Version = result.Version
            }), "Evento atualizado.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ToolResultHelper.Error<PortalAgendaWriteResponse>(exception.Message, errorCode: "portal_agenda_invalid_request");
        }
    }

    [McpServerTool(
        Name = "remove_agenda_event",
        Title = "Remover evento da agenda",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PortalAgendaRemovalResponse>))]
    [MoodleToolMetadata(
        Family = "portal-agenda",
        Classification = "R3",
        Kind = "destructive-write",
        CanonicalOperation = "portal.agenda.remove",
        RequiredPlatformPermission = "agenda.manage",
        Evidence = "Remove um evento privado da agenda do portal.")]
    [Description("Remove um evento da agenda pertencente ao usuário autenticado. É uma operação destrutiva e exige confirmação do cliente MCP.")]
    public async Task<CallToolResult> RemoveAsync(
        [Description("UUID do evento.")] Guid eventId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (eventId == Guid.Empty)
                throw new ArgumentException("Informe um eventId UUID válido.", nameof(eventId));

            var identity = await identityResolver.ResolveAsync(cancellationToken);
            try
            {
                await new ProfessionalPlannerService(dbContext).DeleteEventAsync(identity.Id, eventId, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                return ToolResultHelper.Error<PortalAgendaRemovalResponse>("Evento não encontrado.", errorCode: "portal_agenda_event_not_found");
            }
            return Success(new PortalAgendaRemovalResponse(eventId, true), "Evento removido da agenda.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ToolResultHelper.Error<PortalAgendaRemovalResponse>(exception.Message, errorCode: "portal_agenda_invalid_request");
        }
    }

    private static void ValidateRange(DateTimeOffset startAt, DateTimeOffset? endAt)
    {
        if (endAt is not null && endAt <= startAt)
            throw new ArgumentException("O fim do evento deve ser posterior ao início.", nameof(endAt));
    }

    private static CalendarEventDto ToDto(CalendarEventEntity calendarEvent, IReadOnlyList<PlannerReferenceDto>? references = null) =>
        new(calendarEvent.Id, calendarEvent.Title, calendarEvent.Description, calendarEvent.StartAt, calendarEvent.EndAt, calendarEvent.Type, calendarEvent.CreatedAt, calendarEvent.UpdatedAt, references ?? []);

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

public sealed record PortalAgendaListResponse(
    [property: JsonPropertyName("events")] IReadOnlyList<CalendarEventDto> Events,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To);

public sealed record PortalAgendaWriteResponse(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("event")] CalendarEventDto Event);

public sealed record PortalAgendaRemovalResponse(
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("removed")] bool Removed);
