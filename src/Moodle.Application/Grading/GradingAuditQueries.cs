using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Moodle.Application.Abstractions;

namespace Moodle.Application.Grading;

public sealed record GetGradingAuditQuery(
    string AuditId,
    int Page,
    int PageSize) : IRequest<GradingAuditResult>;

public sealed record GetGradingBatchAuditQuery(
    Guid BatchJobId,
    int Page,
    int PageSize) : IRequest<GradingAuditResult>;

public sealed record GradingAuditResult(
    [property: JsonPropertyName("auditId")] string? AuditId,
    [property: JsonPropertyName("batchJobId")] Guid? BatchJobId,
    [property: JsonPropertyName("totalEvents")] int TotalEvents,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("events")] IReadOnlyList<GradingAuditEvent> Events);

public sealed record GradingAuditEvent(
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("moodleFunction")] string? MoodleFunction,
    [property: JsonPropertyName("actorSubject")] string ActorSubject,
    [property: JsonPropertyName("actorMoodleUserId")] long? ActorMoodleUserId,
    [property: JsonPropertyName("courseId")] long? CourseId,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("request")] JsonElement Request,
    [property: JsonPropertyName("response")] JsonElement Response);

public sealed class GetGradingAuditQueryHandler(
    IMoodleAuditLogRepository auditLogs)
    : IRequestHandler<GetGradingAuditQuery, GradingAuditResult>
{
    public async Task<GradingAuditResult> Handle(
        GetGradingAuditQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AuditId))
        {
            throw new ArgumentException("O auditId e obrigatorio.", "auditId");
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var auditId = request.AuditId.Trim();
        var total = await auditLogs.CountByCorrelationIdAsync(auditId, cancellationToken);
        var logs = await auditLogs.ListByCorrelationIdAsync(
            auditId,
            page,
            pageSize,
            cancellationToken);
        var events = logs.Select(log => new GradingAuditEvent(
            log.CreatedAt,
            log.ToolName,
            log.Status,
            log.MoodleFunction,
            log.ActorSubject,
            log.ActorMoodleUserId,
            log.CourseId,
            log.ErrorCode,
            log.ErrorMessage,
            ParseJson(log.RequestSanitizedJson),
            ParseJson(log.ResponseSummaryJson))).ToArray();

        return new GradingAuditResult(
            auditId,
            BatchJobId: null,
            total,
            page,
            pageSize,
            HasMore: page * pageSize < total,
            events);
    }

    private static JsonElement ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EmptyObject();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

public sealed class GetGradingBatchAuditQueryHandler(
    IMoodleAuditLogRepository auditLogs,
    IGradingReviewRepository gradingRepository,
    ICurrentUserContext currentUser)
    : IRequestHandler<GetGradingBatchAuditQuery, GradingAuditResult>
{
    public async Task<GradingAuditResult> Handle(
        GetGradingBatchAuditQuery request,
        CancellationToken cancellationToken)
    {
        if (request.BatchJobId == Guid.Empty)
        {
            throw new ArgumentException("O batchJobId e obrigatorio.", "batchJobId");
        }

        var batch = await gradingRepository.GetBatchAsync(request.BatchJobId, cancellationToken)
            ?? throw new InvalidOperationException("Lote de correcao nao encontrado.");
        GradingAccessControl.EnsureCanAccessBatch(batch, currentUser);

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var total = await auditLogs.CountByBatchJobIdAsync(request.BatchJobId, cancellationToken);
        var logs = await auditLogs.ListByBatchJobIdAsync(
            request.BatchJobId,
            page,
            pageSize,
            cancellationToken);
        var events = logs.Select(log => new GradingAuditEvent(
            log.CreatedAt,
            log.ToolName,
            log.Status,
            log.MoodleFunction,
            log.ActorSubject,
            log.ActorMoodleUserId,
            log.CourseId,
            log.ErrorCode,
            log.ErrorMessage,
            ParseJson(log.RequestSanitizedJson),
            ParseJson(log.ResponseSummaryJson))).ToArray();

        return new GradingAuditResult(
            AuditId: null,
            BatchJobId: request.BatchJobId,
            total,
            page,
            pageSize,
            HasMore: page * pageSize < total,
            events);
    }

    private static JsonElement ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EmptyObject();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
