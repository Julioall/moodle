using System.ComponentModel;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Reports.Queries;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Infrastructure.Reports;
using Moodle.Presentation.Tools;

namespace Moodle.Presentation.Tools.Reports;

public sealed record CourseGradesExcelExportResult(
    string CourseId,
    DateTimeOffset GeneratedAt,
    string FileName,
    string ContentType,
    long FileSizeBytes,
    int TotalStudents,
    int StudentsWithGrade,
    int StudentsWithoutGrade,
    decimal? AveragePercentage,
    string? Warning);

/// <summary>
/// Tools de relatórios pedagógicos para tutores.
/// Fase 10 — Domínio Relatórios.
/// </summary>
[McpServerToolType]
public sealed class MoodleReportTools(
    IMediator mediator,
    IMoodleConnectionSelection moodleSelection,
    IMoodleUserResolver moodleUserResolver,
    MoodleSnapshotToolContext? snapshotContext = null,
    IMoodleCourseReadSnapshotCoordinator? snapshotCoordinator = null)
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [McpServerTool(
        Name = "generate_course_grades_report",
        Title = "Generate Course Grades Report",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GenerateCourseGradesReportResult>))]
    [Description("Gera um relatorio estruturado de notas do curso por estudante. Usa o item total do curso retornado pelo Moodle; nao soma notas de atividades localmente. Para um arquivo Excel, use export_course_grades_excel.")]
    public Task<CallToolResult> GerarRelatorioNotasCursoAsync(
        [Description("Identificador do curso Moodle.")] string courseId,
        [Description("Tamanho das paginas usadas na leitura de participantes. De 1 a 100. Padrao: 100.")] int pageSize = 100,
        [Description("Alias do Moodle a usar.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
        => ExecuteReportAsync<GenerateCourseGradesReportResult>(
            courseId,
            moodleAlias,
            (effectiveCourseId, gradebook, participants) => mediator.Send(
                new GenerateCourseGradesReportQuery(
                    effectiveCourseId,
                    pageSize,
                    PrefetchedGradebook: gradebook,
                    PrefetchedParticipants: participants),
                cancellationToken),
            result => $"Relatorio de notas - curso {courseId}: {result.TotalStudents} estudante(s), " +
                      $"{result.StudentsWithGrade} com nota e {result.StudentsWithoutGrade} sem nota.",
            cancellationToken,
            requirements: CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook,
            liveCriticalGradebook: true);

    [McpServerTool(
        Name = "export_course_grades_excel",
        Title = "Export Course Grades to Excel",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<CourseGradesExcelExportResult>))]
    [Description("Gera e entrega um arquivo Excel formatado com as notas totais do curso por estudante. O arquivo e anexado ao resultado MCP; a regra de notas e a mesma usada pelo relatorio Excel do frontend.")]
    public async Task<CallToolResult> ExportarRelatorioNotasExcelAsync(
        [Description("Identificador do curso Moodle.")] string courseId,
        [Description("Tamanho das paginas usadas na leitura de participantes. De 1 a 100. Padrao: 100.")] int pageSize = 100,
        [Description("Alias do Moodle a usar.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(courseId))
            return ToolResultHelper.Error<CourseGradesExcelExportResult>("Informe um identificador de curso valido.");

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
            return ToolResultHelper.Error<CourseGradesExcelExportResult>("Usuario nao autenticado.");

        try
        {
            var snapshotInputs = await ReadSnapshotInputsAsync(
                courseId,
                moodleAlias,
                moodleUserId.Value,
                CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook,
                liveCriticalGradebook: true,
                cancellationToken);
            var report = await mediator.Send(
                new GenerateCourseGradesReportQuery(
                    snapshotInputs.EffectiveCourseId,
                    pageSize,
                    PrefetchedGradebook: snapshotInputs.PrefetchedGradebook,
                    PrefetchedParticipants: snapshotInputs.PrefetchedParticipants),
                cancellationToken);
            var courseName = await ResolveCourseNameAsync(
                moodleUserId.Value.ToString(),
                snapshotInputs.EffectiveCourseId,
                cancellationToken);
            var fileName = $"relatorio_notas_{Slugify(courseId)}_{report.GeneratedAt:yyyyMMdd-HHmmss}.xlsx";
            var workbook = ExcelGradeReportBuilder.BuildWorkbook(
                courseName,
                report.GeneratedAt,
                [new ExcelGradeUnit(report.CourseId, courseName, report.Students, report.Warning)]);
            var data = new CourseGradesExcelExportResult(
                report.CourseId,
                report.GeneratedAt,
                fileName,
                ExcelContentType,
                workbook.LongLength,
                report.TotalStudents,
                report.StudentsWithGrade,
                report.StudentsWithoutGrade,
                report.AveragePercentage,
                report.Warning);
            var exportFreshness = snapshotInputs.Freshness;
            if (exportFreshness is { Source: "live" })
            {
                exportFreshness = exportFreshness with
                {
                    Complete = false,
                    DecisionSafe = false,
                    RecordCount = report.TotalStudents,
                };
            }
            var response = new ToolResponse<CourseGradesExcelExportResult>(
                "ok",
                data,
                snapshotInputs.Warnings
                    .Concat(report.Warning is null ? [] : [report.Warning])
                .ToArray(),
                AuditId: null,
                DateTimeOffset.UtcNow,
                Freshness: exportFreshness);
            var resource = BlobResourceContents.FromBytes(
                workbook,
                $"mcp://moodle/reports/{Guid.NewGuid():N}/{fileName}",
                ExcelContentType);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = $"Arquivo Excel de notas gerado: {fileName} ({report.TotalStudents} estudante(s))." },
                    new EmbeddedResourceBlock { Resource = resource }
                ],
                StructuredContent = JsonSerializer.SerializeToElement(response),
                IsError = false
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<CourseGradesExcelExportResult>(exception);
        }
    }

    // â”€â”€ Relatório semanal de desempenho â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task<string> ResolveCourseNameAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken)
    {
        try
        {
            var course = await mediator.Send(
                new Moodle.Application.Courses.GetCourseQuery(userExternalId, courseId),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(course?.FullName))
            {
                return course.FullName;
            }

            if (!string.IsNullOrWhiteSpace(course?.DisplayName))
            {
                return course.DisplayName;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Course name is an optional enrichment; keep the grade export available.
        }

        return courseId;
    }

    [McpServerTool(
        Name = "generate_weekly_performance_report",
        Title = "Generate Weekly Performance Report",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GenerateWeeklyPerformanceReportResult>))]
    [Description("Gera relatório semanal de desempenho: cruza acesso, notas por atividade/SA e estado comprovado das entregas. Pode classificar a situação como 'unknown' quando cobertura ou avaliação estiver incompleta; confira EligibleAssignmentCount, UnknownAssignmentsCount e DeliveryCoverage. Retorna destinatários sugeridos somente para evidências positivas observadas. A nota total do curso é apresentada separadamente de notas por atividade.")]
    public Task<CallToolResult> GerarRelatorioSemanalDesempenhoAsync(
        [Description("Identificador do curso Moodle.")] string courseId,
        [Description("Nota mínima esperada em porcentagem (0-100). Padrão: 60.")] decimal minGradePercent = 60m,
        [Description("Dias sem acesso para considerar inativo. Padrão: 7.")] int inactiveDaysThreshold = 7,
        [Description("Máximo de estudantes a analisar. Padrão: 60.")] int maxStudentsToAnalyze = 60,
        [Description("Alias do Moodle a usar.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
        => ExecuteReportAsync<GenerateWeeklyPerformanceReportResult>(
            courseId, moodleAlias,
            (effectiveCourseId, gradebook, participants) => mediator.Send(
                new GenerateWeeklyPerformanceReportQuery(
                    effectiveCourseId,
                    minGradePercent,
                    inactiveDaysThreshold,
                    maxStudentsToAnalyze,
                    PrefetchedGradebook: gradebook,
                    PrefetchedParticipants: participants),
                cancellationToken),
            result => $"Relatório semanal — curso {courseId}: {result.TotalStudents} estudante(s). " +
                      $"{result.StudentsAtRisk} em risco, {result.StudentsWithAttention} em atenção. " +
                      $"{result.StudentsWithUnknownSituation} com situação desconhecida; cobertura de entregas " +
                      $"{(result.DeliveryCoverage.IsComplete ? "completa" : "incompleta")}" +
                      $"{(result.DeliveryCoverage.ReasonCode is null ? "." : $" ({result.DeliveryCoverage.ReasonCode}).")}" +
                      $"Gerado em: {result.GeneratedAt:dd/MM/yyyy HH:mm} UTC.",
            cancellationToken,
            requirements: CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook,
            liveCriticalGradebook: true);

    // â”€â”€ Relatório de conselho de classe â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [McpServerTool(
        Name = "generate_class_council_report",
        Title = "Generate Class Council Report",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GenerateClassCouncilReportResult>))]
    [Description("Gera relatório indicativo de conselho de classe: 'unknown' indica cobertura insuficiente para classificar como regular. Separe nota por atividade de nota total do curso e confira DeliveryCoverage. Não constitui decisão oficial de aprovação ou reprovação.")]
    public Task<CallToolResult> GerarRelatorioConselhoClasseAsync(
        [Description("Identificador do curso Moodle.")] string courseId,
        [Description("Nota mínima esperada em porcentagem (0-100). Padrão: 60.")] decimal minGradePercent = 60m,
        [Description("Dias sem acesso para considerar inativo. Padrão: 7.")] int inactiveDaysThreshold = 7,
        [Description("Máximo de estudantes a analisar. Padrão: 60.")] int maxStudentsToAnalyze = 60,
        [Description("Alias do Moodle a usar.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
        => ExecuteReportAsync<GenerateClassCouncilReportResult>(
            courseId, moodleAlias,
            (effectiveCourseId, gradebook, participants) => mediator.Send(
                new GenerateClassCouncilReportQuery(
                    effectiveCourseId,
                    minGradePercent,
                    inactiveDaysThreshold,
                    maxStudentsToAnalyze,
                    PrefetchedGradebook: gradebook,
                    PrefetchedParticipants: participants),
                cancellationToken),
            result => $"Conselho de classe - curso {courseId}: {result.TotalStudents} estudante(s). " +
                      $"Regular: {result.Regular} | Atenção: {result.NeedAttention} | Recuperação: {result.NeedRecovery} | " +
                      $"Risco: {result.AtRisk} | Situação desconhecida: {result.StudentsWithUnknownSituation}.",
            cancellationToken,
            requirements: CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook,
            liveCriticalGradebook: true);


    [McpServerTool(
        Name = "generate_course_summary",
        Title = "Generate Course Summary",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<CourseOverviewResult>))]
    [Description("Gera resumo executivo do curso: participantes ativos, acesso comprovado, acesso desconhecido e cobertura da população. Timestamp ausente sem evidência explícita não significa nunca acessou. Não consulta boletim individual — use gerar_relatorio_semanal_desempenho para notas por atividade e nota total do curso.")]
    public Task<CallToolResult> GerarResumoCursoAsync(
        [Description("Identificador do curso Moodle.")] string courseId,
        [Description("Dias sem acesso para considerar inativo. Padrão: 7.")] int inactiveDaysThreshold = 7,
        [Description("Máximo de estudantes a analisar. Padrão: 100.")] int maxStudentsToAnalyze = 100,
        [Description("Alias do Moodle a usar.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
        => ExecuteReportAsync<CourseOverviewResult>(
            courseId, moodleAlias,
            (effectiveCourseId, _, _) => mediator.Send(
                new GenerateCourseOverviewQuery(effectiveCourseId, inactiveDaysThreshold, maxStudentsToAnalyze),
                cancellationToken),
            result => $"Resumo - curso {courseId}: {result.TotalActiveStudents} estudante(s). " +
                      $"{result.StudentsWhoAccessed} acessaram, {result.StudentsNeverAccessed} nunca acessaram, " +
                      $"{result.StudentsAccessUnknown} com acesso desconhecido e {result.StudentsInactiveDays} inativos há +{result.InactiveDaysThreshold} dias. " +
                      $"Cobertura da população: {(result.ParticipantCoverageAvailable ? "completa" : "incompleta")}.",
            cancellationToken);

    // â”€â”€ Relatório de pós-execução â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [McpServerTool(
        Name = "generate_full_post_execution_report",
        Title = "Generate Full Post-Execution Report",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GeneratePostExecutionReportResult>))]
    [Description("Gera relatório de pós-execução com situação provável de cada estudante ao fim do curso: 'likely_complete', 'pending_recovery', 'at_risk' ou 'unknown'. ATENÇÃO: indicativo — não constitui decisão oficial. Deve ser validado pelo tutor e coordenação.")]
    public Task<CallToolResult> GerarRelatorioPosExecucaoCompletoAsync(
        [Description("Identificador do curso Moodle.")] string courseId,
        [Description("Nota mínima esperada em porcentagem (0-100). Padrão: 60.")] decimal minGradePercent = 60m,
        [Description("Máximo de estudantes a analisar. Padrão: 60.")] int maxStudentsToAnalyze = 60,
        [Description("Alias do Moodle a usar.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
        => ExecuteReportAsync<GeneratePostExecutionReportResult>(
            courseId, moodleAlias,
            (effectiveCourseId, gradebook, participants) => mediator.Send(
                new GeneratePostExecutionReportQuery(
                    effectiveCourseId,
                    minGradePercent,
                    maxStudentsToAnalyze,
                    PrefetchedGradebook: gradebook,
                    PrefetchedParticipants: participants),
                cancellationToken),
            result => $"Pós-execução — curso {courseId}: {result.TotalStudents} estudante(s). " +
                      $"Provável conclusão: {result.LikelyComplete} | Recuperação: {result.PendingRecovery} | " +
                      $"Risco: {result.AtRisk} | Dados insuficientes: {result.Unknown}.",
            cancellationToken,
            requirements: CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook,
            liveCriticalGradebook: true);


    [Description("Baixa o JSON de qualquer relatório personalizado do Moodle Report Builder acessível ao usuário do token. Retorna os registros paginados limitados ao 'pageSize'.")]
 #if false
    public async Task<CallToolResult> BaixarRelatorioBuilderAsync(
        [Description("Identificador numérico do relatório.")] int reportId,
        [Description("Dicionário opcional de filtros em formato JSON (ex: '{\"user:firstname_operator\":2, \"user:firstname_value\":\"João\"}').")] string? filtersJson = null,
        [Description("Quantidade máxima de registros a retornar. Padrão: 5000.")] int pageSize = 5000,
        [Description("Alias do Moodle a usar.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
            return ToolResultHelper.Error<Moodle.Application.Abstractions.MoodleReportResult>("Usuário não autenticado.");

        Moodle.Application.Abstractions.MoodleReportResult data;
        try
        {
            var filters = !string.IsNullOrWhiteSpace(filtersJson)
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(filtersJson)
                : null;
            data = await reportBuilderClient.DownloadAsync(reportId, pageSize, filters, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToolResultHelper.Error<Moodle.Application.Abstractions.MoodleReportResult>($"Não foi possível baixar o relatório: {ex.Message}");
        }

        var response = new ToolResponse<Moodle.Application.Abstractions.MoodleReportResult>("ok", data, [], AuditId: null, DateTimeOffset.UtcNow);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Relatório {reportId} baixado com sucesso: {data.Rows.Count} registro(s) retornado(s)." }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }


 #endif

    private static string Slugify(string value)
    {
        var slug = new string(value
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());
        slug = string.Join('_', slug.Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "curso" : slug[..Math.Min(slug.Length, 60)];
    }

    private async Task<CallToolResult> ExecuteReportAsync<TResult>(
        string courseId,
        string? moodleAlias,
        Func<string, CourseGradebookSnapshot?, CourseParticipantsPage?, Task<TResult>> execute,
        Func<TResult, string> narrate,
        CancellationToken cancellationToken,
        CourseReadSnapshotRequirements requirements = CourseReadSnapshotRequirements.None,
        bool liveCriticalGradebook = false)
    {
        if (string.IsNullOrWhiteSpace(courseId))
            return ToolResultHelper.Error<TResult>("Informe um identificador de curso válido.");

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
            return ToolResultHelper.Error<TResult>("Usuário não autenticado.");

        var snapshotInputs = await ReadSnapshotInputsAsync(
            courseId,
            moodleAlias,
            moodleUserId.Value,
            requirements,
            liveCriticalGradebook,
            cancellationToken);
        var freshness = snapshotInputs.Freshness;
        var freshnessWarnings = snapshotInputs.Warnings.ToList();

        TResult data;
        try
        {
            data = await execute(
                snapshotInputs.EffectiveCourseId,
                snapshotInputs.PrefetchedGradebook,
                snapshotInputs.PrefetchedParticipants);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return ToolResultHelper.Error<TResult>("Não foi possível gerar o relatório neste momento.");
        }

        if (data is GenerateWeeklyPerformanceReportResult weekly && !string.IsNullOrWhiteSpace(weekly.Warning))
        {
            freshnessWarnings.Add(weekly.Warning);
        }
        else if (data is GenerateClassCouncilReportResult council && !string.IsNullOrWhiteSpace(council.Warning))
        {
            freshnessWarnings.Add(council.Warning);
        }
        else if (data is GeneratePostExecutionReportResult postExecution && !string.IsNullOrWhiteSpace(postExecution.Warning))
        {
            freshnessWarnings.Add(postExecution.Warning);
        }

        if (freshness is { Source: "live" })
        {
            freshness = freshness with
            {
                // Reports do not expose a complete-live-read contract. A live
                // fallback therefore remains non-decision-safe until the
                // durable snapshot is complete.
                Complete = false,
                DecisionSafe = false,
                RecordCount = GetReportRecordCount(data),
            };
        }

        var response = new ToolResponse<TResult>("ok", data, freshnessWarnings, AuditId: null, DateTimeOffset.UtcNow, Freshness: freshness);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = narrate(data) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private async Task<CourseReportSnapshotInputs> ReadSnapshotInputsAsync(
        string courseId,
        string? moodleAlias,
        long moodleUserId,
        CourseReadSnapshotRequirements requirements,
        bool liveCriticalGradebook,
        CancellationToken cancellationToken)
    {
        var empty = new CourseReportSnapshotInputs(courseId, null, null, null, []);
        if (requirements == CourseReadSnapshotRequirements.None)
        {
            return empty;
        }

        var coordinator = snapshotCoordinator ?? snapshotContext as IMoodleCourseReadSnapshotCoordinator;
        if (coordinator is null)
        {
            return empty;
        }

        try
        {
            var courseRead = await coordinator.ReadAsync(
                new CourseReadSnapshotRequest(
                    courseId,
                    moodleAlias,
                    moodleUserId.ToString(),
                    requirements,
                    AllowStale: false),
                cancellationToken);
            if (courseRead is null)
            {
                return empty;
            }

            var snapshotUnsafe = MoodleSnapshotFreshnessWarnings.IsUnsafeForSnapshotDecision(courseRead.Metadata);
            var staleDecisionDatasets = courseRead.Metadata.StaleDatasets
                .Where(dataset =>
                    requirements.HasFlag(CourseReadSnapshotRequirements.Gradebook) &&
                        dataset == MoodleSnapshotDatasets.Gradebook ||
                    requirements.HasFlag(CourseReadSnapshotRequirements.Students) &&
                        dataset == MoodleSnapshotDatasets.Students)
                .ToArray();
            var gradebookSafe = !liveCriticalGradebook &&
                !snapshotUnsafe &&
                !staleDecisionDatasets.Contains(MoodleSnapshotDatasets.Gradebook) &&
                courseRead.Gradebook is { IsStale: false, IsComplete: true } gradebook &&
                gradebook.Data.Coverage.IsComplete;
            var studentsSafe = !snapshotUnsafe &&
                !staleDecisionDatasets.Contains(MoodleSnapshotDatasets.Students) &&
                courseRead.Students is { IsStale: false, IsComplete: true, Data.HasMore: false };
            var prefetchedGradebook = gradebookSafe ? courseRead.Gradebook!.Data : null;
            CourseParticipantsPage? prefetchedParticipants = null;
            if (studentsSafe && courseRead.Students?.Data is { HasMore: false } participants)
            {
                prefetchedParticipants = participants;
            }

            var warnings = new List<string>();
            if (staleDecisionDatasets.Length > 0)
            {
                warnings.Add(
                    $"Snapshot stale ({string.Join(", ", staleDecisionDatasets)}); o relatório decisório foi revalidado por leitura ao vivo e não usará dados antigos.");
            }
            if (liveCriticalGradebook)
            {
                warnings.Add("Notas e dados de recuperação deste relatório foram consultados ao vivo no Moodle; o snapshot serve apenas para contexto e cobertura de estudantes.");
            }

            var hasSnapshotData = prefetchedParticipants is not null || prefetchedGradebook is not null;
            var liveSource = liveCriticalGradebook || staleDecisionDatasets.Length > 0;
            ToolFreshness freshness;
            if (hasSnapshotData)
            {
                var updatedAt = courseRead.Metadata.OldestUpdatedAt;
                var recordCount = (courseRead.Students?.RecordCount ?? 0) +
                    (courseRead.Gradebook?.RecordCount ?? 0);
                freshness = new ToolFreshness(
                    liveSource ? "live" : "snapshot",
                    liveSource ? null : updatedAt,
                    !liveSource && updatedAt.HasValue
                        ? Math.Max(0, (long)(DateTimeOffset.UtcNow - updatedAt.Value).TotalSeconds)
                        : null,
                    false,
                    courseRead.Metadata.RefreshQueued,
                    !liveSource && courseRead.Metadata.IsComplete,
                    liveSource ? 0 : recordCount,
                    DecisionSafe: !liveSource && !snapshotUnsafe,
                    Dataset: "course_read_snapshot",
                    RecordType: "students_and_gradebook");
            }
            else
            {
                // A missing head triggers a live/bulk read below; do not label
                // that result as an empty snapshot.
                freshness = new ToolFreshness(
                    "live",
                    null,
                    null,
                    false,
                    courseRead.Metadata.RefreshQueued,
                    false,
                    0,
                    DecisionSafe: false,
                    Dataset: "course_read_snapshot",
                    RecordType: "students_and_gradebook");
            }

            warnings.AddRange(MoodleSnapshotFreshnessWarnings.BuildWarnings(courseRead.Metadata));
            return new CourseReportSnapshotInputs(
                courseRead.CourseId,
                prefetchedGradebook,
                prefetchedParticipants,
                freshness,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Snapshot lookup is an optimization; the report still uses the
            // live/bulk gateway and its capability-driven fallback.
            return empty;
        }
    }

    private static int GetReportRecordCount<TResult>(TResult data) => data switch
    {
        GenerateCourseGradesReportResult report => report.Students.Count,
        GenerateWeeklyPerformanceReportResult report => report.Students.Count,
        GenerateClassCouncilReportResult report => report.Students.Count,
        GeneratePostExecutionReportResult report => report.Students.Count,
        _ => 0,
    };

    private sealed record CourseReportSnapshotInputs(
        string EffectiveCourseId,
        CourseGradebookSnapshot? PrefetchedGradebook,
        CourseParticipantsPage? PrefetchedParticipants,
        ToolFreshness? Freshness,
        IReadOnlyList<string> Warnings);
}
