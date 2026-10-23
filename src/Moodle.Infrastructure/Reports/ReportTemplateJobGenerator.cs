using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Configuration;
using Moodle.Application.Reports.Templates;
using Moodle.Domain.Reports;

namespace Moodle.Infrastructure.Reports;

internal sealed class ReportTemplateJobGenerator(ReportDatasetResolver resolver, IReportTableRenderer renderer,
    IPlatformPermissionService permissions, IOptions<ReportTemplateOptions> options)
{
    public async Task<RenderedReportArtifact> GenerateAsync(ReportJobEntity job, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.GenerationTimeoutSeconds));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var generationToken = linkedCancellation.Token;
        try
        {
            if (!(await permissions.GetEffectivePermissionsAsync(job.OwnerId, generationToken))
                    .Contains("reports.view", StringComparer.Ordinal))
                throw new ReportDatasetException("report_permission_denied");

            ReportSelectionSnapshot? selection;
            ReportDefinition? definition;
            try
            {
                selection = JsonSerializer.Deserialize<ReportSelectionSnapshot>(job.SelectionSnapshotJson ?? "null",
                    ReportDefinitionValidator.JsonOptions);
                definition = JsonSerializer.Deserialize<ReportDefinition>(job.DefinitionSnapshotJson ?? "null",
                    ReportDefinitionValidator.JsonOptions);
            }
            catch (JsonException) { throw new ReportDatasetException("invalid_report_snapshot"); }

            if (selection?.SchemaVersion != 1 || definition is null ||
                !selection.CourseIds.SequenceEqual(ReportDatasetResolver.NormalizeCourses(
                    JsonSerializer.Deserialize<string[]>(job.CourseIdsJson ?? "[]")), StringComparer.Ordinal) ||
                !string.Equals(selection.ConnectionRef, job.ConnectionAlias, StringComparison.OrdinalIgnoreCase))
                throw new ReportDatasetException("invalid_report_snapshot");

            var result = await resolver.ResolveAsync(new(job.OwnerId, job.ConnectionAlias, selection.ConnectionId,
                selection.CourseIds, definition), generationToken);
            ReportMetrics.Selection(result.RequestedCourseCount);
            ReportMetrics.Dataset(result.Table);
            job.TotalCourses = selection.CourseIds.Count;
            job.ProcessedCourses = job.TotalCourses;
            job.CourseNamesJson = JsonSerializer.Serialize(result.Table.Groups.Select(g => new
                { Name = g.CourseName ?? g.CourseId, CategoryName = g.CategoryPath }));
            job.ResultMetadataJson = JsonSerializer.Serialize(new { result.Table.Coverage, result.Table.Warnings,
                result.Table.CollectionStartedAt, result.Table.CollectionEndedAt, result.Table.GeneratedAt,
                result.Partial, result.Truncated }, ReportDefinitionValidator.JsonOptions);
            generationToken.ThrowIfCancellationRequested();

            using var metric = ReportMetrics.Begin(ReportMetrics.Stage.Render);
            try
            {
                var artifact = renderer.Render(result.Table, generationToken);
                generationToken.ThrowIfCancellationRequested();
                ReportMetrics.Artifact(artifact.Content.Length);
                metric.Outcome = ReportMetrics.Result.Success;
                return artifact;
            }
            catch (OperationCanceledException) { metric.Outcome = ReportMetrics.Result.Cancelled; throw; }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ReportDatasetException("report_deadline_exceeded");
        }
    }
}
