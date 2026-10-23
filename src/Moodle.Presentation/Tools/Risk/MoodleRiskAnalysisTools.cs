using System.ComponentModel;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Risk.Queries;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Presentation.Tools;

namespace Moodle.Presentation.Tools.Risk;

[McpServerToolType]
public sealed class MoodleRiskAnalysisTools(
    IMediator mediator,
    IMoodleConnectionSelection moodleSelection,
    IMoodleUserResolver moodleUserResolver,
    MoodleSnapshotToolContext? snapshotContext = null)
{
    [McpServerTool(
        Name = "report_students_at_risk",
        Title = "Report Students At Risk",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<IReadOnlyList<StudentRiskReport>>))]
    [Description("Gera um relatório de risco cruzando acesso e nota total do curso retornada pelo Moodle. A nota total não é a lista de notas por atividade; use list_students_below_min_grade para esse escopo. Confira os avisos de cobertura; cobertura incompleta não prova ausência de risco. Completion detalhado não é consultado.")]
    public Task<CallToolResult> GerarRelatorioRiscoEstudantesAsync(
        [Description("Identificador do curso Moodle.")]
        string courseId,
        [Description("Maximo de estudantes para analisar (ex: 20, 50, 100). Padrao: 50.")]
        int maxStudentsToAnalyze = 50,
        [Description("Limite de dias de inatividade para considerar como fator de risco. Padrao: 7.")]
        int inactivityThresholdDays = 7,
        [Description("Nota minima esperada em porcentagem (0-100) para considerar como fator de risco. Padrao: 60.")]
        decimal minGradePercentage = 60m,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return GetRiskReportCoreAsync(
            courseId,
            maxStudentsToAnalyze,
            inactivityThresholdDays,
            minGradePercentage,
            moodleAlias,
            cancellationToken);
    }

    private async Task<CallToolResult> GetRiskReportCoreAsync(
        string courseId,
        int maxStudents,
        int inactivityThresholdDays,
        decimal minGradePercentage,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
        {
            return ToolResultHelper.Error<IReadOnlyList<StudentRiskReport>>("Informe um identificador de curso valido.");
        }

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
        {
            return ToolResultHelper.Error<IReadOnlyList<StudentRiskReport>>("Usuario nao autenticado para gerar relatorio.");
        }

        var effectiveCourseId = courseId;
        CourseGradebookSnapshot? prefetchedGradebook = null;
        CourseParticipantsPage? prefetchedParticipants = null;
        ToolFreshness? freshness = null;
        var freshnessWarnings = new List<string>();
        if (snapshotContext is not null)
        {
            try
            {
                var courseRead = await snapshotContext.ReadAsync(
                    new CourseReadSnapshotRequest(
                        courseId,
                        moodleAlias,
                        moodleUserId.Value.ToString(),
                        CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook),
                    cancellationToken);
                if (courseRead is not null)
                {
                    effectiveCourseId = courseRead.CourseId;
                    var snapshotUnsafe = MoodleSnapshotFreshnessWarnings.IsUnsafeForSnapshotDecision(courseRead.Metadata);
                    freshnessWarnings.AddRange(MoodleSnapshotFreshnessWarnings.BuildWarnings(courseRead.Metadata));
                    prefetchedGradebook = snapshotUnsafe ? null : courseRead.Gradebook?.Data;
                    if (!snapshotUnsafe && courseRead.Students?.Data is { HasMore: false } participants &&
                        courseRead.Students.IsComplete)
                    {
                        prefetchedParticipants = participants;
                    }
                    var updatedAt = courseRead.Metadata.OldestUpdatedAt;
                    freshness = new ToolFreshness(
                        snapshotUnsafe ? "live" : "snapshot",
                        snapshotUnsafe ? null : updatedAt,
                        updatedAt.HasValue
                            && !snapshotUnsafe ? Math.Max(0, (long)(DateTimeOffset.UtcNow - updatedAt.Value).TotalSeconds)
                            : null,
                        !snapshotUnsafe && courseRead.Metadata.StaleDatasets.Count > 0,
                        courseRead.Metadata.RefreshQueued,
                        !snapshotUnsafe && courseRead.Metadata.IsComplete,
                        snapshotUnsafe ? 0 : (courseRead.Students?.RecordCount ?? 0) +
                            (courseRead.Gradebook?.RecordCount ?? 0),
                        DecisionSafe: !snapshotUnsafe,
                        Dataset: "course_read_snapshot",
                        RecordType: "students_and_gradebook");
                }
            }
            catch
            {
                // Snapshot warming is best effort; the current report keeps
                // its live bulk/fallback behavior.
            }
        }

        StudentsAtRiskReportResult result;
        try
        {
            result = await mediator.Send(
                new GetStudentsAtRiskReportQuery(
                    effectiveCourseId,
                    maxStudents,
                    inactivityThresholdDays,
                    minGradePercentage,
                    PrefetchedGradebook: prefetchedGradebook,
                    PrefetchedParticipants: prefetchedParticipants),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ToolResultHelper.Error<IReadOnlyList<StudentRiskReport>>("Nao foi possivel gerar o relatorio neste momento.");
        }

        var data = result.Reports;
        var warnings = new List<string>(BuildWarnings(result));
        warnings.AddRange(freshnessWarnings);
        var response = new ToolResponse<IReadOnlyList<StudentRiskReport>>(
            "ok",
            data,
            warnings,
            AuditId: null,
            DateTimeOffset.UtcNow,
            Freshness: freshness);

        var altos = data.Count(d => d.RiskLevel == RiskLevel.Alto);
        var medios = data.Count(d => d.RiskLevel == RiskLevel.Medio);

        var narration = $"O relatorio de risco do curso {courseId} analisou {result.ParticipantsAnalyzedCount} participante(s). " +
                        $"Foram identificados {data.Count} estudante(s) com algum nivel de risco, " +
                        $"sendo {altos} com risco Alto e {medios} com risco Medio. " +
                        $"Acesso desconhecido: {result.AccessUnknownCount}; cobertura da população " +
                        $"{(result.ParticipantCoverageComplete ? "completa" : "incompleta")}.";

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = narration }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private static IReadOnlyList<string> BuildWarnings(StudentsAtRiskReportResult result)
    {
        var warnings = new List<string>();

        if (result.ClassificationDiagnostics.IncludedByFallbackCount > 0)
        {
            warnings.Add(
                "Nao foi possivel identificar todos os alunos por role. " +
                $"{result.ClassificationDiagnostics.IncludedByFallbackCount} participante(s) foram incluidos por fallback no relatorio.");
        }

        if (result.ParticipantsAnalyzedCount == 0)
        {
            warnings.Add("O curso nao retornou participantes para analise de risco.");
        }
        else if (!result.ParticipantCoverageComplete)
        {
            warnings.Add("A população de participantes está incompleta; o resultado não representa um censo do curso.");
        }
        else if (result.Reports.Count == 0)
        {
            warnings.Add(!result.AccessCoverageComplete || !result.ParticipantCoverageComplete || result.GradebookFailureCount > 0
                ? $"Foram analisados {result.ParticipantsAnalyzedCount} participante(s), mas a cobertura é incompleta; ausência de linhas não comprova ausência de risco."
                : $"Foram analisados {result.ParticipantsAnalyzedCount} participante(s), mas nenhum fator de risco configurado foi detectado.");
        }

        if (result.AccessUnknownCount > 0)
            warnings.Add($"O último acesso ao curso é desconhecido para {result.AccessUnknownCount} participante(s).");

        if (result.GradebookFailureCount > 0)
        {
            warnings.Add(
                $"Nao foi possivel consultar notas de {result.GradebookFailureCount} participante(s); " +
                "o relatorio pode estar parcial.");
        }

        return warnings;
    }


}
