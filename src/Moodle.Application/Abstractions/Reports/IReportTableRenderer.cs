using Moodle.Domain.Reports;

namespace Moodle.Application.Abstractions.Reports;

public sealed record RenderedReportArtifact(string FileName, string ContentType, byte[] Content);
public interface IReportTableRenderer
{
    RenderedReportArtifact Render(ReportTable table, CancellationToken cancellationToken);
}
