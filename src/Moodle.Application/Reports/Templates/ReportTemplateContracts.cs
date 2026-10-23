using Moodle.Domain.Reports;

namespace Moodle.Application.Reports.Templates;

public sealed record ReportTemplate(Guid Id, Guid OwnerId, string Name, string? Description,
    ReportDefinition Definition, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record ReportTemplateInput(string Name, string? Description, ReportDefinition Definition);
public sealed record ReportTemplatePage(IReadOnlyList<ReportTemplate> Items, int Page, int PageSize, int Total);
public enum ReportTemplateMutationStatus { Success, NotFound, RevisionConflict, LimitReached, Invalid }
public sealed record ReportTemplateMutation(ReportTemplateMutationStatus Status,
    ReportTemplate? Template = null, IReadOnlyList<ReportValidationError>? Errors = null);
