using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Configuration;

namespace Moodle.Application.Reports.Templates;

public sealed record GenerateTemplateReport(Guid OwnerId, string ConnectionRef, IReadOnlyCollection<string> CourseIds,
    Guid TemplateId, long ExpectedRevision);
public sealed class ReportGenerationService(IReportExecutionScopeResolver scopeResolver,
    IReportJobSubmissionStore store, IOptions<ReportTemplateOptions> options)
{
    public async Task<Guid> GenerateAsync(GenerateTemplateReport command, CancellationToken ct)
    {
        if (command.ExpectedRevision <= 0)
            throw new ReportDatasetException("invalid_report_origin");
        var ids = ReportDatasetResolver.NormalizeCourses(command.CourseIds, options.Value.MaxCourses);
        ReportExecutionScope scope;
        using (var metric = ReportMetrics.Begin(ReportMetrics.Stage.Selection))
        {
            try
            {
                scope = await scopeResolver.ResolveAsync(command.OwnerId, command.ConnectionRef, ids, ct);
                ReportMetrics.Selection(scope.CourseIds.Count);
                metric.Outcome = ReportMetrics.Result.Success;
            }
            catch (OperationCanceledException) { metric.Outcome = ReportMetrics.Result.Cancelled; throw; }
        }
        return await store.SubmitAsync(new(command.OwnerId, command.TemplateId, command.ExpectedRevision, scope), ct);
    }

    public Task<Guid> RefreshAsync(Guid ownerId, Guid sourceJobId, CancellationToken ct) =>
        store.RefreshAsync(ownerId, sourceJobId, ct);
}
