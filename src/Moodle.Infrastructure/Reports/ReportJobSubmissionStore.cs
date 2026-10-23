using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Reports.Templates;
using Moodle.Domain.Reports;

namespace Moodle.Infrastructure.Reports;

public sealed class ReportJobSubmissionStore(ConnectorDbContext db, ReportDefinitionValidator validator) : IReportJobSubmissionStore
{
    public async Task<ReportJobSource?> GetSourceAsync(Guid ownerId, Guid id, CancellationToken ct)
    {
        var job = await db.ReportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.OwnerId == ownerId && j.Id == id, ct);
        return job is null ? null : Source(job);
    }

    public async Task<Guid> SubmitAsync(ReportSubmission input, CancellationToken ct)
    {
        if (input.Scope.OwnerId != input.OwnerId || input.ExpectedRevision <= 0)
            throw new ReportDatasetException("invalid_report_origin");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var owners = await db.UserAccounts.FromSqlInterpolated($"SELECT * FROM user_accounts WHERE \"Id\" = {input.OwnerId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        if (owners.Count != 1 || owners[0].ConnectorClientId != input.Scope.ClientId)
            throw new ReportDatasetException("connection_changed");
        var connections = await db.ConnectorClients.FromSqlInterpolated($"SELECT * FROM connector_clients WHERE \"Id\" = {input.Scope.ConnectionId} FOR SHARE")
            .AsNoTracking().ToListAsync(ct);
        if (connections.Count != 1 || !connections[0].IsActive || connections[0].ClientId != input.Scope.ClientId ||
            !string.Equals(connections[0].MoodleAlias, input.Scope.ConnectionRef, StringComparison.OrdinalIgnoreCase))
            throw new ReportDatasetException("connection_changed");
        if (await ReportStorageCalculator.GetUsedBytesAsync(db, input.OwnerId, ct) >= ReportStorageCalculator.LimitBytes)
            throw new ReportDatasetException("report_storage_limit_exceeded");
        var templates = await db.ReportTemplates.FromSqlInterpolated($"SELECT * FROM report_templates WHERE \"Id\" = {input.TemplateId} AND \"OwnerId\" = {input.OwnerId} FOR SHARE")
            .AsNoTracking().ToListAsync(ct);
        var template = templates.SingleOrDefault() ?? throw new ReportDatasetException("report_template_not_found");
        if (template.Revision != input.ExpectedRevision) throw new ReportDatasetException("report_template_revision_conflict");
        if (!validator.Parse(template.DefinitionJson).IsValid) throw new ReportDatasetException("invalid_definition");
        var normalizedIds = ReportDatasetResolver.NormalizeCourses(input.Scope.CourseIds);
        var job = NewJob(input.OwnerId, input.Scope, normalizedIds, template.Id, template.Name,
            template.Revision, template.DefinitionJson, legacy: false);
        db.ReportJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        ReportMetrics.Queued();
        db.Entry(job).State = EntityState.Detached;
        return job.Id;
    }

    public async Task<Guid> RefreshAsync(Guid ownerId, Guid sourceJobId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var owners = await db.UserAccounts.FromSqlInterpolated($"SELECT * FROM user_accounts WHERE \"Id\" = {ownerId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var owner = owners.SingleOrDefault() ?? throw new ReportDatasetException("report_job_not_found");
        var jobs = await db.ReportJobs.FromSqlInterpolated($"SELECT * FROM report_jobs WHERE \"Id\" = {sourceJobId} AND \"OwnerId\" = {ownerId} FOR SHARE")
            .AsNoTracking().ToListAsync(ct);
        var source = jobs.SingleOrDefault() ?? throw new ReportDatasetException("report_job_not_found");
        if (source.Status is not ("completed" or "failed")) throw new ReportDatasetException("report_job_not_terminal");

        var legacy = source.DefinitionSnapshotJson is null && source.ReportType == "grades";
        if (!legacy && (source.ReportType != "template" || !validator.Parse(source.DefinitionSnapshotJson).IsValid))
            throw new ReportDatasetException("invalid_report_origin");
        if (string.IsNullOrWhiteSpace(owner.ConnectorClientId) || owner.ConnectorClientId != source.ClientId)
            throw new ReportDatasetException("connection_changed");

        var savedSelection = source.SelectionSnapshotJson is null
            ? null
            : JsonSerializer.Deserialize<ReportSelectionSnapshot>(source.SelectionSnapshotJson, ReportDefinitionValidator.JsonOptions);
        if (savedSelection is not null &&
            (savedSelection.SchemaVersion != 1 ||
             !string.Equals(savedSelection.ConnectionRef, source.ConnectionAlias, StringComparison.OrdinalIgnoreCase)))
            throw new ReportDatasetException("report_selection_unavailable");
        var courseIds = savedSelection?.CourseIds ??
            (source.CourseIdsJson is null ? [] : JsonSerializer.Deserialize<string[]>(source.CourseIdsJson) ?? []);
        if (courseIds.Count == 0 && source.ScopeType == "course" && !string.IsNullOrWhiteSpace(source.CourseId))
            courseIds = [source.CourseId];
        var normalizedIds = ReportDatasetResolver.NormalizeCourses(courseIds);
        if (normalizedIds.Length == 0 || source.ScopeType == "category" && source.CourseIdsJson is null)
            throw new ReportDatasetException("report_selection_unavailable");

        var savedConnectionId = savedSelection?.ConnectionId;
        List<ConnectorClientCredentialEntity> connections;
        if (!string.IsNullOrWhiteSpace(savedConnectionId))
        {
            connections = await db.ConnectorClients.FromSqlInterpolated($"SELECT * FROM connector_clients WHERE \"Id\" = {savedConnectionId} FOR SHARE")
                .AsNoTracking().ToListAsync(ct);
        }
        else
        {
            var alias = source.ConnectionAlias.Trim().ToLowerInvariant();
            connections = await db.ConnectorClients.FromSqlInterpolated($"SELECT * FROM connector_clients WHERE \"ClientId\" = {source.ClientId} AND lower(\"MoodleAlias\") = {alias} FOR SHARE")
                .AsNoTracking().ToListAsync(ct);
        }
        var connection = connections.SingleOrDefault();
        if (connection is null || !connection.IsActive || connection.ClientId != source.ClientId ||
            !string.Equals(connection.MoodleAlias, source.ConnectionAlias, StringComparison.OrdinalIgnoreCase))
            throw new ReportDatasetException("connection_changed");
        if (await ReportStorageCalculator.GetUsedBytesAsync(db, ownerId, ct) >= ReportStorageCalculator.LimitBytes)
            throw new ReportDatasetException("report_storage_limit_exceeded");

        var job = NewJob(ownerId, new(ownerId, source.ClientId, connection.MoodleAlias,
            connection.Id, normalizedIds), normalizedIds, source.TemplateId, source.TemplateNameSnapshot,
            source.TemplateRevisionSnapshot, source.DefinitionSnapshotJson, legacy, source.CourseNamesJson);
        db.ReportJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        ReportMetrics.Queued();
        db.Entry(job).State = EntityState.Detached;
        return job.Id;
    }

    private static ReportJobEntity NewJob(Guid ownerId, ReportExecutionScope scope, string[] courseIds,
        Guid? templateId, string? templateName, long? templateRevision, string? definitionJson, bool legacy,
        string? courseNamesJson = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ReportJobEntity { Id = Guid.NewGuid(), OwnerId = ownerId, ClientId = scope.ClientId,
            ConnectionAlias = scope.ConnectionRef, ReportType = legacy ? "grades" : "template", ScopeType = "courses",
            CourseIdsJson = JsonSerializer.Serialize(courseIds), TotalCourses = courseIds.Length, TemplateId = templateId,
            CourseNamesJson = courseNamesJson, TemplateNameSnapshot = templateName, TemplateRevisionSnapshot = templateRevision,
            DefinitionSnapshotJson = definitionJson,
            SelectionSnapshotJson = JsonSerializer.Serialize(new ReportSelectionSnapshot(1, scope.ConnectionId,
                scope.ConnectionRef, courseIds), ReportDefinitionValidator.JsonOptions),
            RequestedAt = now, UpdatedAt = now };
    }

    private static ReportJobSource Source(ReportJobEntity job) => new(job.Id, job.TemplateId,
        job.TemplateNameSnapshot, job.TemplateRevisionSnapshot,
        job.DefinitionSnapshotJson is null ? null : JsonSerializer.Deserialize<ReportDefinition>(job.DefinitionSnapshotJson, ReportDefinitionValidator.JsonOptions),
        job.SelectionSnapshotJson is null ? null : JsonSerializer.Deserialize<ReportSelectionSnapshot>(job.SelectionSnapshotJson, ReportDefinitionValidator.JsonOptions),
        job.Status, job.ReportType);
}
