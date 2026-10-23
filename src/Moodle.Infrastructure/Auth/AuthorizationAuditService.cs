using Moodle.Application.Abstractions;
using Moodle.Application.Auditing;
using Moodle.Domain;

namespace Moodle.Infrastructure;

public sealed class AuthorizationAuditService(ConnectorDbContext dbContext) : IAuthorizationAuditService
{
    public async Task RecordFailureAsync(
        AuthorizationFailureAuditRequest request,
        CancellationToken cancellationToken)
    {
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? Guid.NewGuid().ToString("N")
            : request.CorrelationId;

        await dbContext.MoodleAuditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = correlationId,
            ToolName = request.Area,
            RiskLevel = ToolRiskLevel.SensitiveRead,
            ActorSubject = string.IsNullOrWhiteSpace(request.ActorSubject) ? "anonymous" : request.ActorSubject,
            ActorEmail = request.ActorEmail,
            RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                request.Path,
                request.AuthenticationType
            }),
            ResponseSummaryJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                request.Reason,
                request.Message
            }),
            Status = "authorization_failed",
            ErrorCode = request.Reason,
            ErrorMessage = request.Message
        }, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
