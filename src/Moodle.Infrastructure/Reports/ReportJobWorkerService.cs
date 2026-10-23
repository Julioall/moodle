using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Reports.Queries;
using Moodle.Domain;
using Moodle.Application.Reports.Templates;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Infrastructure.Reports;

internal sealed class ReportJobWorkerService(
    IServiceScopeFactory scopeFactory,
    ILogger<ReportJobWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ReportJobWorkerService iniciado.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ReportJobProcessor>();
                await processor.ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogError("Falha no ciclo de processamento de relatórios. FailureReason={FailureReason}", "worker_error");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        logger.LogInformation("ReportJobWorkerService encerrado.");
    }
}

internal sealed class ReportJobProcessor(
    ConnectorDbContext dbContext,
    IMoodleCoursesGateway coursesGateway,
    IMediator mediator,
    IConnectorExecutionContext executionContext,
    IMoodleConnectionSelection connectionSelection,
    ReportJobArtifactWriter artifactWriter,
    IReportArtifactStore artifactStore,
    ReportJobClaimer jobClaimer,
    ReportJobLeaseRenewer leaseRenewer,
    ReportArtifactDeletionQueue deletionQueue,
    ILogger<ReportJobProcessor> logger, ReportTemplateJobGenerator? templateGenerator = null,
    IPlatformPermissionService? permissions = null, IMoodleCurrentUserIdGateway? currentUser = null,
    IOptions<ReportTemplateOptions>? reportTemplateOptions = null)
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var job = await jobClaimer.ClaimNextAsync(cancellationToken);

        if (job is null)
        {
            return false;
        }

        using var metric = ReportMetrics.Begin(ReportMetrics.Stage.Processing);
        ReportMetrics.Claimed(job.RequestedAt);

        var leaseRemaining = (job.LeaseUntil ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
        using var leaseExpiration = new CancellationTokenSource(leaseRemaining > TimeSpan.Zero ? leaseRemaining : TimeSpan.Zero);
        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseExpiration.Token);
        var processingToken = processingCancellation.Token;
        var claimedLease = job.WorkerLeaseId ?? throw new ReportDatasetException("report_lease_lost");
        var publisher = new ReportJobPublisher(dbContext);
        using var leaseRenewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseRenewalTask = job.WorkerLeaseId is Guid workerLeaseId
            ? RenewLeaseUntilProcessingCompletesAsync(job.Id, workerLeaseId, processingCancellation, leaseExpiration, leaseRenewalCancellation.Token)
            : Task.CompletedTask;

        try
        {
            executionContext.Enter(job.ClientId, job.OwnerId.ToString(), null);
            connectionSelection.Alias = job.ConnectionAlias;
            using var admissionWork = MoodleAdmissionWorkScope.BeginScope(
                MoodleAdmissionOrigin.Report,
                job.OwnerId.ToString("N"),
                MoodleAdmissionPriority.Background);

            if (permissions is not null && !(await permissions.GetEffectivePermissionsAsync(job.OwnerId, processingToken)).Contains("reports.view", StringComparer.Ordinal))
                throw new ReportDatasetException("report_permission_denied");
            if (job.SelectionSnapshotJson is not null)
            {
                var selection = JsonSerializer.Deserialize<Application.Abstractions.Reports.ReportSelectionSnapshot>(
                    job.SelectionSnapshotJson, ReportDefinitionValidator.JsonOptions);
                var connectionId = await MoodleConnectionIdentity.ResolveAsync(dbContext, job.OwnerId, job.ClientId, job.ConnectionAlias, processingToken);
                if (selection?.SchemaVersion != 1 || selection.ConnectionId != connectionId ||
                    !selection.CourseIds.SequenceEqual(ReportDatasetResolver.NormalizeCourses(JsonSerializer.Deserialize<string[]>(job.CourseIdsJson ?? "[]")), StringComparer.Ordinal))
                    throw new ReportDatasetException("connection_changed");
            }
            if (job.DefinitionSnapshotJson is not null)
            {
                using var templateDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(
                    reportTemplateOptions?.Value.GenerationTimeoutSeconds ?? 600));
                using var templateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    processingToken, templateDeadline.Token);
                var templateToken = templateCancellation.Token;
                try
                {
                    var artifact = await (templateGenerator ?? throw new ReportDatasetException("report_generator_unavailable"))
                        .GenerateAsync(job, templateToken);
                    templateToken.ThrowIfCancellationRequested();
                    if (await ReportStorageCalculator.GetUsedBytesAsync(dbContext, job.OwnerId, templateToken, job.Id) + artifact.Content.LongLength > ReportStorageCalculator.LimitBytes)
                        throw new ReportDatasetException("report_storage_limit_exceeded");
                    await artifactWriter.PrepareAsync(job, artifact.ContentType, artifact.FileName, artifact.Content, templateToken);
                    if (!await publisher.PublishAsync(job, claimedLease, templateToken)) throw new ReportDatasetException("report_lease_lost");
                    metric.Outcome = ReportMetrics.Result.Success;
                    return true;
                }
                catch (OperationCanceledException) when (templateDeadline.IsCancellationRequested && !processingToken.IsCancellationRequested)
                {
                    throw new ReportDatasetException("report_deadline_exceeded");
                }
            }
            var courses = await ResolveCoursesAsync(job, processingToken);
            if (currentUser is not null && courses.Count > 0)
            {
                var userId = (await currentUser.GetCurrentUserIdAsync(processingToken)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var live = await coursesGateway.GetReportCoursesLiveAsync(userId, courses.Select(c => c.CourseId).ToArray(), false, false, processingToken);
                if (courses.Any(c => !live.Any(l => l.CourseId == c.CourseId))) throw new ReportDatasetException("course_unavailable");
            }
            if (courses.Count == 0)
            {
                throw new InvalidOperationException("Nenhum curso foi encontrado para gerar o relatório.");
            }

            job.CourseNamesJson = JsonSerializer.Serialize(courses.Select(course => new ReportCourseMetadata(
                ResolveCourseName(course),
                course.CategoryName)).ToArray());
            job.TotalCourses = courses.Count;
            job.ProgressPercent = 0;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await publisher.ProgressAsync(job, claimedLease, processingToken);

            var unitsByTurma = new Dictionary<string, List<ExcelGradeUnit>>(StringComparer.OrdinalIgnoreCase);
            foreach (var course in courses)
            {
                processingToken.ThrowIfCancellationRequested();
                var unit = await GenerateUnitAsync(course, processingToken);
                var turmaName = string.IsNullOrWhiteSpace(course.CategoryName) ? "Sem turma" : course.CategoryName!;
                if (!unitsByTurma.TryGetValue(turmaName, out var units))
                {
                    units = [];
                    unitsByTurma[turmaName] = units;
                }

                units.Add(unit);
                job.ProcessedCourses++;
                job.ProgressPercent = (int)Math.Round(job.ProcessedCourses * 100d / courses.Count, MidpointRounding.AwayFromZero);
                job.UpdatedAt = DateTimeOffset.UtcNow;
                if (ReportProgressPersistencePolicy.ShouldPersist(job.ProcessedCourses, courses.Count))
                {
                    await publisher.ProgressAsync(job, claimedLease, processingToken);
                }
            }

            var workbooks = unitsByTurma
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                {
                    var fileName = $"relatorio_notas_{Slugify(item.Key)}_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.xlsx";
                    return (FileName: fileName, Content: ExcelGradeReportBuilder.BuildWorkbook(item.Key, job.RequestedAt, item.Value));
                })
                .ToArray();

            var output = workbooks.Length == 1
                ? (ContentType: ExcelContentType, FileName: workbooks[0].FileName, Content: workbooks[0].Content)
                : (ContentType: "application/zip", FileName: $"relatorios_notas_{Slugify(job.ConnectionAlias)}_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip", Content: ExcelGradeReportBuilder.BuildZip(workbooks));

            var usedBytes = await ReportStorageCalculator.GetUsedBytesAsync(dbContext, job.OwnerId, processingToken, job.Id);
            if (usedBytes + output.Content.LongLength > ReportStorageCalculator.LimitBytes)
            {
                throw new InvalidOperationException($"O limite de {ReportStorageCalculator.FormatBytes(ReportStorageCalculator.LimitBytes)} por usuário seria ultrapassado. Exclua relatórios antigos antes de gerar outro arquivo.");
            }

            await artifactWriter.PrepareAsync(job, output.ContentType, output.FileName, output.Content, processingToken);
            if (!await publisher.PublishAsync(job, claimedLease, processingToken)) throw new ReportDatasetException("report_lease_lost");
            logger.LogInformation("Relatório {ReportJobId} concluído com {CourseCount} curso(s) e {WorkbookCount} arquivo(s).", job.Id, courses.Count, workbooks.Length);
            metric.Outcome = ReportMetrics.Result.Success;
            return true;
        }
        catch (OperationCanceledException)
        {
            metric.Outcome = leaseExpiration.IsCancellationRequested ? ReportMetrics.Result.LeaseLost : cancellationToken.IsCancellationRequested ? ReportMetrics.Result.Cancelled : ReportMetrics.Result.Timeout;
            await CleanupUnpersistedArtifactAsync(job);
            await publisher.FailAsync(job, claimedLease, "A geração do relatório foi cancelada antes da publicação.");
            if (cancellationToken.IsCancellationRequested) throw;
            return true;
        }
        catch (Exception exception)
        {
            await CleanupUnpersistedArtifactAsync(job);
            var code = exception is ReportDatasetException dataset ? dataset.Code : "report_generation_failed";
            metric.Outcome = code switch
            {
                "report_lease_lost" => ReportMetrics.Result.LeaseLost,
                "report_deadline_exceeded" => ReportMetrics.Result.Timeout,
                "report_storage_limit_exceeded" or "report_row_limit" or "report_activity_limit" => ReportMetrics.Result.Limit,
                _ => ReportMetrics.Result.Failed
            };
            await publisher.FailAsync(job, claimedLease, code);
            logger.LogWarning("Relatório {ReportJobId} falhou ({FailureCode}).", job.Id, code);
            return true;
        }        finally
        {
            leaseRenewalCancellation.Cancel();
            try
            {
                await leaseRenewalTask;
            }
            catch (OperationCanceledException) when (leaseRenewalCancellation.IsCancellationRequested)
            {
            }
            connectionSelection.Alias = null;
            executionContext.Clear();
        }
    }

    private async Task RenewLeaseUntilProcessingCompletesAsync(
        Guid reportJobId,
        Guid workerLeaseId,
        CancellationTokenSource processingCancellation,
        CancellationTokenSource leaseExpiration,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                if (!await leaseRenewer.RenewAsync(reportJobId, workerLeaseId, cancellationToken))
                {
                    processingCancellation.Cancel();
                    logger.LogWarning("A concessão do relatório {ReportJobId} não pode mais ser renovada.", reportJobId);
                    return;
                }
                leaseExpiration.CancelAfter(ReportJobClaimer.LeaseDuration);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                logger.LogWarning("Falha ao renovar a concessão do relatório. FailureReason={FailureReason}", "database_error");
            }
        }
    }

    private async Task CleanupUnpersistedArtifactAsync(ReportJobEntity job)
    {
        var blobKey = job.ArtifactBlobKey;
        if (string.IsNullOrWhiteSpace(blobKey))
        {
            return;
        }

        try
        {
            await artifactStore.DeleteAsync(blobKey, CancellationToken.None);
        }
        catch (Exception exception)
        {
            try
            {
                await deletionQueue.EnqueueAsync(blobKey, exception);
            }
            catch (Exception queueException)
            {
                logger.LogError(
                    queueException,
                    "Falha ao registrar a exclusão do artifact do relatório {ReportJobId}; a referência será mantida no job.",
                    job.Id);
                return;
            }
        }

        job.ArtifactBlobKey = null;
        job.FileSizeBytes = 0;
        job.ContentText = null;
        job.ContentBase64 = null;
        job.ContentType = null;
        job.FileName = null;
    }

    private async Task<IReadOnlyList<CourseSummary>> ResolveCoursesAsync(
        ReportJobEntity job,
        CancellationToken cancellationToken)
    {
        var userExternalId = job.OwnerId.ToString();
        if (job.ScopeType == "course")
        {
            var course = await coursesGateway.GetMyCourseAsync(userExternalId, job.CourseId!, cancellationToken);
            return course is null ? throw new InvalidOperationException("O curso selecionado não foi encontrado na conexão Moodle.") : [course];
        }

        if (job.ScopeType == "courses")
        {
            var courseIds = string.IsNullOrWhiteSpace(job.CourseIdsJson)
                ? []
                : JsonSerializer.Deserialize<string[]>(job.CourseIdsJson) ?? [];
            var courses = new List<CourseSummary>(courseIds.Length);
            foreach (var courseId in courseIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var course = await coursesGateway.GetMyCourseAsync(userExternalId, courseId, cancellationToken);
                if (course is not null)
                {
                    courses.Add(course);
                }
            }

            return courses;
        }

        if (job.ScopeType != "category" || string.IsNullOrWhiteSpace(job.CategoryPath))
        {
            throw new InvalidOperationException("O escopo do relatório é inválido.");
        }

        var categoryCourses = new List<CourseSummary>();
        var page = 1;
        const int pageSize = 100;
        while (true)
        {
            var result = await coursesGateway.GetMyCoursesByCategoryAsync(
                userExternalId,
                job.CategoryPath,
                pageSize,
                page,
                cancellationToken);
            categoryCourses.AddRange(result.Items);
            if (!result.HasNextPage || result.Items.Count == 0)
            {
                break;
            }

            page++;
        }

        return categoryCourses;
    }

    private async Task<ExcelGradeUnit> GenerateUnitAsync(CourseSummary course, CancellationToken cancellationToken)
    {
        try
        {
            var report = await mediator.Send(new GenerateCourseGradesReportQuery(course.CourseId, ReadOptions: GradebookReadOptions.Live), cancellationToken);
            return new ExcelGradeUnit(course.CourseId, ResolveCourseName(course), report.Students, report.Warning);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ExcelGradeUnit(course.CourseId, ResolveCourseName(course), [], exception.Message);
        }
    }

    private static string ResolveCourseName(CourseSummary course)
    {
        if (!string.IsNullOrWhiteSpace(course.FullName))
        {
            return course.FullName;
        }

        if (!string.IsNullOrWhiteSpace(course.DisplayName))
        {
            return course.DisplayName;
        }

        return course.CourseId;
    }

    private sealed record ReportCourseMetadata(string Name, string? CategoryName);

    private static string Slugify(string value)
    {
        var slug = new string(value
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());
        slug = string.Join('_', slug.Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "turma" : slug[..Math.Min(slug.Length, 60)];
    }
}
