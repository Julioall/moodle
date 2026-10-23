namespace Moodle.Application.Abstractions;

public sealed record AuthorizationFailureAuditRequest(
    string Area,
    string Reason,
    string Message,
    string? ActorSubject,
    string? ActorEmail,
    string? Path,
    string? AuthenticationType,
    string? CorrelationId = null);

public interface IAuthorizationAuditService
{
    Task RecordFailureAsync(
        AuthorizationFailureAuditRequest request,
        CancellationToken cancellationToken);
}
