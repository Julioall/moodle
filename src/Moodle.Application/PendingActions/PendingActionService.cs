using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.Auditing;
using Moodle.Application.Tools;
using Moodle.Domain;

namespace Moodle.Application.PendingActions;

public sealed class PendingActionService(
    IPendingMoodleActionRepository pendingActions,
    IMoodleAuditLogRepository auditLogs,
    ICurrentUserContext currentUser,
    IMoodleUserResolver moodleUserResolver) : IPendingActionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PendingActionResponse> CreatePendingActionAsync(
        string toolName,
        ToolRiskLevel riskLevel,
        object payload,
        object preview,
        string confirmationText,
        TimeSpan expiresIn,
        long? courseId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new ArgumentException("O nome da tool e obrigatorio.", nameof(toolName));
        }

        if (string.IsNullOrWhiteSpace(currentUser.Subject))
        {
            throw new InvalidOperationException("Usuario autenticado nao identificado.");
        }

        var now = DateTimeOffset.UtcNow;
        var previewJson = AuditPayloadSanitizer.SerializeSanitized(preview);
        var previewSanitized = AuditPayloadSanitizer.ToSanitizedElement(preview);
        var correlationId = Guid.NewGuid().ToString("N");
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);

        var action = new PendingMoodleAction
        {
            ToolName = toolName,
            RiskLevel = riskLevel,
            CreatedBySubject = currentUser.Subject,
            CreatedByEmail = currentUser.Email,
            CreatedByMoodleUserId = moodleUserId,
            CourseId = courseId,
            PayloadJson = AuditPayloadSanitizer.SerializeSanitized(payload),
            PreviewJson = previewJson,
            ConfirmationText = confirmationText,
            ExpiresAt = now.Add(expiresIn),
            IdempotencyKey = IdempotencyKey.New().ToString(),
            CorrelationId = correlationId
        };

        await pendingActions.AddAsync(action, cancellationToken);
        await auditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = correlationId,
            ToolName = toolName,
            RiskLevel = riskLevel,
            ActorSubject = currentUser.Subject,
            ActorEmail = currentUser.Email,
            ActorMoodleUserId = moodleUserId,
            CourseId = courseId,
            RequestSanitizedJson = previewJson,
            ResponseSummaryJson = JsonSerializer.Serialize(new { action.Id, action.ExpiresAt }, JsonOptions),
            Status = "pending_created"
        }, cancellationToken);

        await pendingActions.SaveChangesAsync(cancellationToken);

        return new PendingActionResponse(
            "pending_confirmation",
            action.Id,
            action.ToolName,
            action.RiskLevel,
            previewSanitized,
            action.ConfirmationText,
            action.ExpiresAt);
    }
}
