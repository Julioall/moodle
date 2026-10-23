using Moodle.Domain.Reports;

namespace Moodle.Application.Abstractions.Reports;

public sealed record ReportExecutionScope(Guid OwnerId, string ClientId, string ConnectionRef,
    string ConnectionId, IReadOnlyList<string> CourseIds);
public sealed record ReportSelectionSnapshot(int SchemaVersion, string ConnectionId, string ConnectionRef,
    IReadOnlyList<string> CourseIds);
public sealed record ReportJobSource(Guid Id, Guid? TemplateId, string? TemplateName, long? TemplateRevision,
    ReportDefinition? Definition, ReportSelectionSnapshot? Selection, string Status, string ReportType);
public sealed record ReportSubmission(Guid OwnerId, Guid TemplateId, long ExpectedRevision, ReportExecutionScope Scope);
public interface IReportJobSubmissionStore
{
    Task<ReportJobSource?> GetSourceAsync(Guid ownerId, Guid id, CancellationToken ct);
    Task<Guid> SubmitAsync(ReportSubmission submission, CancellationToken ct);
    Task<Guid> RefreshAsync(Guid ownerId, Guid sourceJobId, CancellationToken ct);
}
