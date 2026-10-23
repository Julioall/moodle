namespace Moodle.Application.Abstractions.Reports;

public interface IReportExecutionScopeResolver
{
    Task<ReportExecutionScope> ResolveAsync(Guid ownerId, string connectionRef,
        IReadOnlyCollection<string> courseIds, CancellationToken ct);
}
