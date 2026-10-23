using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Configuration;
using Moodle.Application.Reports.Templates;
using Moodle.Application.MoodleApi;
using Moodle.Domain.Reports;
using Moodle.Infrastructure;
using Moodle.Presentation.Security;

namespace Moodle.Presentation.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateReportTemplateInput(string Name, string? Description, ReportDefinition Definition, long ExpectedRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReportPreviewInput(string ConnectionRef, IReadOnlyList<string> CourseIds,
    ReportDefinition? Definition = null, Guid? TemplateId = null, long? ExpectedRevision = null);

internal static class ReportTemplateEndpoints
{
    private const string IdentityKey = "report-template-identity";
    public static void Map(WebApplication app, string rateLimitPolicy)
    {
        var group = app.MapGroup("/api/reports").RequireRateLimiting(rateLimitPolicy);
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var db = context.RequestServices.GetRequiredService<ConnectorDbContext>();
            var identity = await PortalEndpointAuthorization.ResolveAppIdentityAsync(context, db, context.RequestAborted);
            if (identity is null) return Results.Unauthorized();
            if (!PortalEndpointAuthorization.HasAppPermission(context, AppPermissionCatalog.ReportsView))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            context.Items[IdentityKey] = identity;
            context.Response.Headers.CacheControl = "no-store";
            var execution = context.RequestServices.GetRequiredService<IConnectorExecutionContext>();
            var selection = context.RequestServices.GetRequiredService<IMoodleConnectionSelection>();
            try
            {
                if (context.Request.Method is "POST" or "PUT" or "DELETE")
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                execution.Enter(identity.ConnectorClientId ?? identity.Id.ToString(), identity.Id.ToString(), identity.Email);
                return await next(invocation);
            }
            catch (ReportDatasetException e) { return Error(context, e.Code); }
            catch (MoodleApiException) { return Error(context, "report_source_unavailable"); }
            finally { selection.Alias = null; execution.Clear(); }
        });
        var templates = group.MapGroup("/templates");
        templates.AddEndpointFilter((invocation, next) => Enabled(invocation.HttpContext)
            ? next(invocation) : ValueTask.FromResult<object?>(Results.NotFound()));
        templates.MapGet("", async (HttpContext context, ReportTemplateService service, int page = 1,
            int pageSize = 20, CancellationToken ct = default) => Results.Ok(await service.ListAsync(Owner(context), page, pageSize, ct)));
        templates.MapGet("/{id:guid}", async (Guid id, HttpContext context, ReportTemplateService service, CancellationToken ct) =>
        {
            var model = await service.GetAsync(Owner(context), id, ct);
            return model is null ? Results.NotFound() : Results.Ok(model);
        });
        templates.MapPost("", async (ReportTemplateInput input, HttpContext context, ReportTemplateService service, CancellationToken ct) =>
            Mutation(await service.CreateAsync(Owner(context), input, ct), create: true));
        templates.MapPut("/{id:guid}", async (Guid id, UpdateReportTemplateInput input, HttpContext context,
            ReportTemplateService service, CancellationToken ct) => Mutation(await service.UpdateAsync(Owner(context), id,
                input.ExpectedRevision, new(input.Name, input.Description, input.Definition), ct)));
        templates.MapDelete("/{id:guid}", async (Guid id, HttpContext context, ReportTemplateService service,
            long? expectedRevision = null, CancellationToken ct = default) => expectedRevision is not > 0
                ? Error(context, "invalid_revision") : Mutation(await service.DeleteAsync(Owner(context), id, expectedRevision.Value, ct), delete: true));
        group.MapGet("/catalog", (HttpContext context, ReportCatalogService catalog, IOptions<ReportTemplateOptions> options) =>
            Enabled(context) ? Results.Ok(new { schemaVersion = ReportCatalogService.SchemaVersion,
                sourceId = ReportCatalogService.SourceId, fields = catalog.Fields, limits = options.Value }) : Results.NotFound());
        group.MapPost("/preview", async (ReportPreviewInput input, HttpContext context, ReportPreviewService preview,
            ReportTemplateService templatesService, ConnectorDbContext db, CancellationToken ct) =>
        {
            var owner = Owner(context);
            var result = await preview.PreviewAsync(owner, async deadline =>
            {
                ReportDatasetResolver.NormalizeCourses(input.CourseIds);
                var origins = (input.Definition is null ? 0 : 1) + (input.TemplateId is null ? 0 : 1);
                if (origins != 1 || input.TemplateId is not null && input.ExpectedRevision is not > 0 ||
                    input.TemplateId is null && input.ExpectedRevision is not null) throw new ReportDatasetException("invalid_report_origin");
                ReportDefinition definition;
                if (input.TemplateId is Guid templateId)
                {
                    if (!Enabled(context)) throw new ReportDatasetException("report_templates_disabled");
                    var template = await templatesService.GetAsync(owner, templateId, deadline) ?? throw new ReportDatasetException("report_template_not_found");
                    if (template.Revision != input.ExpectedRevision) throw new ReportDatasetException("report_template_revision_conflict");
                    definition = template.Definition;
                }
                else
                {
                    if (!Enabled(context)) throw new ReportDatasetException("report_templates_disabled");
                    definition = input.Definition!;
                }
                var identity = Identity(context);
                if (string.IsNullOrWhiteSpace(input.ConnectionRef)) throw new ReportDatasetException("connection_required");
                var connectionId = await MoodleConnectionIdentity.ResolveAsync(db, owner, identity.ConnectorClientId ?? "",
                    input.ConnectionRef, deadline);
                context.RequestServices.GetRequiredService<IMoodleConnectionSelection>().Alias = input.ConnectionRef;
                return new ReportDatasetRequest(owner, input.ConnectionRef, connectionId, input.CourseIds, definition, true);
            }, ct);
            return Results.Ok(new { columns = result.Table.Columns, rows = result.Table.Rows,
                result.SampledCourseIds, sampledCourses = result.Table.Groups.Select(group => new
                    { courseId = group.CourseId, name = group.CourseName }),
                result.RequestedCourseCount, returnedRows = result.Table.Rows.Count,
                result.Truncated, result.Partial, coverage = result.Table.Coverage, warnings = result.Table.Warnings,
                generatedAt = result.Table.GeneratedAt, referenceAt = result.Table.CollectionStartedAt,
                collectionEndedAt = result.Table.CollectionEndedAt });
        });
        group.MapGet("/jobs/{id:guid}", async (Guid id, HttpContext context, ConnectorDbContext db, CancellationToken ct) =>
        {
            var owner = Owner(context);
            var job = await db.ReportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id && j.OwnerId == owner, ct);
            return job is null ? Results.NotFound() : Results.Ok(new { job.Id, job.ReportType, job.ScopeType,
                connectionRef = job.ConnectionAlias, job.Status, job.ProgressPercent, job.TotalCourses, job.ProcessedCourses,
                job.CourseId, courseIds = Parse(job.CourseIdsJson), job.CategoryPath,
                job.TemplateId, job.TemplateNameSnapshot, job.TemplateRevisionSnapshot,
                definitionSnapshot = Parse(job.DefinitionSnapshotJson), selectionSnapshot = Parse(job.SelectionSnapshotJson),
                resultMetadata = Parse(job.ResultMetadataJson), job.RequestedAt, job.StartedAt, job.CompletedAt,
                job.UpdatedAt, job.FileName, job.ContentType, job.FileSizeBytes, job.ErrorMessage,
                downloadUrl = job.Status == "completed" ? $"/api/reports/jobs/{id}/download" : null });
        });
    }

    internal static async Task<IResult> SubmitAsync(CreateReportJobInput input, HttpContext context,
        AppIdentity identity, CancellationToken ct)
    {
        if (!Enabled(context)) return Results.NotFound();
        if (input.TemplateId is null || input.ExpectedRevision is not > 0 || input.RefreshFromJobId is not null)
            return Error(context, "invalid_report_origin");
        if (input.ScopeType != "courses" || input.CategoryPath is not null || input.CourseId is not null)
            return Error(context, "invalid_report_scope");
        var execution = context.RequestServices.GetRequiredService<IConnectorExecutionContext>();
        try
        {
            execution.Enter(identity.ConnectorClientId ?? identity.Id.ToString(), identity.Id.ToString(), identity.Email);
            var id = await context.RequestServices.GetRequiredService<ReportGenerationService>().GenerateAsync(new(identity.Id,
                input.ConnectionRef ?? "", input.CourseIds ?? [], input.TemplateId.Value, input.ExpectedRevision.Value), ct);
            return Results.Accepted($"/api/reports/jobs/{id}", new { id, status = "queued" });
        }
        catch (ReportDatasetException e) { return Error(context, e.Code); }
        catch (MoodleApiException) { return Error(context, "report_source_unavailable"); }
        finally { context.RequestServices.GetRequiredService<IMoodleConnectionSelection>().Alias = null; execution.Clear(); }
    }

    internal static async Task<IResult> RefreshAsync(Guid sourceJobId, HttpContext context,
        AppIdentity identity, CancellationToken ct)
    {
        var jobs = context.RequestServices.GetRequiredService<IReportJobSubmissionStore>();
        var source = await jobs.GetSourceAsync(identity.Id, sourceJobId, ct);
        if (source is null) return Error(context, "report_job_not_found");
        if (source.ReportType is not ("grades" or "template")) return Error(context, "invalid_report_origin");
        if (source.ReportType == "template" && !Enabled(context)) return Results.NotFound();
        if (source.ReportType == "template" && source.Definition is null) return Error(context, "invalid_report_origin");
        if (source.Status is not ("completed" or "failed")) return Error(context, "report_job_not_terminal");

        var execution = context.RequestServices.GetRequiredService<IConnectorExecutionContext>();
        try
        {
            execution.Enter(identity.ConnectorClientId ?? identity.Id.ToString(), identity.Id.ToString(), identity.Email);
            var id = await context.RequestServices.GetRequiredService<ReportGenerationService>()
                .RefreshAsync(identity.Id, sourceJobId, ct);
            return Results.Accepted($"/api/reports/jobs/{id}", new { id, status = "queued" });
        }
        catch (ReportDatasetException e) { return Error(context, e.Code); }
        catch (MoodleApiException) { return Error(context, "report_source_unavailable"); }
        finally { context.RequestServices.GetRequiredService<IMoodleConnectionSelection>().Alias = null; execution.Clear(); }
    }
    private static AppIdentity Identity(HttpContext context) => (AppIdentity)context.Items[IdentityKey]!;
    private static Guid Owner(HttpContext context) => Identity(context).Id;
    private static bool Enabled(HttpContext context) => context.RequestServices.GetRequiredService<IOptions<ReportTemplateOptions>>().Value.TemplatesEnabled;
    private static JsonElement? Parse(string? json) => json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
    private static IResult Mutation(ReportTemplateMutation result, bool create = false, bool delete = false) => result.Status switch
    {
        ReportTemplateMutationStatus.Success when delete => Results.NoContent(),
        ReportTemplateMutationStatus.Success when create => Results.Created($"/api/reports/templates/{result.Template!.Id}", result.Template),
        ReportTemplateMutationStatus.Success => Results.Ok(result.Template),
        ReportTemplateMutationStatus.NotFound => Results.NotFound(),
        ReportTemplateMutationStatus.RevisionConflict => Results.Conflict(new { error = new { code = "report_template_revision_conflict" } }),
        ReportTemplateMutationStatus.LimitReached => Results.Conflict(new { error = new { code = "report_template_limit" } }),
        _ => Results.BadRequest(new { error = new { code = "invalid_template", details = result.Errors } })
    };
    internal static IResult Error(HttpContext context, string code)
    {
        var status = code switch
        {
            "report_template_not_found" or "report_job_not_found" or "report_templates_disabled" => 404,
            "report_permission_denied" => 403,
            "report_template_revision_conflict" or "report_storage_limit_exceeded" or "report_job_not_terminal" or "connection_changed" or "report_selection_unavailable" => 409,
            "preview_timeout" => 504,
            "preview_busy" => 429,
            "report_source_unavailable" or "population_unavailable" => 502,
            _ => 400
        };
        if (status == 429)
        {
            context.Response.Headers.RetryAfter = "1";
            if (string.Equals(code, "preview_busy", StringComparison.Ordinal))
            {
                AdmissionFailureTelemetry.MarkPreviewBusy(context);
            }
        }
        return Results.Json(new { error = new { code, message = "Não foi possível concluir a operação de relatório." } }, statusCode: status);
    }
}
