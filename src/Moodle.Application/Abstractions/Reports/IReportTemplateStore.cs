using Moodle.Application.Reports.Templates;

namespace Moodle.Application.Abstractions.Reports;

public interface IReportTemplateStore
{
    Task EnsureDefaultsAsync(Guid ownerId, IReadOnlyList<ReportTemplateInput> defaults, int limit, CancellationToken cancellationToken);
    Task<ReportTemplatePage> ListAsync(Guid ownerId, int page, int pageSize, CancellationToken cancellationToken);
    Task<ReportTemplate?> GetAsync(Guid ownerId, Guid id, CancellationToken cancellationToken);
    Task<ReportTemplateMutation> CreateAsync(Guid ownerId, ReportTemplateInput input, int limit, CancellationToken cancellationToken);
    Task<ReportTemplateMutation> UpdateAsync(Guid ownerId, Guid id, long expectedRevision, ReportTemplateInput input, CancellationToken cancellationToken);
    Task<ReportTemplateMutation> DeleteAsync(Guid ownerId, Guid id, long expectedRevision, CancellationToken cancellationToken);
}
