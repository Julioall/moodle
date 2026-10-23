using System.ComponentModel;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Presentation.Tools;

namespace Moodle.Presentation.Tools.Gradebook;

[McpServerToolType]
public sealed class MoodleStudentPerformanceTools(
    IMediator mediator,
    IMoodleConnectionSelection moodleSelection,
    IMoodleUserResolver moodleUserResolver,
    MoodleSnapshotToolContext? snapshotContext = null,
    IMoodleCourseReadSnapshotCoordinator? snapshotCoordinator = null)
{
    // ── Desempenho por atividade ──────────────────────────────────────────────

    [McpServerTool(
        Name = "get_student_activity_grades",
        Title = "Get Student Activity Grades",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<StudentGradeItemsResult>))]
    [Description("Retorna os itens avaliativos (SAs) do boletim de um estudante com indicação de quais estão abaixo do conceito mínimo. Usado pelo tutor para identificar oportunidades de recuperação paralela.")]
    public Task<CallToolResult> ConsultarDesempenhoEstudantePorAtividadeAsync(
        [Description("Identificador do curso Moodle.")]
        string courseId,
        [Description("Identificador do estudante (ID do Moodle).")]
        string studentId,
        [Description("Nota mínima esperada em porcentagem (0-100). Padrão: 60.")]
        decimal minGradePercent = 60m,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrão do usuário.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return GetGradeItemsCoreAsync(courseId, studentId, minGradePercent, moodleAlias, cancellationToken);
    }

    private async Task<CallToolResult> GetGradeItemsCoreAsync(
        string courseId,
        string studentId,
        decimal minGradePercent,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
            return ToolResultHelper.Error<StudentGradeItemsResult>("Informe um identificador de curso válido.");

        if (string.IsNullOrWhiteSpace(studentId))
            return ToolResultHelper.Error<StudentGradeItemsResult>("Informe um identificador de estudante válido.");

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
            return ToolResultHelper.Error<StudentGradeItemsResult>("Usuário não autenticado.");

        var effectiveCourseId = courseId;
        CourseGradebookSnapshot? prefetchedGradebook = null;
        ToolFreshness? freshness = null;
        var freshnessWarnings = new List<string>();
        var snapshotContainsStudent = false;
        var coordinator = snapshotCoordinator ?? snapshotContext as IMoodleCourseReadSnapshotCoordinator;
        if (coordinator is not null)
        {
            try
            {
                var courseRead = await coordinator.ReadAsync(
                    new CourseReadSnapshotRequest(
                        courseId,
                        moodleAlias,
                        moodleUserId.Value.ToString(),
                        CourseReadSnapshotRequirements.Gradebook),
                    cancellationToken);
                if (courseRead is not null)
                {
                    effectiveCourseId = courseRead.CourseId;
                    var snapshotUnsafe = MoodleSnapshotFreshnessWarnings.IsUnsafeForSnapshotDecision(courseRead.Metadata);
                    freshnessWarnings.AddRange(MoodleSnapshotFreshnessWarnings.BuildWarnings(courseRead.Metadata));
                    prefetchedGradebook = snapshotUnsafe ? null : courseRead.Gradebook?.Data;
                    snapshotContainsStudent = prefetchedGradebook?.TryGetForStudent(studentId, out _) == true;
                    if (snapshotContainsStudent && courseRead.Gradebook is not null)
                    {
                        freshness = new ToolFreshness(
                            "snapshot",
                            courseRead.Gradebook.UpdatedAt,
                            Math.Max(0, (long)(DateTimeOffset.UtcNow - courseRead.Gradebook.UpdatedAt).TotalSeconds),
                            courseRead.Gradebook.IsStale,
                            courseRead.Metadata.RefreshQueued,
                            courseRead.Gradebook.IsComplete && courseRead.Gradebook.Data.Coverage.IsComplete,
                            courseRead.Gradebook.RecordCount,
                            DecisionSafe: !snapshotUnsafe && !courseRead.Gradebook.IsStale);
                    }
                    else
                    {
                        freshness = new ToolFreshness(
                            "live",
                            null,
                            null,
                            false,
                            courseRead.Metadata.RefreshQueued,
                            false,
                            0,
                            DecisionSafe: false,
                            Dataset: MoodleSnapshotDatasets.Gradebook,
                            RecordType: "student_grade_items");
                    }
                }
            }
            catch
            {
                // Snapshot lookup is an optimization. Keep the live path.
            }
        }

        StudentGradeItemsResult data;
        try
        {
            data = await mediator.Send(
                new GetStudentGradeItemsQuery(
                    effectiveCourseId,
                    studentId,
                    minGradePercent,
                    prefetchedGradebook),
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (MoodleApiException exception)
        {
            return ToolResultHelper.Error<StudentGradeItemsResult>(exception);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<StudentGradeItemsResult>(exception);
        }

        if (freshness?.Stale == true)
        {
            freshnessWarnings.Add("O boletim retornado vem de um snapshot stale; use-o apenas como leitura informativa enquanto a atualização é processada.");
        }
        var response = new ToolResponse<StudentGradeItemsResult>(
            "ok", data, freshnessWarnings, AuditId: null, DateTimeOffset.UtcNow, Freshness: freshness);
        var narration = $"Desempenho do estudante {studentId} no curso {courseId}: {data.Items.Count} atividade(s) avaliativa(s). " +
                        $"{data.BelowMinimumItems.Count} abaixo do mínimo de {data.MinGradePercent}%.";

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = narration }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    // ── Alunos abaixo do mínimo ───────────────────────────────────────────────

    [McpServerTool(
        Name = "list_students_below_min_grade",
        Title = "List Students Below Min Grade",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GetStudentsBelowMinGradeResult>))]
    [Description("Lista estudantes ativos com pelo menos uma nota de atividade/SA abaixo do mínimo; não avalia a nota total do curso. Retorna público-alvo sugerido para recuperação paralela somente quando a cobertura dessa leitura é suficiente. Lista vazia significa apenas que nenhuma atividade abaixo do limiar foi observada no escopo e na cobertura retornados.")]
    public Task<CallToolResult> ListarAlunosAbaixoMinimoAsync(
        [Description("Identificador do curso Moodle.")]
        string courseId,
        [Description("Nota mínima esperada em porcentagem (0-100). Padrão: 60.")]
        decimal minGradePercent = 60m,
        [Description("Máximo de estudantes para analisar. Padrão: 100.")]
        int maxStudentsToAnalyze = 100,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrão do usuário.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return GetStudentsBelowMinCoreAsync(courseId, minGradePercent, maxStudentsToAnalyze, moodleAlias, cancellationToken);
    }

    private async Task<CallToolResult> GetStudentsBelowMinCoreAsync(
        string courseId,
        decimal minGradePercent,
        int maxStudentsToAnalyze,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
            return ToolResultHelper.Error<GetStudentsBelowMinGradeResult>("Informe um identificador de curso válido.");

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
            return ToolResultHelper.Error<GetStudentsBelowMinGradeResult>("Usuário não autenticado.");

        var effectiveCourseId = courseId;
        CourseGradebookSnapshot? prefetchedGradebook = null;
        CourseParticipantsPage? prefetchedParticipants = null;
        ToolFreshness? freshness = null;
        var freshnessWarnings = new List<string>();
        var coordinator = snapshotCoordinator ?? snapshotContext as IMoodleCourseReadSnapshotCoordinator;
        if (coordinator is not null)
        {
            try
            {
                var courseRead = await coordinator.ReadAsync(
                    new CourseReadSnapshotRequest(
                        courseId,
                        moodleAlias,
                        moodleUserId.Value.ToString(),
                        CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook,
                        AllowStale: false),
                    cancellationToken);
                if (courseRead is not null)
                {
                    effectiveCourseId = courseRead.CourseId;
                    var snapshotUnsafe = MoodleSnapshotFreshnessWarnings.IsUnsafeForSnapshotDecision(courseRead.Metadata);
                    freshnessWarnings.AddRange(MoodleSnapshotFreshnessWarnings.BuildWarnings(courseRead.Metadata));
                    var gradebookSafe = !snapshotUnsafe && courseRead.Gradebook is { IsStale: false, IsComplete: true } envelope &&
                        envelope.Data.Coverage.IsComplete;
                    var studentsSafe = !snapshotUnsafe && courseRead.Students is { IsStale: false, IsComplete: true, Data.HasMore: false };
                    if (gradebookSafe)
                    {
                        prefetchedGradebook = courseRead.Gradebook!.Data;
                    }

                    if (studentsSafe && courseRead.Students?.Data is { HasMore: false } participants)
                    {
                        prefetchedParticipants = participants;
                    }

                    var staleDecisionDatasets = courseRead.Metadata.StaleDatasets
                        .Where(dataset => dataset is MoodleSnapshotDatasets.Students or MoodleSnapshotDatasets.Gradebook)
                        .ToArray();
                    var snapshotSafeForDecision = staleDecisionDatasets.Length == 0 && !snapshotUnsafe;
                    if (staleDecisionDatasets.Length > 0)
                    {
                        freshnessWarnings.Add(
                            $"Snapshot stale ({string.Join(", ", staleDecisionDatasets)}); a análise decisória foi desviada para leitura ao vivo e não usará esses dados antigos.");
                    }

                    var updatedAt = courseRead.Metadata.OldestUpdatedAt;
                    freshness = new ToolFreshness(
                        snapshotSafeForDecision ? "snapshot" : "live",
                        snapshotSafeForDecision ? updatedAt : null,
                        snapshotSafeForDecision && updatedAt.HasValue
                            ? Math.Max(0, (long)(DateTimeOffset.UtcNow - updatedAt.Value).TotalSeconds)
                            : null,
                        snapshotSafeForDecision && courseRead.Metadata.StaleDatasets.Count > 0,
                        courseRead.Metadata.RefreshQueued,
                        snapshotSafeForDecision && courseRead.Metadata.IsComplete,
                        snapshotSafeForDecision
                            ? (courseRead.Students?.RecordCount ?? 0) + (courseRead.Gradebook?.RecordCount ?? 0)
                            : 0,
                        DecisionSafe: snapshotSafeForDecision,
                        Dataset: "course_read_snapshot",
                        RecordType: "students_and_gradebook");
                }
            }
            catch
            {
                // Snapshot warming is best effort; the current request uses
                // the live bulk gateway and its explicit fallback.
            }
        }

        GetStudentsBelowMinGradeResult data;
        try
        {
            data = await mediator.Send(
                new GetStudentsBelowMinGradeQuery(
                    effectiveCourseId,
                    minGradePercent,
                    maxStudentsToAnalyze,
                    PrefetchedGradebook: prefetchedGradebook,
                    PrefetchedParticipants: prefetchedParticipants),
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (MoodleApiException exception)
        {
            return ToolResultHelper.Error<GetStudentsBelowMinGradeResult>(exception);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<GetStudentsBelowMinGradeResult>(exception);
        }

        var liveReadIsComplete = data.GradebookIncompleteStudentIds.Count == 0;
        if (freshness is { Source: "live" })
        {
            // A stale snapshot only selects the live path; it must not make a
            // successful live revalidation permanently unsafe for decisions.
            freshness = freshness with
            {
                Complete = liveReadIsComplete,
                DecisionSafe = liveReadIsComplete,
            };
        }

        var decisionSafe = freshness?.Source == "live"
            ? liveReadIsComplete
            : freshness?.DecisionSafe != false && liveReadIsComplete;
        if (!decisionSafe)
        {
            // A partial live read must not turn the rows that happened to be
            // visible into a definitive pedagogical target list.
            data = data with { Students = [], SuggestedRecipientIds = [] };
            freshnessWarnings.Add("A análise não é segura para seleção pedagógica porque a cobertura do gradebook está incompleta.");
        }

        var response = new ToolResponse<GetStudentsBelowMinGradeResult>(
            "ok", data, freshnessWarnings, AuditId: null, DateTimeOffset.UtcNow, Freshness: freshness);
        var narration = $"Análise do curso {courseId}: {data.TotalStudentsAnalyzed} estudante(s) analisado(s). " +
                        $"{data.Students.Count} com pelo menos uma SA abaixo de {data.MinGradePercent}%.";

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = narration }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }
}
